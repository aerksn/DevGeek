using System.Text.Json;
using PortPilot.Core;

internal static class AppearanceTests
{
    internal static async Task Run(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("Appearance survives restart and preserves quick starts, pins and account settings", () =>
        {
            var store = new SettingsStore(Path.Combine(root, "appearance-settings"));
            foreach (var mode in Enum.GetValues<BackgroundMode>())
            {
                var settings = new Settings { Topmost = true, Compact = true, OpenAiBalanceEnabled = true, OpenAiBackgroundEnabled = true, ClaudeUsageEnabled = true,
                    Appearance = new() { Mode = mode, Color = "#1468AB", Transparency = 91, ShowBorder = false },
                    QuickStarts = [new() { Name = "Projekt", Directory = root, Command = "echo sample" }] };
                store.Save(settings); var restored = new SettingsStore(store.DirectoryPath).Load();
                check(restored.Appearance == settings.Appearance && restored.Topmost && restored.Compact && restored.OpenAiBalanceEnabled
                    && restored.OpenAiBackgroundEnabled && restored.ClaudeUsageEnabled && restored.QuickStarts.Single().Command == "echo sample", "A saved appearance lost data after restart.");
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
