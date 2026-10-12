using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PortPilot.Core;

if (args.FirstOrDefault() == "app-server" && Environment.GetEnvironmentVariable("PORTPILOT_CODEX_FIXTURE") is { } quotaFixture)
{
    var init = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
    if (init.RootElement.GetProperty("method").GetString() != "initialize") throw new Exception("Missing initialize");
    Console.WriteLine("{\"id\":1,\"result\":{}}");
    var initialized = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
    if (initialized.RootElement.GetProperty("method").GetString() != "initialized") throw new Exception("Missing initialized");
    var read = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
    if (read.RootElement.GetProperty("method").GetString() != "account/rateLimits/read") throw new Exception("Unexpected RPC");
    if (quotaFixture == "cancel") { await Task.Delay(Timeout.Infinite); return; }
    Console.WriteLine("{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
    Console.WriteLine("{\"id\":99,\"result\":{}}");
    Console.WriteLine(quotaFixture == "error" ? "{\"id\":2,\"error\":{\"code\":-32000,\"message\":\"PRIVATE-DO-NOT-LOG\"}}"
        : "{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":73,\"windowDurationMins\":10080}}}}");
    await Console.In.ReadToEndAsync(); return;
}
if (args.FirstOrDefault() == "--verify-assistant-processes")
{
    var activity = AssistantProcesses.Scan();
    Console.WriteLine($"Codex active: {activity.CodexRunning}; Claude app active: {activity.ClaudeDesktopRunning}; Claude CLI active: {activity.ClaudeCliRunning}");
    return;
}
if (args.FirstOrDefault() == "--verify-live-usage")
{
    var activity = AssistantProcesses.Scan();
    Console.WriteLine($"Codex active: {activity.CodexRunning}; Claude Code active: {activity.ClaudeRunning}; CLI found: {activity.CodexExecutable is not null}");
    if (activity.CodexExecutable is { } cli)
    {
        var client = new CodexUsageClient();
        var quota = await client.ReadAsync(cli);
        Console.WriteLine($"Read-only RPC: {quota.Windows.Count} quota windows; helper exited: {client.HelperPid == 0}");
        if (quota.Windows.Count == 0) Environment.ExitCode = 1;
    }
    else Environment.ExitCode = 1;
    return;
}

if (Environment.GetEnvironmentVariable("PORTPILOT_OPEN_FIXTURE") is { } openFixture)
{
    File.WriteAllText(openFixture, JsonSerializer.Serialize(new { Args = args, Directory = Environment.CurrentDirectory }));
    return;
}

if (args.Length == 3 && args[0] == "--keep-launch")
{
    Environment.ExitCode = LaunchSession.HoldJob(args[1], args[2]); return;
}
if (args.FirstOrDefault() == "--fixture-listener")
{
    var listener = new TcpListener(IPAddress.Loopback, args.Length > 2 ? int.Parse(args[2]) : 0); listener.Start();
    File.WriteAllText(args[1], JsonSerializer.Serialize(new Fixture(Environment.ProcessId, ((IPEndPoint)listener.LocalEndpoint).Port)));
    Console.WriteLine("fixture-ready");
    await Task.Delay(Timeout.Infinite); return;
}
if (args.FirstOrDefault() == "--fixture-parent")
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    info.ArgumentList.Add("--fixture-listener"); info.ArgumentList.Add(args[1]);
    using var child = Process.Start(info)!;
    if (args.Contains("--exit")) return;
    await child.WaitForExitAsync(); return;
}
if (args.FirstOrDefault() == "--fixture-managed-launch")
{
    var entry = JsonSerializer.Deserialize<QuickStart>(File.ReadAllText(args[2]))!;
    using var session = new LaunchManager(args[1]).Start(entry);
    var watch = Stopwatch.StartNew();
    while (!File.Exists(args[3]) || new FileInfo(args[3]).Length == 0)
    {
        if (watch.Elapsed > TimeSpan.FromSeconds(10)) { await session.StopAsync(); throw new TimeoutException("Fixture did not start."); }
        await Task.Delay(50);
    }
    while (session.ExitCode == 259)
    {
        if (watch.Elapsed > TimeSpan.FromSeconds(10)) { await session.StopAsync(); throw new TimeoutException("Root launcher did not exit."); }
        await Task.Delay(50);
    }
    return; // Exit the entire owning process, not just one handle in the test process.
}
if (args.FirstOrDefault() == "--host" && Environment.GetEnvironmentVariable("PORTPILOT_DOCKER_FIXTURE") is { } dockerFixture)
{
    File.WriteAllText(dockerFixture, JsonSerializer.Serialize(new { Args = args, Context = Environment.GetEnvironmentVariable("DOCKER_CONTEXT") }));
    if (Environment.GetEnvironmentVariable("PORTPILOT_DOCKER_OUTPUT") is { } dockerOutput) Console.WriteLine(dockerOutput);
    if (Environment.GetEnvironmentVariable("PORTPILOT_DOCKER_FAILURE") == "1") { Console.Error.WriteLine("fixture-restart-failed"); Environment.ExitCode = 1; }
    return;
}

var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, "test-results"));
Directory.CreateDirectory(root);
var passed = new List<string>();
var failures = new List<string>();
var ports = new PortService();

async Task Test(string title, Func<Task> action)
{
    try { await action(); passed.Add(title); Console.WriteLine("PASS " + title); }
    catch (Exception ex) { failures.Add(title + ": " + ex); Console.WriteLine("FAIL " + title + ": " + ex.Message); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Until(Func<bool> predicate)
{
    var watch = Stopwatch.StartNew();
    while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Condition did not become true."); await Task.Delay(80); }
}
async Task Throws(Func<Task> action)
{
    try { await action(); } catch { return; }
    throw new Exception("Expected rejection.");
}
QuickStart Entry(string command) => new() { Name = "Test", Directory = root, Command = command };

await Test("Native IPv4 + IPv6 listener discovery and PID ownership", () =>
{
    var ipv4 = new TcpListener(IPAddress.Loopback, 0);
    var ipv6 = new TcpListener(IPAddress.IPv6Loopback, 0); ipv6.Server.DualMode = false;
    try
    {
        ipv4.Start(); ipv6.Start();
        var scan = ports.Scan();
        Check(scan.Any(p => p.Pid == Environment.ProcessId && p.Address == "127.0.0.1" && p.Port == ((IPEndPoint)ipv4.LocalEndpoint).Port), "IPv4 fixture missing.");
        Check(scan.Any(p => p.Pid == Environment.ProcessId && p.Address == "::1" && p.Port == ((IPEndPoint)ipv6.LocalEndpoint).Port), "IPv6 fixture missing.");
        Check(scan.Where(p => p.Pid == Environment.ProcessId).All(p => !p.CanStop), "App must not offer self termination.");
    }
    finally { ipv4.Stop(); ipv6.Stop(); }
    return Task.CompletedTask;
});

await Test("Docker TCP/UDP, IPv6, unpublished and stopped container parsing", () =>
{
    const string json = """
    [{"Id":"aaaaaaaaaaaaaaaa","Name":"/db","State":{"Running":true},"NetworkSettings":{"Ports":{"5432/tcp":[{"HostIp":"0.0.0.0","HostPort":"55432"},{"HostIp":"::","HostPort":"55432"}],"53/udp":[{"HostIp":"127.0.0.1","HostPort":"5353"}],"8000/tcp":null}}},{"Id":"bbbbbbbbbbbbbbbb","Name":"/stopped","State":{"Running":false},"NetworkSettings":{"Ports":{"80/tcp":[{"HostIp":"0.0.0.0","HostPort":"8080"}]}}}]
    """;
    var scan = DockerService.ParseInspect(json, "npipe://./pipe/docker_engine");
    Check(scan.Count == 3, "Expected exactly three published bindings.");
    Check(scan.Count(p => p.Port == 55432 && p.ContainerPort == 5432) == 2, "Port mapping lost.");
    Check(scan.Single(p => p.Protocol == "udp").Port == 5353, "UDP missing.");
    Check(scan.All(p => p.CanStop && p.IsDevelopment && p.Name == "db"), "Docker classification incorrect.");
    return Task.CompletedTask;
});

await Test("Remote Docker endpoints rejected; local Windows pipes allowed", async () =>
{
    Check(DockerService.IsLocalEndpoint("npipe:////./pipe/dockerDesktopLinuxEngine"), "Desktop pipe rejected.");
    Check(DockerService.IsLocalEndpoint("npipe://./pipe/docker_engine"), "Engine pipe rejected.");
    foreach (var endpoint in new[] { "ssh://prod", "tcp://localhost:2375", "npipe://server/pipe/docker", "unix:///var/run/docker.sock" })
        Check(!DockerService.IsLocalEndpoint(endpoint), "Unexpected endpoint accepted: " + endpoint);
    await Throws(() => DockerService.StopAsync(new(80, "0.0.0.0", 0, "remote", "Docker", null, ContainerId: "fake", DockerEndpoint: "tcp://prod:2375")));
    await Throws(() => DockerService.RestartAsync(new(80, "0.0.0.0", 0, "remote", "Docker", null, ContainerId: "fake", DockerEndpoint: "tcp://prod:2375")));
    await Throws(() => DockerService.RestartAsync(new(80, "127.0.0.1", 9999, "node", "node", DateTime.UtcNow)));
});

await Test("Settings roundtrip, backup, and malformed-file preservation", () =>
{
    var store = new SettingsStore(Path.Combine(root, "settings-" + Guid.NewGuid().ToString("N")));
    var config = new Settings { Topmost = true, Compact = true, QuickStarts = [new() { Name = "Miljö åäö", Directory = @"C:\repo med mellanslag", Command = "echo \"hej\" && echo klart" }] };
    store.Save(config); var loaded = store.Load();
    Check(loaded.QuickStarts[0].Name == "Miljö åäö" && loaded.QuickStarts[0].Command == config.QuickStarts[0].Command && loaded.Topmost && loaded.Compact, "Settings changed.");
    config.Topmost = false; store.Save(config); Check(File.Exists(store.FilePath + ".bak"), "Backup missing.");
    File.WriteAllText(store.FilePath, "broken-json");
    bool rejected = false; try { store.Load(); } catch (JsonException) { rejected = true; }
    Check(rejected && File.ReadAllText(store.FilePath) == "broken-json", "Corrupt file was not preserved.");
    return Task.CompletedTask;
});

await Test("Quick start preserves working directory, shell operators and log output", async () =>
{
    var directory = Path.Combine(root, "mapp med åäö och mellanslag"); Directory.CreateDirectory(directory);
    var entry = Entry("echo PORTPILOT_OK && cd && exit /b 7"); entry.Directory = directory;
    using var session = LaunchSession.Start(entry, root);
    await Until(() => !session.IsRunning);
    Check(session.ExitCode == 7, "Nonzero exit code lost.");
    Check(session.ReadLog().Contains("PORTPILOT_OK"), "Output not captured.");
    // cmd's legacy code page can replace non-ASCII in its output; verify the working directory using a file.
    using var marker = LaunchSession.Start(new() { Name = "cwd", Directory = directory, Command = "echo OK>cwd-marker.txt" }, root);
    await Until(() => !marker.IsRunning);
    Check(File.Exists(Path.Combine(directory, "cwd-marker.txt")), "Working directory incorrect.");
});

await Test("Quick start rejects nonexistent working directory before execution", async () =>
{
    var entry = Entry("echo should-not-run"); entry.Directory = Path.Combine(root, "missing-" + Guid.NewGuid().ToString("N"));
    await Throws(() => { using var session = LaunchSession.Start(entry, root); return Task.CompletedTask; });
});

await Test("Job start/stop terminates only the fixture process tree and frees its port", async () =>
{
    var ready = Path.Combine(root, "fixture-" + Guid.NewGuid().ToString("N") + ".json");
    using var session = LaunchSession.Start(Entry($"\"{Environment.ProcessPath}\" --fixture-parent \"{ready}\""), root);
    try
    {
        await Until(() => File.Exists(ready) && new FileInfo(ready).Length > 0);
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(ready))!;
        Check(session.ProcessCount >= 3, "cmd + parent + child not tracked.");
        Check(ports.Scan().Any(p => p.Pid == fixture.Pid && p.Port == fixture.Port), "Fixture listener missing.");
        session.Stop(); await Until(() => !session.IsRunning);
        Check(session.WasStopped, "Stop state missing.");
        await Until(() => !ports.Scan().Any(p => p.Pid == fixture.Pid && p.Port == fixture.Port));
    }
    finally { if (session.IsRunning) { session.Stop(); await Until(() => !session.IsRunning); } }
});

await Test("Job still tracks child after launcher and cmd have exited", async () =>
{
    var ready = Path.Combine(root, "orphan-" + Guid.NewGuid().ToString("N") + ".json");
    using var session = LaunchSession.Start(Entry($"\"{Environment.ProcessPath}\" --fixture-parent \"{ready}\" --exit"), root);
    try
    {
        await Until(() => File.Exists(ready) && new FileInfo(ready).Length > 0);
        await Until(() => session.ExitCode != 259);
        Check(session.IsRunning, "Live descendant was lost after root exit.");
        session.Stop(); await Until(() => !session.IsRunning);
    }
    finally { if (session.IsRunning) { session.Stop(); await Until(() => !session.IsRunning); } }
});

await Test("Port stop rejects stale process identity and then stops the known fixture", async () =>
{
    var ready = Path.Combine(root, "stop-" + Guid.NewGuid().ToString("N") + ".json");
    using var session = LaunchSession.Start(Entry($"\"{Environment.ProcessPath}\" --fixture-listener \"{ready}\""), root);
    try
    {
        await Until(() => File.Exists(ready) && new FileInfo(ready).Length > 0);
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(ready))!;
        var entry = ports.Scan().Single(p => p.Pid == fixture.Pid && p.Port == fixture.Port);
        Check(entry.CanStop, "Fixture unexpectedly protected.");
        await Throws(() => ports.StopAsync(entry with { StartedUtc = entry.StartedUtc?.AddMinutes(-1) }));
        Check(ports.Scan().Any(p => p.Pid == fixture.Pid && p.Port == fixture.Port), "Stale identity killed fixture.");
        await ports.StopAsync(entry);
        await Until(() => !session.IsRunning);
        Check(!ports.Scan().Any(p => p.Pid == fixture.Pid && p.Port == fixture.Port), "Listener survived stop.");
    }
    finally { if (session.IsRunning) { session.Stop(); await Until(() => !session.IsRunning); } }
});

await Test("Project names use the nearest application package and skip dependency package names", () =>
{
    var directory = Path.Combine(root, "project name " + Guid.NewGuid().ToString("N"));
    var subdirectory = Path.Combine(directory, "src");
    var dependency = Path.Combine(directory, "node_modules", "vite", "bin");
    Directory.CreateDirectory(subdirectory); Directory.CreateDirectory(dependency);
    File.WriteAllText(Path.Combine(directory, "package.json"), "{\"name\":\"@example/demo-app\"}");
    File.WriteAllText(Path.Combine(directory, "node_modules", "vite", "package.json"), "{\"name\":\"vite\"}");
    var project = ProjectNameResolver.FromPaths(subdirectory, []);
    Check(project?.Name == "@example/demo-app" && project.Directory == directory, "Package name or project root missing.");
    var argument = ProjectNameResolver.FromPaths(null, [Path.Combine(dependency, "vite.js")]);
    Check(argument == project, "CLI path selected the dependency instead of the app.");
    File.WriteAllText(Path.Combine(directory, "package.json"), "broken-json");
    Check(ProjectNameResolver.FromPaths(subdirectory, [])?.Name == Path.GetFileName(directory), "Malformed package did not fall back to the project folder.");
    File.WriteAllText(Path.Combine(directory, "package.json"), "{\"name\":\"\"}");
    Check(ProjectNameResolver.FromPaths(subdirectory, [])?.Name == Path.GetFileName(directory), "Empty package name did not fall back.");
    Check(ProjectNameResolver.FromPaths(null, []) is null, "Unavailable metadata invented a project.");
    Check(ProjectNameResolver.FromPaths(subdirectory, ["bad\0path.js", @"C:\bad" + '\0' + "path.js", "--cwd="])?.Name == Path.GetFileName(directory), "Malformed arguments broke the working-directory fallback.");
    return Task.CompletedTask;
});

await Test("Project labels preserve runtime classification, raw stop identity and owner grouping", () =>
{
    var entry = new PortEntry(3000, "127.0.0.1", 91901, "node", "node", DateTime.UtcNow, ProjectName: "demo-web", ProjectDirectory: @"C:\repos\demo-web");
    var groups = PortGroup.Create([entry, entry with { Port = 3001 }, entry with { Pid = 91902, Port = 3002 }]);
    Check(entry.Name == "node" && entry.DisplayName == "demo-web" && entry.IsDevelopment && entry.CanStop, "Project label changed runtime behavior.");
    Check(groups.Count == 2 && groups[0].Name == "demo-web" && groups[0].PortCount == 2 && groups[0].StopTarget?.Name == "node", "Project label merged stop owners or lost ports.");
    Check(groups[0].Matches("demo-web") && groups[0].Matches("NODE") && groups[0].Matches(@"C:\repos"), "Project/runtime/path search failed.");
    Check(!(entry with { Name = "svchost", ProjectName = "friendly-project" }).CanStop, "Project label bypassed system process protection.");
    return Task.CompletedTask;
});

if (Environment.GetEnvironmentVariable("PORTPILOT_TEST_NODE") is { } nodePath && File.Exists(nodePath))
await Test("Real Node working directory resolves its project and display labels do not break safe stop", async () =>
{
    var directory = Path.Combine(root, "node project " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var ready = Path.Combine(root, "node-ready-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(Path.Combine(directory, "package.json"), "{\"name\":\"portpilot-node-fixture\"}");
    File.WriteAllText(Path.Combine(directory, "server.js"), "const fs=require('node:fs');const server=require('node:net').createServer();server.listen(0,'127.0.0.1',()=>fs.writeFileSync(process.argv[2],JSON.stringify({Pid:process.pid,Port:server.address().port})));\n");
    var start = new ProcessStartInfo(nodePath) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("server.js"); start.ArgumentList.Add(ready);
    using var fixtureProcess = Process.Start(start)!;
    try
    {
        await Until(() => File.Exists(ready) && new FileInfo(ready).Length > 0);
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(ready))!;
        var entry = ports.Scan().Single(p => p.Pid == fixture.Pid && p.Port == fixture.Port);
        Check(entry.Name == "node" && entry.DisplayName == "portpilot-node-fixture" && entry.ProjectDirectory == directory && entry.IsDevelopment, "Live Node project was not resolved from its working directory.");
        Check(new ProjectNameResolver().Resolve(fixture.Pid, entry.StartedUtc?.AddMinutes(-1)) is null, "Stale process identity was read as a current project.");
        await ports.StopAsync(entry);
        await Until(() => fixtureProcess.HasExited);
        Check(!ports.Scan().Any(p => p.Pid == fixture.Pid && p.Port == fixture.Port), "Renamed Node fixture survived safe stop.");
    }
    finally { if (!fixtureProcess.HasExited) { fixtureProcess.Kill(entireProcessTree: true); await fixtureProcess.WaitForExitAsync(); } }
});

await Test("Protected Windows and Docker backend processes cannot be stopped", () =>
{
    foreach (var name in new[] { "svchost", "System", "com.docker.backend", "wslrelay" })
        Check(!new PortEntry(3000, "0.0.0.0", 9999, name, "TCP", DateTime.UtcNow).CanStop, name + " was not protected.");
    Check(new PortEntry(3000, "127.0.0.1", 9999, "node", "node", DateTime.UtcNow).IsDevelopment, "Node not classified.");
    return Task.CompletedTask;
});

await Test("Six Docker bindings collapse to one owner with three unique ports", () =>
{
    var entries = (from port in new[] { 3210, 3211, 11434 }
                   from address in new[] { "::", "0.0.0.0" }
                   select new PortEntry(port, address, 0, "demo-backend-1", "Docker", null,
                       ContainerId: "backend-id", DockerEndpoint: "npipe://./pipe/docker_engine", ContainerPort: port)).ToList();
    var groups = PortGroup.Create(entries);
    Check(groups.Count == 1, "Duplicate owner rows remain.");
    var group = groups[0];
    Check(group.PortCount == 3 && group.Entries.Count == 6, "Unique ports or address details lost.");
    Check(group.PortSummary == ":3210  ·  :3211  ·  :11434", "Unexpected port summary.");
    Check(group.Matches("3211") && group.PortCount == 3 && group.Matches("::"), "Search did not retain whole group.");
    Check(group.BindingDetails.Contains("[::]:3210 → 3210/tcp"), "IPv6 mapping is ambiguous or missing.");
    Check(group.StopTarget?.ContainerId == "backend-id", "Wrong stop target.");
    return Task.CompletedTask;
});

await Test("Equal names do not merge different PIDs, start times, containers or Docker endpoints", () =>
{
    var started = DateTime.UtcNow;
    var entries = new List<PortEntry>
    {
        new(3000, "127.0.0.1", 91001, "node", "node", started),
        new(3000, "::1", 91001, "node", "node", started),
        new(3001, "127.0.0.1", 91002, "node", "node", started),
        new(3002, "127.0.0.1", 91001, "node", "node", started.AddMinutes(1)),
        new(3210, "0.0.0.0", 0, "backend", "Docker", null, ContainerId: "one", DockerEndpoint: "npipe://./pipe/a"),
        new(3210, "0.0.0.0", 0, "backend", "Docker", null, ContainerId: "two", DockerEndpoint: "npipe://./pipe/a"),
        new(3210, "0.0.0.0", 0, "backend", "Docker", null, ContainerId: "one", DockerEndpoint: "npipe://./pipe/b")
    };
    var groups = PortGroup.Create(entries);
    Check(groups.Count == 6, "Unrelated owners were combined.");
    Check(groups.Single(g => g.Entries.Count == 2).StopTarget?.Pid == 91001, "Incorrect process selected for stop.");
    Check(groups.Count(g => g.Description.Contains("PID 91001")) == 2, "PID metadata missing.");
    return Task.CompletedTask;
});

await Test("TCP and UDP stay distinct; refresh preserves expansion and stop state", () =>
{
    var entry = new PortEntry(5353, "0.0.0.0", 0, "dns", "Docker", null, ContainerId: "dns", DockerEndpoint: "npipe://./pipe/docker_engine", ContainerPort: 53);
    var group = PortGroup.Create([entry, entry with { Protocol = "udp" }]).Single();
    Check(group.PortCount == 2 && group.PortSummary.Contains(":5353/udp"), "Protocol identity lost.");
    group.IsExpanded = true; group.IsStopping = true;
    group.Update(PortGroup.Create([entry with { Address = "::" }]).Single());
    Check(group.IsExpanded && !group.CanStop && group.Entries[0].Address == "::", "Refresh reset UI state or retained stale details.");
    group.IsStopping = false; Check(group.CanStop, "Stop state not released.");
    var rejected = false;
    try { group.Update(PortGroup.Create([entry with { ContainerId = "different" }]).Single()); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Group silently switched stop targets.");
    return Task.CompletedTask;
});

await Test("Managed launch survives owner process exit, reconnects live descendants and rejects duplicate start", async () =>
{
    var directory = Path.Combine(root, "reconnect-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var manager = new LaunchManager(directory);
    var ready = Path.Combine(directory, "listener.json");
    var entry = Entry($"echo reconnect-log && \"{Environment.ProcessPath}\" --fixture-parent \"{ready}\" --exit");
    var config = Path.Combine(directory, "entry.json"); File.WriteAllText(config, JsonSerializer.Serialize(entry));
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in new[] { "--fixture-managed-launch", directory, config, ready }) start.ArgumentList.Add(arg);
    LaunchSession? session = null;
    try
    {
        using var owner = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await owner.WaitForExitAsync(timeout.Token);
        Check(owner.ExitCode == 0, "Owning process failed.");
        // Roundtrip the same saved quick start just as the desktop app does.
        var restoredEntry = JsonSerializer.Deserialize<QuickStart>(File.ReadAllText(config))!;
        session = manager.Restore(restoredEntry);
        Check(session?.IsRunning == true && session.WasRestored, "Session did not reconnect after owner exit.");
        restoredEntry.Session = session;
        Check(restoredEntry.Status.Contains("återansluten") && restoredEntry.CanRestart && !restoredEntry.CanEdit, "Restored UI state incorrect.");
        Check(session!.ReadLog().Contains("reconnect-log"), "Original log not recovered.");
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(ready))!;
        var oldRecord = File.ReadAllText(manager.RecordPath(entry.Id));
        await Throws(() => { using var duplicate = manager.Start(restoredEntry); return Task.CompletedTask; });
        Check(session.IsRunning && oldRecord == File.ReadAllText(manager.RecordPath(entry.Id)), "Duplicate start replaced active ownership.");
        await session.StopAsync();
        await Until(() => !ports.Scan().Any(p => p.Pid == fixture.Pid && p.Port == fixture.Port));
    }
    finally
    {
        session ??= manager.Restore(entry);
        if (session is not null) { if (session.IsRunning) await session.StopAsync(); session.Dispose(); }
        // A failed reconnect test must still clean up only its own recorded listener.
        if (File.Exists(ready) && new FileInfo(ready).Length > 0)
        {
            var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(ready))!;
            var listener = ports.Scan().FirstOrDefault(p => p.Pid == fixture.Pid && p.Port == fixture.Port && p.Name == "PortPilot.Tests");
            if (listener is not null) await ports.StopAsync(listener);
        }
    }
});

await Test("Restart frees the same port before relaunch, retains history, and invalid replacement keeps the run alive", async () =>
{
    var directory = Path.Combine(root, "restart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
    var ready = Path.Combine(directory, "listener.json");
    var entry = Entry($"\"{Environment.ProcessPath}\" --fixture-listener \"{ready}\" {port}");
    var manager = new LaunchManager(directory);
    entry.Session = manager.Start(entry);
    try
    {
        await Until(() => File.Exists(ready) && new FileInfo(ready).Length > 0);
        var previous = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(ready))!;
        var originalLog = entry.Session.LogPath;
        var validDirectory = entry.Directory; entry.Directory = Path.Combine(root, "missing-" + Guid.NewGuid().ToString("N"));
        await Throws(() => manager.RestartAsync(entry)); entry.Directory = validDirectory;
        Check(entry.Session.IsRunning && ports.Scan().Any(p => p.Pid == previous.Pid && p.Port == port), "Invalid replacement stopped a working environment.");
        File.Delete(ready);
        await manager.RestartAsync(entry);
        await Until(() => File.Exists(ready) && new FileInfo(ready).Length > 0);
        var current = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(ready))!;
        Check(current.Pid != previous.Pid && current.Port == port, "Restart did not replace the listener on the same port.");
        Check(!ports.Scan().Any(p => p.Pid == previous.Pid) && entry.Session!.IsRunning, "Old process survived restart.");
        Check(originalLog != entry.Session.LogPath && File.Exists(originalLog), "Log history overwritten.");
        entry.Operation = "Startar om…"; entry.IsBusy = true;
        Check(!entry.CanAct && !entry.CanRestart && !entry.CanEdit, "Busy quick start still allows overlapping actions.");
        entry.IsBusy = false;
    }
    finally { if (entry.Session is { } session) { if (session.IsRunning) await session.StopAsync(); session.Dispose(); } }
});

await Test("State write failure never executes the command; malformed state blocks launch and remains intact", async () =>
{
    var directory = Path.Combine(root, "persistence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "sessions"), "block directory creation");
    var marker = Path.Combine(directory, "must-not-exist.txt");
    var entry = Entry($"echo unsafe>\"{marker}\"");
    await Throws(() => { using var session = new LaunchManager(directory).Start(entry); return Task.CompletedTask; });
    Check(!File.Exists(marker), "Command ran before ownership was saved.");
    File.Delete(Path.Combine(directory, "sessions")); Directory.CreateDirectory(Path.Combine(directory, "sessions"));
    var manager = new LaunchManager(directory); File.WriteAllText(manager.RecordPath(entry.Id), "broken-json");
    await Throws(() => { using var session = manager.Start(entry); return Task.CompletedTask; });
    Check(!File.Exists(marker) && File.ReadAllText(manager.RecordPath(entry.Id)) == "broken-json", "Corrupt state was overwritten or launched.");
});

await Test("Completed and missing jobs keep logs without stale PID actions, and can start again", async () =>
{
    var directory = Path.Combine(root, "completed-" + Guid.NewGuid().ToString("N"));
    var entry = Entry("echo completed-log && exit /b 7"); var manager = new LaunchManager(directory);
    using (var first = manager.Start(entry))
    {
        await Until(() => !first.IsRunning); entry.Session = first;
        Check(entry.Status.Contains("Misslyckades · kod 7"), "Failure status missing.");
    }
    entry.Session = manager.Restore(entry);
    using (entry.Session)
    {
        Check(entry.Session is { IsRunning: false } && entry.Session.ReadLog().Contains("completed-log") && !entry.CanRestart, "Completed state or log lost.");
        Check(entry.Status == "Avslutad · se logg", "Unknown exit code presented as a real code.");
    }
    using var second = manager.Start(entry); await Until(() => !second.IsRunning);
    Check(second.ExitCode == 7, "A stale record prevented a new run.");
});

await Test("Concurrent starts create exactly one environment", async () =>
{
    var directory = Path.Combine(root, "concurrent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var ready = Path.Combine(directory, "listener.json"); var manager = new LaunchManager(directory);
    var entry = Entry($"\"{Environment.ProcessPath}\" --fixture-listener \"{ready}\"");
    LaunchSession? TryStart() { try { return manager.Start(entry); } catch (InvalidOperationException) { return null; } }
    var sessions = await Task.WhenAll(Task.Run(TryStart), Task.Run(TryStart));
    try { Check(sessions.Count(s => s is not null) == 1, "More than one start succeeded."); }
    finally { foreach (var session in sessions.OfType<LaunchSession>()) { await session.StopAsync(); session.Dispose(); } }
});

await Test("Docker restart pins the scanned pipe and container, ignores ambient context, and surfaces CLI failure", async () =>
{
    // A CLI stand-in verifies the real process boundary without touching the user's Docker daemon.
    var directory = Path.Combine(root, "docker-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    foreach (var path in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(path, Path.Combine(directory, Path.GetFileName(path)));
    File.Copy(Environment.ProcessPath!, Path.Combine(directory, "docker.exe"));
    var record = Path.Combine(directory, "arguments.json");
    var variables = new[] { "PATH", "DOCKER_CONTEXT", "PORTPILOT_DOCKER_FIXTURE", "PORTPILOT_DOCKER_FAILURE" };
    var saved = variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
    var previousDirectory = Environment.CurrentDirectory;
    try
    {
        // Windows searches the working directory before System32 and PATH.
        // Keep an installed Docker CLI out of this isolated fixture boundary.
        Environment.CurrentDirectory = directory;
        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + saved["PATH"]);
        Environment.SetEnvironmentVariable("DOCKER_CONTEXT", "must-not-use");
        Environment.SetEnvironmentVariable("PORTPILOT_DOCKER_FIXTURE", record);
        var entry = new PortEntry(3210, "0.0.0.0", 0, "fixture", "Docker", null, ContainerId: new string('a', 64), DockerEndpoint: "npipe://./pipe/portpilot_fixture_only");
        var group = PortGroup.Create([entry]).Single(); Check(group.CanRestart, "Container restart is unavailable.");
        group.IsStopping = true; Check(!group.CanRestart && !group.CanStop, "Container busy state allows another action.");
        await DockerService.RestartAsync(entry);
        using var result = JsonDocument.Parse(File.ReadAllText(record));
        Check(result.RootElement.GetProperty("Args").EnumerateArray().Select(a => a.GetString()).SequenceEqual(new[] { "--host", entry.DockerEndpoint, "container", "restart", "--timeout", "10", entry.ContainerId }), "Wrong restart target or arguments.");
        Check(result.RootElement.GetProperty("Context").ValueKind == JsonValueKind.Null, "Ambient Docker context escaped into restart.");
        Environment.SetEnvironmentVariable("PORTPILOT_DOCKER_FAILURE", "1");
        var rejected = false;
        try { await DockerService.RestartAsync(entry); } catch (InvalidOperationException ex) { rejected = ex.Message.Contains("fixture-restart-failed"); }
        Check(rejected, "CLI failure was swallowed.");
    }
    finally { Environment.CurrentDirectory = previousDirectory; foreach (var variable in variables) Environment.SetEnvironmentVariable(variable, saved[variable]); }
});

if (args.Length > 1)
await Test("Packaged desktop host preserves the job, releases after stop and leaves the portable exe replaceable", async () =>
{
    var directory = Path.Combine(root, "packaged-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var ready = Path.Combine(directory, "listener.json");
    var manager = new LaunchManager(directory, shadowCopyHost: true, hostExecutable: Path.GetFullPath(args[1]));
    var entry = Entry($"\"{Environment.ProcessPath}\" --fixture-listener \"{ready}\"");
    LaunchSession? session = manager.Start(entry);
    try
    {
        await Until(() => File.Exists(ready) && new FileInfo(ready).Length > 0);
        session.Dispose(); session = manager.Restore(entry);
        Check(session?.IsRunning == true, "Packaged WPF host did not preserve ownership.");
        Check(Directory.GetFiles(Path.Combine(directory, "runtime"), "PortPilot.LaunchHost.exe", SearchOption.AllDirectories).Length == 1, "Cached host missing.");
        using (var portable = new FileStream(args[1], FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { Check(portable.Length > 0, "Portable exe unexpectedly empty."); }
        Process? host = null;
        foreach (var candidate in Process.GetProcessesByName("PortPilot.LaunchHost"))
        {
            try { if (candidate.MainModule?.FileName?.StartsWith(directory, StringComparison.OrdinalIgnoreCase) == true) { host = candidate; continue; } }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            candidate.Dispose();
        }
        using (host)
        {
            Check(host is not null, "Packaged helper not found.");
            await session!.StopAsync(); session.Dispose(); session = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host!.WaitForExitAsync(timeout.Token);
            Check(host.ExitCode == 0, "Packaged helper did not exit cleanly.");
        }
    }
    finally { if (session is not null) { if (session.IsRunning) await session.StopAsync(); session.Dispose(); } }
});

await BalanceTests.Run(root, Test, Check, Throws);
await TelemetryTests.Run(root, Test, Check);
await ProjectOpenerTests.Run(root, Test, Check);
await WebServiceTests.Run(Test, Check, Throws);
await AssistantUsageTests.Run(root, Test, Check);
await AppearanceTests.Run(root, Test, Check);
var report = $"{passed.Count} passed, {failures.Count} failed\n" + string.Join("\n", passed.Select(p => "PASS " + p)) + "\n" + string.Join("\n", failures);
File.WriteAllText(Path.Combine(root, "test-report.txt"), report);
Console.WriteLine($"\n{passed.Count} passed, {failures.Count} failed");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

internal sealed record Fixture(int Pid, int Port);
