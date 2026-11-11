using System.Windows;
using OtterMonitor.Core;

namespace OtterMonitor.App;

public partial class MainWindow
{
    private void ApplyDashboardVisibility()
    {
        var visible = settings.Visibility;
        BalanceButton.Visibility = visible.OpenAi ? Visibility.Visible : Visibility.Collapsed;
        JevBalanceButton.Visibility = visible.Jev ? Visibility.Visible : Visibility.Collapsed;
        AnthropicBalanceButton.Visibility = visible.Anthropic ? Visibility.Visible : Visibility.Collapsed;
        AnthropicBalanceColumn.Width = visible.Anthropic ? GridLength.Auto : new GridLength(0);
        OpenAiBalanceColumn.Width = visible.OpenAi ? GridLength.Auto : new GridLength(0);
        JevBalanceColumn.Width = visible.Jev ? GridLength.Auto : new GridLength(0);
        BalanceCaptionColumn.Width = new GridLength(1, GridUnitType.Star);
        SizeApiBalances();
        Changed(nameof(OpenAiHeaderLabel)); Changed(nameof(JevHeaderLabel)); Changed(nameof(AnthropicHeaderLabel));
        LayoutAssistantUsage();
    }
    private void SizeApiBalances()
    {
        var visible = settings.Visibility;
        var count = (visible.OpenAi ? 1 : 0) + (visible.Jev ? 1 : 0) + (visible.Anthropic ? 1 : 0);
        var available = count == 0 ? 0 : Math.Max(0, CompactDragArea.ActualWidth - 24) / count;
        OpenAiBalanceColumn.MaxWidth = Math.Min(visible.Jev ? 130 : 220, available);
        JevBalanceColumn.MaxWidth = Math.Min(visible.OpenAi ? 110 : 220, available);
        AnthropicBalanceColumn.MaxWidth = Math.Min(150, available);
    }
    private bool SetDashboardVisibility(DashboardVisibility visible)
    {
        var previous = settings.Visibility;
        settings.Visibility = visible;
        if (!Save()) { settings.Visibility = previous; return false; }
        ApplyDashboardVisibility(); return true;
    }
    public SettingsWindow CreateSettingsWindow()
    {
        var dialog = new SettingsWindow(settings, new(SetDashboardVisibility, SetEditor, SetGeneralOptions, SetAppearance, window =>
    {
        var dialog = CreateAppearanceDialog(); dialog.Owner = window; dialog.ShowDialog();
        window.ApplyAppearance(settings.Appearance);
    }, language =>
    {
        var previous = settings.Language; settings.Language = language == "sv" ? "sv" : "en";
        if (Save()) return true;
        settings.Language = previous; return false;
    }), preview) { Owner = this };
        dialog.ApplyAppearance(settings.Appearance, material.Enabled);
        return dialog;
    }

    private async Task VerifyDashboardSettingsAsync(Action<bool, string> check)
    {
        var original = settings.Visibility;
        var accounts = (settings.OpenAiBalanceEnabled, settings.JevBalanceEnabled, settings.AnthropicBalanceEnabled, settings.ClaudeUsageEnabled);
        try
        {
            foreach (var mask in Enumerable.Range(0, 32))
            {
                var value = new DashboardVisibility { OpenAi = (mask & 1) != 0, Jev = (mask & 2) != 0, Codex = (mask & 4) != 0, Claude = (mask & 8) != 0, Anthropic = (mask & 16) != 0 };
                check(SetDashboardVisibility(value), "Visibility selection applies"); UpdateLayout();
                check(BalanceButton.IsVisible == value.OpenAi && JevBalanceButton.IsVisible == value.Jev && AnthropicBalanceButton.IsVisible == value.Anthropic
                    && CompactCodexUsage.IsVisible == value.Codex && CompactClaudeUsage.IsVisible == value.Claude
                    && UsageSection.IsVisible == (value.Codex || value.Claude), "Visibility combination " + mask + " hides exactly the selected sections");
                check((OpenAiBalanceColumn.Width.Value == 0) == !value.OpenAi && (JevBalanceColumn.Width.Value == 0) == !value.Jev && (AnthropicBalanceColumn.Width.Value == 0) == !value.Anthropic
                    && CompactCaptionArea.ActualWidth >= 24 && DenseScroll.ViewportHeight >= 132, "Hidden sections release space while preserving dragging and three port rows");
                if (value.Codex != value.Claude)
                    check(System.Windows.Controls.Grid.GetColumnSpan(value.Codex ? CompactCodexUsage : CompactClaudeUsage) == 3, "A single visible quota fills the footer");
                check((settings.OpenAiBalanceEnabled, settings.JevBalanceEnabled, settings.AnthropicBalanceEnabled, settings.ClaudeUsageEnabled) == accounts && AssistantRows.Count == 2,
                    "Hiding sections preserves account opt-in and received quota data");
            }
            SetDashboardVisibility(new()); UpdateLayout();
            foreach (var button in new[] { BalanceButton, JevBalanceButton, AnthropicBalanceButton, CompactCodexUsage, CompactClaudeUsage })
            {
                button.ApplyTemplate();
                check(button.Template.FindName("UsageBody", button) is System.Windows.Controls.Border { CornerRadius.TopLeft: 7 }, "All balance and quota hover surfaces have rounded corners");
            }
            check(JevLogo.IsVisible && JevLogo.Data.Bounds.Height > 21 && JevLogo.Data.Bounds.Width > 15, "JEV uses the official TypeSafe vector symbol");
            check(ClaudeLogo.IsVisible && ClaudeLogo.Data.Bounds.Width > 20 && ClaudeLogo.Data.Bounds.Height > 20,
                "Anthropic API uses the Claude starburst beside its credit balance");
            var opened = false; Exception? failure = null;
            var close = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            close.Tick += (_, _) =>
            {
                var window = OwnedWindows.OfType<SettingsWindow>().FirstOrDefault(window => window.IsLoaded);
                if (window is null) return;
                close.Stop(); opened = true;
                try { window.VerifyInputs(check); }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); }
            };
            close.Start();
            appearanceMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single(item => item.Tag is "settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            close.Stop();
            if (failure is not null) throw failure;
            check(opened && settings.Visibility == new DashboardVisibility(), "The settings menu opens the real view and its eye buttons apply changes immediately");
        }
        finally { SetDashboardVisibility(original); UpdateLayout(); }
    }
}
