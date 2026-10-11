using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PortPilot.Core;

namespace PortPilot.App;

// Own WebView2 session. Chrome's profile, cookies and processes are never used.
public sealed partial class BillingBrowser : IDisposable
{
    private readonly Window window;
    private readonly TextBlock address = new(), status = new() { Text = "Förbereder Billing…", FontSize = 11 };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer observeTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly string directory;
    private readonly Func<BalanceMessage, bool> receive;
    private readonly Action<string?> error;
    private readonly bool fixture;
    private readonly bool rememberSession;
    private readonly BillingSessionStore sessionStore;
    private CoreWebView2Controller? controller;
    private Task? initializing;
    private bool disposed, reading, navigating;
    private long generation;
    private string previous = "";
    private readonly string reader;
    private DateTime navigationStarted;
    private string fixtureHtml = "";
    private bool authenticated;
    private DateTime sessionSaved;
    private Task? sessionCapture;
    private string? sessionError;

    public BillingBrowser(string directory, Func<BalanceMessage, bool> receive, Action<string?> error) : this(directory, receive, error, false) { }
    private BillingBrowser(string directory, Func<BalanceMessage, bool> receive, Action<string?> error, bool fixture, bool rememberSession = true)
    {
        this.directory = directory; this.receive = receive; this.error = error; this.fixture = fixture;
        this.rememberSession = rememberSession; sessionStore = new(directory);
        using var resource = typeof(BillingBrowser).Assembly.GetManifestResourceStream("PortPilot.BalanceReader.js") ?? throw new IOException("Saldoläsaren saknas.");
        using var text = new StreamReader(resource); reader = text.ReadToEnd();
        window = new Window { Title = "DevGeek · OpenAI Billing", Width = 1040, Height = 780, MinWidth = 760, MinHeight = 500, ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var layout = new DockPanel();
        var top = new DockPanel { Height = 48, Margin = new Thickness(10, 0, 10, 0), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Add("Till Billing", () => controller?.CoreWebView2.Navigate(BalanceNativeHost.BillingUrl));
        Add("Uppdatera saldo", () => { _ = RefreshAsync(manual: true); });
        Add("Dölj", () => window.Hide());
        DockPanel.SetDock(buttons, Dock.Right); top.Children.Add(buttons);
        address.FontSize = 11; address.VerticalAlignment = VerticalAlignment.Center; address.TextTrimming = TextTrimming.CharacterEllipsis;
        top.Children.Add(address); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        status.Margin = new Thickness(12, 4, 12, 4); status.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(status, Dock.Bottom); layout.Children.Add(status); window.Content = layout;
        window.Closing += HideOnClose;
        window.SizeChanged += (_, _) => Resize();
        window.IsVisibleChanged += (_, _) => UpdateVisibility();
        window.StateChanged += (_, _) => UpdateVisibility();
        refreshTimer.Tick += async (_, _) => await RefreshAsync(manual: false);
        observeTimer.Tick += async (_, _) => await ObserveAsync();

        void Add(string label, Action action)
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(5, 0, 0, 0) };
            button.Click += (_, _) => action(); buttons.Children.Add(button);
        }
    }

    public Task StartAsync() => initializing ??= InitializeAsync();
    private async Task InitializeAsync()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(directory, "billing-profile"));
            if (disposed) return;
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "Default"; options.IsInPrivateModeEnabled = false;
            var created = await environment.CreateCoreWebView2ControllerAsync(handle, options);
            if (disposed) { created.Close(); return; }
            controller = created; controller.IsVisible = false; Resize();
            var core = controller.CoreWebView2;
            if (fixture)
            {
                // Serve only our own test HTML; no request to an account or website is made by this test.
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, args) => args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(fixtureHtml)), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            }
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            if (rememberSession)
            {
                try { await sessionStore.RestoreAsync(core.CookieManager, () => !disposed); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or COMException or TimeoutException)
                { sessionError = "Den sparade inloggningen kunde inte återställas. Logga in igen via OpenAI-loggan."; }
                if (disposed) return;
            }
            core.NavigationStarting += (_, args) =>
            {
                if (disposed) { args.Cancel = true; return; }
                if (!fixture && (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https")) { args.Cancel = true; return; }
                generation++; navigating = true; navigationStarted = DateTime.UtcNow; previous = "";
                if (!BillingNavigation.IsOverview(args.Uri))
                {
                    var state = BillingNavigation.StateFor(args.Uri);
                    if (state == "login_required") ForgetSavedSession();
                    Emit(new(1, state));
                }
                UpdateAddress(args.Uri);
            };
            core.NavigationCompleted += async (_, args) =>
            {
                if (disposed) return;
                navigating = false;
                if (!args.IsSuccess) { Emit(new(1, "unreadable")); error("Billing kunde inte laddas. Öppna Billing via OpenAI-loggan och försök igen."); return; }
                if (!fixture && BillingNavigation.IsPlatformHome(core.Source)) { core.Navigate(BalanceNativeHost.BillingUrl); return; }
                await ObserveAsync();
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (window.IsVisible && args.IsUserInitiated && Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https") core.Navigate(args.Uri);
            };
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.ProcessFailed += (_, _) => { if (!disposed) { Emit(new(1, "unreadable")); error("Billing-sessionen avbröts. Koppla från och anslut igen via OpenAI-loggan."); } };
            if (!fixture)
            {
                core.Navigate(BalanceNativeHost.BillingUrl);
                refreshTimer.Start(); observeTimer.Start();
            }
            error(sessionError);
        }
        catch (Exception ex)
        {
            error("Bakgrundsläsaren kunde inte startas. Kontrollera att Microsoft Edge WebView2 Runtime är installerad. " + ex.Message);
            throw;
        }
    }

    public async Task ShowAsync()
    {
        await StartAsync(); if (disposed || controller is null) return;
        window.Show(); window.WindowState = WindowState.Normal; window.Activate(); Resize(); UpdateVisibility();
    }
    public async Task RefreshAsync(bool manual)
    {
        if (disposed || controller is null) return;
        var core = controller.CoreWebView2;
        var busy = navigating && DateTime.UtcNow - navigationStarted < TimeSpan.FromSeconds(60);
        if (busy || !manual && !BillingNavigation.CanAutoRefresh(core.Source, window.IsVisible, window.WindowState == WindowState.Minimized, busy)) return;
        try
        {
            if (BillingNavigation.IsOverview(core.Source))
            {
                if (!manual)
                {
                    var currentGeneration = generation;
                    var safe = await core.ExecuteScriptAsync("!document.activeElement?.matches('input,textarea,select,[contenteditable=true]') && !document.querySelector('[role=dialog],dialog[open]')").WaitAsync(TimeSpan.FromSeconds(15));
                    if (safe != "true" || disposed || generation != currentGeneration) return;
                }
                core.Reload();
            }
            else if (manual) core.Navigate(BalanceNativeHost.BillingUrl);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
        { if (!disposed) { Emit(new(1, "unreadable")); error("Billing kunde inte uppdateras. Öppna sessionen via OpenAI-loggan och försök igen."); } }
    }
    private async Task ObserveAsync()
    {
        if (disposed || reading || navigating || controller is null) return;
        var core = controller.CoreWebView2;
        if (!BillingNavigation.IsOverview(core.Source))
        {
            var state = BillingNavigation.StateFor(core.Source);
            if (state == "login_required") ForgetSavedSession();
            Emit(new(1, state)); return;
        }
        var currentGeneration = generation;
        reading = true;
        try
        {
            // Module binding is local to this script; no global bridge, credentials or page APIs are accessed.
            var script = "(() => { const module = { exports: {} }; " + reader + "; return module.exports.read(document,location.href); })()";
            var json = await core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(15));
            if (disposed || generation != currentGeneration) return; // Discard results from a page that has since navigated.
            var message = BalanceMessage.Parse(Encoding.UTF8.GetBytes(json));
            if (message.State == "ok") { authenticated = true; await CaptureSessionAsync(force: false); }
            else if (message.State == "login_required") ForgetSavedSession();
            if (disposed || generation != currentGeneration) return;
            if (Emit(message)) error(sessionError);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException or COMException or TimeoutException)
        { if (!disposed && generation == currentGeneration) { Emit(new(1, "unreadable")); error("Saldot kunde inte läsas. Öppna Billing via OpenAI-loggan och kontrollera inloggningen."); } }
        finally { reading = false; }
    }
    private bool Emit(BalanceMessage message)
    {
        if (disposed) return false;
        var fingerprint = JsonSerializer.Serialize(message, BalanceStore.JsonOptions);
        if (fingerprint == previous) return true; // Merely reading the same DOM must not renew its timestamp.
        if (!receive(message)) { status.Text = "Avläsningen kunde inte sparas. DevGeek försöker igen."; return false; }
        previous = fingerprint;
        status.Text = message.State switch
        {
            "ok" => $"Saldo avläst {DateTime.Now:HH:mm:ss}. Dölj fönstret för avläsning i bakgrunden var femte minut.",
            "login_required" => "Logga in här. Sessionen sparas krypterat för nästa appstart när saldot har lästs.",
            "paused" => "Välj Till Billing för att återgå till saldoöversikten.",
            _ => "Saldofältet kunde inte läsas. Kontrollera Billing eller vänta tills sidan har laddats."
        };
        return true;
    }
    private void UpdateAddress(string url)
    {
        // Display origin/path for the person signing in; do not display or log OAuth query parameters.
        address.Text = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : "Billing";
    }
    public void ForgetSavedSession()
    {
        authenticated = false; sessionSaved = DateTime.MinValue; generation++;
        if (!rememberSession) return;
        try { sessionStore.Clear(); sessionError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { sessionError = "Den sparade sessionen kunde inte raderas. Kontrollera DevGeeks datamapp."; }
    }
    public async Task FlushSessionAsync()
    {
        if (sessionCapture is { IsCompleted: false }) await sessionCapture;
        await CaptureSessionAsync(force: true);
    }
    private Task CaptureSessionAsync(bool force)
    {
        if (sessionCapture is { IsCompleted: false }) return sessionCapture;
        if (!rememberSession || disposed || controller is null || !authenticated || navigating || !BillingNavigation.IsOverview(controller.CoreWebView2.Source)
            || !force && DateTime.UtcNow - sessionSaved < TimeSpan.FromMinutes(1)) return Task.CompletedTask;
        return sessionCapture = SaveAsync();
        async Task SaveAsync()
        {
            var currentGeneration = generation;
            try
            {
                var cookies = await BillingSessionStore.GetOwnCookiesAsync(controller!.CoreWebView2.CookieManager).WaitAsync(TimeSpan.FromSeconds(3));
                if (disposed || !authenticated || currentGeneration != generation) return;
                sessionStore.Save(cookies, DateTime.UtcNow); sessionSaved = DateTime.UtcNow; sessionError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or COMException or TimeoutException)
            { if (!disposed) sessionError = "Inloggningen kunde inte sparas för nästa appstart. Kontrollera DevGeeks datamapp."; }
        }
    }
    private void HideOnClose(object? sender, CancelEventArgs args)
    {
        if (disposed) return;
        args.Cancel = true; window.Hide();
    }
    private void UpdateVisibility()
    {
        if (controller is not null) controller.IsVisible = window.IsVisible && window.WindowState != WindowState.Minimized;
    }
    private void Resize()
    {
        if (controller is null) return;
        if (!GetClientRect(new WindowInteropHelper(window).Handle, out var rect)) return;
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleY;
        var top = (int)Math.Round(48 * scale); var bottom = (int)Math.Round(28 * scale);
        controller.Bounds = new System.Drawing.Rectangle(0, top, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom - top - bottom));
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; refreshTimer.Stop(); observeTimer.Stop();
        controller?.Close(); controller = null; window.Close();
    }

    public static async Task<string> VerifyAsync(string directory)
    {
        var messages = new List<BalanceMessage>(); var errors = new List<string>(); var checks = new List<string>();
        var accepting = true;
        using var browser = new BillingBrowser(directory, value => { if (!accepting) return false; messages.Add(value); return true; }, value => { if (value is not null) errors.Add(value); }, fixture: true);
        await browser.StartAsync();
        Check(browser.controller is not null && !browser.controller.IsVisible && !browser.window.IsVisible, "Own WebView2 starts hidden with an isolated profile");
        async Task Load(string html, bool automaticReload = false)
        {
            var complete = new TaskCompletionSource<bool>();
            void Done(object? sender, CoreWebView2NavigationCompletedEventArgs args) => complete.TrySetResult(args.IsSuccess);
            browser.controller!.CoreWebView2.NavigationCompleted += Done;
            try
            {
                browser.fixtureHtml = html;
                if (automaticReload) await browser.RefreshAsync(manual: false);
                else browser.controller.CoreWebView2.Navigate(BalanceNativeHost.BillingUrl);
                Check(await complete.Task.WaitAsync(TimeSpan.FromSeconds(20)), "Fixture navigation succeeds");
            }
            finally { browser.controller.CoreWebView2.NavigationCompleted -= Done; }
            // The production handler may still be reading, so wait for that read before checking.
            var until = DateTime.UtcNow.AddSeconds(20);
            while (browser.reading || browser.navigating) { if (DateTime.UtcNow > until) throw new TimeoutException("Reader did not finish."); await Task.Delay(50); }
            await browser.ObserveAsync();
        }
        await Load("<div><span><span>API credit balance</span></span><div>$24.50</div></div><p>When credits reach $0, requests stop.</p><button>Buy credits</button>");
        Check(messages.LastOrDefault() is { State: "ok", Amount: "24.50" }, "Hidden real DOM reads the credit card, excluding the warning");
        var count = messages.Count; await browser.ObserveAsync();
        Check(messages.Count == count, "Repeated polling does not renew an unchanged DOM reading");
        await Load("<div><span><span>API credit balance</span></span><div>$19.25</div></div>", automaticReload: true);
        Check(messages.LastOrDefault() is { State: "ok", Amount: "19.25" }, "Automatic refresh of a hidden page returns a fresh changed balance");
        await Load("<div><span><span>API credit balance</span></span><div></div></div><p>Credit reaches $0</p>");
        Check(messages.LastOrDefault() is { State: "unreadable" }, "A missing field never turns the warning into a balance");
        accepting = false; var previousCount = messages.Count;
        await Load("<div><span><span>API credit balance</span></span><div>$18.00</div></div>");
        Check(messages.Count == previousCount, "A failed local save is not treated as a delivered reading");
        accepting = true; await browser.ObserveAsync();
        Check(messages.LastOrDefault() is { State: "ok", Amount: "18.00" }, "A reading is retried after local storage recovers");
        Check(!browser.window.IsVisible && !browser.controller!.IsVisible && errors.Count == 0, "All navigation and reads finish with no visible browser window");
        return string.Join(Environment.NewLine, checks);
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add("PASS " + name); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
}
