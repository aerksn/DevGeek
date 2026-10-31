using System.Text;
using System.Text.Json;
using OtterMonitor.Core;

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
        await test("JEV and OpenAI balances stay separate and JEV errors never invent a new amount", () =>
        {
            var directory = Path.Combine(root, "jev-balance-isolation");
            var openai = new BalanceStore(directory); var jev = new BalanceStore(directory, jev: true); var now = DateTime.UtcNow;
            openai.Apply(new(1, "ok", "73.21", "USD"), now);
            var balance = jev.Apply(new(1, "ok", "5.00", "USD"), now);
            check(balance.Label(now, "JEV") == "JEV · $5.00" && balance.Hint(now, "JEV").Contains("Available credits"), "JEV label/source incorrect.");
            var failed = jev.Apply(new(1, "login_required"), now.AddMinutes(1));
            check(openai.Read()!.Amount == 73.21m && failed.Amount == 5m && failed.ObservedUtc == now && failed.Label(now, "JEV").Contains("äldre"), "JEV changed another account or renewed an old balance.");
            check(new BalanceSnapshot(null, null, now, "login_required").Label(now, "JEV") == "JEV · logga in", "Missing JEV balance became zero.");
            return Task.CompletedTask;
        });
        await test("JEV navigation confines reads and automatic refreshes to TypeSafe Billing", () =>
        {
            var url = JevBillingNavigation.OverviewUrl;
            check(JevBillingNavigation.IsOverview(url + "/?x=1") && JevBillingNavigation.IsHome("https://console.typesafe.ai/home"), "JEV routes rejected.");
            foreach (var other in new[] { "http://console.typesafe.ai/settings/billing", "https://console.typesafe.ai:8443/settings/billing", "https://console.typesafe.ai.evil.test/settings/billing", "https://console.typesafe.ai/usage", "https://console.typesafe.ai/settings/billing/history" })
                check(!JevBillingNavigation.IsOverview(other), "Unrelated JEV route accepted.");
            check(JevBillingNavigation.StateFor("https://console.typesafe.ai/login?returnTo=/settings/billing") == "login_required"
                && JevBillingNavigation.StateFor("https://console.typesafe.ai/keys") == "paused", "JEV login state incorrect.");
            check(JevBillingNavigation.CanAutoRefresh(url, false, false, false) && JevBillingNavigation.CanAutoRefresh(url, true, true, false)
                && !JevBillingNavigation.CanAutoRefresh(url, true, false, false) && !JevBillingNavigation.CanAutoRefresh(url, false, false, true)
                && !JevBillingNavigation.CanAutoRefresh("https://console.typesafe.ai/login", false, false, false), "JEV refresh interrupts login/visible navigation.");
            return Task.CompletedTask;
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
                check(settings.OpenAiBalanceEnabled == enabled && !settings.JevBalanceEnabled && settings.QuickStarts.Single().Id == command.Id, "Old mode changed opt-in or existing commands.");
                settings.JevBalanceEnabled = true;
                store.Save(settings);
                using var saved = JsonDocument.Parse(File.ReadAllText(store.FilePath));
                check(!saved.RootElement.TryGetProperty("OpenAiBackgroundEnabled", out _) && store.Load().JevBalanceEnabled && store.Load().QuickStarts.Single().Command == command.Command, "JEV opt-in/commands lost or retired mode persisted.");
            }
            return Task.CompletedTask;
        });
    }
}
