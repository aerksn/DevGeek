using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortPilot.Core;

internal static class AssistantUsageTests
{
    internal static async Task Run(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("Codex quota uses named buckets, preserves zero and ignores missing windows without inventing a reset", () =>
        {
            using var doc = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":2000000000},"secondary":null},"spark":{"limitName":"Spark","secondary":{"usedPercent":110,"windowDurationMins":10080}}}}""");
            var quota = AssistantUsage.ParseCodex(doc.RootElement, DateTimeOffset.UtcNow);
            check(quota.Windows.Count == 2 && quota.Windows[0].Remaining == 100 && quota.Windows[0].Period == "5h"
                && quota.Windows[1].Remaining == 0 && quota.Windows[1].Name == "Spark" && quota.Windows[1].ResetsAt is null, "Quota mapping lost zero/null/name semantics.");
            check(quota.Windows[0].Label(DateTimeOffset.FromUnixTimeSeconds(2000000001)) == "5h —", "Expired quota was still shown as current.");
            using var missing = JsonDocument.Parse("""{"rateLimits":{"primary":null,"secondary":{"windowDurationMins":60}}}""");
            check(AssistantUsage.ParseCodex(missing.RootElement, DateTimeOffset.UtcNow).Windows.Count == 0, "Missing usage became zero percent.");
            return Task.CompletedTask;
        });

        await test("Codex RPC handles initialization, notifications, errors and cancellation without leaving its helper alive", async () =>
        {
            var previous = Environment.GetEnvironmentVariable("PORTPILOT_CODEX_FIXTURE");
            try
            {
                foreach (var mode in new[] { "success", "error", "cancel" })
                {
                    Environment.SetEnvironmentVariable("PORTPILOT_CODEX_FIXTURE", mode);
                    var client = new CodexUsageClient();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "cancel" ? 2 : 10));
                    var operation = client.ReadAsync(Environment.ProcessPath!, timeout.Token);
                    var helper = client.HelperPid;
                    var failed = false;
                    try { var value = await operation; check(mode == "success" && value.Windows.Single().Remaining == 27, "Wrong RPC quota."); }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException) { failed = true; check(!ex.Message.Contains("PRIVATE-DO-NOT-LOG"), "RPC diagnostics leaked."); }
                    check(failed == (mode != "success") && client.HelperPid == 0, "RPC failure/cleanup state incorrect.");
                    try { using var proc = Process.GetProcessById(helper); check(proc.HasExited, "Quota helper survived request."); } catch (ArgumentException) { }
                }
            }
            finally { Environment.SetEnvironmentVariable("PORTPILOT_CODEX_FIXTURE", previous); }
        });

        await test("Claude installer preserves user settings and backs up once; removal preserves subsequent settings", () =>
        {
            var data = Path.Combine(root, "claude setup"); Directory.CreateDirectory(data);
            var settings = Path.Combine(data, "settings.json");
            var original = """{"model":"sample","env":{"TEST":"keep me"},"permissions":{"allow":["Read"]}}""";
            File.WriteAllText(settings, original);
            var bridge = new ClaudeUsageInstaller(data, settings); bridge.Install(); bridge.Install();
            var parsed = JsonNode.Parse(File.ReadAllText(settings))!;
            check(parsed["model"]!.GetValue<string>() == "sample" && parsed["env"]!["TEST"]!.GetValue<string>() == "keep me" && bridge.IsInstalled(), "Unrelated settings were lost.");
            check(Directory.GetFiles(data, "*.bak").Length == 1 && File.ReadAllText(Directory.GetFiles(data, "*.bak")[0]) == original, "Installer backup/idempotence failed.");
            parsed["newSetting"] = true; File.WriteAllText(settings, parsed.ToJsonString()); bridge.Uninstall();
            check(JsonNode.Parse(File.ReadAllText(settings))!["newSetting"]!.GetValue<bool>() && JsonNode.Parse(File.ReadAllText(settings))!["statusLine"] is null, "Removal lost newer settings.");
            return Task.CompletedTask;
        });

        await test("Claude bridge refuses existing status lines and corrupt JSON without overwriting either", () =>
        {
            var data = Path.Combine(root, "claude preserve"); Directory.CreateDirectory(data);
            foreach (var content in new[] { """{"statusLine":{"type":"command","command":"my-status"}}""", "{broken" })
            {
                var settings = Path.Combine(data, "settings.json"); File.WriteAllText(settings, content);
                var refused = false;
                try { new ClaudeUsageInstaller(data, settings).Install(); } catch (Exception ex) when (ex is IOException or JsonException) { refused = true; }
                check(refused && File.ReadAllText(settings) == content, "Existing configuration was overwritten.");
            }
            return Task.CompletedTask;
        });

        await test("Real Claude statusline command handles literal paths, writes only quotas and retains observations on missing input", async () =>
        {
            var data = Path.Combine(root, "claude's $literal & folder"); Directory.CreateDirectory(data);
            var bridge = new ClaudeUsageInstaller(data, Path.Combine(data, "settings.json")); bridge.Install();
            async Task<string> Invoke(string input)
            {
                var info = new ProcessStartInfo("powershell.exe", bridge.Command["powershell.exe ".Length..]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var process = Process.Start(info)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(timeout.Token); } finally { if (!process.HasExited) process.Kill(true); }
                check(process.ExitCode == 0 && string.IsNullOrWhiteSpace(await stderr), "Statusline shell failed."); return await stdout;
            }
            var output = await Invoke("""{"secret":"never-save-this","transcript_path":"private","rate_limits":{"five_hour":{"used_percentage":0,"resets_at":2000000000},"seven_day":{"used_percentage":62,"resets_at":2000500000}}}""");
            var saved = File.ReadAllText(bridge.CachePath);
            var quota = AssistantUsage.ReadClaude(bridge.CachePath)!;
            check(quota.Windows.Count == 2 && quota.Windows[0].Remaining == 100 && quota.Windows[1].Remaining == 38 && output.Contains("5h 100% left"), "Real script/schema mapping failed.");
            check(!saved.Contains("never-save-this") && !saved.Contains("transcript") && !saved.Contains("secret"), "Private stdin fields reached disk.");
            await Invoke("{}"); check(File.ReadAllText(bridge.CachePath) == saved, "Missing quota invented zero or a fresh timestamp.");
            await Invoke("bad json"); check(File.ReadAllText(bridge.CachePath) == saved, "Malformed input replaced the valid snapshot.");
            await Invoke("""{"rate_limits":{"seven_day":{"used_percentage":100}}}""");
            check(AssistantUsage.ReadClaude(bridge.CachePath)!.Windows is [{ Remaining: 0, DurationMinutes: 10080, ResetsAt: null }], "Missing window/reset was treated as zero usage.");
        });

        await test("Assistant process classification supports the downloaded Claude app without treating it as a CLI", () =>
        {
            check(AssistantProcesses.IsClaudeCli(@"C:\Users\test\.local\bin\claude.exe") && !AssistantProcesses.IsClaudeCli(@"C:\Apps\Claude\Claude.exe"), "Claude desktop was treated as Code.");
            check(AssistantProcesses.IsClaudeDesktop(@"C:\Program Files\WindowsApps\Claude_2.31226.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe")
                && AssistantProcesses.IsClaudeDesktop(@"C:\Users\test\AppData\Local\AnthropicClaude\app-1.2\Claude.exe")
                && !AssistantProcesses.IsClaudeDesktop(@"C:\Users\test\.local\bin\claude.exe")
                && !AssistantProcesses.IsClaudeDesktop(@"C:\Apps\unrelated\claude.exe"), "Desktop installation recognition failed.");
            check(AssistantProcesses.IsCodexCli(@"C:\Users\test\AppData\Local\OpenAI\Codex\bin\version\codex.exe")
                && !AssistantProcesses.IsCodexCli(@"C:\Apps\Codex\Codex.exe"), "Desktop executable was treated as RPC CLI.");
            return Task.CompletedTask;
        });

        await test("Native Claude visibility appears while a CLI process runs and disappears after that process exits", async () =>
        {
            var baseline = AssistantProcesses.Scan().ClaudeCliRunning;
            var fixture = Path.Combine(root, "native-claude", ".local", "bin"); Directory.CreateDirectory(fixture);
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)), true);
            var executable = Path.Combine(fixture, "claude.exe"); File.Copy(Environment.ProcessPath!, executable, true);
            var marker = Path.Combine(fixture, "ready.json");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--fixture-listener"); start.ArgumentList.Add(marker);
            using var child = Process.Start(start)!;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(marker)) await Task.Delay(50, timeout.Token);
                var active = AssistantProcesses.Scan();
                check(active.ClaudeRunning && active.ClaudeCliRunning, "Running native Claude CLI not detected.");
                check(AssistantProcesses.Scan(child.Id).ClaudeCliRunning == baseline, "Excluded helper PID was detected as the user's CLI.");
            }
            finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
            check(AssistantProcesses.Scan().ClaudeCliRunning == baseline, "Exited Claude CLI remained visible.");
        });

        await test("Claude page observations preserve reported zero and reject missing, duplicate or invalid quotas", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var read = ClaudePageUsage.Parse("""{"state":"ok","windows":[{"minutes":300,"usedPercent":0},{"minutes":10080,"usedPercent":62}]}""", now);
            check(read.Snapshot!.ObservedAt == now && read.Snapshot.Windows is [{ Remaining: 100, ResetsAt: null }, { Remaining: 38 }], "Page percentages or observation timestamp changed.");
            foreach (var json in new[] {
                """{"state":"ok","windows":[]}""",
                """{"state":"ok","windows":[{"minutes":300}]}""",
                """{"state":"ok","windows":[{"minutes":300,"usedPercent":101}]}""",
                """{"state":"ok","windows":[{"minutes":300,"usedPercent":-1}]}""",
                """{"state":"ok","windows":[{"minutes":300,"usedPercent":25},{"minutes":300,"usedPercent":30}]}""" })
            {
                var rejected = false; try { ClaudePageUsage.Parse(json, now); } catch (InvalidDataException) { rejected = true; }
                check(rejected, "Invalid page quota was accepted.");
            }
            check(ClaudePageUsage.Parse("""{"state":"login_required"}""", now).Snapshot is null, "Login requirement invented a quota.");
            return Task.CompletedTask;
        });
        await test("Claude navigation preserves exact HTTPS origin checks and distinguishes the SPA dialog route", () =>
        {
            check(ClaudePageUsage.IsUsagePage(ClaudePageUsage.Url) && ClaudePageUsage.IsUsagePage(ClaudePageUsage.Url + "/"), "Valid usage route rejected.");
            foreach (var url in new[] { "http://claude.ai/settings/usage", "https://claude.ai.example.com/settings/usage", "https://claude.ai:444/settings/usage", "https://claude.ai/chat/123", "https://claude.ai/settings/billing" })
                check(!ClaudePageUsage.IsUsagePage(url), "An unrelated source could provide the quota.");
            check(ClaudePageUsage.IsClaudePage("https://claude.ai/new") && !ClaudePageUsage.IsUsagePage("https://claude.ai/new"), "The SPA route could not reach dialog validation.");
            foreach (var url in new[] { "http://claude.ai/new", "https://claude.ai.example.com/new", "https://claude.ai:444/new", "https://claude.ai/login", "invalid" })
                check(!ClaudePageUsage.IsClaudePage(url), "A foreign or login page reached quota evaluation.");
            return Task.CompletedTask;
        });
    }
}
