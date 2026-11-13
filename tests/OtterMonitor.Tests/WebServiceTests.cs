using OtterMonitor.Core;

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
            check(WebServiceOpener.SelectUrl([entry], reading, now) == new Uri("https://localhost:8443/"), "The default browser address did not replace IPv6 loopback with localhost.");
            check(WebServiceOpener.SelectUrl([entry], reading, now, BrowserAddressMode.DetectedIp) == url, "The detected-IP option lost the original IPv6 HTTPS endpoint.");
            check(WebServiceOpener.SelectUrl([entry], reading with { SampledUtc = now.AddSeconds(-31) }, now) is null
                && WebServiceOpener.SelectUrl([entry], reading with { SampledUtc = now.AddSeconds(1) }, now) is null
                && WebServiceOpener.SelectUrl([entry], reading with { WebUrls = null }, now) is null, "A stale or TCP-only observation launched a browser.");
            foreach (var invalid in new[] { "https://[::1]:8444/", "https://203.0.113.5:8443/", "https://user:password@[::1]:8443/", "file:///C:/sample.html" })
                check(WebServiceOpener.SelectUrl([entry], reading with { WebUrls = [new Uri(invalid)] }, now) is null, "An unrelated browser destination was accepted.");
            return Task.CompletedTask;
        });
        await test("Browser address choices preserve verified ports, paths and specific interface bindings", () =>
        {
            var now = DateTime.UtcNow;
            foreach (var address in new[] { "127.0.0.1", "::1", "0.0.0.0", "::" })
            {
                var entry = new PortEntry(3000, address, 12345, "node", "node", now);
                var verified = new UriBuilder("http", ServiceProbe.LocalAddress(address)!.ToString(), 3000, "/app") { Query = "view=dev", Fragment = "start" }.Uri;
                var reading = new ProbeReading("HTTP 200", ServiceTone.Good, 1, "", now, [verified]);
                check(WebServiceOpener.SelectUrl([entry], reading, now)?.AbsoluteUri == "http://localhost:3000/app?view=dev#start", "Localhost conversion changed the scheme, port, path or query.");
                var group = PortGroup.Create([entry]).Single(); group.SetProbe(reading);
                check(group.Address == "http://localhost:3000/app?view=dev#start", "Copy address did not use the default localhost choice.");
                group.BrowserAddress = BrowserAddressMode.DetectedIp;
                check(group.BrowserUrl == verified && group.Address == verified.ToString(), "Changing the address choice did not immediately update browser/copy targets.");
            }
            var interfaceAddress = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses).Select(info => info.Address)
                .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip));
            if (interfaceAddress is not null)
            {
                var entry = new PortEntry(3000, interfaceAddress.ToString(), 12345, "node", "node", now);
                var verified = new UriBuilder("http", interfaceAddress.ToString(), 3000).Uri;
                check(WebServiceOpener.SelectUrl([entry], new("HTTP 200", ServiceTone.Good, 1, "", now, [verified]), now) == verified,
                    "A specific interface binding was replaced with an unreachable localhost address.");
            }
            return Task.CompletedTask;
        });
        await test("Default browser boundary accepts only local HTTP URLs without shell arguments", async () =>
        {
            var uri = new Uri("http://127.0.0.1:3000/"); var start = WebServiceOpener.BrowserStart(uri);
            check(start.FileName == uri.AbsoluteUri && start.UseShellExecute && start.ArgumentList.Count == 0 && start.Arguments.Length == 0, "URL was assembled as a shell command.");
            check(WebServiceOpener.BrowserStart(new Uri("https://localhost:8443/")).FileName == "https://localhost:8443/", "The browser boundary rejected localhost.");
            foreach (var uriText in new[] { "file:///C:/sample.html", "javascript:alert(1)", "https://203.0.113.5:3000/", "http://user:pass@127.0.0.1:3000/", "http://localhost.example.com:3000/", "http://user:pass@localhost:3000/", "http://0.0.0.0:3000/", "http://[::]:3000/" })
                await throws(() => { WebServiceOpener.BrowserStart(new Uri(uriText)); return Task.CompletedTask; });
        });
        await test("IPv4 and IPv6 choices select only verified bindings and fall back without inventing an address", () =>
        {
            var now = DateTime.UtcNow;
            var v4 = new PortEntry(3000, "0.0.0.0", 12345, "node", "node", now);
            var v6 = v4 with { Address = "::" };
            var ipv4 = new Uri("http://127.0.0.1:3000/app?q=1"); var ipv6 = new Uri("http://[::1]:3000/app?q=1");
            var reading = new ProbeReading("HTTP 200", ServiceTone.Good, 1, "", now, [ipv4, ipv6]);
            check(WebServiceOpener.SelectUrl([v4, v6], reading, now, BrowserAddressMode.Ipv4) == ipv4
                && WebServiceOpener.SelectUrl([v4, v6], reading, now, BrowserAddressMode.Ipv6) == ipv6,
                "An explicit address family ignored the verified matching endpoint.");
            check(WebServiceOpener.SelectUrl([v6], reading, now, BrowserAddressMode.Ipv4) == ipv6
                && WebServiceOpener.SelectUrl([v4], reading, now, BrowserAddressMode.Ipv6) == ipv4,
                "An unsupported family invented a URL instead of falling back to a verified binding.");
            check(WebServiceOpener.SelectUrl([v4, v6], reading with { WebUrls = [ipv4] }, now, BrowserAddressMode.Ipv6) == ipv4,
                "An unverified IPv6 endpoint was offered just because a listener exists.");
            check(WebServiceOpener.CanUseAddress([v4], BrowserAddressMode.Ipv4) && !WebServiceOpener.CanUseAddress([v4], BrowserAddressMode.Ipv6)
                && WebServiceOpener.CanUseAddress([v6], BrowserAddressMode.Ipv6) && !WebServiceOpener.CanUseAddress([v6], BrowserAddressMode.Ipv4),
                "Address availability did not respect the listener's address family.");
            check(!WebServiceOpener.CanUseAddress([v4 with { Address = "127.0.0.1" }], BrowserAddressMode.NetworkIp)
                && !WebServiceOpener.CanUseAddress([v6 with { Address = "::1" }], BrowserAddressMode.NetworkIp)
                && !WebServiceOpener.CanUseAddress([v4 with { Name = "postgres" }], BrowserAddressMode.Ipv4),
                "Loopback-only or non-web services enabled an incompatible address choice.");
            check(WebServiceOpener.SelectUrl([v4], reading with { WebUrls = [new Uri("http://localhost:3000/")] }, now) is null,
                "An unverified hostname was accepted as probe evidence.");
            return Task.CompletedTask;
        });
        await test("Network IP requires a matching local listener and its own verified HTTP URL", () =>
        {
            var now = DateTime.UtcNow;
            foreach (var address in WebServiceOpener.NetworkAddresses())
            {
                var wildcard = new PortEntry(3000, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "0.0.0.0" : "::", 12345, "node", "node", now);
                var url = new UriBuilder("http", address.ToString(), 3000).Uri;
                var loopback = new UriBuilder("http", ServiceProbe.LocalAddress(wildcard.Address)!.ToString(), 3000).Uri;
                var reading = new ProbeReading("HTTP 200", ServiceTone.Good, 1, "", now, [loopback, url]);
                check(WebServiceOpener.SelectUrl([wildcard], reading, now, BrowserAddressMode.NetworkIp) == url,
                    "A verified network address was lost for its wildcard listener.");
                check(WebServiceOpener.SelectUrl([wildcard with { Address = loopback.Host.Trim('[', ']') }], reading, now, BrowserAddressMode.NetworkIp) == loopback,
                    "A loopback-only listener was opened through a network IP.");
                check(WebServiceOpener.SelectUrl([wildcard], reading with { WebUrls = [loopback] }, now, BrowserAddressMode.NetworkIp) == loopback,
                    "An unverified network address was invented from a wildcard listener.");
                check(WebServiceOpener.CanUseAddress([wildcard], BrowserAddressMode.NetworkIp), "A compatible network choice was disabled.");
            }
            return Task.CompletedTask;
        });
    }
}
