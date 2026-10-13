using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PortPilot.Core;

namespace PortPilot.App;

// A separate, user-signed-in Claude profile. Never opens Claude Desktop's credentials.
internal sealed class ClaudeUsageBrowser : IDisposable
{
    private readonly Window window = new() { Title = "Otter Monitor · Claude usage", Width = 1000, Height = 760, MinWidth = 720, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterScreen };
    private readonly TextBlock status = new() { Text = "Logga in med samma Claude-konto som i appen.", Margin = new Thickness(12, 4, 12, 4), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock address = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer observeTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly string directory, reader;
    private readonly Action<ClaudePageUsage> receive;
    private readonly Action<string?> error;
    private readonly BillingSessionStore session;
    private readonly bool fixture;
    private CoreWebView2Controller? controller;
    private Task? initializing, checkpoint;
    private bool disposed, navigating, reading, authenticated, resumeAfterSignIn;
    private long generation;
    private string previous = "", fixtureHtml = "";
    private string? sessionError;

    public bool IsVisible => window.IsVisible;
    public ClaudeUsageBrowser(string directory, Action<ClaudePageUsage> receive, Action<string?> error, Action disconnect)
        : this(directory, receive, error, disconnect, false) { }
    private ClaudeUsageBrowser(string directory, Action<ClaudePageUsage> receive, Action<string?> error, Action disconnect, bool fixture)
    {
        this.directory = directory; this.receive = receive; this.error = error; this.fixture = fixture; session = new(directory, claude: true);
        using var resource = typeof(ClaudeUsageBrowser).Assembly.GetManifestResourceStream("PortPilot.ClaudeUsageReader.js") ?? throw new IOException("Claude-läsaren saknas.");
        using var text = new StreamReader(resource); reader = text.ReadToEnd();
        var layout = new DockPanel();
        var top = new DockPanel { Height = 48, Margin = new Thickness(10, 0, 10, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Add("Till användning", () => controller?.CoreWebView2.Navigate(ClaudePageUsage.Url));
        Add("Uppdatera", () => { _ = RefreshAsync(manual: true); });
        Add("Koppla från", () => { ForgetSession(); controller?.CoreWebView2.CookieManager.DeleteAllCookies(); disconnect(); Dispose(); });
        Add("Dölj", () => window.Hide());
        DockPanel.SetDock(buttons, Dock.Right); top.Children.Add(buttons); top.Children.Add(address);
        DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        DockPanel.SetDock(status, Dock.Bottom); layout.Children.Add(status); window.Content = layout;
        window.Closing += HideOnClose;
        window.SizeChanged += (_, _) => Resize();
        window.IsVisibleChanged += (_, _) => UpdateVisibility(); window.StateChanged += (_, _) => UpdateVisibility();
        observeTimer.Tick += async (_, _) => await ObserveAsync();
        refreshTimer.Tick += async (_, _) => await RefreshAsync(manual: false);
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
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(directory, "claude-profile"));
            if (disposed) return;
            var created = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).EnsureHandle());
            if (disposed) { created.Close(); return; }
            controller = created; controller.IsVisible = false; Resize();
            var core = controller.CoreWebView2;
            if (fixture)
            {
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, args) => args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(fixtureHtml)), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            }
            core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false; core.Settings.IsWebMessageEnabled = false;
            try { await session.RestoreAsync(core.CookieManager, () => !disposed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException or COMException or TimeoutException)
            { sessionError = "Claude-inloggningen kunde inte återställas. Logga in igen här."; }
            if (disposed) return;
            core.NavigationStarting += (_, args) =>
            {
                if (disposed || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https") { args.Cancel = true; return; }
                navigating = true; generation++; previous = "";
                address.Text = uri.GetLeftPart(UriPartial.Path);
                if (uri.Host == "claude.ai" && uri.AbsolutePath.StartsWith("/login", StringComparison.Ordinal)) { resumeAfterSignIn = true; ForgetSession(); }
                if (!ClaudePageUsage.IsUsagePage(args.Uri)) Publish(new(uri.AbsolutePath.Contains("login") || uri.Host != "claude.ai" ? "login_required" : "paused", null));
            };
            core.NavigationCompleted += async (_, args) =>
            {
                navigating = false; if (disposed) return;
                if (!args.IsSuccess) { Publish(new("unreadable", null)); return; }
                if (!fixture && resumeAfterSignIn && Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && uri.Host == "claude.ai" && uri.AbsolutePath.TrimEnd('/') is "" or "/new")
                { resumeAfterSignIn = false; core.Navigate(ClaudePageUsage.Url); return; }
                await ObserveAsync();
            };
            core.SourceChanged += async (_, args) =>
            {
                if (disposed) return;
                if (Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)) address.Text = uri.GetLeftPart(UriPartial.Path);
                if (!args.IsNewDocument && !navigating) await ObserveAsync();
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (window.IsVisible && args.IsUserInitiated && Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https") core.Navigate(args.Uri);
            };
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.ProcessFailed += (_, _) => { if (!disposed) { Publish(new("unreadable", null)); error("Claude-läsaren avbröts. Koppla från och anslut igen."); } };
            if (!fixture) { core.Navigate(ClaudePageUsage.Url); observeTimer.Start(); refreshTimer.Start(); }
        }
        catch { error("Claude-läsaren kunde inte startas. Kontrollera WebView2 Runtime och försök igen."); throw; }
    }
    public async Task ShowAsync()
    {
        await StartAsync(); if (disposed) return;
        window.Show(); window.WindowState = WindowState.Normal; window.Activate(); Resize(); UpdateVisibility();
    }
    public async Task RefreshAsync(bool manual)
    {
        if (disposed || navigating || controller is null || !manual && window.IsVisible && window.WindowState != WindowState.Minimized) return;
        try
        {
            if (ClaudePageUsage.IsClaudePage(controller.CoreWebView2.Source)) controller.CoreWebView2.Navigate(ClaudePageUsage.Url);
            else if (manual) controller.CoreWebView2.Navigate(ClaudePageUsage.Url);
            await Task.CompletedTask;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { Publish(new("unreadable", null)); }
    }
    private async Task ObserveAsync()
    {
        if (disposed || navigating || reading || controller is null || !ClaudePageUsage.IsClaudePage(controller.CoreWebView2.Source)) return;
        reading = true; var current = generation;
        try
        {
            var json = await controller.CoreWebView2.ExecuteScriptAsync("(() => { const module = { exports: {} }; " + reader + "; return module.exports.read(document,location.href); })()").WaitAsync(TimeSpan.FromSeconds(15));
            if (disposed || current != generation || json == previous) return;
            var observation = ClaudePageUsage.Parse(json, DateTimeOffset.UtcNow);
            if (observation.State == "ok") { authenticated = true; await FlushSessionAsync(); }
            if (disposed || current != generation) return;
            previous = json; Publish(observation);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or JsonException or IOException or TimeoutException)
        { if (!disposed && current == generation) Publish(new("unreadable", null)); }
        finally { reading = false; }
    }
    private void Publish(ClaudePageUsage value)
    {
        if (disposed) return;
        status.Text = value.State switch
        {
            "ok" => $"Kvot avläst {DateTime.Now:HH:mm:ss}. Dölj fönstret för uppdatering varje minut medan Claude-appen körs.",
            "login_required" => "Logga in med samma Claude-konto som i appen. Sessionen sparas för nästa appstart.",
            "paused" => "Välj Till användning för att läsa kontots kvot.",
            _ => "Kvoten kunde inte läsas. Kontrollera inloggningen och att användningssidan har laddats."
        };
        receive(value); error(sessionError);
    }
    private void ForgetSession()
    {
        authenticated = false;
        try { session.Clear(); sessionError = null; }
        catch (IOException) { sessionError = "Den sparade Claude-sessionen kunde inte raderas."; }
        catch (UnauthorizedAccessException) { sessionError = "Den sparade Claude-sessionen kunde inte raderas."; }
    }
    public Task FlushSessionAsync()
    {
        if (checkpoint is { IsCompleted: false }) return checkpoint;
        if (disposed || !authenticated || navigating || controller is null || !ClaudePageUsage.IsClaudePage(controller.CoreWebView2.Source)) return Task.CompletedTask;
        return checkpoint = CaptureAsync();
        async Task CaptureAsync()
        {
            var current = generation;
            try
            {
                var cookies = await session.GetCookiesAsync(controller!.CoreWebView2.CookieManager).WaitAsync(TimeSpan.FromSeconds(3));
                if (disposed || !authenticated || current != generation) return;
                session.Save(cookies, DateTime.UtcNow); sessionError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException or COMException or TimeoutException)
            { sessionError = "Claude-inloggningen kunde inte sparas. Du kan behöva logga in igen nästa gång."; }
        }
    }
    private void HideOnClose(object? sender, CancelEventArgs e) { if (!disposed) { e.Cancel = true; window.Hide(); } }
    private void UpdateVisibility() { if (controller is not null) controller.IsVisible = window.IsVisible && window.WindowState != WindowState.Minimized; }
    private void Resize()
    {
        if (controller is null || !GetClientRect(new WindowInteropHelper(window).Handle, out var rect)) return;
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleY;
        var top = (int)Math.Round(48 * scale); var bottom = (int)Math.Round(28 * scale);
        controller.Bounds = new System.Drawing.Rectangle(0, top, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom - top - bottom));
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; observeTimer.Stop(); refreshTimer.Stop(); controller?.Close(); controller = null; window.Close();
    }

    internal static async Task<string> VerifyAsync(string directory, string phase)
    {
        var checks = new List<string>(); var values = new List<ClaudePageUsage>();
        using var browser = new ClaudeUsageBrowser(directory, values.Add, _ => { }, () => { }, fixture: true);
        await browser.StartAsync(); var core = browser.controller!.CoreWebView2;
        using var runtime = Process.GetProcessById((int)core.BrowserProcessId);
        Check(!browser.window.IsVisible && !browser.controller.IsVisible && !core.Profile.IsInPrivateModeEnabled, "Claude usage starts hidden in its own persistent profile");
        Check(browser.session.IsSessionDomain(".claude.ai") && !browser.session.IsSessionDomain("openai.com") && !browser.session.IsSessionDomain("claude.ai.example.com"), "Claude session domains are separate from Billing and reject lookalikes");
        if (phase == "seed")
        {
            var cookie = core.CookieManager.CreateCookie("portpilot_fixture_claude", "synthetic-only", ".claude.ai", "/"); cookie.IsSecure = cookie.IsHttpOnly = true;
            core.CookieManager.AddOrUpdateCookie(cookie);
            var other = core.CookieManager.CreateCookie("unrelated", "synthetic-only", ".openai.com", "/"); core.CookieManager.AddOrUpdateCookie(other);
            await Load(Card("Current session", "37% used")); await browser.FlushSessionAsync();
            Check(browser.session.Read(DateTime.UtcNow) is [{ Name: "portpilot_fixture_claude" }], "Only the own Claude session cookie is checkpointed");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(browser.session.FilePath)).Contains("synthetic-only"), "Claude session checkpoint is encrypted with Windows DPAPI");
            cookie.Value = "synthetic-final"; core.CookieManager.AddOrUpdateCookie(cookie);
            await MainWindow.VerifyClaudeShutdownAsync(Path.Combine(directory, "host"), browser);
            Check(browser.disposed && browser.session.Read(DateTime.UtcNow).Any(item => item.Name == "portpilot_fixture_claude" && item.Value == "synthetic-final"),
                "The real PortPilot close handler checkpoints the latest Claude login before disposal");
        }
        else if (phase == "probe")
        {
            var cookies = await browser.session.GetCookiesAsync(core.CookieManager);
            Check(cookies.Any(cookie => cookie.Name == "portpilot_fixture_claude" && cookie.Value == "synthetic-final" && cookie.IsSession && cookie.IsHttpOnly && cookie.IsSecure), "A fresh process restores the Claude login cookie and its security attributes");
            Check((await core.CookieManager.GetCookiesAsync("https://platform.openai.com/")).All(cookie => cookie.Name != "unrelated"), "The Claude checkpoint never restores OpenAI cookies");
            await Load(Card("Current session", "37% used"));
            Check(values.Last().Snapshot!.Windows.Single().Remaining == 63, "The restarted hidden usage reader returns the observed quota");
        }
        else if (phase == "revoke")
        {
            await Load("<p>Sign in to Claude</p>", url: "https://claude.ai/login");
            core.CookieManager.DeleteAllCookies();
            Check(!File.Exists(browser.session.FilePath) && !browser.authenticated, "Returning to Claude login revokes the saved session");
        }
        else if (phase == "probe-revoked")
        {
            Check((await browser.session.GetCookiesAsync(core.CookieManager)).All(cookie => cookie.Name != "portpilot_fixture_claude"), "A logged-out Claude cookie is not resurrected on restart");
        }
        else
        {
            await Load(Card("Current session", "37% used") + "<h2>Weekly limits</h2>" + Card("All models", "62% used") + Card("Sonnet only", "99% used") + Card("Usage credits", "100% used · $50"));
            Check(values.Last().Snapshot!.Windows is [{ Remaining: 63, DurationMinutes: 300 }, { Remaining: 38, DurationMinutes: 10080 }], "Visible session and all-model quotas exclude model-only and paid-credit cards");
            await Load("<aside>" + Card("Current session", "99% used") + "</aside><div role='dialog'><h2>Plan usage limits</h2>"
                + ModernRow("Current session", "0% used", "Starts when a message is sent") + "<h2>Weekly limits</h2>"
                + ModernRow("All models", "13% used", "Resets Fri 2:00 PM") + ModernRow("Fable", "16% used", "Resets Fri 2:00 PM")
                + "<h3>Cloud session credits</h3>" + ModernRow("Included credit", "$250 of $250 left", "Expires later") + "</div>");
            Check(values.Last().Snapshot!.Windows is [{ Remaining: 100, DurationMinutes: 300 }, { Remaining: 87, DurationMinutes: 10080 }], "Current settings dialog reads deeply nested h5 quota rows and excludes Fable, sidebar and cloud credits");
            var spaDialog = "<aside>" + Card("Current session", "99% used") + "</aside><div role='dialog'><h3>Plan usage limits</h3>"
                + "<div><div><div><span>Current session</span></div><p>Starts when a message is sent</p></div><div><div></div><span>0% used</span></div></div>"
                + "<h3>Weekly limits</h3><div><div><div><span>All models</span></div><p>Resets Fri 2:00 PM</p></div><div><div></div><span>13% used</span></div></div>"
                + ModernRow("Fable", "16% used", "Resets Fri 2:00 PM") + "</div>";
            await Load(spaDialog + "<script>history.replaceState(null,'','/new')</script>");
            Check(new Uri(core.Source).AbsolutePath == "/new" && values.Last().Snapshot!.Windows is [{ Remaining: 100 }, { Remaining: 87 }],
                "The real settings dialog remains readable after Claude's SPA moves the usage route to /new");
            await core.ExecuteScriptAsync("document.querySelector('[role=dialog]').remove()"); await browser.ObserveAsync();
            Check(values.Last().State == "paused", "Closing the usage dialog on /new never reads quota-like chat content");
            await Load(spaDialog, url: "https://claude.ai.example.com/new");
            Check(values.Last().State == "login_required", "The SPA dialog cannot bypass exact Claude HTTPS origin checks");
            await Load(Card("Current session", "20% used"), url: "https://claude.ai/new");
            Check(values.Last().State == "paused", "A bare quota-like card on /new cannot replace the usage dialog");
            await Load(spaDialog, automaticReload: true);
            Check(new Uri(core.Source).AbsolutePath == "/settings/usage" && values.Last().Snapshot!.Windows is [{ Remaining: 100 }, { Remaining: 87 }],
                "A hidden refresh from /new reopens the usage page instead of reloading chat");
            await Load(Card("Current session", "Used 29%") + Card("All models", "87% remaining"));
            Check(values.Last().Snapshot!.Windows is [{ Remaining: 71 }, { Remaining: 87 }], "Usage prefix and explicit remaining percentages keep their correct meaning");
            await Load(Card("Current session", "<progress max='1' value='0.29'></progress>"));
            Check(values.Last().Snapshot!.Windows.Single().Remaining == 71, "Native fractional progress is read as a quota percentage");
            await Load(Card("Current session", "-5% used")); Check(values.Last().State == "unreadable", "Negative quota percentages are rejected rather than being read as positive");
            await Load(Card("Current session", "20% used") + Card("Current session", "40% used"));
            Check(values.Last().State == "unreadable", "Conflicting visible session rows cannot become a guessed quota");
            var count = values.Count; await browser.ObserveAsync(); Check(values.Count == count, "Unchanged polling cannot renew a stale observation");
            await Load(Card("Current session", "0% used")); Check(values.Last().Snapshot!.Windows.Single().Remaining == 100, "A genuinely reported zero usage is preserved");
            await Load(Card("Current session", "")); Check(values.Last().State == "unreadable", "Missing percentage never becomes zero usage");
            await Load(Card("Current session", "20% used · 50% used")); Check(values.Last().State == "unreadable", "Ambiguous percentages are rejected");
            await Load(Card("Current session", "120% used")); Check(values.Last().State == "unreadable", "Out-of-range DOM values are rejected");
            await Load(Card("Current session", "<div role='progressbar' aria-valuemin='0' aria-valuemax='100' aria-valuenow='14'></div>"));
            Check(values.Last().Snapshot!.Windows.Single().Remaining == 86, "An accessible usage progressbar is read without tokens or network calls");
            await Load("<div style='display:none'>" + Card("Current session", "80% used") + "</div>" + Card("Current session", "30% used"));
            Check(values.Last().Snapshot!.Windows.Single().Remaining == 70, "Hidden duplicate cards do not override the visible quota");
            await Load(Card("Aktuell session", "25% använt") + Card("Alla modeller", "45% använt"));
            Check(values.Last().Snapshot!.Windows.Count == 2, "Swedish session and all-model labels are supported");
            await Load(Card("Current session", "40% used"), automaticReload: true);
            Check(values.Last().Snapshot!.Windows.Single().Remaining == 60, "The hidden page refreshes automatically");
            Check(!browser.window.IsVisible && !browser.controller.IsVisible, "All quota checks run without a visible browser");
        }
        browser.Dispose(); await runtime.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(runtime.HasExited, "The fixture's entire browser process exits cleanly");
        return string.Join(Environment.NewLine, checks);
        async Task Load(string html, bool automaticReload = false, string? url = null)
        {
            browser.fixtureHtml = html; var complete = new TaskCompletionSource<bool>();
            void Done(object? sender, CoreWebView2NavigationCompletedEventArgs args) => complete.TrySetResult(args.IsSuccess);
            core.NavigationCompleted += Done;
            try { if (automaticReload) await browser.RefreshAsync(false); else core.Navigate(url ?? ClaudePageUsage.Url); Check(await complete.Task.WaitAsync(TimeSpan.FromSeconds(20)), "Local Claude fixture navigation succeeds"); }
            finally { core.NavigationCompleted -= Done; }
            var until = DateTime.UtcNow.AddSeconds(20);
            while (browser.reading || browser.navigating) { if (DateTime.UtcNow > until) throw new TimeoutException("Claude fixture did not finish."); await Task.Delay(50); }
            await browser.ObserveAsync();
        }
        static string Card(string name, string value) => $"<section><h3>{name}</h3><p>{value}</p></section>";
        static string ModernRow(string name, string value, string reset) => $"<div class='quota-row'><div><div><div><div><div><div><h5>{name}</h5></div></div></div></div></div><p>{reset}</p></div><div class='meter'></div><p>{value}</p></div>";
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add("PASS " + name); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
}

public partial class MainWindow
{
    internal static async Task VerifyClaudeShutdownAsync(string directory, ClaudeUsageBrowser browser)
    {
        // Own hidden fixture window: exercise shutdown without polling or operating the user's desktop.
        var main = new MainWindow(new SettingsStore(directory), new Settings { QuickStarts = [], ClaudeUsageEnabled = true }, preview: false)
        { Left = -16000, Top = -16000, ShowInTaskbar = false, ShowActivated = false };
        main.claudeBrowser = browser;
        var finished = new TaskCompletionSource<bool>(); main.Closed += (_, _) => finished.TrySetResult(true);
        _ = new WindowInteropHelper(main).EnsureHandle(); main.Close();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
