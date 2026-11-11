using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using OtterMonitor.Core;

namespace OtterMonitor.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly SettingsStore store;
    private readonly Settings settings;
    private readonly BalanceStore balanceStore;
    private BalanceSnapshot? balance;
    private string? balanceError;
    private BillingBrowser? billingBrowser;
    private readonly bool preview;
    private readonly WindowMaterial material;
    private readonly LaunchManager launches;
    private readonly HashSet<string> portActions = [];
    private readonly PortService portService = new();
    private readonly DockerService dockerService = new();
    private readonly ResourceMonitor resourceMonitor = new();
    private readonly ServiceProbe serviceProbe = new();
    private readonly CancellationTokenSource telemetryStop = new();
    private Dictionary<string, ResourceReading> processResources = [];
    private bool probing;
    private bool captureLive;
    private DateTime probesChecked = DateTime.MinValue;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private List<PortEntry> nativePorts = [];
    private DockerSnapshot docker = new([], 0, UiText.T("Docker · kontrollerar…"));
    private DateTime dockerChecked = DateTime.MinValue;
    private bool refreshing, dockerRefreshing, ready, closed;
    private bool developmentOnly = true;
    private bool scanned;
    private Dictionary<string, PortGroup> groupCache = [];
    public ObservableCollection<PortGroup> Ports { get; } = [];
    public ObservableCollection<QuickStart> QuickStarts { get; } = [];
    /// <summary>Quick starts that own no listed port: shown as their own rows under the ports.</summary>
    public ObservableCollection<QuickStart> LaunchRows { get; } = [];
    private readonly Dictionary<(string Group, Guid Run), bool> jobMembership = [];
    private readonly Dictionary<string, QuickStart> previewLaunches = [];
    private Editor? editor;
    public Editor CurrentEditor => editor ??= Editor.Resolve(settings.Editor);
    public string EditorActionLabel => UiText.T("Öppna i ") + CurrentEditor.Name;
    private string AccountBalanceLabel => !settings.OpenAiBalanceEnabled ? UiText.T("OpenAI · koppla") : (balanceError is null ? balance : balance is null ? null : balance with { State = "unreadable" })?.Label(DateTime.UtcNow) ?? UiText.T("OpenAI · väntar");
    public string BalanceLabel => AccountBalanceLabel + (preview ? UiText.T(" · exempel") : "");
    public string BalanceHint => preview ? UiText.T("Designförhandsvisning med exempeldata. Detta är inte ditt kontos saldo. Öppna app/OtterMonitor.exe för din sparade saldokoppling. Klicka på loggan eller beloppet för information.")
        : balanceError ?? (!settings.OpenAiBalanceEnabled ? UiText.T("Öppna Billing via OpenAI-loggan och logga in för att se ditt USD-saldo.\nOtter Monitor kan läsa av i bakgrunden medan Chrome är stängt. Dra i den tomma ytan bredvid saldot för att flytta fönstret.")
        : (balance?.Hint(DateTime.UtcNow) ?? UiText.T("Kopplingen är aktiverad. Öppna Billing via OpenAI-loggan för en avläsning.")) + UiText.T("\nOtter Monitors egen Billing-session. Öppna den via OpenAI-loggan om inloggningen behöver förnyas."));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new(name));
        if (name == nameof(BalanceLabel)) { PropertyChanged?.Invoke(this, new(nameof(OpenAiHeaderLabel))); PropertyChanged?.Invoke(this, new(nameof(OpenAiBalanceIsStale))); }
        if (name == nameof(JevBalanceLabel)) { PropertyChanged?.Invoke(this, new(nameof(JevHeaderLabel))); PropertyChanged?.Invoke(this, new(nameof(JevBalanceIsStale))); }
        if (name == nameof(AnthropicBalanceLabel)) { PropertyChanged?.Invoke(this, new(nameof(AnthropicHeaderLabel))); PropertyChanged?.Invoke(this, new(nameof(AnthropicBalanceIsStale))); }
    }
    private static string HeaderBalance(string label, string provider) => label.StartsWith(provider + " · ", StringComparison.Ordinal)
        ? label[(provider.Length + 3)..].Replace(UiText.T(" · äldre"), "", StringComparison.Ordinal) : label;
    private static bool IsBalanceStale(BalanceSnapshot? snapshot, string? error) => snapshot?.Amount is not null && (error is not null || !snapshot.IsFresh(DateTime.UtcNow));
    public bool OpenAiBalanceIsStale => settings.OpenAiBalanceEnabled && IsBalanceStale(balance, balanceError);
    public bool JevBalanceIsStale => settings.JevBalanceEnabled && IsBalanceStale(jevBalance, jevBalanceError);
    public bool AnthropicBalanceIsStale => settings.AnthropicBalanceEnabled && IsBalanceStale(anthropicBalance, anthropicBalanceError);
    public string OpenAiHeaderLabel => HeaderBalance(BalanceLabel, "OpenAI");
    public string JevHeaderLabel => HeaderBalance(JevBalanceLabel, "JEV");
    public string AnthropicHeaderLabel => HeaderBalance(AnthropicBalanceLabel, "Anthropic API");

    public MainWindow(SettingsStore store, Settings settings, bool preview)
    {
        this.store = store; this.settings = settings; this.preview = preview;
        balanceStore = new(store.DirectoryPath);
        jevBalanceStore = new(store.DirectoryPath, jev: true);
        anthropicBalanceStore = new(store.DirectoryPath, anthropic: true);
        launches = new(store.DirectoryPath, shadowCopyHost: !File.Exists(Path.Combine(AppContext.BaseDirectory, "OtterMonitor.dll")));
        InitializeComponent(); DataContext = this;
        CompactDragArea.SizeChanged += (_, _) => SizeApiBalances();
        SizeChanged += (_, _) => { Changed(nameof(OpenAiHeaderLabel)); Changed(nameof(JevHeaderLabel)); Changed(nameof(AnthropicHeaderLabel)); };
        material = new(this, enabled: !preview, hasTintLayer: true);
        InitializeAppearance();
        EmptyMascot.Source = IconFrame(64);
        OtterMenuLogo.Source = IconFrame(32);
        foreach (var entry in settings.QuickStarts) QuickStarts.Add(entry);
        Topmost = !preview && settings.Topmost;
        UpdatePin(); ConfigureCompactWindow();
        ApplyDashboardVisibility();
        ready = true; ApplyFilter();
        timer.Tick += async (_, _) =>
        {
            RefreshBalance();
            RefreshJevBalance(); RefreshAnthropicBalance();
            _ = RefreshAssistantUsageAsync();
            foreach (var entry in QuickStarts) entry.Refresh();
            TrackLaunchNotices(); TrackQuotaNotices();
            await RefreshPortsAsync();
            if (DateTime.UtcNow - dockerChecked > TimeSpan.FromSeconds(10)) _ = RefreshDockerAsync();
        };
        Loaded += async (_, _) => { if (!preview) { RefreshBalance(); RefreshJevBalance(); RefreshAnthropicBalance(); _ = RefreshAssistantUsageAsync(); if (settings.OpenAiBalanceEnabled) _ = StartBackgroundBalanceAsync(); if (settings.JevBalanceEnabled) _ = StartJevBalanceAsync(); if (settings.AnthropicBalanceEnabled) _ = StartAnthropicBalanceAsync(); RestoreLaunches(); StartTray(); timer.Start(); _ = RefreshDockerAsync(); await RefreshPortsAsync(); } };
        Closing += OnClosing;
        // Logging off or shutting down must exit, never hide to the tray.
        if (Application.Current is { } app) app.SessionEnding += (_, _) => exitRequested = quietExit = true;
        Closed += (_, _) => { closed = true; timer.Stop(); telemetryStop.Cancel(); serviceProbe.Dispose(); appearanceMenu?.Dispose(); billingBrowser?.Dispose(); jevBillingBrowser?.Dispose(); anthropicBillingBrowser?.Dispose(); claudeBrowser?.Dispose(); foreach (var entry in QuickStarts) entry.Session?.Dispose(); tray?.Dispose(); tray = null; };
    }

    private void RefreshBalance()
    {
        try { balance = settings.OpenAiBalanceEnabled ? balanceStore.Read() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (balance is not null) balance = balance with { State = "unreadable" };
            balanceError = UiText.T("Saldofilen kunde inte läsas. Öppna kopplingen via OpenAI-loggan och läs av Billing igen.");
        }
        Changed(nameof(BalanceLabel)); Changed(nameof(BalanceHint));
    }
    private bool DisconnectBalance()
    {
        if (preview) return false;
        var previous = settings.OpenAiBalanceEnabled;
        var persisted = false;
        try
        {
            settings.OpenAiBalanceEnabled = false;
            if (!Save()) { settings.OpenAiBalanceEnabled = previous; return false; }
            persisted = true;
            billingBrowser?.ForgetSavedSession();
            new BillingSessionStore(store.DirectoryPath).Clear();
            billingBrowser?.Dispose(); billingBrowser = null; balanceError = null;
            RefreshBalance(); return true;
        }
        catch (Exception ex) { if (!persisted) settings.OpenAiBalanceEnabled = previous; RefreshBalance(); Error(ex); return false; }
    }
    private async Task StartBackgroundBalanceAsync()
    {
        if (preview || closed || !settings.OpenAiBalanceEnabled) return;
        try
        {
            billingBrowser ??= new(store.DirectoryPath, message =>
            {
                if (closed || !settings.OpenAiBalanceEnabled) return false;
                try { balanceStore.Apply(message, DateTime.UtcNow); RefreshBalance(); return true; }
                catch (Exception) { balanceError = UiText.T("Saldot kunde inte sparas lokalt. Kontrollera Otter Monitors datamapp."); Changed(nameof(BalanceHint)); Changed(nameof(BalanceLabel)); return false; }
            }, message => { if (closed) return; balanceError = message; Changed(nameof(BalanceHint)); Changed(nameof(BalanceLabel)); });
            await billingBrowser.StartAsync();
        }
        catch (Exception) { billingBrowser?.Dispose(); billingBrowser = null; Changed(nameof(BalanceHint)); }
    }
    private async Task OpenBackgroundBillingAsync()
    {
        if (preview) return;
        var previous = settings.OpenAiBalanceEnabled;
        settings.OpenAiBalanceEnabled = true;
        if (!Save()) { settings.OpenAiBalanceEnabled = previous; return; }
        if (!previous)
        {
            try { balanceStore.Apply(new(1, "unreadable"), DateTime.UtcNow); }
            catch (Exception) { balanceError = UiText.T("Saldot kunde inte sparas lokalt. Kontrollera Otter Monitors datamapp."); }
        }
        await StartBackgroundBalanceAsync();
        if (billingBrowser is null) throw new InvalidOperationException(balanceError ?? UiText.T("Billing-sessionen kunde inte startas."));
        await billingBrowser.ShowAsync();
        RefreshBalance();
    }
    private async Task RefreshBackgroundBillingAsync()
    {
        if (!settings.OpenAiBalanceEnabled) { await OpenBackgroundBillingAsync(); return; }
        await StartBackgroundBalanceAsync(); if (billingBrowser is not null) await billingBrowser.RefreshAsync(manual: true);
    }
    public BalanceDialog CreateBalanceDialog() => new(OpenBackgroundBillingAsync, RefreshBackgroundBillingAsync, DisconnectBalance, () => BalanceHint, preview) { Owner = this };
    private void Balance_Click(object sender, RoutedEventArgs e) => CreateBalanceDialog().ShowDialog();

    private void RestoreLaunches()
    {
        foreach (var entry in QuickStarts)
        {
            try { entry.Session = launches.Restore(entry); }
            catch (Exception ex) { entry.RecoveryError = ex.Message; SetStatus(UiText.T("Kunde inte återansluta ") + entry.Name + ": " + ex.Message, problem: true); }
            entry.Refresh();
        }
        var running = QuickStarts.Count(q => q.Session?.IsRunning == true);
        if (running > 0) SetStatus(UiText.F("\u00C5teransl\u00F6t {0} k\u00F6rande snabbstarter.", running));
    }

    private IEnumerable<PortEntry> CombinedPorts()
    {
        // Replace only Docker's own forwarding listeners. Unrelated listeners on the same number remain visible.
        return nativePorts.Where(p => !(p.Name.StartsWith("com.docker", StringComparison.OrdinalIgnoreCase) || p.Name is "vpnkit" or "wslrelay" or "System") || !docker.Ports.Any(d => d.Port == p.Port && d.Protocol == "tcp"))
            .Concat(docker.Ports).OrderBy(p => p.Port).ThenBy(p => p.Name).ThenBy(p => p.Address);
    }

    private async Task RefreshPortsAsync()
    {
        if (refreshing || closed) return;
        refreshing = true;
        try
        {
            var result = await Task.Run(() => { var ports = portService.Scan(); return (Ports: ports, Resources: resourceMonitor.SampleProcesses(ports)); });
            if (closed) return;
            nativePorts = result.Ports; processResources = result.Resources; scanned = true;
            ApplyFilter();
            _ = RefreshProbesAsync();
        }
        catch (Exception ex) { SetStatus(UiText.T("Portavläsning misslyckades: ") + ex.Message, problem: true); }
        finally { refreshing = false; }
    }

    private async Task RefreshDockerAsync()
    {
        if (dockerRefreshing || closed) return;
        dockerRefreshing = true;
        try
        {
            var result = await dockerService.ScanAsync();
            if (closed) return;
            docker = result;
            ApplyFilter();
            _ = RefreshProbesAsync();
        }
        catch (Exception ex) { docker = new([], 0, "Docker · " + ex.Message); ApplyFilter(); }
        finally { dockerChecked = DateTime.UtcNow; dockerRefreshing = false; }
    }

    public async Task RefreshAllForCaptureAsync() { captureLive = true; await RefreshPortsAsync(); await RefreshDockerAsync(); await RefreshProbesAsync(force: true, capture: true); }

    private async Task RefreshProbesAsync(bool force = false, bool capture = false)
    {
        if (closed || probing || preview && !capture || !force && DateTime.UtcNow - probesChecked < TimeSpan.FromSeconds(10)) return;
        var targets = Ports.Select(group => (group.Key, Entries: group.Entries.ToArray())).ToArray();
        if (targets.Length == 0) return;
        probing = true;
        try
        {
            var results = await Task.WhenAll(targets.Select(async target => (target.Key, target.Entries, Reading: await serviceProbe.MeasureAsync(target.Entries, telemetryStop.Token))));
            if (closed) return;
            foreach (var result in results)
                if (groupCache.TryGetValue(result.Key, out var group) && group.Entries.SequenceEqual(result.Entries)) { group.SetProbe(result.Reading); TrackServiceNotices(group, result.Reading); }
            UpdateTray();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) foreach (var target in targets) if (groupCache.TryGetValue(target.Key, out var group)) group.SetProbe(new(UiText.T("Ej kontrollerad"), ServiceTone.Neutral, null, UiText.T("Statuskontrollen kunde inte slutföras. Försök uppdatera igen."), DateTime.UtcNow)); }
        finally { probesChecked = DateTime.UtcNow; probing = false; }
    }

    private void ApplyFilter()
    {
        if (!ready) return;
        var query = FilterBox.Text.Trim();
        var current = PortGroup.Create(CombinedPorts());
        var next = new Dictionary<string, PortGroup>();
        foreach (var group in current)
        {
            if (groupCache.TryGetValue(group.Key, out var existing)) { existing.Update(group); next.Add(group.Key, existing); }
            else next.Add(group.Key, group);
            next[group.Key].IsStopping = portActions.Contains(group.Key);
            if (!preview || captureLive)
            {
                var resources = group.IsDocker ? docker.Resources?.GetValueOrDefault(group.Key) : processResources.GetValueOrDefault(group.Key);
                next[group.Key].SetResources(resources ?? new(null, null, group.Entries[0].StartedUtc, DateTime.UtcNow, group.IsDocker));
            }
            else next[group.Key].RefreshTelemetry();
        }
        groupCache = next;
        LinkLaunches(next.Values);
        // A search looks through every listening port, also outside the development filter.
        var filtered = next.Values.Where(p => (query.Length > 0 || !developmentOnly || p.IsDevelopment) && p.Matches(query)).ToList();
        Sync(Ports, filtered);
        Sync(LaunchRows, QuickStarts.Where(q => !q.OwnsPorts && q.Matches(query)).ToList());
        UpdateEmptyState(query);
        EmptyLaunches.Visibility = QuickStarts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTray();
    }

    // Keep the same row objects (and their focus and hover state) during polling and search changes.
    private static void Sync<T>(ObservableCollection<T> target, List<T> wanted)
    {
        for (int i = target.Count - 1; i >= 0; i--) if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            var index = target.IndexOf(wanted[i]);
            if (index < 0) target.Insert(i, wanted[i]);
            else if (index != i) target.Move(index, i);
        }
    }

    /// <summary>Marks each process whose Windows job belongs to a running quick start. Docker containers are never claimed.</summary>
    private void LinkLaunches(IEnumerable<PortGroup> groups)
    {
        var running = QuickStarts.Where(q => q.Session is { } session && IsAlive(session)).ToList();
        var owners = new HashSet<QuickStart>();
        foreach (var group in groups)
        {
            QuickStart? owner = null;
            if (preview) previewLaunches.TryGetValue(group.Key, out owner);
            else if (!group.IsDocker && group.Entries[0].StartedUtc is not null)
                owner = running.FirstOrDefault(q => OwnsProcess(q.Session!, group));
            group.Launch = owner;
            if (owner is not null) owners.Add(owner);
        }
        foreach (var entry in QuickStarts) entry.OwnsPorts = owners.Contains(entry);
    }

    private bool OwnsProcess(LaunchSession session, PortGroup group)
    {
        var key = (group.Key, session.Record.RunId);
        if (jobMembership.TryGetValue(key, out var member)) return member;
        if (jobMembership.Count > 512) jobMembership.Clear();
        return jobMembership[key] = session.Contains(group.Entries[0].Pid);
    }

    private static bool IsAlive(LaunchSession session)
    {
        try { return session.IsRunning; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private void Error(Exception ex) => Dialogs.Info(this, UiText.T("Det gick inte att slutföra"), ex.Message);
    private void DevOnly_Click(object sender, RoutedEventArgs e) { developmentOnly = ((MenuItem)sender).IsChecked; ApplyFilter(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (preview) return; _ = RefreshDockerAsync(); await RefreshPortsAsync(); }

    private async void StopPort_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as PortGroup;
        if (preview || group is null || !group.CanStop || group.StopTarget is not { } entry) return;
        // A process started by a quick start is stopped together with its whole job, so a watcher cannot restart it.
        if (group.Launch is { IsRunning: true } launch) { await StopLaunchAsync(launch); return; }
        var description = entry.IsDocker
            ? UiText.F("Stoppa containern {0}?\n\nPortar: {1}\n\nAlla portar fr\u00E5n den h\u00E4r containern st\u00E4ngs. Docker f\u00E5r upp till 10 sekunder att avsluta den.", entry.Name, group.PortSummary)
            : UiText.F("Avsluta {0} ({1}, PID {2}) och dess underprocesser?\n\nPortar: {3}\n\nAlla portar som processen anv\u00E4nder st\u00E4ngs. P\u00E5g\u00E5ende arbete i processen avbryts. En extern watcher kan starta den igen.", entry.DisplayName, entry.Name, entry.Pid, group.PortSummary);
        if (!Dialogs.Confirm(this, UiText.T("Stoppa ") + entry.DisplayName, description, UiText.T("Stoppa"))) return;
        portActions.Add(group.Key); group.IsStopping = true;
        try
        {
            if (entry.IsDocker) { await DockerService.StopAsync(entry); await RefreshDockerAsync(); }
            else await portService.StopAsync(entry);
            SetStatus(UiText.F("Stoppade {0}. Portlistan uppdateras.", entry.DisplayName));
            await RefreshPortsAsync();
        }
        catch (Exception ex) { Error(ex); }
        finally { portActions.Remove(group.Key); group.IsStopping = false; ApplyFilter(); }
    }

    private async void RestartContainer(PortGroup group)
    {
        if (preview || !group.CanRestart || group.StopTarget is not { } entry) return;
        if (!Dialogs.Confirm(this, UiText.T("Starta om ") + entry.Name,
            UiText.F("Starta om containern {0}?\n\nPortar: {1}\n\nContainern stoppas och startas igen. Anslutningar bryts under omstarten.", entry.Name, group.PortSummary), UiText.T("Starta om"))) return;
        portActions.Add(group.Key); group.IsStopping = true;
        try
        {
            SetStatus(UiText.T("Startar om ") + entry.Name + "…");
            await DockerService.RestartAsync(entry);
            SetStatus(UiText.T("Startade om ") + entry.Name + ".");
        }
        catch (Exception ex) { Error(ex); }
        finally
        {
            await RefreshDockerAsync(); await RefreshPortsAsync();
            portActions.Remove(group.Key); group.IsStopping = false; ApplyFilter();
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (preview) return;
        appearanceMenu.IsOpen = false;
        var dialog = CreateNewQuickStartDialog();
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        QuickStarts.Add(dialog.Result);
        if (!Save()) QuickStarts.Remove(dialog.Result);
        else SetStatus(UiText.F("Lade till {0}. Starta den med \u25B6 i listan.", dialog.Result.Name));
        ApplyFilter();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (preview || RowLaunch(sender) is not { } entry) return;
        if (!entry.CanEdit) { Dialogs.Info(this, UiText.T("Snabbstarten är upptagen"), UiText.T("Återanslut och stoppa snabbstarten innan du redigerar den.")); return; }
        var dialog = new QuickStartDialog(entry) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var index = QuickStarts.IndexOf(entry);
        if (dialog.DeleteRequested) QuickStarts.Remove(entry);
        else if (dialog.Result is not null) { dialog.Result.Session = entry.Session; QuickStarts[index] = dialog.Result; }
        if (!Save()) { if (dialog.DeleteRequested) QuickStarts.Insert(index, entry); else QuickStarts[index] = entry; }
        else if (dialog.DeleteRequested) entry.Session?.Dispose();
        ApplyFilter();
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (preview || RowLaunch(sender) is not { CanAct: true } entry) return;
        if (entry.RecoveryError is null && entry.IsRunning) { await StopLaunchAsync(entry); return; }
        entry.Operation = UiText.T("Startar…"); entry.IsBusy = true;
        try
        {
            if (entry.RecoveryError is not null)
            {
                entry.Session = launches.Restore(entry); entry.RecoveryError = null;
                SetStatus(entry.Session?.IsRunning == true ? UiText.T("Återanslöt ") + entry.Name + "." : UiText.T("Körningen är avslutad. Du kan starta igen."));
                return;
            }
            entry.Session?.Dispose(); entry.Session = null;
            // Save the ID before launch as well, including the first default quick start.
            if (!Save()) return;
            entry.Session = launches.Start(entry);
            SetStatus(UiText.T("Startade ") + entry.Name + UiText.T(". Raden flyttar upp bland portarna när servern lyssnar."));
            entry.Refresh(); await RefreshPortsAsync();
        }
        catch (Exception ex) { Error(ex); }
        finally { entry.IsBusy = false; ApplyFilter(); }
    }

    private async Task StopLaunchAsync(QuickStart entry)
    {
        if (entry.IsBusy || entry.Session is not { } session) return;
        if (!Dialogs.Confirm(this, UiText.T("Stoppa ") + entry.Name, UiText.T("Kommandot och alla processer som det startade avslutas. Pågående arbete i processerna avbryts."), UiText.T("Stoppa"))) return;
        entry.Operation = UiText.T("Stoppar…"); entry.IsBusy = true;
        try
        {
            await session.StopAsync(); SetStatus(UiText.T("Stoppade ") + entry.Name + ".");
            entry.Refresh(); await RefreshPortsAsync();
        }
        catch (Exception ex) { Error(ex); }
        finally { entry.IsBusy = false; ApplyFilter(); }
    }

    private void RestartLaunch_Click(object sender, RoutedEventArgs e) { if (RowLaunch(sender) is { } entry) RestartLaunch(entry); }

    private async void RestartLaunch(QuickStart entry)
    {
        if (preview || !entry.CanRestart) return;
        if (!Dialogs.Confirm(this, UiText.T("Starta om ") + entry.Name,
            UiText.T("Kommandot och dess underprocesser avslutas. Sedan körs samma kommando igen i samma arbetsmapp."), UiText.T("Starta om"))) return;
        entry.Operation = UiText.T("Startar om…"); entry.IsBusy = true;
        try
        {
            await launches.RestartAsync(entry);
            SetStatus(UiText.T("Startade om ") + entry.Name + ".");
            await RefreshPortsAsync();
        }
        catch (Exception ex) { Error(ex); }
        finally { entry.IsBusy = false; ApplyFilter(); }
    }

    private void Log_Click(object sender, RoutedEventArgs e) { if (!preview && RowLaunch(sender) is { } entry) ShowLaunchLog(entry); }

    private void ShowLaunchLog(QuickStart entry)
    {
        if (entry.Session is null) { Dialogs.Info(this, UiText.T("Ingen logg ännu"), UiText.T("Starta kommandot för att se dess utdata här.")); return; }
        ShowLog("launch|" + entry.Id, () => LogWindow.ForQuickStart(entry, RestartLaunch, StopLaunchAsync));
    }

    // One log window per quick start or container; asking again brings the open one forward.
    private void ShowLog(string key, Func<LogWindow> create)
    {
        if (OwnedWindows.OfType<LogWindow>().FirstOrDefault(window => Equals(window.Tag, key)) is { } open) { open.Activate(); return; }
        var window = create(); window.Tag = key; window.Owner = this; window.Show();
    }

    private bool Save()
    {
        if (preview) return true;
        settings.QuickStarts = QuickStarts.ToList(); settings.Topmost = Topmost;
        try { store.Save(settings); return true; } catch (Exception ex) { Error(ex); return false; }
    }
    private void Pin_Click(object sender, RoutedEventArgs e) { Topmost = !Topmost; UpdatePin(); Save(); }
    private void UpdatePin()
    {
        DropdownButton.Foreground = (System.Windows.Media.Brush)FindResource(Topmost ? "AccentBrush" : "MutedBrush");
        UpdateAppearanceMenu();
    }
    private void ConfigureCompactWindow()
    {
        // Fixed layout lives in XAML; this restores the size after a manual resize.
        SizeToContent = SizeToContent.Manual;
        MinWidth = 300; MinHeight = 196;
        Width = 360; Height = 196;
        SizeToContent = SizeToContent.Height; LayoutAssistantUsage(); ApplyFilter();
    }

    private void QuickStarts_Click(object sender, RoutedEventArgs e)
    {
        appearanceMenu.IsOpen = false;
        var parent = (Panel)LaunchSection.Parent;
        parent.Children.Remove(LaunchSection);
        LaunchSection.Visibility = Visibility.Visible;
        var dialog = new Window { Title = UiText.T("Otter Monitor · Snabbstarter"), Owner = this, Width = 560, MinWidth = 420,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = LaunchSection, DataContext = this, Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush") };
        try { dialog.ShowDialog(); }
        finally { dialog.Content = null; LaunchSection.Visibility = Visibility.Collapsed; parent.Children.Add(LaunchSection); }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private bool closingBilling, billingCloseReady;
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (preview) return;
        if (closingBilling) { e.Cancel = true; return; }
        if (billingCloseReady) return;
        if (HideToTray(e)) return;
        if (QuickStarts.Any(q => q.IsBusy) || portActions.Count > 0) { e.Cancel = true; SetStatus(UiText.T("Vänta tills den pågående åtgärden är klar innan du stänger."), problem: true); return; }
        if (!quietExit && QuickStarts.Any(q => q.Session?.IsRunning == true) && !Dialogs.Confirm(this, UiText.T("Stäng Otter Monitor?"), UiText.T("Dina startade processer fortsätter att köras. Otter Monitor återansluter snabbstarterna nästa gång appen öppnas.\n\nVill du stoppa en hel snabbstart, välj Avbryt och Stoppa först."), UiText.T("Stäng"))) { e.Cancel = true; return; }
        Save();
        if (billingBrowser is not null || jevBillingBrowser is not null || anthropicBillingBrowser is not null || claudeBrowser is not null)
        {
            e.Cancel = true; closingBilling = true; IsEnabled = false; timer.Stop();
            try { await Task.WhenAll(billingBrowser?.FlushSessionAsync() ?? Task.CompletedTask, jevBillingBrowser?.FlushSessionAsync() ?? Task.CompletedTask, anthropicBillingBrowser?.FlushSessionAsync() ?? Task.CompletedTask, claudeBrowser?.FlushSessionAsync() ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (Exception) { /* The periodic checkpoint remains available if shutdown storage is unavailable. */ }
            finally { billingCloseReady = true; closingBilling = false; _ = Dispatcher.BeginInvoke(new Action(Close)); }
        }
    }

    public void LoadPreview(bool empty)
    {
        settings.OpenAiBalanceEnabled = true;
        balance = new(24.50m, DateTime.UtcNow, DateTime.UtcNow, "ok");
        settings.AnthropicBalanceEnabled = true;
        anthropicBalance = new(18.75m, DateTime.UtcNow, DateTime.UtcNow, "ok");
        AnthropicBalanceChanged();
        settings.JevBalanceEnabled = true;
        jevBalance = new(8.50m, DateTime.UtcNow, DateTime.UtcNow, "ok");
        JevBalanceChanged();
        Changed(nameof(BalanceLabel)); Changed(nameof(BalanceHint));
        scanned = true;
        docker = new([], 0, UiText.T("Docker · förhandsvisning"));
        if (!empty)
        {
            nativePorts = [new(3000, "127.0.0.1", 18240, "bun", "bun", DateTime.UtcNow, ProjectName: "demo-app", ProjectDirectory: @"C:\repos\demo-app"), new(5173, "127.0.0.1", 24516, "node", "node", DateTime.UtcNow, ProjectName: "demo-web", ProjectDirectory: @"C:\repos\demo-web"), new(8080, "0.0.0.0", 20104, "dotnet", ".NET", DateTime.UtcNow)];
            var previewPorts = new List<PortEntry> { new(5432, "0.0.0.0", 0, "demo-postgres", "Docker", null, ContainerId: "preview", DockerEndpoint: "npipe://./pipe/docker_engine", ContainerPort: 5432) };
            previewPorts.AddRange(PreviewContainerBindings());
            docker = new(previewPorts, 2, UiText.T("Docker Desktop · 2 containrar · förhandsvisning"));
            QuickStarts.Add(new() { Name = UiText.T("Starta dev miljö"), Directory = @"C:\repos\demo-app", Command = "bun run dev" });
            // Preview has no real jobs; link the demo-app process to a quick start the way job membership would.
            var linked = new QuickStart { Name = "demo-app dev", Directory = @"C:\repos\demo-app", Command = "bun run dev" };
            QuickStarts.Add(linked);
            previewLaunches[PortGroup.OwnerKey(nativePorts[0])] = linked;
        }
        ApplyFilter();
        if (!empty) SetPreviewTelemetry();
        if (!empty) LoadUsagePreview();
    }

    private void SetPreviewTelemetry()
    {
        foreach (var group in groupCache.Values)
        {
            var first = group.Entries[0];
            group.SetResources(new(first.Port == 5432 ? 0.3 : 2.1, first.Port == 5432 ? 82_000_000 : 280_000_000, DateTime.UtcNow.AddHours(-2).AddMinutes(-14), DateTime.UtcNow, first.IsDocker));
            var tcp = first.Port == 5432;
            group.SetProbe(new(tcp ? "TCP" : "HTTP 200", ServiceTone.Good, tcp ? 1.2 : 24.2, UiText.T("Förhandsvisning · Exempeldata"), DateTime.UtcNow));
        }
    }

    public void EnableGlassPreview() { material.Enabled = true; ApplyAppearance(settings.Appearance); }
    public string DescribeMaterial() => material.Describe();

    private static IEnumerable<PortEntry> PreviewContainerBindings() =>
        from port in new[] { 3210, 3211, 11434 }
        from address in new[] { "0.0.0.0", "::" }
        select new PortEntry(port, address, 0, "demo-backend-1", "Docker", null,
            ContainerId: "preview-backend", DockerEndpoint: "npipe://./pipe/docker_engine", ContainerPort: port);

    public void LoadGroupExamplePreview()
    {
        if (!preview) return;
        nativePorts = [];
        docker = new(PreviewContainerBindings().ToList(), 1, UiText.T("Docker · 1 container · förhandsvisning"));
        ApplyFilter();
        SetPreviewTelemetry();
    }

    public async Task<string> VerifyPreviewControlsAsync()
    {
        if (!preview) throw new InvalidOperationException("UI-kontroll kräver förhandsvisning.");
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add("PASS " + name); }
        Check(Ports.Count == 5, "Five owner groups render");
        Check(new System.Windows.Media.Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var glyphs)
            && glyphs.CharacterToGlyphMap.ContainsKey('å') && glyphs.FontUri.ToString().Contains("bricolage", StringComparison.OrdinalIgnoreCase), "Bundled Bricolage is used with real Swedish glyphs, without requiring a Windows font installation");
        Check(ReferenceEquals(DensePortList.ItemsSource, Ports) && Ports.Single(p => p.Name == "demo-backend-1").PortCount == 3, "Compact view groups all ports by their owning process/container");
        var backend = Ports.Single(p => p.Name == "demo-backend-1");
        Check(backend.PortCount == 3 && backend.Entries.Count == 6, "Six IPv4/IPv6 bindings form one group with three ports");
        Check(backend.ServiceIcon == "docker" && Ports.Single(p => p.Name == "demo-web").ServiceIcon == "node", "Runtime labels map to vector icons while names remain project names");
        Check(backend.ResourceSummary.Contains("RAM 280 MB") && backend.ResourceSummary.Contains("CPU") && backend.ResourceSummary.Contains("2h 14m") && backend.StatusText.Contains("HTTP 200"), "Resource, uptime and status readings render on the same owner group");
        Check(backend.CanRestart && !Ports.First(p => p.Name == "demo-web").CanRestart, "Restart offered only for Docker owner groups");
        portActions.Add(backend.Key); ApplyFilter();
        Check(!backend.CanRestart && !backend.CanStop, "Container actions stay disabled while restart is in progress");
        portActions.Remove(backend.Key); ApplyFilter();
        var quickStart = QuickStarts[0]; quickStart.IsBusy = true;
        Check(!quickStart.CanAct && !quickStart.CanRestart && !quickStart.CanEdit, "Quick start disables overlapping actions");
        quickStart.IsBusy = false;
        Check(quickStart.CanAct && quickStart.CanEdit && !quickStart.CanRestart, "Idle quick start enables start and edit only");
        ApplyFilter();
        Check(ReferenceEquals(backend, Ports.Single(p => p.Key == backend.Key)), "Rows keep their group objects across refreshes");
        nativePorts.Add(new(443, "127.0.0.1", 9999, "other-service", "TCP", null));
        ApplyFilter(); Check(Ports.Count == 5, "Development filter excludes unknown service");
        var devItem = appearanceMenu.Items.OfType<MenuItem>().Single(item => item.Tag is "development");
        devItem.IsChecked = false; devItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Check(Ports.Count == 6, "Dropdown all-port filter includes unknown service");
        nativePorts.RemoveAll(p => p.Name == "other-service"); devItem.IsChecked = true; devItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var samePortStart = DateTime.UtcNow;
        nativePorts.Add(new(3000, "::", 55555, "bun", "bun", samePortStart));
        nativePorts.Add(new(3000, "127.0.0.1", 55555, "bun", "bun", samePortStart));
        ApplyFilter();
        Check(Ports.Count(p => p.Entries.Any(entry => entry.Port == 3000)) == 2, "Compact grouping keeps separate process identities on the same port");
        nativePorts.RemoveAll(p => p.Pid == 55555); ApplyFilter();
        Pin_Click(this, new()); Check(Topmost, "Always-on-top control enables Window.Topmost");
        Pin_Click(this, new()); Check(!Topmost, "Always-on-top control releases Window.Topmost");
        ConfigureCompactWindow(); UpdateLayout();
        Check(Width == 360 && SizeToContent == SizeToContent.Height && DensePorts.Visibility == Visibility.Visible && LaunchSection.Visibility == Visibility.Collapsed, "Compact view only shows the dense list and small toolbar");
        Check(DensePortList.Padding == new Thickness(0) && DenseScroll.Padding == new Thickness(0) && ToolbarRow.Height.Value == 36, "Compact list retains the original single-row balance toolbar");
        Check(DenseScroll.MinHeight == 156 && DenseScroll.ViewportHeight >= 156 && MinHeight >= 196 && DenseScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "The port list reserves at least three dense rows and enable scrolling");
        Check(CompactDragArea.Visibility == Visibility.Visible && WindowChrome.GetIsHitTestVisibleInChrome(Toolbar) && WindowChrome.GetIsHitTestVisibleInChrome(DropdownButton), "The complete toolbar receives pointer input for click-or-drag gestures");
        int HitTest(FrameworkElement element)
        {
            var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            var coordinates = unchecked((int)(ushort)(short)Math.Round(point.X) | ((int)(short)Math.Round(point.Y) << 16));
            return SendPreviewWindowMessage(new WindowInteropHelper(this).Handle, 0x0084, IntPtr.Zero, new IntPtr(coordinates)).ToInt32();
        }
        Check(HitTest(CompactCaptionArea) == 1 && HitTest(DropdownButton) == 1 && HitTest(BalanceButton) == 1 && HitTest(CompactBalanceText) == 1 && HitTest(OpenAiLogo) == 1, "Buttons and blank header space share the managed click-or-drag gesture");
        Check(OpenAiLogo.Data.Bounds.Width > 20 && OpenAiLogo.IsVisible && RowVisuals<System.Windows.Shapes.Path>(BalanceButton).Contains(OpenAiLogo), "OpenAI balance uses the complete Blossom vector in its interactive button");
        Check(CompactBalanceText.Text == OpenAiHeaderLabel && OpenAiHeaderLabel.Contains("$24.50") && BalanceLabel.EndsWith(" · exempel") && CompactCaptionArea.ActualWidth >= 24, "Compact balances retain their amounts with provider icons");
        foreach (var width in new[] { 360d, 680d })
        {
            Width = width; UpdateLayout();
            Check(!OpenAiHeaderLabel.Contains("OpenAI") && !JevHeaderLabel.Contains("JEV") && !AnthropicHeaderLabel.Contains("Anthropic")
                && OpenAiLogo.IsVisible && JevLogo.IsVisible && AnthropicHeaderLabel.Contains("$"), "API header uses icons and amounts at width " + width);
        }
        Width = 360; UpdateLayout();
        Check(HitTest(JevBalanceButton) == 1 && HitTest(CompactJevBalanceText) == 1 && CompactJevBalanceText.Text == JevHeaderLabel && JevHeaderLabel.Contains("$8.50"), "JEV balance has its own clickable icon and amount beside OpenAI");
        Check(HitTest(OtterMenuButton) == 1 && OtterMenuButton.TranslatePoint(new Point(0, 0), this).X < BalanceButton.TranslatePoint(new Point(0, 0), this).X,
            "The original single toolbar places the Otter menu to the left of the API balances");
        VerifyToolbarDrag(Check);
        var balanceOpened = false;
        var closeBalance = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        closeBalance.Tick += (_, _) =>
        {
            var dialog = OwnedWindows.OfType<BalanceDialog>().FirstOrDefault();
            if (dialog is not { IsLoaded: true }) return;
            balanceOpened = true; closeBalance.Stop(); dialog.Close();
        };
        closeBalance.Start(); BalanceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); closeBalance.Stop();
        Check(balanceOpened, "Compact balance click opens the real owned dialog rather than silently ignoring preview clicks");
        var jevOpened = false;
        var closeJev = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        closeJev.Tick += (_, _) =>
        {
            var dialog = OwnedWindows.OfType<BalanceDialog>().FirstOrDefault(window => window.Title == "JEV-saldo");
            if (dialog is not { IsLoaded: true }) return;
            jevOpened = RowVisuals<Button>(dialog).Where(button => button.Content?.ToString() is "Logga in / Öppna Billing" or "Uppdatera saldo nu" or "Koppla från").All(button => !button.IsEnabled);
            closeJev.Stop(); dialog.Close();
        };
        closeJev.Start(); JevBalanceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); closeJev.Stop();
        Check(jevOpened && jevBillingBrowser is null, "JEV preview opens its own dialog with account actions disabled and no real session");
        var stops = RowVisuals<Button>(DensePorts).Where(button => button.Name == "DenseStop").ToArray();
        Check(stops.Length == Ports.Count && stops.All(button => button.ActualWidth == 28 && button.ActualHeight == 24 && button.Padding == new Thickness(6, 3, 6, 3)), "Every compact stop target has a 28 by 24 button with reduced padding");
        balance = balance! with { State = "page_closed" }; Changed(nameof(BalanceLabel)); Changed(nameof(BalanceHint));
        Check(BalanceLabel.Contains("äldre") && balance.Hint(DateTime.UtcNow).Contains("stängd"), "Closing Billing retains the last amount but visibly marks it older");
        UpdateLayout();
        Check(OpenAiStaleIndicator.IsVisible && !OpenAiHeaderLabel.Contains("äldre") && OpenAiHeaderLabel.Contains("$24.50"), "An older balance keeps its amount and shows a small indicator instead of status text");
        balance = new(0m, DateTime.UtcNow, DateTime.UtcNow, "ok"); Changed(nameof(BalanceLabel));
        Check(BalanceLabel == "OpenAI · $0.00 · exempel", "Zero balance is displayed as a real value within the clearly marked preview");
        UpdateLayout();
        Check(!OpenAiStaleIndicator.IsVisible && OpenAiHeaderLabel.Contains("$0.00"), "A fresh zero balance clears the older indicator");
        balance = new(24.50m, DateTime.UtcNow, DateTime.UtcNow, "ok"); Changed(nameof(BalanceLabel)); Changed(nameof(BalanceHint));
        var isolatedBalancePath = Path.Combine(Path.GetTempPath(), "OtterMonitor-balance-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedBalancePath);
        var accountWindow = new MainWindow(new SettingsStore(isolatedBalancePath), new Settings { QuickStarts = [], OpenAiBalanceEnabled = true }, false);
        try
        {
            accountWindow.RefreshBalance();
            Check(accountWindow.Width == 360 && accountWindow.MinWidth == 300, "A production window opens in the compact layout");
            Check(accountWindow.BalanceLabel == "OpenAI · väntar", "Production mode with no stored balance never displays the preview amount");
            new BalanceStore(isolatedBalancePath).Apply(new(1, "ok", "73.21", "USD"), DateTime.UtcNow);
            accountWindow.RefreshBalance();
            Check(accountWindow.BalanceLabel == "OpenAI · $73.21", "Production balance binding reads its own persisted USD value without an example suffix");
            accountWindow.RefreshJevBalance();
            Check(accountWindow.JevBalanceLabel == "JEV · koppla", "Existing accounts do not opt in to JEV automatically");
            accountWindow.settings.JevBalanceEnabled = true; accountWindow.RefreshJevBalance();
            Check(accountWindow.JevBalanceLabel == "JEV · väntar", "Connected JEV with no balance never shows zero or preview data");
            new BalanceStore(isolatedBalancePath, jev: true).Apply(new(1, "ok", "5.00", "USD"), DateTime.UtcNow);
            accountWindow.RefreshJevBalance();
            Check(accountWindow.JevBalanceLabel == "JEV · $5.00" && accountWindow.BalanceLabel == "OpenAI · $73.21", "The two production header bindings read separate account balances");
            Check(accountWindow.DisconnectJevBalance() && accountWindow.JevBalanceLabel == "JEV · koppla" && accountWindow.settings.OpenAiBalanceEnabled, "Disconnecting JEV leaves OpenAI connected");
        }
        finally
        {
            accountWindow.Close();
            foreach (var fixtureFile in new[] { "settings.json", "settings.json.bak", "openai-balance.json", "jev-balance.json" }) File.Delete(Path.Combine(isolatedBalancePath, fixtureFile));
            Directory.Delete(isolatedBalancePath);
        }
        nativePorts.AddRange(Enumerable.Range(12000, 40).Select((port, i) => new PortEntry(port, "127.0.0.1", 55600 + i, "node", "node", samePortStart)));
        ApplyFilter(); UpdateLayout();
        Check(DenseScroll.ExtentHeight > DenseScroll.ViewportHeight && ActualHeight <= MaxHeight, "Long dense lists scroll within the compact window height limit");
        DenseScroll.ScrollToBottom(); UpdateLayout();
        Check(DenseScroll.VerticalOffset > 0, "The scrollbar moves to later groups without growing the window");
        DenseScroll.ScrollToTop();
        nativePorts.RemoveAll(p => p.Pid >= 55600 && p.Pid < 55640); ApplyFilter();
        ConfigureCompactWindow(); UpdateLayout();
        Check(Width == 360 && DensePorts.IsVisible
            && !appearanceMenu.Items.OfType<MenuItem>().Any(item => item.Tag is "view"), "Only the compact layout exists and the dropdown offers no view switch");
        Check(appearanceMenu.Items.OfType<MenuItem>().Any(item => item.Tag is "quickstarts"), "Quick starts remain accessible from the compact dropdown");
        var launchesOpened = false;
        var closeLaunches = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        closeLaunches.Tick += (_, _) =>
        {
            var dialog = OwnedWindows.OfType<Window>().FirstOrDefault(window => window.Title == "Otter Monitor · Snabbstarter" && window.IsLoaded);
            if (dialog is null) return;
            launchesOpened = ReferenceEquals(dialog.Content, LaunchSection) && LaunchSection.IsVisible && Width == 360
                && RowVisuals<TextBlock>(LaunchSection).Any(text => text.Text == QuickStarts[0].Name);
            closeLaunches.Stop(); dialog.Close();
        };
        closeLaunches.Start(); QuickStarts_Click(this, new()); closeLaunches.Stop(); UpdateLayout();
        Check(launchesOpened && LaunchSection.Visibility == Visibility.Collapsed && DensePorts.IsVisible && ReferenceEquals(LaunchSection.DataContext, this),
            "Quick starts open in an owned dialog, keep their bindings and restore the compact window on close");
        VerifyProjectRows(Check);
        VerifyLaunchRowsAndFilter(Check);
        VerifyFeedback(Check);
        await VerifyQuickStartPicker(Check);
        await VerifyLogWindow(Check);
        material.VerifyModernBlur(Check);
        VerifyAssistantUsage(Check);
        await VerifyDashboardSettingsAsync(Check);
        await VerifyAppearanceAsync(Check);
        return string.Join(Environment.NewLine, checks);
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendPreviewWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
