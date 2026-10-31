using System.IO;
using System.Windows;
using OtterMonitor.Core;

namespace OtterMonitor.App;

public partial class MainWindow
{
    private readonly BalanceStore anthropicBalanceStore;
    private BalanceSnapshot? anthropicBalance;
    private BillingBrowser? anthropicBillingBrowser;
    private string? anthropicBalanceError;
    public string AnthropicBalanceLabel => (!settings.AnthropicBalanceEnabled ? UiText.T("Anthropic API · koppla")
        : (anthropicBalanceError is null ? anthropicBalance : anthropicBalance is null ? null : anthropicBalance with { State = "unreadable" })?.Label(DateTime.UtcNow, "Anthropic API") ?? UiText.T("Anthropic API · väntar"))
        + (preview ? UiText.T(" · exempel") : "");
    public string AnthropicBalanceHint => preview ? UiText.T("Designförhandsvisning · exempeldata. Detta är inte ditt Anthropic API-saldo. Kontokopplingen är avstängd i förhandsvisningen.")
        : anthropicBalanceError ?? (!settings.AnthropicBalanceEnabled ? UiText.T("Klicka på Anthropic API för att logga in och se dina återstående pengar hos Anthropic.")
        : anthropicBalance?.Hint(DateTime.UtcNow, "Anthropic API") ?? UiText.T("Anthropic API-kopplingen är aktiverad. Öppna Billing för att läsa saldot."));

    private void AnthropicBalanceChanged() { Changed(nameof(AnthropicBalanceLabel)); Changed(nameof(AnthropicHeaderLabel)); Changed(nameof(AnthropicBalanceHint)); }
    private void RefreshAnthropicBalance()
    {
        try { anthropicBalance = settings.AnthropicBalanceEnabled ? anthropicBalanceStore.Read() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (anthropicBalance is not null) anthropicBalance = anthropicBalance with { State = "unreadable" };
            anthropicBalanceError = UiText.T("Anthropic API-saldofilen kunde inte läsas. Öppna Anthropic API Billing och läs av igen.");
        }
        AnthropicBalanceChanged();
    }
    private async Task StartAnthropicBalanceAsync()
    {
        if (preview || closed || !settings.AnthropicBalanceEnabled) return;
        try
        {
            anthropicBillingBrowser ??= new(store.DirectoryPath, message =>
            {
                if (closed || !settings.AnthropicBalanceEnabled) return false;
                try { anthropicBalanceStore.Apply(message, DateTime.UtcNow); RefreshAnthropicBalance(); return true; }
                catch (Exception) { anthropicBalanceError = UiText.T("Anthropic API-saldot kunde inte sparas lokalt. Kontrollera Otter Monitors datamapp."); AnthropicBalanceChanged(); return false; }
            }, message => { if (closed) return; anthropicBalanceError = message; AnthropicBalanceChanged(); }, anthropic: true);
            await anthropicBillingBrowser.StartAsync();
        }
        catch (Exception)
        {
            anthropicBillingBrowser?.Dispose(); anthropicBillingBrowser = null;
            anthropicBalanceError ??= UiText.T("Anthropic API Billing kunde inte startas. Kontrollera WebView2 och försök igen.");
            AnthropicBalanceChanged();
        }
    }
    private async Task OpenAnthropicBillingAsync()
    {
        if (preview) return;
        var previous = settings.AnthropicBalanceEnabled;
        settings.AnthropicBalanceEnabled = true;
        if (!Save()) { settings.AnthropicBalanceEnabled = previous; return; }
        if (!previous)
        {
            try { anthropicBalanceStore.Apply(new(1, "unreadable"), DateTime.UtcNow); }
            catch (Exception) { anthropicBalanceError = UiText.T("Anthropic API-saldot kunde inte sparas lokalt. Kontrollera Otter Monitors datamapp."); }
        }
        await StartAnthropicBalanceAsync();
        if (anthropicBillingBrowser is null) throw new InvalidOperationException(anthropicBalanceError ?? UiText.T("Anthropic API Billing kunde inte startas."));
        await anthropicBillingBrowser.ShowAsync(); RefreshAnthropicBalance();
    }
    private async Task RefreshAnthropicBillingAsync()
    {
        if (preview) return;
        if (!settings.AnthropicBalanceEnabled) { await OpenAnthropicBillingAsync(); return; }
        await StartAnthropicBalanceAsync();
        if (anthropicBillingBrowser is not null) await anthropicBillingBrowser.RefreshAsync(manual: true);
    }
    private bool DisconnectAnthropicBalance()
    {
        if (preview) return false;
        var previous = settings.AnthropicBalanceEnabled;
        settings.AnthropicBalanceEnabled = false;
        if (!Save()) { settings.AnthropicBalanceEnabled = previous; return false; }
        try
        {
            anthropicBillingBrowser?.DisconnectAccount();
            anthropicBillingBrowser?.Dispose(); anthropicBillingBrowser = null;
            new BillingSessionStore(store.DirectoryPath, anthropic: true).Clear();
            anthropicBalanceError = null;
            RefreshAnthropicBalance(); return true;
        }
        catch (Exception ex) { RefreshAnthropicBalance(); Error(ex); return false; }
    }
    public BalanceDialog CreateAnthropicBalanceDialog() => new(OpenAnthropicBillingAsync, RefreshAnthropicBillingAsync, DisconnectAnthropicBalance, () => AnthropicBalanceHint, preview, "Anthropic API") { Owner = this };
    private void AnthropicBalance_Click(object sender, RoutedEventArgs e) => CreateAnthropicBalanceDialog().ShowDialog();
}
