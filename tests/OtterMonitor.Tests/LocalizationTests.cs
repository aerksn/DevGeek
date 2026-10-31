using System.Text.Json;
using System.Text.RegularExpressions;
using OtterMonitor.Core;

internal static class LocalizationTests
{
    internal static async Task Run(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("English defaults, Swedish selection and Anthropic visibility survive saving old/new settings", () =>
        {
            var store = new SettingsStore(Path.Combine(root, "language-settings")); Directory.CreateDirectory(store.DirectoryPath);
            File.WriteAllText(store.FilePath, "{\"Version\":1,\"QuickStarts\":[]}");
            var settings = store.Load();
            check(settings.Language == "en" && !settings.AnthropicBalanceEnabled && settings.Visibility.Anthropic, "Old settings do not default to English or enabled visibility.");
            settings.Language = "sv"; settings.AnthropicBalanceEnabled = true; settings.Visibility = settings.Visibility with { Anthropic = false };
            store.Save(settings); var restored = store.Load();
            check(restored.Language == "sv" && restored.AnthropicBalanceEnabled && !restored.Visibility.Anthropic, "Language/visibility/account settings did not survive restart independently.");
            return Task.CompletedTask;
        });
        await test("Both languages translate actions and formatted data without altering provider names or monetary values", () =>
        {
            try
            {
                UiText.SetLanguage("en");
                check(UiText.T("Inställningar") == "Settings" && UiText.T("API-saldon") == "API balances" && UiText.T("Kodassistenter") == "Coding assistants", "English labels missing.");
                var now = DateTime.UtcNow; var balance = new BalanceSnapshot(18.75m, now, now, "ok");
                check(balance.Label(now, "Anthropic API") == "Anthropic API · $18.75" && balance.Hint(now, "Anthropic API").Contains("Anthropic API button"), "English balance changed money/provider or references another account.");
                check(UiText.F("Återanslöt {0} körande snabbstarter.", 3) == "Reconnected 3 running quick starts.", "Formatted translation changed its argument.");
                check(new BalanceSnapshot(null, null, now, "unreadable").Label(now, "Anthropic API") == "Anthropic API · unavailable"
                    && new BalanceSnapshot(null, null, now, "paused").Label(now, "Anthropic API") == "Anthropic API · open Billing", "Unreadable/paused balances still pretend to wait for a value.");
                UiText.SetLanguage("sv"); check(UiText.T("Inställningar") == "Inställningar", "Swedish source unavailable.");
                using var resource = typeof(UiText).Assembly.GetManifestResourceStream("OtterMonitor.Core.Localization.en.json")!;
                var dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(resource)!;
                foreach (var pair in dictionary)
                    check(Regex.Matches(pair.Key, @"\{\d+(?:[^}]*)\}").Select(m => m.Value).SequenceEqual(Regex.Matches(pair.Value, @"\{\d+(?:[^}]*)\}").Select(m => m.Value)), "Translation lost format placeholders: " + pair.Key);
            }
            finally { UiText.SetLanguage("sv"); }
            return Task.CompletedTask;
        });
        await test("Anthropic API navigation and balances stay separate from subscription usage and other providers", () =>
        {
            var now=DateTime.UtcNow; var directory=Path.Combine(root,"anthropic-balance");
            var anthropic=new BalanceStore(directory,anthropic:true); var openai=new BalanceStore(directory); var jev=new BalanceStore(directory,jev:true);
            anthropic.Apply(new(1,"ok","18.75","USD"),now); openai.Apply(new(1,"ok","5.00","USD"),now); jev.Apply(new(1,"ok","8.50","USD"),now);
            var failed=anthropic.Apply(new(1,"login_required"),now.AddMinutes(1));
            check(failed.Amount==18.75m && failed.ObservedUtc==now && !failed.IsFresh(now) && openai.Read()!.Amount==5m && jev.Read()!.Amount==8.50m,"Anthropic failure changed balances or freshness.");
            foreach(var url in new[]{AnthropicBillingNavigation.OverviewUrl,"https://console.anthropic.com/settings/billing/?x=1"})
                check(AnthropicBillingNavigation.IsOverview(url)&&AnthropicBillingNavigation.CanAutoRefresh(url,false,false,false)&&!AnthropicBillingNavigation.CanAutoRefresh(url,true,false,false),"Billing refresh confinement failed.");
            foreach(var url in new[]{"https://claude.ai/settings/usage","http://platform.claude.com/settings/billing","https://platform.claude.com:8443/settings/billing","https://platform.claude.com.evil.test/settings/billing","https://platform.claude.com/settings/billing/history"})
                check(!AnthropicBillingNavigation.IsOverview(url),"Non-Billing origin accepted.");
            check(AnthropicBillingNavigation.StateFor("https://platform.claude.com/login")=="login_required","Login not recognized.");
            return Task.CompletedTask;
        });
    }
}
