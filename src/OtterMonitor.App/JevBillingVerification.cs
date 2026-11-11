using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;
using OtterMonitor.Core;

namespace OtterMonitor.App;

public sealed partial class BillingBrowser
{
    // Real WebView2 DOM/session tests, using local synthetic pages and cookies only.
    internal static async Task<string> VerifyJevAsync(string directory)
    {
        var checks = new List<string>(); var messages = new List<BalanceMessage>(); var errors = new List<string>();
        var cache = new BalanceStore(directory, jev: true);
        using var browser = new BillingBrowser(directory, message => { cache.Apply(message, DateTime.UtcNow); messages.Add(message); return true; }, message => { if (message is not null) errors.Add(message); }, fixture: true, jev: true);
        await browser.StartAsync();
        var core = browser.controller!.CoreWebView2;
        Check(!browser.window.IsVisible && browser.controller.IsVisible && browser.OverviewUrl == JevBillingNavigation.OverviewUrl, "JEV keeps its renderer active in its own hidden Billing session");
        string Card(string amount) => "<section><div><div><h3>Available credits</h3></div></div><div><span>" + amount + "</span></div><p><button>Balance includes expiring credits</button></p><div><button>Add funds $100</button><button>Redeem code</button><button><span>Auto-recharge $50</span></button></div></section>";
        Check(await core.ExecuteScriptAsync("document.visibilityState === 'visible'") == "true", "Hidden JEV host permits visibility-gated account loading");
        await Load("<div id='balance'></div><script>if(document.visibilityState === 'visible') document.getElementById('balance').innerHTML = " + System.Text.Json.JsonSerializer.Serialize(Card("$6.25")) + ";</script>");
        Check(messages.LastOrDefault() is { State: "ok", Amount: "6.25" }, "Visibility-gated JEV balance loads before the Billing window is ever shown");
        await Load("<div role='dialog' aria-label='Settings'><h1>Billing</h1>" + Card("$5.00") + "<section aria-label='Billing history'><table><tr><td>$99.00</td><td>$12.00</td></tr></table></section></div>");
        Check(messages.LastOrDefault() is { State: "ok", Amount: "5.00" }, "Available credits wins over billing history and payment controls inside the real settings dialog");
        var received = messages.Count; var observed = cache.Read()!.ObservedUtc;
        await browser.ObserveAsync();
        Check(messages.Count == received && cache.Read()!.ObservedUtc == observed, "Polling an unchanged DOM never renews the observed time");
        await core.ExecuteScriptAsync("document.body.insertAdjacentHTML('beforeend', '<div role=\"dialog\" aria-label=\"Add funds\"><input id=\"payment-amount\"></div>')");
        Check(!await SafeRefresh(), "An additional payment dialog prevents automatic refresh");
        await core.ExecuteScriptAsync("document.querySelector('[aria-label=\"Add funds\"]').remove(); document.body.insertAdjacentHTML('beforeend', '<input id=\"editing\">'); document.getElementById('editing').focus()");
        Check(!await SafeRefresh(), "Focused input prevents automatic refresh");
        await core.ExecuteScriptAsync("document.getElementById('editing').remove()");
        Check(await SafeRefresh(), "The Billing Settings dialog alone permits background refresh");
        await Load(Card("$0.00"), refresh: true);
        Check(messages.LastOrDefault() is { State: "ok", Amount: "0.00" }, "Hidden refresh reports a fresh zero balance");
        foreach (var html in new[] { Card("Loading…") + "<p>$999</p>", Card("$7") + Card("$8"), "<div style='display:none'>" + Card("$5") + "</div>", Card("EUR $5"), Card("$5 $6"), "<section><h3>Available credits</h3><p><span>$50</span></p></section>" })
        {
            await Load(html);
            Check(messages.LastOrDefault() is { State: "unreadable" } && cache.Read()!.Amount == 0m && !cache.Read()!.IsFresh(DateTime.UtcNow), "Unreadable/hidden/ambiguous fields preserve the last balance as older");
        }
        await Load(Card("$4.25"));
        var cookies = core.CookieManager;
        AddCookie("jev_fixture_session", "console.typesafe.ai"); AddCookie("jev_fixture_shared", ".typesafe.ai"); AddCookie("unrelated_fixture", ".openai.com");
        await browser.FlushSessionAsync();
        var saved = browser.sessionStore.Read(DateTime.UtcNow);
        Check(saved.Any(cookie => cookie.Name == "jev_fixture_session") && saved.Any(cookie => cookie.Name == "jev_fixture_shared") && saved.All(cookie => cookie.Name != "unrelated_fixture"), "JEV checkpoints contain only TypeSafe session cookies");
        Check(browser.sessionStore.FilePath.EndsWith("jev-session.bin") && new BillingSessionStore(directory).Read(DateTime.UtcNow).Length == 0, "JEV and OpenAI checkpoints are separate");
        Check(!browser.sessionStore.IsSessionDomain("evil.typesafe.ai") && !browser.sessionStore.IsSessionDomain("typesafe.ai.evil.test"), "Unrelated cookie domains cannot enter the JEV checkpoint");
        using var runtime = Process.GetProcessById((int)core.BrowserProcessId);
        browser.Dispose();
        await runtime.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        using var restarted = new BillingBrowser(directory, message => { messages.Add(message); return true; }, _ => { }, fixture: true, jev: true);
        await restarted.StartAsync();
        var restored = await restarted.sessionStore.GetCookiesAsync(restarted.controller!.CoreWebView2.CookieManager);
        Check(restored.Any(cookie => cookie.Name == "jev_fixture_session") && restored.Any(cookie => cookie.Name == "jev_fixture_shared"), "JEV login survives a complete WebView2 process restart through its encrypted checkpoint");
        restarted.controller.CoreWebView2.Navigate("https://console.typesafe.ai/login");
        var until = DateTime.UtcNow.AddSeconds(20);
        while (File.Exists(restarted.sessionStore.FilePath)) { if (DateTime.UtcNow > until) throw new TimeoutException("Login did not clear the session."); await Task.Delay(50); }
        await restarted.FlushSessionAsync();
        Check(!File.Exists(restarted.sessionStore.FilePath) && messages.LastOrDefault() is { State: "login_required" }, "Expired login revokes the checkpoint and shutdown cannot recreate it");
        Check(errors.Count == 0, "No WebView2 errors during the JEV integration checks");
        return string.Join(Environment.NewLine, checks);

        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add("PASS " + name); }
        async Task<bool> SafeRefresh() => await core.ExecuteScriptAsync("(() => { const module = { exports: {} }; " + browser.reader + "; return module.exports.canRefresh(document); })()") == "true";
        void AddCookie(string name, string domain)
        {
            var cookie = cookies.CreateCookie(name, "synthetic-only", domain, "/"); cookie.IsSecure = cookie.IsHttpOnly = true;
            cookies.AddOrUpdateCookie(cookie);
        }
        async Task Load(string html, bool refresh = false)
        {
            var completed = new TaskCompletionSource<bool>();
            void Done(object? sender, CoreWebView2NavigationCompletedEventArgs args) => completed.TrySetResult(args.IsSuccess);
            core.NavigationCompleted += Done;
            try
            {
                browser.fixtureHtml = html;
                if (refresh) await browser.RefreshAsync(manual: false); else core.Navigate(JevBillingNavigation.OverviewUrl);
                Check(await completed.Task.WaitAsync(TimeSpan.FromSeconds(20)), "Synthetic Billing navigation succeeds");
            }
            finally { core.NavigationCompleted -= Done; }
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (browser.reading || browser.navigating) { if (DateTime.UtcNow > deadline) throw new TimeoutException("JEV reader did not finish."); await Task.Delay(50); }
            await browser.ObserveAsync();
        }
    }
}
