using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;
using PortPilot.Core;

namespace PortPilot.App;

public sealed partial class BillingBrowser
{
    // Synthetic cookies in a caller-supplied test profile only. No account or external site is contacted.
    public static async Task<string> VerifySessionAsync(string directory, string phase, bool baseline)
    {
        var checks = new List<string>(); var messages = new List<BalanceMessage>();
        using var browser = new BillingBrowser(directory, message => { messages.Add(message); return true; }, _ => { }, fixture: true, rememberSession: !baseline);
        await browser.StartAsync();
        var core = browser.controller!.CoreWebView2; var manager = core.CookieManager;
        using var runtime = Process.GetProcessById((int)core.BrowserProcessId);
        Check(!core.Profile.IsInPrivateModeEnabled && core.Profile.ProfileName == "Default", "A stable non-private profile is used");
        var initial = await BillingSessionStore.GetOwnCookiesAsync(manager);
        if (phase == "seed")
        {
            Check(initial.All(cookie => !cookie.Name.StartsWith("portpilot_fixture_")), "The seed uses a new isolated profile");
            Add("portpilot_fixture_session", "synthetic-session-only", "platform.openai.com", "/", CoreWebView2CookieSameSiteKind.Strict);
            Add("portpilot_fixture_auth", "synthetic-auth-only", ".openai.com", "/", CoreWebView2CookieSameSiteKind.Lax);
            Add("portpilot_fixture_path", "synthetic-path-only", "platform.openai.com", "/settings", CoreWebView2CookieSameSiteKind.None);
            Add("portpilot_fixture_persistent", "synthetic-persistent-only", "platform.openai.com", "/", CoreWebView2CookieSameSiteKind.Lax, persistent: true);
            Add("portpilot_fixture_unrelated", "synthetic-unrelated-only", "example.com", "/", CoreWebView2CookieSameSiteKind.Lax);
            await Load(BalanceNativeHost.BillingUrl);
            await core.ExecuteScriptAsync("localStorage.setItem('portpilot_fixture_storage','durable');");
            await browser.FlushSessionAsync();
            Check(messages.LastOrDefault() is { State: "ok" }, "A successful Billing reading checkpoints the session");
            if (baseline) Check(!File.Exists(browser.sessionStore.FilePath), "Baseline only uses WebView2 profile persistence");
            else
            {
                var saved = browser.sessionStore.Read(DateTime.UtcNow);
                Check(saved.Length == 3 && saved.All(cookie => cookie.Name is "portpilot_fixture_session" or "portpilot_fixture_auth" or "portpilot_fixture_path"), $"Only OpenAI session cookies enter the checkpoint (fixture count: {saved.Length})");
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(browser.sessionStore.FilePath)).Contains("synthetic-"), "The checkpoint contains no plaintext fixture values");
            }
            Add("portpilot_fixture_session", "synthetic-final-only", "platform.openai.com", "/", CoreWebView2CookieSameSiteKind.Strict);
            await MainWindow.VerifyBillingShutdownAsync(Path.Combine(directory, "host"), browser);
            Check(browser.disposed, "The real main-window close handler releases its Billing controller");
            if (!baseline) Check(browser.sessionStore.Read(DateTime.UtcNow).Any(cookie => cookie.Name == "portpilot_fixture_session" && cookie.Value == "synthetic-final-only"), "The main-window close flushes the latest session before disposal");
        }
        else if (phase is "probe" or "probe-revoked")
        {
            Check(initial.Any(cookie => cookie.Name == "portpilot_fixture_persistent" && !cookie.IsSession && cookie.Value == "synthetic-persistent-only"), "WebView2 keeps persistent cookies after a complete process exit");
            if (baseline || phase == "probe-revoked")
                Check(initial.All(cookie => cookie.Name != "portpilot_fixture_session"), baseline ? "Baseline loses the session cookie despite the same profile" : "A cleared session is never resurrected on restart");
            else
            {
                Check(initial.Any(cookie => cookie.Name == "portpilot_fixture_session" && cookie.Value == "synthetic-final-only" && cookie.IsSession && cookie.IsSecure && cookie.IsHttpOnly && cookie.SameSite == CoreWebView2CookieSameSiteKind.Strict && cookie.Path == "/"), "Restart restores the session and preserves its original security attributes");
                Check(initial.Any(cookie => cookie.Name == "portpilot_fixture_auth" && cookie.Domain == ".openai.com"), "Restart restores the shared OpenAI authentication domain");
                Check(initial.Any(cookie => cookie.Name == "portpilot_fixture_path" && cookie.Path == "/settings" && cookie.SameSite == CoreWebView2CookieSameSiteKind.None), "Restart preserves path and SameSite scope");
                Check((await manager.GetCookiesAsync("https://example.com/")).All(cookie => cookie.Name != "portpilot_fixture_unrelated"), "Cookies from another site are never restored");
                // Existing browser state takes precedence over an older checkpoint.
                Add("portpilot_fixture_session", "synthetic-newer-only", "platform.openai.com", "/", CoreWebView2CookieSameSiteKind.Strict);
                await browser.sessionStore.RestoreAsync(manager);
                Check((await manager.GetCookiesAsync("https://platform.openai.com/")).Any(cookie => cookie.Name == "portpilot_fixture_session" && cookie.Value == "synthetic-newer-only"), "An existing newer cookie is not overwritten by the checkpoint");
                var validation = new BillingSessionStore(Path.Combine(directory, "validation"));
                var own = await BillingSessionStore.GetOwnCookiesAsync(manager); var now = DateTime.UtcNow;
                validation.Save(own, now);
                Check(validation.Read(now + BillingSessionStore.MaximumAge + TimeSpan.FromSeconds(1)).Length == 0 && !File.Exists(validation.FilePath), "Expired local checkpoints are discarded");
                validation.Save(own, now.AddMinutes(1));
                Check(Rejects(() => validation.Read(now)), "Future-dated checkpoints are rejected");
                validation.Save(own, now);
                var encrypted = File.ReadAllBytes(validation.FilePath); encrypted[^1] ^= 0x80; File.WriteAllBytes(validation.FilePath, encrypted);
                Check(Rejects(() => validation.Read(DateTime.UtcNow)) && File.ReadAllBytes(validation.FilePath).SequenceEqual(encrypted), "Damaged encrypted checkpoints fail safely and remain intact");
            }
            await Load(BalanceNativeHost.BillingUrl);
            Check(await core.ExecuteScriptAsync("localStorage.getItem('portpilot_fixture_storage') === 'durable'") == "true", "Local browser storage survives the same app restart");
            Check(await core.ExecuteScriptAsync("!document.cookie.includes('portpilot_fixture_session')") == "true", "HttpOnly session data stays unavailable to page scripts");
        }
        else if (phase == "revoke")
        {
            Check(initial.Any(cookie => cookie.Name == "portpilot_fixture_session"), "Revoke starts with the previously remembered session");
            foreach (var cookie in initial.Where(cookie => cookie.IsSession)) manager.DeleteCookie(cookie);
            await Load("https://auth.openai.com/log-in");
            await browser.FlushSessionAsync();
            Check(messages.LastOrDefault() is { State: "login_required" } && !File.Exists(browser.sessionStore.FilePath), "Returning to login clears the checkpoint and shutdown cannot restore it");
        }
        else throw new ArgumentException("Unknown session test phase.");
        browser.Dispose();
        await runtime.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(runtime.HasExited, "The entire fixture browser process exits before the next app launch");
        return string.Join(Environment.NewLine, checks);

        void Add(string name, string value, string domain, string path, CoreWebView2CookieSameSiteKind sameSite, bool persistent = false)
        {
            var cookie = manager.CreateCookie(name, value, domain, path); cookie.IsSecure = cookie.IsHttpOnly = true; cookie.SameSite = sameSite;
            if (persistent) cookie.Expires = DateTime.UtcNow.AddDays(2);
            manager.AddOrUpdateCookie(cookie);
        }
        async Task Load(string url)
        {
            browser.fixtureHtml = "<div><span><span>API credit balance</span></span><div>$24.50</div></div>";
            var complete = new TaskCompletionSource<bool>();
            void Done(object? sender, CoreWebView2NavigationCompletedEventArgs args) => complete.TrySetResult(args.IsSuccess);
            core.NavigationCompleted += Done;
            try { core.Navigate(url); Check(await complete.Task.WaitAsync(TimeSpan.FromSeconds(20)), "Local fixture navigation succeeds"); }
            finally { core.NavigationCompleted -= Done; }
            var until = DateTime.UtcNow.AddSeconds(15);
            while (browser.reading || browser.navigating) { if (DateTime.UtcNow > until) throw new TimeoutException("Fixture reader did not finish."); await Task.Delay(50); }
            await browser.ObserveAsync();
        }
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add("PASS " + name); }
        static bool Rejects(Action action) { try { action(); return false; } catch (Exception ex) when (ex is InvalidDataException or CryptographicException) { return true; } }
    }
}

public partial class MainWindow
{
    internal static async Task VerifyBillingShutdownAsync(string directory, BillingBrowser browser)
    {
        // A hidden native handle exercises the normal close path without Loaded, port polling or Docker calls.
        var main = new MainWindow(new SettingsStore(directory), new Settings { QuickStarts = [] }, preview: false)
        { Left = -16000, Top = -16000, ShowInTaskbar = false, ShowActivated = false };
        main.billingBrowser = browser;
        var closed = new TaskCompletionSource<bool>(); main.Closed += (_, _) => closed.TrySetResult(true);
        _ = new System.Windows.Interop.WindowInteropHelper(main).EnsureHandle();
        main.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
