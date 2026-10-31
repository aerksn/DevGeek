using System.Text.Json;
using OtterMonitor.Core;

internal static class AppearanceTests
{
    internal static async Task Run(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("Visibility defaults keep existing dashboards visible and survive restart independently of account connections", () =>
        {
            var store = new SettingsStore(Path.Combine(root, "visibility-settings")); Directory.CreateDirectory(store.DirectoryPath);
            foreach (var legacy in new[] { "{\"Version\":1,\"OpenAiBalanceEnabled\":true,\"QuickStarts\":[]}", "{\"Version\":1,\"Visibility\":null,\"QuickStarts\":[]}" })
            {
                File.WriteAllText(store.FilePath, legacy);
                check(store.Load().Visibility == new DashboardVisibility(), "Missing/null visibility hides existing sections.");
            }
            foreach (var mask in Enumerable.Range(0, 16))
            {
                var visibility = new DashboardVisibility { OpenAi = (mask & 1) != 0, Jev = (mask & 2) != 0, Codex = (mask & 4) != 0, Claude = (mask & 8) != 0 };
                var settings = new Settings { Visibility = visibility, OpenAiBalanceEnabled = true, JevBalanceEnabled = true, ClaudeUsageEnabled = true, Topmost = true,
                    Appearance = new() { Color = "#1468AB" }, QuickStarts = [new() { Name = "Saved", Directory = root, Command = "echo sample" }] };
                store.Save(settings); var restored = new SettingsStore(store.DirectoryPath).Load();
                check(restored.Visibility == visibility && restored.OpenAiBalanceEnabled && restored.JevBalanceEnabled && restored.ClaudeUsageEnabled
                    && restored.Topmost && restored.Appearance == settings.Appearance && restored.QuickStarts.Single().Id == settings.QuickStarts.Single().Id,
                    "Visibility changed a connection or failed to survive restart.");
            }
            return Task.CompletedTask;
        });
        await test("Appearance survives restart and preserves quick starts, pins and account settings", () =>
        {
            var store = new SettingsStore(Path.Combine(root, "appearance-settings"));
            foreach (var mode in Enum.GetValues<BackgroundMode>())
            {
                var settings = new Settings { Topmost = true, OpenAiBalanceEnabled = true, ClaudeUsageEnabled = true,
                    Appearance = new() { Mode = mode, Color = "#1468AB", Transparency = 91, ShowBorder = false },
                    QuickStarts = [new() { Name = "Projekt", Directory = root, Command = "echo sample" }] };
                store.Save(settings); var restored = new SettingsStore(store.DirectoryPath).Load();
                check(restored.Appearance == settings.Appearance && restored.Topmost && restored.OpenAiBalanceEnabled
                    && restored.ClaudeUsageEnabled && restored.QuickStarts.Single().Command == "echo sample", "A saved appearance lost data after restart.");
            }
            return Task.CompletedTask;
        });
        await test("Old settings load Acrylic defaults, invalid color/opacity are normalized and corrupt JSON remains intact", () =>
        {
            var store = new SettingsStore(Path.Combine(root, "appearance-migration")); Directory.CreateDirectory(store.DirectoryPath);
            File.WriteAllText(store.FilePath, """{"Version":1,"Topmost":true,"QuickStarts":[]}""");
            check(store.Load().Appearance == new AppearanceSettings() && store.Load().Topmost && !store.Load().ClaudeUsageEnabled, "Old settings did not keep defaults.");
            var settings = new Settings { Appearance = new() { Color = "broken", Transparency = 800, Mode = (BackgroundMode)55 } };
            store.Save(settings); var loaded = store.Load();
            check(loaded.Appearance is { Mode: BackgroundMode.Acrylic, Color: "#372B53", Transparency: 100 }, "Invalid style input was not normalized.");
            File.WriteAllText(store.FilePath, "{broken");
            try { store.Load(); throw new Exception("Malformed settings accepted."); } catch (JsonException) { }
            check(File.ReadAllText(store.FilePath) == "{broken", "Malformed settings were overwritten.");
            return Task.CompletedTask;
        });
    }
}
