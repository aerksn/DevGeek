using System.Diagnostics;
using System.Text.Json;
using PortPilot.Core;

internal static class ProjectOpenerTests
{
    internal static async Task Run(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("Project opening reads actual cwd, rejects stale PID data and never guesses an executable folder", () =>
        {
            using var process = Process.GetCurrentProcess();
            var entry = new PortEntry(3000, "127.0.0.1", process.Id, process.ProcessName, "TCP", process.StartTime.ToUniversalTime());
            check(ProjectOpener.ResolveDirectory(entry) == Environment.CurrentDirectory, "The running owner's real working directory was not used.");
            check(ProjectOpener.ResolveDirectory(entry with { StartedUtc = entry.StartedUtc!.Value.AddSeconds(1) }) is null, "An unrelated PID lifetime supplied a directory.");
            check(ProjectOpener.ResolveDirectory(entry with { ProjectDirectory = root }) == root, "Known project root did not take priority over process cwd.");
            check(ProjectOpener.LocalDirectory("relative-folder") is null && ProjectOpener.LocalDirectory(@"\\remote\share") is null
                && ProjectOpener.LocalDirectory(Path.Combine(root, "missing-project-" + Guid.NewGuid())) is null, "Missing or remote directories were accepted.");
            return Task.CompletedTask;
        });

        await test("Terminal and Zed launch boundaries preserve exact directories and separate literal arguments", async () =>
        {
            var directory = Path.Combine(root, "projekt åäö & $draft %value% (demo)"); Directory.CreateDirectory(directory);
            foreach (var zed in new[] { false, true })
            {
                var start = zed ? ProjectOpener.ZedStart(directory, Environment.ProcessPath!) : ProjectOpener.TerminalStart(directory, Environment.ProcessPath!);
                var report = Path.Combine(root, "project-open-" + Guid.NewGuid().ToString("N") + ".json");
                start.Environment["PORTPILOT_OPEN_FIXTURE"] = report;
                using var child = Process.Start(start)!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await child.WaitForExitAsync(timeout.Token); }
                finally { if (!child.HasExited) child.Kill(true); }
                check(child.ExitCode == 0, "Fixture process failed.");
                using var document = JsonDocument.Parse(File.ReadAllText(report));
                var arguments = document.RootElement.GetProperty("Args").EnumerateArray().Select(value => value.GetString()).ToArray();
                check(document.RootElement.GetProperty("Directory").GetString() == directory, "The launched process received the wrong cwd.");
                check(arguments.SequenceEqual(zed ? [directory] : new[] { "-w", "new", "new-tab", "--startingDirectory", directory }), "A path was split or interpreted as a command.");
            }
        });

        await test("Terminal fallback keeps semicolon paths out of command parsing and opens the correct PowerShell cwd", async () =>
        {
            var directory = Path.Combine(root, "literal;folder & $value"); Directory.CreateDirectory(directory);
            var start = ProjectOpener.TerminalStart(directory, "must-not-start-wt.exe");
            check(Path.GetFileName(start.FileName) == "powershell.exe" && start.UseShellExecute && start.WorkingDirectory == directory
                && !start.ArgumentList.Any(value => value.Contains(directory)), "The semicolon path entered WT or shell command text.");
            // Exercise the same shell/cwd in an isolated hidden process; no interactive terminal is opened by the test.
            start.UseShellExecute = false; start.CreateNoWindow = true; start.RedirectStandardOutput = true;
            start.ArgumentList.Remove("-NoExit"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add("[Console]::Write((Get-Location).ProviderPath)");
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await child.WaitForExitAsync(timeout.Token); }
            finally { if (!child.HasExited) child.Kill(true); }
            check(child.ExitCode == 0 && await output == directory, "PowerShell did not retain the literal project directory.");
            var missing = false;
            try { ProjectOpener.TerminalStart(Path.Combine(root, "missing-" + Guid.NewGuid()), null); }
            catch (DirectoryNotFoundException) { missing = true; }
            check(missing, "A missing project launched a shell in an arbitrary folder.");
        });

        await test("Docker project opening uses Compose host metadata and never treats container paths as Windows projects", () =>
        {
            var json = JsonSerializer.Serialize(new[] { new {
                Id = "project-container", Name = "/fixture", State = new { Running = true },
                Config = new { Labels = new Dictionary<string, string> { ["com.docker.compose.project.working_dir"] = root } },
                NetworkSettings = new { Ports = new Dictionary<string, object> { ["3000/tcp"] = new[] { new { HostIp = "0.0.0.0", HostPort = "3000" } } } }
            } });
            var entry = DockerService.ParseInspect(json, "npipe://./pipe/docker_engine").Single();
            check(entry.ProjectDirectory == root && ProjectOpener.ResolveDirectory(entry) == root, "Compose host project directory was lost.");
            check(ProjectOpener.ResolveDirectory(entry with { ProjectDirectory = "/app" }) is null
                && ProjectOpener.ResolveDirectory(entry with { ProjectDirectory = null }) is null, "A container path was guessed as a host directory.");
            return Task.CompletedTask;
        });
    }
}
