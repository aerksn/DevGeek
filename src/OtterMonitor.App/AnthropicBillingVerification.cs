using System.IO;
using Microsoft.Web.WebView2.Core;

namespace OtterMonitor.App;

public sealed partial class BillingBrowser
{
    // Explicit diagnostic: read the existing app profile without changing opt-ins, checkpoints or balance files.
    internal static async Task<(BalanceMessage Message, string? Diagnostic)> ReadAnthropicBalanceAsync(string directory)
    {
        if (!Directory.Exists(Path.Combine(directory, "anthropic-profile"))) throw new DirectoryNotFoundException();
        BalanceMessage last = new(1, "unreadable");
        using var browser = new BillingBrowser(directory, message => { last = message; return true; }, _ => { }, fixture: false, rememberSession: false, anthropic: true);
        await browser.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            await browser.ObserveAsync();
            if (last.State == "ok") return (last, null);
            await Task.Delay(300);
        }
        // Only route, layout metadata and balance-related links; no cookies, account details or page HTML.
        var diagnostic = await browser.controller!.CoreWebView2.ExecuteScriptAsync("""
            (() => {
              const clean = value => String(value ?? '').replace(/\s+/g,' ').trim();
              const path = value => { try { const url=new URL(value,location.href);return url.origin+url.pathname; } catch { return ''; } };
              const parents = element => { const result=[];for(let node=element.parentElement;node && result.length<6;node=node.parentElement)result.push({tag:node.tagName,role:node.getAttribute('role')});return result; };
              const links=[...document.querySelectorAll('a[href],[role=link]')].filter(element => /^\$[\d,.]+$/.test(clean(element.textContent)) || /\/settings\/billing\/?$/.test(element.getAttribute('href') ?? '')).slice(0,8).map(element=>({tag:element.tagName,role:element.getAttribute('role'),text:clean(element.innerText).slice(0,80),content:clean(element.textContent).slice(0,80),url:path(element.getAttribute('href')),rects:element.getClientRects().length,parents:parents(element)}));
              return {url:path(location.href),visibility:document.visibilityState,width:innerWidth,height:innerHeight,headers:document.querySelectorAll('header,[role=banner]').length,links,creditHeadings:[...document.querySelectorAll('h1,h2,h3,h4')].filter(element=>/^credit balance$/i.test(clean(element.textContent))).map(element=>({tag:element.tagName,rects:element.getClientRects().length,parents:parents(element)})),loginButton:!![...document.querySelectorAll('button')].find(element=>/^continue with (google|email)$/i.test(clean(element.textContent)))};
            })()
            """);
        return (last, diagnostic);
    }
    internal static async Task<string> VerifyAnthropicAsync(string directory)
    {
        var checks = new List<string>(); var messages = new List<BalanceMessage>();
        var cache = new BalanceStore(directory, anthropic: true);
        using var browser = new BillingBrowser(directory, message => { cache.Apply(message, DateTime.UtcNow); messages.Add(message); return true; }, _ => { }, fixture: true, anthropic: true);
        await browser.StartAsync();
        var core = browser.controller!.CoreWebView2;
        Check(browser.controller.Bounds.Width >= 640 && browser.controller.Bounds.Height >= 400, "A never-shown Billing window retains a desktop-sized viewport for responsive account pages");
        Check(!browser.window.IsVisible && browser.controller.IsVisible && browser.OverviewUrl == AnthropicBillingNavigation.OverviewUrl, "Anthropic API keeps its renderer active inside an isolated hidden host for visibility-gated account data");
        Check(await core.ExecuteScriptAsync("document.visibilityState === 'visible'") == "true", "The hidden native host permits Console's visibility-gated data requests without opening a window");
        string Card(string amount) => "<section><div><h3>Credit balance</h3><span>" + amount + "</span></div></section>";
        await Load(Card("$18.75") + "<p>When balance reaches $0, requests stop.</p><button>Buy credits $100</button>");
        Check(messages.Last() is { State: "ok", Amount: "18.75" }, "Real WebView2 reads the credit card without payment controls or warnings");
        var observed = cache.Read()!.ObservedUtc; await browser.ObserveAsync();
        Check(cache.Read()!.ObservedUtc == observed, "Unchanged polling preserves the observation time");
        await Load(Card("$0.00"), refresh: true);
        Check(cache.Read() is { Amount: 0m, State: "ok" }, "Hidden background reload reads a real zero balance");
        foreach (var html in new[] { Card("Loading…") + "<p>$99</p>", Card("$5") + Card("$6"), "<div style='display:none'>" + Card("$5") + "</div>", Card("EUR $5"), Card("$5 $6") })
        {
            await Load(html);
            Check(cache.Read() is { Amount: 0m, State: "unreadable" }, "Missing, hidden, ambiguous and foreign-currency fields preserve the last balance as older");
        }
        await Load(Card("$7.50"));
        await Load("<style>@media(max-width:400px){header{display:none}}</style><header><a href='/settings/billing'>$41.27</a></header><h1>Billing</h1><section><h2>Credit balance</h2><p>Purchased credits are used by the API. Promotional credits may apply to specific models and products.</p><div>$1.25</div><p>$40.00 promotional credit available</p><button>Set up auto-reload</button><button>Buy credits</button></section><h2>Promotional credits</h2><table><tr><td>Remaining balance</td><td>$40.00 of $100.00</td></tr></table><h2>Invoice history</h2><table><tr><td>$999.00</td></tr></table>");
        Check(cache.Read() is { Amount: 41.27m, State: "ok" }, "The observed Console layout reads the provider's header total rather than mixing purchased, promotional and invoice amounts");
        await Load("<header><a href='/settings/billing'>$0.00</a><a hidden href='/settings/billing'>$15</a></header>" + Card("Loading…"));
        Check(cache.Read() is { Amount: 0m, State: "ok" }, "Zero total remains valid and hidden responsive header duplicates are excluded");
        await Load("<header><a href='/settings/billing'>$5</a><a href='/settings/billing'>$6</a></header>" + Card("$1"));
        Check(cache.Read() is { Amount: 0m, State: "unreadable" }, "Conflicting organization/header totals retain the last value as older instead of falling back to a different amount");
        await Load(Card("$7.50"));
        var cookie = core.CookieManager.CreateCookie("anthropic_fixture", "synthetic-only", ".claude.com", "/");
        cookie.IsSecure = cookie.IsHttpOnly = true; core.CookieManager.AddOrUpdateCookie(cookie);
        await browser.FlushSessionAsync();
        Check(browser.sessionStore.Read(DateTime.UtcNow).Any(item => item.Name == "anthropic_fixture"), "Anthropic sign-in is checkpointed in its own encrypted store");
        Check(new BillingSessionStore(directory, claude: true).Read(DateTime.UtcNow).Length == 0
            && new BillingSessionStore(directory).Read(DateTime.UtcNow).Length == 0
            && !browser.sessionStore.IsSessionDomain("claude.ai") && !browser.sessionStore.IsSessionDomain("platform.claude.com.evil.test"), "API sign-in stays separate from Claude Code, OpenAI and untrusted domains");
        browser.DisconnectAccount();
        Check(!File.Exists(browser.sessionStore.FilePath) && !(await browser.sessionStore.GetCookiesAsync(core.CookieManager)).Any(item => item.Name == "anthropic_fixture"), "Disconnect deletes the checkpoint and the profile's sign-in cookies");
        // Emulate the Google GIS return without contacting Google or entering credentials.
        var parentHtml = Card("$7.50") + "<script>window.addEventListener('message', e => { if(e.origin === 'https://accounts.google.com' && e.data === 'otter-synthetic-sign-in') { document.body.innerHTML = '<div><h3>Credit balance</h3><span>$12.34</span></div>'; document.body.dataset.oauth = 'complete'; } });</script>";
        browser.fixtureResponse = url => new Uri(url).Host == "accounts.google.com"
            ? "<script>if(window.opener){window.opener.postMessage('otter-synthetic-sign-in','https://platform.claude.com');window.close();}</script>"
            : parentHtml;
        foreach (var blankFirst in new[] { false, true })
        {
            await Load(parentHtml);
            var script = blankFirst ? "(() => { const popup=window.open('about:blank','otter_auth'); if(popup) popup.location='https://accounts.google.com/gsi/transform'; return !!popup; })()"
                : "!!window.open('https://accounts.google.com/gsi/transform','otter_auth')";
            Check(await core.ExecuteScriptAsync(script) == "true", "Sign-in receives a real child WindowProxy" + (blankFirst ? " through about:blank" : ""));
            var until = DateTime.UtcNow.AddSeconds(20);
            while (await core.ExecuteScriptAsync("document.body.dataset.oauth === 'complete'") != "true" || browser.authPopups.Count != 0)
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("Synthetic OAuth popup did not return to Billing.");
                await Task.Delay(50);
            }
            await browser.ObserveAsync();
            Check(AnthropicBillingNavigation.IsOverview(core.Source) && cache.Read() is { Amount: 12.34m, State: "ok" },
                "Google-style postMessage reaches the preserved Billing opener, closes the popup and updates the balance");
        }
        Check(!BillingAuthPopup.Allowed("http://accounts.google.com/gsi/transform") && !BillingAuthPopup.Allowed("javascript:alert(1)")
            && !BillingAuthPopup.Allowed("file:///C:/test.html"), "Popups cannot navigate to insecure, script or file URLs");
        return string.Join(Environment.NewLine, checks);

        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add("PASS " + name); }
        async Task Load(string html, bool refresh = false)
        {
            var completed = new TaskCompletionSource<bool>();
            void Done(object? sender, CoreWebView2NavigationCompletedEventArgs args) => completed.TrySetResult(args.IsSuccess);
            core.NavigationCompleted += Done;
            try
            {
                browser.fixtureHtml = html;
                if (refresh) await browser.RefreshAsync(false); else core.Navigate(AnthropicBillingNavigation.OverviewUrl);
                Check(await completed.Task.WaitAsync(TimeSpan.FromSeconds(20)), "Synthetic Billing navigation succeeds");
            }
            finally { core.NavigationCompleted -= Done; }
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (browser.reading || browser.navigating) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Anthropic reader timed out."); await Task.Delay(40); }
            await browser.ObserveAsync();
        }
    }
}
