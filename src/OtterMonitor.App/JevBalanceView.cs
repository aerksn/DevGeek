using System.IO;
using System.Windows;
using OtterMonitor.Core;

namespace OtterMonitor.App;

public partial class MainWindow
{
    private readonly BalanceStore jevBalanceStore;
    private BalanceSnapshot? jevBalance;
    private BillingBrowser? jevBillingBrowser;
    private string? jevBalanceError;
    public string JevBalanceLabel => (!settings.JevBalanceEnabled ? UiText.T("JEV · koppla")
        : (jevBalanceError is null ? jevBalance : jevBalance is null ? null : jevBalance with { State = "unreadable" })?.Label(DateTime.UtcNow, "JEV") ?? UiText.T("JEV · väntar"))
        + (preview ? UiText.T(" · exempel") : "");
    public string JevBalanceHint => preview ? UiText.T("Designförhandsvisning · exempeldata. Detta är inte ditt JEV-saldo. Kontokopplingen är avstängd i förhandsvisningen.")
        : jevBalanceError ?? (!settings.JevBalanceEnabled ? UiText.T("Klicka på JEV för att logga in och se dina återstående pengar hos TypeSafe.")
        : jevBalance?.Hint(DateTime.UtcNow, "JEV") ?? UiText.T("JEV-kopplingen är aktiverad. Öppna Billing för att läsa saldot."));

    private void JevBalanceChanged() { Changed(nameof(JevBalanceLabel)); Changed(nameof(JevBalanceHint)); }
    private void RefreshJevBalance()
    {
        try { jevBalance = settings.JevBalanceEnabled ? jevBalanceStore.Read() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (jevBalance is not null) jevBalance = jevBalance with { State = "unreadable" };
            jevBalanceError = UiText.T("JEV-saldofilen kunde inte läsas. Öppna JEV Billing och läs av igen.");
        }
        JevBalanceChanged();
    }
    private async Task StartJevBalanceAsync()
    {
        if (preview || closed || !settings.JevBalanceEnabled) return;
        try
        {
            jevBillingBrowser ??= new(store.DirectoryPath, message =>
            {
                if (closed || !settings.JevBalanceEnabled) return false;
                try { jevBalanceStore.Apply(message, DateTime.UtcNow); RefreshJevBalance(); return true; }
                catch (Exception) { jevBalanceError = UiText.T("JEV-saldot kunde inte sparas lokalt. Kontrollera Otter Monitors datamapp."); JevBalanceChanged(); return false; }
            }, message => { if (closed) return; jevBalanceError = message; JevBalanceChanged(); }, jev: true);
            await jevBillingBrowser.StartAsync();
        }
        catch (Exception)
        {
            jevBillingBrowser?.Dispose(); jevBillingBrowser = null;
            jevBalanceError ??= UiText.T("JEV Billing kunde inte startas. Kontrollera WebView2 och försök igen.");
            JevBalanceChanged();
        }
    }
    private async Task OpenJevBillingAsync()
    {
        if (preview) return;
        var previous = settings.JevBalanceEnabled;
        settings.JevBalanceEnabled = true;
        if (!Save()) { settings.JevBalanceEnabled = previous; return; }
        if (!previous)
        {
            try { jevBalanceStore.Apply(new(1, "unreadable"), DateTime.UtcNow); }
            catch (Exception) { jevBalanceError = UiText.T("JEV-saldot kunde inte sparas lokalt. Kontrollera Otter Monitors datamapp."); }
        }
        await StartJevBalanceAsync();
        if (jevBillingBrowser is null) throw new InvalidOperationException(jevBalanceError ?? UiText.T("JEV Billing kunde inte startas."));
        await jevBillingBrowser.ShowAsync(); RefreshJevBalance();
    }
    private async Task RefreshJevBillingAsync()
    {
        if (preview) return;
        if (!settings.JevBalanceEnabled) { await OpenJevBillingAsync(); return; }
        await StartJevBalanceAsync();
        if (jevBillingBrowser is not null) await jevBillingBrowser.RefreshAsync(manual: true);
    }
    private bool DisconnectJevBalance()
    {
        if (preview) return false;
        var previous = settings.JevBalanceEnabled;
        settings.JevBalanceEnabled = false;
        if (!Save()) { settings.JevBalanceEnabled = previous; return false; }
        try
        {
            jevBillingBrowser?.ForgetSavedSession();
            jevBillingBrowser?.Dispose(); jevBillingBrowser = null;
            new BillingSessionStore(store.DirectoryPath, jev: true).Clear();
            jevBalanceError = null;
            RefreshJevBalance(); return true;
        }
        catch (Exception ex) { RefreshJevBalance(); Error(ex); return false; }
    }
    public BalanceDialog CreateJevBalanceDialog() => new(OpenJevBillingAsync, RefreshJevBillingAsync, DisconnectJevBalance, () => JevBalanceHint, preview, "JEV") { Owner = this };
    private void JevBalance_Click(object sender, RoutedEventArgs e) => CreateJevBalanceDialog().ShowDialog();
}
