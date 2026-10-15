using PortPilot.Core;

internal static class ShellTests
{
    internal static async Task Run(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("Service notices need a healthy start and two bad readings, and report recovery once", () =>
        {
            var tracker = new NoticeTracker();
            check(tracker.Service("a", "api", ServiceTone.Bad, "HTTP 500") is null && tracker.Service("a", "api", ServiceTone.Bad, "HTTP 500") is null,
                "A service that was never healthy while watched produced a notice.");
            check(tracker.Service("a", "api", ServiceTone.Good, "HTTP 200") is null, "Recovery was reported without an earlier alert.");
            check(tracker.Service("a", "api", ServiceTone.Bad, "HTTP 500") is null, "A single bad reading alerted; HMR reloads would be noisy.");
            check(tracker.Service("a", "api", ServiceTone.Warning, "HTTP 404") is null && tracker.Service("a", "api", ServiceTone.Bad, "HTTP 500") is null,
                "A non-bad reading did not reset the bad streak.");
            var alert = tracker.Service("a", "api", ServiceTone.Bad, "Svarar inte");
            check(alert is { Kind: NoticeKind.Error } && alert.Title.Contains("api") && alert.Title.Contains("Svarar inte"), "Second bad reading in a row did not alert.");
            check(tracker.Service("a", "api", ServiceTone.Bad, "Svarar inte") is null, "The same outage alerted twice.");
            check(tracker.Service("a", "api", ServiceTone.Neutral, "Äldre") is null, "A neutral reading was reported as recovery.");
            check(tracker.Service("a", "api", ServiceTone.Good, "HTTP 200") is { Kind: NoticeKind.Info } recovered && recovered.Title.Contains("igen"), "Recovery after an alert was not reported.");
            tracker.KeepServices(["b"]);
            tracker.Service("a", "api", ServiceTone.Bad, "HTTP 500");
            check(tracker.Service("a", "api", ServiceTone.Bad, "HTTP 500") is null, "A replaced process inherited the old healthy history.");
            return Task.CompletedTask;
        });

        await test("Quick start notices only report a watched run that ended by itself with a failure", () =>
        {
            var tracker = new NoticeTracker();
            var id = Guid.NewGuid(); var run = Guid.NewGuid();
            check(tracker.Launch(id, "web", run, false, false, 1) is null, "A run that had already failed before the app watched it produced a notice.");
            var next = Guid.NewGuid();
            check(tracker.Launch(id, "web", next, true, false, 259) is null, "Starting a run produced a notice.");
            check(tracker.Launch(id, "web", next, false, false, 1) is { Kind: NoticeKind.Error } failed && failed.Message.Contains("kod 1"), "A crash with an exit code was not reported.");
            check(tracker.Launch(id, "web", next, false, false, 1) is null, "The same failure was reported twice.");
            var stopped = Guid.NewGuid();
            tracker.Launch(id, "web", stopped, true, false, 259);
            check(tracker.Launch(id, "web", stopped, false, true, 1) is null, "A user stop was reported as a failure.");
            var clean = Guid.NewGuid();
            tracker.Launch(id, "web", clean, true, false, 259);
            check(tracker.Launch(id, "web", clean, false, false, 0) is null, "A clean exit (docker compose up -d) was reported as a failure.");
            var restored = Guid.NewGuid();
            tracker.Launch(id, "web", restored, true, false, null);
            check(tracker.Launch(id, "web", restored, false, false, null) is { Kind: NoticeKind.Warning }, "A reattached run without an exit code was not reported as ended.");
            var restart = Guid.NewGuid();
            tracker.Launch(id, "web", restart, true, false, 259);
            check(tracker.Launch(id, "web", Guid.NewGuid(), true, false, 259) is null, "A restart into a new run was reported.");
            return Task.CompletedTask;
        });

        await test("Quota notices fire once when a window falls to 15 % and once when it is refilled", () =>
        {
            var tracker = new NoticeTracker();
            var now = DateTimeOffset.UtcNow;
            UsageSnapshot At(double used, DateTimeOffset? observed = null) => new(observed ?? now, [new("Codex", used, 300, now.AddHours(2)), new("Codex", 10, 10080, now.AddDays(3))]);
            check(!tracker.Quota("Codex", At(90), now, TimeSpan.FromMinutes(2)).Any(), "The first reading alerted although nothing had changed while watched.");
            var reset = new NoticeTracker(); reset.Quota("Codex", At(50), now, TimeSpan.FromMinutes(2)).ToList();
            var low = reset.Quota("Codex", At(86), now, TimeSpan.FromMinutes(2)).ToList();
            check(low.Count == 1 && low[0].Kind == NoticeKind.Warning && low[0].Title.Contains("14 %") && low[0].Title.Contains("5h"), "Falling to 14 % did not produce one low-quota warning.");
            check(!reset.Quota("Codex", At(95), now, TimeSpan.FromMinutes(2)).Any(), "A lower value inside the same low period alerted again.");
            check(!reset.Quota("Codex", At(5, now.AddHours(-1)), now, TimeSpan.FromMinutes(2)).Any(), "A stale snapshot was treated as a reset.");
            var refill = reset.Quota("Codex", At(0), now, TimeSpan.FromMinutes(2)).ToList();
            check(refill.Count == 1 && refill[0].Kind == NoticeKind.Info && refill[0].Title.Contains("återställts"), "A refilled window after a low period was not reported.");
            check(!reset.Quota("Codex", At(20), now, TimeSpan.FromMinutes(2)).Any() && !reset.Quota("Codex", null, now, TimeSpan.FromMinutes(2)).Any(), "Normal usage or a missing quota produced a notice.");
            return Task.CompletedTask;
        });

        await test("Container logs merge stdout and stderr chronologically without colors", () =>
        {
            var merged = DockerService.MergeLogs("2026-10-15T08:00:01.000000000Z ready\n2026-10-15T08:00:03.000000000Z \u001b[32mGET /\u001b[0m 200\n",
                "2026-10-15T08:00:02.000000000Z warn: slow\r\n");
            var lines = merged.Split(Environment.NewLine);
            check(lines.Length == 3 && lines[0].EndsWith("ready") && lines[1].EndsWith("warn: slow") && lines[2].EndsWith("GET / 200"), "Log streams were not interleaved by time.");
            check(lines.All(line => line[2] == ':' && line[5] == ':' && line[8..10] == "  ") && !merged.Contains('\u001b'), "Timestamps were not shortened or ANSI codes remained.");
            check(DockerService.MergeLogs("no timestamp here", "") == "no timestamp here", "A line without a timestamp was dropped.");
            return Task.CompletedTask;
        });

        await test("Editor choice keeps the saved editor and otherwise prefers an installed one", () =>
        {
            check(Editor.All.Select(editor => editor.Id).SequenceEqual(["zed", "code", "cursor", "rider"]), "Editor catalog changed.");
            check(Editor.Resolve("code").Id == "code" && Editor.Resolve("CURSOR").Id == "cursor", "A saved editor was not honored.");
            var fallback = Editor.Resolve(null);
            check(fallback.Find() is not null || Editor.All.All(editor => editor.Find() is null) && fallback.Id == "zed", "Without a choice, an installed editor was not preferred.");
            check(Editor.Resolve("unknown-editor") == fallback, "An unknown saved editor did not fall back.");
            var missing = new Editor("none", "Saknas", () => null);
            var directory = Path.Combine(root, "editor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var rejected = false;
            try { ProjectOpener.OpenEditor(directory, missing); } catch (InvalidOperationException ex) { rejected = ex.Message.Contains("Saknas"); }
            check(rejected, "A missing editor did not explain itself.");
            return Task.CompletedTask;
        });

        await test("Settings keep notification, tray and editor choices; older files get the defaults", () =>
        {
            var store = new SettingsStore(Path.Combine(root, "shell-settings-" + Guid.NewGuid().ToString("N")));
            store.Save(new Settings { Editor = "rider", Notifications = false, CloseToTray = false });
            var loaded = store.Load();
            check(loaded.Editor == "rider" && !loaded.Notifications && !loaded.CloseToTray, "Shell settings were not saved.");
            File.WriteAllText(store.FilePath, "{\"Version\":1,\"QuickStarts\":[]}");
            var old = store.Load();
            check(old.Editor is null && old.Notifications && old.CloseToTray, "An older settings file did not get the new defaults.");
            return Task.CompletedTask;
        });

        await test("Quick start rows match by name, command and folder and report failures", () =>
        {
            var entry = new QuickStart { Name = "Webb", Command = "bun run dev", Directory = @"C:\repos\shop" };
            check(entry.Matches("webb") && entry.Matches("RUN DEV") && entry.Matches("shop") && !entry.Matches("api"), "Quick start search missed a field.");
            check(!entry.IsRunning && !entry.HasFailed && entry.RowStatus == "Redo att starta", "An idle quick start was reported as running or failed.");
            return Task.CompletedTask;
        });
    }
}
