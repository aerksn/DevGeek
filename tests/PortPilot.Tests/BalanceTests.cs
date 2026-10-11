using System.Text;
using System.Text.Json;
using PortPilot.Core;

internal static class BalanceTests
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check, Func<Func<Task>, Task> throws)
    {
        await test("USD balance accepts zero/negative amounts and rejects invalid currency, precision and sensitive fields", async () =>
        {
            foreach (var amount in new[] { "0.00", "-0.50", "123456.78" })
                check(BalanceMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, state = "ok", amount, currency = "USD" })).DollarAmount is not null, "Valid USD rejected.");
            foreach (var json in new[] { "{}", "null", "{\"schema\":2,\"state\":\"paused\"}", "{\"schema\":1,\"state\":\"ok\",\"amount\":\"$7.20\",\"currency\":\"USD\"}", "{\"schema\":1,\"state\":\"ok\",\"amount\":\"7.20\",\"currency\":\"EUR\"}", "{\"schema\":1,\"state\":\"ok\",\"amount\":\"1,234.56\",\"currency\":\"USD\"}", "{\"schema\":1,\"state\":\"ok\",\"amount\":\"7.200\",\"currency\":\"USD\"}", "{\"schema\":1,\"state\":\"paused\",\"amount\":\"7.20\"}", "{\"schema\":1,\"state\":\"paused\",\"token\":\"fixture-only\"}" })
                await throws(() => { BalanceMessage.Parse(Encoding.UTF8.GetBytes(json)); return Task.CompletedTask; });
        });
        await test("Balance errors preserve amount/time and stale or future readings never appear current", () =>
        {
            var store = new BalanceStore(Path.Combine(root, "balance-state-" + Guid.NewGuid().ToString("N"))); var now = DateTime.UtcNow;
            var first = store.Apply(new(1, "ok", "24.50", "USD"), now);
            check(first.IsFresh(now) && !first.IsFresh(now.AddMinutes(13)) && !first.IsFresh(now.AddSeconds(-1)), "Incorrect freshness.");
            var failed = store.Apply(new(1, "page_closed"), now.AddMinutes(1));
            check(failed.Amount == 24.50m && failed.ObservedUtc == now && !failed.IsFresh(now.AddMinutes(1)) && failed.Label(now).Contains("äldre") && store.Read() == failed, "Error renewed or lost the last balance.");
            return Task.CompletedTask;
        });
        await test("Malformed balance remains intact instead of being overwritten", async () =>
        {
            var directory = Path.Combine(root, "balance-corrupt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var store = new BalanceStore(directory); File.WriteAllText(store.FilePath, "broken-json");
            await throws(() => { store.Apply(new(1, "ok", "24.50", "USD"), DateTime.UtcNow); return Task.CompletedTask; });
            check(File.ReadAllText(store.FilePath) == "broken-json", "Corrupt state overwritten.");
        });
        await test("Background navigation only reads OpenAI Billing and distinguishes sign-in from other pages", () =>
        {
            check(BillingNavigation.IsOverview(BillingNavigation.OverviewUrl + "?fixture=1") && BillingNavigation.IsOverview("https://platform.openai.com/account/billing/"), "Overview route rejected.");
            foreach (var url in new[] { "https://platform.openai.com.evil.test/account/billing", "http://platform.openai.com/account/billing", "https://platform.openai.com:8443/account/billing", "https://platform.openai.com/settings/organization/billing/history" })
                check(!BillingNavigation.IsOverview(url), "Unrelated route accepted: " + url);
            check(BillingNavigation.StateFor("https://auth.openai.com/log-in") == "login_required" && BillingNavigation.StateFor("https://platform.openai.com/home") == "paused", "Sign-in state incorrect.");
            check(BillingNavigation.CanAutoRefresh(BillingNavigation.OverviewUrl, false, false, false) && BillingNavigation.CanAutoRefresh(BillingNavigation.OverviewUrl, true, true, false), "Hidden/minimized overview did not permit refresh.");
            check(!BillingNavigation.CanAutoRefresh(BillingNavigation.OverviewUrl, true, false, false) && !BillingNavigation.CanAutoRefresh(BillingNavigation.OverviewUrl, false, false, true)
                && !BillingNavigation.CanAutoRefresh("https://auth.openai.com/log-in", false, false, false), "Visible window, active navigation or login was refreshed.");
            return Task.CompletedTask;
        });
        await test("Retired balance-mode settings preserve opt-in and existing commands on load and save", () =>
        {
            var store = new SettingsStore(Path.Combine(root, "balance-settings-migration")); Directory.CreateDirectory(store.DirectoryPath);
            foreach (var enabled in new[] { false, true })
            {
                var command = new QuickStart { Name = "Saved project", Directory = @"C:\repos\example", Command = "bun run dev" };
                File.WriteAllText(store.FilePath, JsonSerializer.Serialize(new { Version = 1, OpenAiBalanceEnabled = enabled, OpenAiBackgroundEnabled = !enabled, QuickStarts = new[] { command } }));
                var settings = store.Load();
                check(settings.OpenAiBalanceEnabled == enabled && settings.QuickStarts.Single().Id == command.Id, "Old mode changed opt-in or existing commands.");
                store.Save(settings);
                using var saved = JsonDocument.Parse(File.ReadAllText(store.FilePath));
                check(!saved.RootElement.TryGetProperty("OpenAiBackgroundEnabled", out _) && store.Load().QuickStarts.Single().Command == command.Command, "Retired mode persisted or saved commands changed.");
            }
            return Task.CompletedTask;
        });
    }
}
