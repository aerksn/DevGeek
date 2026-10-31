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
using OtterMonitor.Core;

namespace OtterMonitor.App;

// Own WebView2 session. Chrome's profile, cookies and processes are never used.
public sealed partial class BillingBrowser : IDisposable
{
    private readonly Window window;
    private readonly TextBlock address = new(), status = new() { Text = UiText.T("Förbereder Billing…"), FontSize = 11 };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer observeTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly string directory;
    private readonly Func<BalanceMessage, bool> receive;
    private readonly Action<string?> error;
    private readonly bool fixture;
    private readonly bool jev;
    private readonly bool anthropic;
    private string OverviewUrl => anthropic ? AnthropicBillingNavigation.OverviewUrl : jev ? JevBillingNavigation.OverviewUrl : BillingNavigation.OverviewUrl;
    private bool IsOverview(string url) => anthropic ? AnthropicBillingNavigation.IsOverview(url) : jev ? JevBillingNavigation.IsOverview(url) : BillingNavigation.IsOverview(url);
    private string StateFor(string url) => anthropic ? AnthropicBillingNavigation.StateFor(url) : jev ? JevBillingNavigation.StateFor(url) : BillingNavigation.StateFor(url);
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
    private Func<string, string>? fixtureResponse;
    private readonly List<BillingAuthPopup> authPopups = [];
    private bool authenticated;
    private DateTime sessionSaved;
    private Task? sessionCapture;
    private string? sessionError;

    public BillingBrowser(string directory, Func<BalanceMessage, bool> receive, Action<string?> error, bool jev = false, bool anthropic = false) : this(directory, receive, error, false, true, jev, anthropic) { }
    private BillingBrowser(string directory, Func<BalanceMessage, bool> receive, Action<string?> error, bool fixture, bool rememberSession = true, bool jev = false, bool anthropic = false)
    {
        this.directory = directory; this.receive = receive; this.fixture = fixture; this.jev = jev; this.anthropic = anthropic;
        this.error = message => error(message is null ? null : message.Replace(UiText.T("OpenAI-loggan"), anthropic ? UiText.T("Anthropic-knappen") : jev ? UiText.T("JEV-knappen") : UiText.T("OpenAI-loggan")));
        this.rememberSession = rememberSession; sessionStore = new(directory, jev: jev, anthropic: anthropic);
        using var resource = typeof(BillingBrowser).Assembly.GetManifestResourceStream(anthropic ? "OtterMonitor.AnthropicBalanceReader.js" : jev ? "OtterMonitor.JevBalanceReader.js" : "OtterMonitor.BalanceReader.js") ?? throw new IOException(UiText.T("Saldoläsaren saknas."));
        using var text = new StreamReader(resource); reader = text.ReadToEnd();
        window = new Window { Title = anthropic ? "Otter Monitor · Anthropic API Billing" : jev ? "Otter Monitor · JEV Billing" : "Otter Monitor · OpenAI Billing", Width = 1040, Height = 780, MinWidth = 760, MinHeight = 500, ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var layout = new DockPanel();
        var top = new DockPanel { Height = 48, Margin = new Thickness(10, 0, 10, 0), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Add(UiText.T("Till Billing"), () => controller?.CoreWebView2.Navigate(OverviewUrl));
        Add(UiText.T("Uppdatera saldo"), () => { _ = RefreshAsync(manual: true); });
        Add(UiText.T("Dölj"), () => window.Hide());
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
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(directory, anthropic ? "anthropic-profile" : jev ? "jev-profile" : "billing-profile"));
            if (disposed) return;
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "Default"; options.IsInPrivateModeEnabled = false;
            var created = await environment.CreateCoreWebView2ControllerAsync(handle, options);
            if (disposed) { created.Close(); return; }
            controller = created;
            // Claude Console gates account data on document visibility. Keep its renderer
            // active inside the hidden native host; the Billing window itself remains hidden.
            controller.IsVisible = anthropic; Resize();
            var core = controller.CoreWebView2;
            ConfigureFixture(core, environment);
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            if (rememberSession)
            {
                try { await sessionStore.RestoreAsync(core.CookieManager, () => !disposed); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or COMException or TimeoutException)
                { sessionError = UiText.T("Den sparade inloggningen kunde inte återställas. Logga in igen via OpenAI-loggan."); }
                if (disposed) return;
            }
            core.NavigationStarting += (_, args) =>
            {
                if (disposed) { args.Cancel = true; return; }
                if (!fixture && (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https")) { args.Cancel = true; return; }
                generation++; navigating = true; navigationStarted = DateTime.UtcNow; previous = "";
                if (!IsOverview(args.Uri))
                {
                    var state = StateFor(args.Uri);
                    if (state == "login_required") ForgetSavedSession();
                    Emit(new(1, state));
                }
                UpdateAddress(args.Uri);
            };
            core.NavigationCompleted += async (_, args) =>
            {
                if (disposed) return;
                navigating = false;
                if (!args.IsSuccess) { Emit(new(1, "unreadable")); error(UiText.T("Billing kunde inte laddas. Öppna Billing via OpenAI-loggan och försök igen.")); return; }
                if (!fixture && (anthropic ? AnthropicBillingNavigation.IsHome(core.Source) : jev ? JevBillingNavigation.IsHome(core.Source) : BillingNavigation.IsPlatformHome(core.Source))) { core.Navigate(OverviewUrl); return; }
                await ObserveAsync();
            };
            core.SourceChanged += async (_, args) =>
            {
                if (disposed) return;
                UpdateAddress(core.Source);
                if (anthropic && !args.IsNewDocument && !navigating)
                {
                    if (!fixture && AnthropicBillingNavigation.IsHome(core.Source)) core.Navigate(OverviewUrl);
                    else await ObserveAsync();
                }
            };
            core.NewWindowRequested += async (_, args) =>
            {
                if (anthropic) { await OpenAuthPopupAsync(environment, args, window); return; }
                args.Handled = true;
                if (window.IsVisible && args.IsUserInitiated && Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https") core.Navigate(args.Uri);
            };
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.ProcessFailed += (_, _) => { if (!disposed) { Emit(new(1, "unreadable")); error(UiText.T("Billing-sessionen avbröts. Koppla från och anslut igen via OpenAI-loggan.")); } };
            if (!fixture)
            {
                core.Navigate(OverviewUrl);
                refreshTimer.Start(); observeTimer.Start();
            }
            error(sessionError);
        }
        catch (Exception ex)
        {
            error(UiText.T("Bakgrundsläsaren kunde inte startas. Kontrollera att Microsoft Edge WebView2 Runtime är installerad. ") + ex.Message);
            throw;
        }
    }

    private void ConfigureFixture(CoreWebView2 core, CoreWebView2Environment environment)
    {
        if (!fixture) return;
        // Every request in the opener and its popups is served locally in tests.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) => args.Response = environment.CreateWebResourceResponse(
            new MemoryStream(Encoding.UTF8.GetBytes(fixtureResponse?.Invoke(args.Request.Uri) ?? fixtureHtml)), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
    }
    private async Task OpenAuthPopupAsync(CoreWebView2Environment environment, CoreWebView2NewWindowRequestedEventArgs args, Window opener)
    {
        args.Handled = true;
        if (disposed || !fixture && (!opener.IsVisible || !args.IsUserInitiated) || !BillingAuthPopup.Allowed(args.Uri) || authPopups.Count >= 3) return;
        var deferral = args.GetDeferral();
        var popup = new BillingAuthPopup(opener, closed => authPopups.Remove(closed));
        authPopups.Add(popup);
        try
        {
            await popup.InitializeAsync(environment, core => ConfigureFixture(core, environment),
                async (nested, owner) => await OpenAuthPopupAsync(environment, nested, owner));
            if (disposed || popup.IsDisposed) { popup.Dispose(); return; }
            // Supply the new WebView before completing the request. Never navigate the opener to the popup URI.
            args.NewWindow = popup.Core;
            popup.Show(fixture);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException)
        {
            popup.Dispose();
            if (!disposed) error(UiText.T("Inloggningsfönstret kunde inte öppnas. Försök igen från Billing."));
        }
        finally
        {
            // Dispose completes this SDK deferral. Calling Complete as well completes it twice.
            try { deferral.Dispose(); }
            catch (COMException) { popup.Dispose(); }
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
        if (busy || !manual && !(anthropic ? AnthropicBillingNavigation.CanAutoRefresh(core.Source, window.IsVisible, window.WindowState == WindowState.Minimized, busy)
            : jev ? JevBillingNavigation.CanAutoRefresh(core.Source, window.IsVisible, window.WindowState == WindowState.Minimized, busy)
            : BillingNavigation.CanAutoRefresh(core.Source, window.IsVisible, window.WindowState == WindowState.Minimized, busy))) return;
        try
        {
            if (IsOverview(core.Source))
            {
                if (!manual)
                {
                    var currentGeneration = generation;
                    var refreshCheck = jev ? "(() => { const module = { exports: {} }; " + reader + "; return module.exports.canRefresh(document); })()"
                        : "!document.activeElement?.matches('input,textarea,select,[contenteditable=true]') && !document.querySelector('[role=dialog],dialog[open]')";
                    var safe = await core.ExecuteScriptAsync(refreshCheck).WaitAsync(TimeSpan.FromSeconds(15));
                    if (safe != "true" || disposed || generation != currentGeneration) return;
                }
                core.Reload();
            }
            else if (manual) core.Navigate(OverviewUrl);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
        { if (!disposed) { Emit(new(1, "unreadable")); error(UiText.T("Billing kunde inte uppdateras. Öppna sessionen via OpenAI-loggan och försök igen.")); } }
    }
    private async Task ObserveAsync()
    {
        if (disposed || reading || navigating || controller is null) return;
        var core = controller.CoreWebView2;
        if (!IsOverview(core.Source))
        {
            var state = StateFor(core.Source);
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
        { if (!disposed && generation == currentGeneration) { Emit(new(1, "unreadable")); error(UiText.T("Saldot kunde inte läsas. Öppna Billing via OpenAI-loggan och kontrollera inloggningen.")); } }
        finally { reading = false; }
    }
    private bool Emit(BalanceMessage message)
    {
        if (disposed) return false;
        var fingerprint = JsonSerializer.Serialize(message, BalanceStore.JsonOptions);
        if (fingerprint == previous) return true; // Merely reading the same DOM must not renew its timestamp.
        if (!receive(message)) { status.Text = UiText.T("Avläsningen kunde inte sparas. Otter Monitor försöker igen."); return false; }
        previous = fingerprint;
        status.Text = message.State switch
        {
            "ok" => UiText.F("Saldo avl\u00E4st {0:HH:mm:ss}. D\u00F6lj f\u00F6nstret f\u00F6r avl\u00E4sning i bakgrunden var femte minut.", DateTime.Now),
            "login_required" => UiText.T("Logga in här. Sessionen sparas krypterat för nästa appstart när saldot har lästs."),
            "paused" => UiText.T("Välj Till Billing för att återgå till saldoöversikten."),
            _ => UiText.T("Saldofältet kunde inte läsas. Kontrollera Billing eller vänta tills sidan har laddats.")
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
        { sessionError = "Den sparade sessionen kunde inte raderas. Kontrollera Otter Monitors datamapp."; }
    }
    public void DisconnectAccount()
    {
        controller?.CoreWebView2.CookieManager.DeleteAllCookies();
        ForgetSavedSession();
    }
    public async Task FlushSessionAsync()
    {
        if (sessionCapture is { IsCompleted: false }) await sessionCapture;
        await CaptureSessionAsync(force: true);
    }
    private Task CaptureSessionAsync(bool force)
    {
        if (sessionCapture is { IsCompleted: false }) return sessionCapture;
        if (!rememberSession || disposed || controller is null || !authenticated || navigating || !IsOverview(controller.CoreWebView2.Source)
            || !force && DateTime.UtcNow - sessionSaved < TimeSpan.FromMinutes(1)) return Task.CompletedTask;
        return sessionCapture = SaveAsync();
        async Task SaveAsync()
        {
            var currentGeneration = generation;
            try
            {
                var cookies = await sessionStore.GetCookiesAsync(controller!.CoreWebView2.CookieManager).WaitAsync(TimeSpan.FromSeconds(3));
                if (disposed || !authenticated || currentGeneration != generation) return;
                sessionStore.Save(cookies, DateTime.UtcNow); sessionSaved = DateTime.UtcNow; sessionError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or COMException or TimeoutException)
            { if (!disposed) sessionError = UiText.T("Inloggningen kunde inte sparas för nästa appstart. Kontrollera Otter Monitors datamapp."); }
        }
    }
    private void HideOnClose(object? sender, CancelEventArgs args)
    {
        if (disposed) return;
        args.Cancel = true; window.Hide();
    }
    private void UpdateVisibility()
    {
        var visible = window.IsVisible && window.WindowState != WindowState.Minimized;
        if (controller is not null) controller.IsVisible = anthropic || visible;
        if (!visible) foreach (var popup in authPopups.ToArray()) popup.Dispose();
    }
    private void Resize()
    {
        if (controller is null) return;
        if (!GetClientRect(new WindowInteropHelper(window).Handle, out var rect)) return;
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleY;
        var top = (int)Math.Round(48 * scale); var bottom = (int)Math.Round(28 * scale);
        // EnsureHandle may create a zero-sized HWND before WPF has ever shown the window.
        // Keep a real desktop viewport for the site's responsive layout during background reads.
        var width = rect.Right > 64 ? rect.Right : (int)Math.Round(window.Width * scale);
        var height = rect.Bottom > top + bottom + 64 ? rect.Bottom : (int)Math.Round(window.Height * scale);
        controller.Bounds = new System.Drawing.Rectangle(0, top, Math.Max(1, width), Math.Max(1, height - top - bottom));
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; refreshTimer.Stop(); observeTimer.Stop();
        foreach (var popup in authPopups.ToArray()) popup.Dispose();
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
                else browser.controller.CoreWebView2.Navigate(BillingNavigation.OverviewUrl);
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
