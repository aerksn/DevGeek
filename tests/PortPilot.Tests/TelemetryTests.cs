using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PortPilot.Core;

internal static class TelemetryTests
{
    internal static async Task Run(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("Resource samples use actual memory/start time and reject a replaced PID lifetime", async () =>
        {
            using var process = Process.GetCurrentProcess(); var started = process.StartTime.ToUniversalTime();
            var entry = new PortEntry(12345, "127.0.0.1", process.Id, process.ProcessName, "TCP", started);
            var monitor = new ResourceMonitor(); var first = monitor.SampleProcesses([entry])[PortGroup.OwnerKey(entry)];
            check(first.MemoryBytes > 0 && first.StartedUtc == started && first.CpuPercent is null, "Initial sample fabricated CPU or omitted real resource data.");
            await Task.Delay(90);
            var second = monitor.SampleProcesses([entry])[PortGroup.OwnerKey(entry)];
            check(second.CpuPercent is >= 0 and <= 100, "The second CPU sample is not an interval percentage.");
            var wrong = entry with { StartedUtc = started.AddSeconds(1) };
            var replacement = monitor.SampleProcesses([wrong])[PortGroup.OwnerKey(wrong)];
            check(replacement.MemoryBytes is null && replacement.CpuPercent is null && replacement.StartedUtc is null, "A reused PID borrowed the previous owner's resources.");
            check(ResourceMonitor.CalculateCpu(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), 4) == 50, "CPU was not normalized to host logical capacity.");
            check(ResourceMonitor.CalculateCpu(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 4) is null, "A reset counter became a CPU value.");
        });
        await test("Docker resources parse Linux/Windows units, unavailable values and CPU above one core", () =>
        {
            var values = DockerService.ParseStats("""
                {"ID":"container-one","CPUPerc":"124.25%","MemUsage":"2.5MiB / 1GiB"}
                {"ID":"container-two","CPUPerc":"--","MemUsage":"38.21 MB"}
                """);
            check(values["container-one"].CpuPercent == 124.25 && values["container-one"].MemoryBytes == 2621440, "Linux resource units were misread.");
            check(values["container-two"].CpuPercent is null && values["container-two"].MemoryBytes == 38210000, "Unavailable CPU or Windows memory was misread.");
            check(DockerService.ParseMemory("1.5 GiB") == 1610612736 && DockerService.ParseMemory("10kB") == 10000, "Binary and decimal units were confused.");
            check(new[] { "-2MiB", "NaN", "1PB", "10", "1,5MiB" }.All(value => DockerService.ParseMemory(value) is null), "Invalid resources became a real number.");
            var entries = DockerService.ParseInspect("""
                [{"Id":"container-one","Name":"/web","State":{"Running":true,"StartedAt":"2026-10-06T10:20:30.123456789Z","Health":{"Status":"unhealthy"}},"NetworkSettings":{"Ports":{"8080/tcp":[{"HostIp":"0.0.0.0","HostPort":"8080"}]}}}]
                """, "npipe://./pipe/docker_engine");
            check(entries[0].StartedUtc?.Kind == DateTimeKind.Utc && entries[0].ContainerHealth == "unhealthy", "Docker uptime or health was discarded.");
            return Task.CompletedTask;
        });
        await test("Uptime spans days, preserves unavailable values and never shows future start times", () =>
        {
            var now = DateTime.UtcNow;
            check(new ResourceReading(null, null, now.AddDays(-2).AddHours(-3), now).Uptime(now) == "2d 3h", "Day uptime is incorrect.");
            check(new ResourceReading(0, 0, now, now).MemoryText == "0 MB" && new ResourceReading(0, 0, now, now).CpuText != "—", "A real zero was hidden.");
            check(new ResourceReading(null, null, now.AddSeconds(1), now).Uptime(now) == "—", "A future timestamp became a negative runtime.");
            return Task.CompletedTask;
        });
        await test("Docker stats command pins the scanned local pipe, deduplicates owners and skips remote contexts", async () =>
        {
            var directory = Path.Combine(root, "stats-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            foreach (var path in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(path, Path.Combine(directory, Path.GetFileName(path)));
            File.Copy(Environment.ProcessPath!, Path.Combine(directory, "docker.exe"));
            var record = Path.Combine(directory, "arguments.json");
            var variables = new[] { "PATH", "DOCKER_CONTEXT", "PORTPILOT_DOCKER_FIXTURE", "PORTPILOT_DOCKER_FAILURE", "PORTPILOT_DOCKER_OUTPUT" };
            var saved = variables.ToDictionary(variable => variable, Environment.GetEnvironmentVariable);
            var previousDirectory = Environment.CurrentDirectory;
            try
            {
                // The current directory wins over an installed System32 Docker CLI.
                Environment.CurrentDirectory = directory;
                Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + saved["PATH"]);
                Environment.SetEnvironmentVariable("DOCKER_CONTEXT", "must-not-use");
                Environment.SetEnvironmentVariable("PORTPILOT_DOCKER_FIXTURE", record);
                Environment.SetEnvironmentVariable("PORTPILOT_DOCKER_FAILURE", null);
                var id = new string('a', 64);
                Environment.SetEnvironmentVariable("PORTPILOT_DOCKER_OUTPUT", JsonSerializer.Serialize(new { ID = id, CPUPerc = "3.5%", MemUsage = "20MiB / 1GiB" }));
                var entry = new PortEntry(8080, "0.0.0.0", 0, "own-fixture", "Docker", DateTime.UtcNow.AddMinutes(-5), ContainerId: id, DockerEndpoint: "npipe://./pipe/portpilot_fixture_only");
                var values = await DockerService.ReadStatsAsync([entry, entry with { Address = "::" }, entry with { ContainerId = "remote", DockerEndpoint = "tcp://prod:2375" }]);
                using var document = JsonDocument.Parse(File.ReadAllText(record));
                check(document.RootElement.GetProperty("Args").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "--host", entry.DockerEndpoint, "stats", "--no-stream", "--no-trunc", "--format", "{{json .}}", id })
                    && document.RootElement.GetProperty("Context").ValueKind == JsonValueKind.Null, "Stats used the ambient Docker context or wrong container IDs.");
                check(values.Count == 1 && values[PortGroup.OwnerKey(entry)].MemoryBytes == 20971520 && values[PortGroup.OwnerKey(entry)].StartedUtc == entry.StartedUtc, "Stats were duplicated or lost the container's runtime.");
            }
            finally { Environment.CurrentDirectory = previousDirectory; foreach (var variable in variables) Environment.SetEnvironmentVariable(variable, saved[variable]); }
        });
        await test("HTTP probes read response headers and HEAD-only status with measured latency", async () =>
        {
            await using var server = new HttpFixture(_ => 200, delay: 25);
            using var probe = new ServiceProbe(); var result = await probe.MeasureAsync([server.Entry()]);
            check(result.Label == "HTTP 200" && result.Tone == ServiceTone.Good && result.Milliseconds >= 20, "HTTP status or response timing is incorrect.");
            check(server.Methods.SequenceEqual(["HEAD"]) && result.Hint.Contains(server.Port.ToString()), "Probe downloaded the page or omitted its target.");
            var url = WebServiceOpener.SelectUrl([server.Entry()], result, DateTime.UtcNow);
            check(url is { Scheme: "http", Host: "127.0.0.1" } && url.Port == server.Port
                && WebServiceOpener.BrowserStart(url).FileName == url.AbsoluteUri && WebServiceOpener.BrowserStart(url).UseShellExecute,
                "A verified HTTP response did not retain the exact local URL for the default browser.");
        });
        await test("HEAD fallback handles 405 and HTTP failures remain visibly distinct from TCP reachability", async () =>
        {
            await using var server = new HttpFixture(method => method == "HEAD" ? 405 : 503);
            using var probe = new ServiceProbe(); var result = await probe.MeasureAsync([server.Entry()]);
            check(result.Label == "HTTP 503" && result.Tone == ServiceTone.Bad && server.Methods.SequenceEqual(["HEAD", "GET"]), "HTTP failure became a green TCP result.");
            check(WebServiceOpener.SelectUrl([server.Entry()], result, DateTime.UtcNow)?.Port == server.Port, "A browser-reachable error page was discarded.");
            await using var missing = new HttpFixture(_ => 404);
            result = await probe.MeasureAsync([missing.Entry()]);
            check(result.Label == "HTTP 404" && result.Tone == ServiceTone.Warning, "A missing root route was silently treated as healthy.");
        });
        await test("Status checks deduplicate IPv4/IPv6, expose a failed port and handle UDP without sending", async () =>
        {
            await using var server = new HttpFixture(_ => 200);
            var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start(); var unusedPort = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
            using var probe = new ServiceProbe(); var entry = server.Entry() with { Name = "postgres", Kind = "TCP" };
            var result = await probe.MeasureAsync([entry, entry with { Address = "0.0.0.0" }, entry with { Port = unusedPort }]);
            check(result.Label == "1/2 svarar" && result.Tone == ServiceTone.Warning && server.Methods.IsEmpty, "Non-HTTP ports received an HTTP request or bindings counted twice.");
            check(result.WebUrls?.Count == 0, "A TCP-only service was mistaken for a website.");
            result = await probe.MeasureAsync([entry with { Protocol = "udp" }]);
            check(result.Label == "UDP" && result.Milliseconds is null && result.Tone == ServiceTone.Neutral, "UDP received a fabricated TCP status.");
            check(ServiceProbe.LocalAddress("0.0.0.0")?.Equals(IPAddress.Loopback) == true && ServiceProbe.LocalAddress("::")?.Equals(IPAddress.IPv6Loopback) == true
                && ServiceProbe.LocalAddress("example.com") is null && ServiceProbe.LocalAddress("203.0.113.5") is null, "The check allowed DNS or a remote address.");
        });
        await test("Status checks never follow redirects and cancellation stops pending work", async () =>
        {
            await using var server = new HttpFixture(_ => 302);
            using var probe = new ServiceProbe(); var result = await probe.MeasureAsync([server.Entry()]);
            check(result.Label == "HTTP 302" && server.Methods.Count == 1, "A redirect triggered an additional request.");
            await using var slow = new HttpFixture(_ => 200, delay: 2000);
            using var stop = new CancellationTokenSource(60);
            try { await probe.MeasureAsync([slow.Entry()], stop.Token); throw new Exception("Expected cancellation."); } catch (OperationCanceledException) { }
            result = await probe.MeasureAsync([server.Entry() with { ContainerId = "own-fixture", DockerEndpoint = "npipe://./pipe/docker_engine", ContainerPort = 8080, ContainerHealth = "unhealthy" }]);
            check(result.Label == "Ej frisk" && result.Tone == ServiceTone.Bad, "Docker health failure was hidden by an open TCP port.");
        });
    }
    private sealed class HttpFixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task accepting;
        private readonly ConcurrentBag<Task> clients = [];
        private readonly Func<string, int> response; private readonly int delay;
        internal readonly ConcurrentQueue<string> Methods = new();
        internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        internal HttpFixture(Func<string, int> response, int delay = 0) { this.response = response; this.delay = delay; listener.Start(); accepting = AcceptAsync(); }
        internal PortEntry Entry() => new(Port, "127.0.0.1", Environment.ProcessId, "node", "node", DateTime.UtcNow);
        private async Task AcceptAsync()
        {
            try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); clients.Add(RespondAsync(client)); } }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            try
            {
                var bytes = new byte[4096]; var stream = client.GetStream();
                var count = await stream.ReadAsync(bytes, stop.Token); if (count == 0) return;
                var method = Encoding.ASCII.GetString(bytes, 0, count).Split(' ')[0]; Methods.Enqueue(method);
                await Task.Delay(delay, stop.Token);
                var code = response(method);
                // A huge body length verifies that the probe reads only headers, never the page body.
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Test\r\nContent-Length: 1000000000\r\nLocation: http://203.0.113.5/\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await accepting; await Task.WhenAll(clients); stop.Dispose(); }
    }
}
