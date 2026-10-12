using PortPilot.Core;

internal static class WebServiceTests
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, Action<bool, string> check, Func<Func<Task>, Task> throws)
    {
        await test("Browser activation selects the lowest verified Node web port and excludes debugger, UDP and other runtimes", () =>
        {
            var now = DateTime.UtcNow;
            var node = new PortEntry(3000, "0.0.0.0", 12345, "node", "node", now);
            var entries = new[] { node with { Port = 5173 }, node, node with { Port = 9229 }, node with { Port = 24678 } };
            var urls = new[] { new Uri("http://127.0.0.1:9229/"), new Uri("http://127.0.0.1:5173/"), new Uri("http://127.0.0.1:3000/"), new Uri("http://127.0.0.1:24678/") };
            var reading = new ProbeReading("HTTP 200", ServiceTone.Good, 1, "", now, urls);
            check(WebServiceOpener.SelectUrl(entries, reading, now)?.Port == 3000, "The group opened an auxiliary port or ignored the verified web port.");
            check(WebServiceOpener.SelectUrl([node with { Name = "postgres" }], reading, now) is null
                && WebServiceOpener.SelectUrl([node with { Protocol = "udp" }], reading, now) is null
                && !WebServiceOpener.IsCandidate(node with { ContainerId = "docker-owner" }), "Non-Node owners became browser targets.");
            check(WebServiceOpener.IsCandidate(node with { Name = "bun" }) && WebServiceOpener.IsCandidate(node with { Name = "deno" }), "Compatible JavaScript runtimes lost browser activation.");
            return Task.CompletedTask;
        });
        await test("Browser targets keep IPv6 and HTTPS bindings and reject stale or unverified destinations", () =>
        {
            var now = DateTime.UtcNow;
            var entry = new PortEntry(8443, "::", 12345, "node", "node", now);
            var url = new Uri("https://[::1]:8443/");
            var reading = new ProbeReading("HTTP 200", ServiceTone.Good, 1, "", now, [url]);
            check(WebServiceOpener.SelectUrl([entry], reading, now) == url, "A valid local IPv6 HTTPS endpoint was lost.");
            check(WebServiceOpener.SelectUrl([entry], reading with { SampledUtc = now.AddSeconds(-31) }, now) is null
                && WebServiceOpener.SelectUrl([entry], reading with { SampledUtc = now.AddSeconds(1) }, now) is null
                && WebServiceOpener.SelectUrl([entry], reading with { WebUrls = null }, now) is null, "A stale or TCP-only observation launched a browser.");
            foreach (var invalid in new[] { "https://[::1]:8444/", "https://203.0.113.5:8443/", "https://user:password@[::1]:8443/", "file:///C:/sample.html" })
                check(WebServiceOpener.SelectUrl([entry], reading with { WebUrls = [new Uri(invalid)] }, now) is null, "An unrelated browser destination was accepted.");
            return Task.CompletedTask;
        });
        await test("Default browser boundary accepts only local HTTP URLs without shell arguments", async () =>
        {
            var uri = new Uri("http://127.0.0.1:3000/"); var start = WebServiceOpener.BrowserStart(uri);
            check(start.FileName == uri.AbsoluteUri && start.UseShellExecute && start.ArgumentList.Count == 0 && start.Arguments.Length == 0, "URL was assembled as a shell command.");
            foreach (var uriText in new[] { "file:///C:/sample.html", "javascript:alert(1)", "https://203.0.113.5:3000/", "http://user:pass@127.0.0.1:3000/" })
                await throws(() => { WebServiceOpener.BrowserStart(new Uri(uriText)); return Task.CompletedTask; });
        });
    }
}
