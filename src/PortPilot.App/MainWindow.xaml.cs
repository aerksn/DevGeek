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
using PortPilot.Core;

namespace PortPilot.App;

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
    private DockerSnapshot docker = new([], 0, "Docker · kontrollerar…");
    private DateTime dockerChecked = DateTime.MinValue;
    private bool refreshing, dockerRefreshing, ready, closed;
    private bool developmentOnly = true;
    private string status = "Läser aktiva portar…";
    private DateTime actionUntil;
    private Dictionary<string, PortGroup> groupCache = [];
    public ObservableCollection<PortGroup> Ports { get; } = [];
    public ObservableCollection<QuickStart> QuickStarts { get; } = [];
    public string Status { get => status; set { status = value; Changed(); } }
    public string DockerStatus => docker.Status;
    public int PortCount => CombinedPorts().Select(p => (p.Port, p.Protocol)).Distinct().Count();
    public int ContainerCount => docker.ContainerCount;
    public int LaunchCount => QuickStarts.Count;
    private string AccountBalanceLabel => !settings.OpenAiBalanceEnabled ? "OpenAI · koppla" : (balanceError is null ? balance : balance is null ? null : balance with { State = "unreadable" })?.Label(DateTime.UtcNow) ?? "OpenAI · väntar";
    public string BalanceLabel => AccountBalanceLabel + (preview ? " · exempel" : "");
    public string BalanceHint => preview ? "Designförhandsvisning med exempeldata. Detta är inte ditt kontos saldo. Öppna app/DevGeek.exe för din sparade saldokoppling. Klicka på loggan eller beloppet för information."
        : balanceError ?? (!settings.OpenAiBalanceEnabled ? "Öppna Billing via OpenAI-loggan och logga in för att se ditt USD-saldo.\nDevGeek kan läsa av i bakgrunden medan Chrome är stängt. Dra i den tomma ytan bredvid saldot för att flytta fönstret."
        : (balance?.Hint(DateTime.UtcNow) ?? "Kopplingen är aktiverad. Öppna Billing via OpenAI-loggan för en avläsning.") + "\nDevGeeks egen Billing-session. Öppna den via OpenAI-loggan om inloggningen behöver förnyas.");
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    public MainWindow(SettingsStore store, Settings settings, bool preview)
    {
        this.store = store; this.settings = settings; this.preview = preview;
        balanceStore = new(store.DirectoryPath);
        launches = new(store.DirectoryPath, shadowCopyHost: !File.Exists(Path.Combine(AppContext.BaseDirectory, "DevGeek.dll")));
        InitializeComponent(); DataContext = this;
        material = new(this, enabled: !preview, hasTintLayer: true);
        InitializeAppearance();
        foreach (var entry in settings.QuickStarts) QuickStarts.Add(entry);
        Topmost = !preview && settings.Topmost;
        UpdatePin(); ConfigureCompactWindow();
        ready = true; ApplyFilter();
        timer.Tick += async (_, _) =>
        {
            RefreshBalance();
            _ = RefreshAssistantUsageAsync();
            foreach (var entry in QuickStarts) entry.Refresh();
            await RefreshPortsAsync();
            if (DateTime.UtcNow - dockerChecked > TimeSpan.FromSeconds(10)) _ = RefreshDockerAsync();
        };
        Loaded += async (_, _) => { if (!preview) { RefreshBalance(); _ = RefreshAssistantUsageAsync(); if (settings.OpenAiBalanceEnabled) _ = StartBackgroundBalanceAsync(); RestoreLaunches(); timer.Start(); _ = RefreshDockerAsync(); await RefreshPortsAsync(); } };
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; timer.Stop(); telemetryStop.Cancel(); serviceProbe.Dispose(); billingBrowser?.Dispose(); claudeBrowser?.Dispose(); foreach (var entry in QuickStarts) entry.Session?.Dispose(); };
    }

    private void RefreshBalance()
    {
        try { balance = settings.OpenAiBalanceEnabled ? balanceStore.Read() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (balance is not null) balance = balance with { State = "unreadable" };
            balanceError = "Saldofilen kunde inte läsas. Öppna kopplingen via OpenAI-loggan och läs av Billing igen.";
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
                catch (Exception) { balanceError = "Saldot kunde inte sparas lokalt. Kontrollera DevGeeks datamapp."; Changed(nameof(BalanceHint)); Changed(nameof(BalanceLabel)); return false; }
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
            catch (Exception) { balanceError = "Saldot kunde inte sparas lokalt. Kontrollera DevGeeks datamapp."; }
        }
        await StartBackgroundBalanceAsync();
        if (billingBrowser is null) throw new InvalidOperationException(balanceError ?? "Billing-sessionen kunde inte startas.");
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
            catch (Exception ex) { entry.RecoveryError = ex.Message; SetStatus("Kunde inte återansluta " + entry.Name + ": " + ex.Message); }
            entry.Refresh();
        }
        var running = QuickStarts.Count(q => q.Session?.IsRunning == true);
        if (running > 0) SetStatus($"Återanslöt {running} körande snabbstarter.");
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
            nativePorts = result.Ports; processResources = result.Resources;
            ApplyFilter(); Changed(nameof(PortCount));
            _ = RefreshProbesAsync();
            if (DateTime.UtcNow > actionUntil) Status = $"Uppdaterad {DateTime.Now:HH:mm:ss}  ·  Automatiskt varannan sekund";
        }
        catch (Exception ex) { SetStatus("Portavläsning misslyckades: " + ex.Message); }
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
            Changed(nameof(DockerStatus)); Changed(nameof(ContainerCount)); Changed(nameof(PortCount)); ApplyFilter();
            _ = RefreshProbesAsync();
        }
        catch (Exception ex) { docker = new([], 0, "Docker · " + ex.Message); Changed(nameof(DockerStatus)); ApplyFilter(); }
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
                if (groupCache.TryGetValue(result.Key, out var group) && group.Entries.SequenceEqual(result.Entries)) group.SetProbe(result.Reading);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) foreach (var target in targets) if (groupCache.TryGetValue(target.Key, out var group)) group.SetProbe(new("Ej kontrollerad", ServiceTone.Neutral, null, "Statuskontrollen kunde inte slutföras. Försök uppdatera igen.", DateTime.UtcNow)); }
        finally { probesChecked = DateTime.UtcNow; probing = false; }
    }

    private void ApplyFilter()
    {
        if (!ready) return;
        const string query = "";
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
        var filtered = next.Values.Where(p => (!developmentOnly || p.IsDevelopment) && p.Matches(query)).ToList();
        // Keep the same group objects and expanded state during polling and search changes.
        for (int i = Ports.Count - 1; i >= 0; i--) if (!filtered.Contains(Ports[i])) Ports.RemoveAt(i);
        for (int i = 0; i < filtered.Count; i++)
        {
            var index = Ports.IndexOf(filtered[i]);
            if (index < 0) Ports.Insert(i, filtered[i]);
            else if (index != i) Ports.Move(index, i);
        }
        DenseEmptyPorts.Visibility = Ports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLaunches.Visibility = QuickStarts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message) { actionUntil = DateTime.UtcNow.AddSeconds(15); Status = message; }
    private void Error(Exception ex) { SetStatus(ex.Message); Dialogs.Info(this, "Det gick inte att slutföra", ex.Message); }
    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void DevOnly_Click(object sender, RoutedEventArgs e) { developmentOnly = ((MenuItem)sender).IsChecked; ApplyFilter(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (preview) return; _ = RefreshDockerAsync(); await RefreshPortsAsync(); }

    private async void StopPort_Click(object sender, RoutedEventArgs e)
    {
        var group = (sender as Button)?.DataContext as PortGroup;
        if (preview || group is null || !group.CanStop || group.StopTarget is not { } entry) return;
        var description = entry.IsDocker
            ? $"Stoppa containern {entry.Name}?\n\nPortar: {group.PortSummary}\n\nAlla portar från den här containern stängs. Docker får upp till 10 sekunder att avsluta den."
            : $"Avsluta {entry.DisplayName} ({entry.Name}, PID {entry.Pid}) och dess underprocesser?\n\nPortar: {group.PortSummary}\n\nAlla portar som processen använder stängs. Pågående arbete i processen avbryts. En extern watcher kan starta den igen.";
        if (!Dialogs.Confirm(this, "Stoppa " + entry.DisplayName, description, "Stoppa")) return;
        portActions.Add(group.Key); group.IsStopping = true;
        try
        {
            if (entry.IsDocker) { await DockerService.StopAsync(entry); await RefreshDockerAsync(); }
            else await portService.StopAsync(entry);
            SetStatus($"Stoppade {entry.DisplayName}. Portlistan uppdateras.");
            await RefreshPortsAsync();
        }
        catch (Exception ex) { Error(ex); }
        finally { portActions.Remove(group.Key); group.IsStopping = false; ApplyFilter(); }
    }

    private async void RestartPort_Click(object sender, RoutedEventArgs e)
    {
        if (preview || sender is not Button { DataContext: PortGroup group } || !group.CanRestart || group.StopTarget is not { } entry) return;
        if (!Dialogs.Confirm(this, "Starta om " + entry.Name,
            $"Starta om containern {entry.Name}?\n\nPortar: {group.PortSummary}\n\nContainern stoppas och startas igen. Anslutningar bryts under omstarten.", "Starta om")) return;
        portActions.Add(group.Key); group.IsStopping = true;
        try
        {
            SetStatus("Startar om " + entry.Name + "…");
            await DockerService.RestartAsync(entry);
            SetStatus("Startade om " + entry.Name + ".");
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
        var dialog = new QuickStartDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        QuickStarts.Add(dialog.Result);
        if (!Save()) QuickStarts.Remove(dialog.Result);
        Changed(nameof(LaunchCount)); ApplyFilter();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (preview || sender is not Button { DataContext: QuickStart entry }) return;
        if (!entry.CanEdit) { Dialogs.Info(this, "Snabbstarten är upptagen", "Återanslut och stoppa snabbstarten innan du redigerar den."); return; }
        var dialog = new QuickStartDialog(entry) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var index = QuickStarts.IndexOf(entry);
        if (dialog.DeleteRequested) QuickStarts.Remove(entry);
        else if (dialog.Result is not null) { dialog.Result.Session = entry.Session; QuickStarts[index] = dialog.Result; }
        if (!Save()) { if (dialog.DeleteRequested) QuickStarts.Insert(index, entry); else QuickStarts[index] = entry; }
        else if (dialog.DeleteRequested) entry.Session?.Dispose();
        Changed(nameof(LaunchCount)); ApplyFilter();
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (preview || sender is not Button { DataContext: QuickStart entry } || !entry.CanAct) return;
        entry.Operation = "Arbetar…"; entry.IsBusy = true;
        try
        {
            if (entry.RecoveryError is not null)
            {
                entry.Session = launches.Restore(entry); entry.RecoveryError = null;
                SetStatus(entry.Session?.IsRunning == true ? "Återanslöt " + entry.Name + "." : "Körningen är avslutad. Du kan starta igen.");
                return;
            }
            if (entry.Session?.IsRunning == true)
            {
                if (!Dialogs.Confirm(this, "Stoppa " + entry.Name, "Kommandot och alla processer som det startade avslutas. Pågående arbete i processerna avbryts.", "Stoppa")) return;
                entry.Operation = "Stoppar…"; entry.Refresh();
                await entry.Session.StopAsync(); SetStatus("Stoppade " + entry.Name + ".");
            }
            else
            {
                entry.Session?.Dispose(); entry.Session = null;
                // Save the ID before launch as well, including the first default quick start.
                if (!Save()) return;
                entry.Session = launches.Start(entry);
                SetStatus("Startade " + entry.Name + ". Öppna Logg för utdata.");
            }
            entry.Refresh(); await RefreshPortsAsync();
        }
        catch (Exception ex) { Error(ex); }
        finally { entry.IsBusy = false; }
    }

    private async void RestartLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (preview || sender is not Button { DataContext: QuickStart entry } || !entry.CanRestart) return;
        if (!Dialogs.Confirm(this, "Starta om " + entry.Name,
            "Kommandot och dess underprocesser avslutas. Sedan körs samma kommando igen i samma arbetsmapp.", "Starta om")) return;
        entry.Operation = "Startar om…"; entry.IsBusy = true;
        try
        {
            await launches.RestartAsync(entry);
            SetStatus("Startade om " + entry.Name + ".");
            await RefreshPortsAsync();
        }
        catch (Exception ex) { Error(ex); }
        finally { entry.IsBusy = false; }
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (preview || sender is not Button { DataContext: QuickStart entry }) return;
        if (entry.Session is null) { Dialogs.Info(this, "Ingen logg ännu", "Starta kommandot för att se dess utdata här."); return; }
        new LogWindow(entry) { Owner = this }.Show();
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
        settings.Compact = true; // Ignore the retired view setting in older saved configurations.
        SizeToContent = SizeToContent.Manual;
        MinWidth = 300; MinHeight = 166; MaxHeight = 342;
        Width = 360; Height = 166;
        DensePorts.Visibility = Visibility.Visible;
        ToolbarRow.Height = new GridLength(30);
        BodyRow.Height = new GridLength(1, GridUnitType.Star); BodyRow.MinHeight = 0;
        Toolbar.Margin = new Thickness(4, 0, 4, 0); ToolbarGap.Width = new GridLength(4);
        CompactDragArea.Visibility = Visibility.Visible;
        foreach (var button in new[] { DropdownButton, MinimizeButton, CloseButton }) button.Style = (Style)FindResource("DenseIconButton");
        DropdownButton.Margin = MinimizeButton.Margin = new Thickness(0, 0, 2, 0);
        if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = 28;
        SizeToContent = SizeToContent.Height; LayoutAssistantUsage(); ApplyFilter();
    }

    private void QuickStarts_Click(object sender, RoutedEventArgs e)
    {
        appearanceMenu.IsOpen = false;
        var parent = (Panel)LaunchSection.Parent;
        parent.Children.Remove(LaunchSection);
        LaunchSection.Visibility = Visibility.Visible;
        var dialog = new Window { Title = "DevGeek · Snabbstarter", Owner = this, Width = 560, MinWidth = 420,
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
        if (QuickStarts.Any(q => q.IsBusy) || portActions.Count > 0) { e.Cancel = true; SetStatus("Vänta tills den pågående åtgärden är klar innan du stänger."); return; }
        if (QuickStarts.Any(q => q.Session?.IsRunning == true) && !Dialogs.Confirm(this, "Stäng DevGeek?", "Dina startade processer fortsätter att köras. DevGeek återansluter snabbstarterna nästa gång appen öppnas.\n\nVill du stoppa en hel snabbstart, välj Avbryt och Stoppa först.", "Stäng")) { e.Cancel = true; return; }
        Save();
        if (billingBrowser is not null || claudeBrowser is not null)
        {
            e.Cancel = true; closingBilling = true; IsEnabled = false; timer.Stop();
            try { await Task.WhenAll(billingBrowser?.FlushSessionAsync() ?? Task.CompletedTask, claudeBrowser?.FlushSessionAsync() ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (Exception) { /* The periodic checkpoint remains available if shutdown storage is unavailable. */ }
            finally { billingCloseReady = true; closingBilling = false; _ = Dispatcher.BeginInvoke(new Action(Close)); }
        }
    }

    public void LoadPreview(bool empty)
    {
        settings.OpenAiBalanceEnabled = true;
        balance = new(24.50m, DateTime.UtcNow, DateTime.UtcNow, "ok");
        Changed(nameof(BalanceLabel)); Changed(nameof(BalanceHint));
        Status = "Förhandsvisning · Exempeldata, inga kommandon körs";
        docker = new([], 0, "Docker · förhandsvisning");
        if (!empty)
        {
            nativePorts = [new(3000, "127.0.0.1", 18240, "bun", "bun", DateTime.UtcNow, ProjectName: "demo-app", ProjectDirectory: @"C:\repos\demo-app"), new(5173, "127.0.0.1", 24516, "node", "node", DateTime.UtcNow, ProjectName: "demo-web", ProjectDirectory: @"C:\repos\demo-web"), new(8080, "0.0.0.0", 20104, "dotnet", ".NET", DateTime.UtcNow)];
            var previewPorts = new List<PortEntry> { new(5432, "0.0.0.0", 0, "demo-postgres", "Docker", null, ContainerId: "preview", DockerEndpoint: "npipe://./pipe/docker_engine", ContainerPort: 5432) };
            previewPorts.AddRange(PreviewContainerBindings());
            docker = new(previewPorts, 2, "Docker Desktop · 2 containrar · förhandsvisning");
            QuickStarts.Add(new() { Name = "Starta dev miljö", Directory = @"C:\repos\demo-app", Command = "bun run dev" });
        }
        ApplyFilter(); Changed(nameof(PortCount)); Changed(nameof(ContainerCount)); Changed(nameof(LaunchCount)); Changed(nameof(DockerStatus));
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
            group.SetProbe(new(tcp ? "TCP" : "HTTP 200", ServiceTone.Good, tcp ? 1.2 : 24.2, "Förhandsvisning · Exempeldata", DateTime.UtcNow));
        }
    }

    public void EnableGlassPreview() { material.Enabled = true; material.Apply(); }
    public string DescribeMaterial() => material.Describe();

    private static IEnumerable<PortEntry> PreviewContainerBindings() =>
        from port in new[] { 3210, 3211, 11434 }
        from address in new[] { "0.0.0.0", "::" }
        select new PortEntry(port, address, 0, "demo-backend-1", "Docker", null,
            ContainerId: "preview-backend", DockerEndpoint: "npipe://./pipe/docker_engine", ContainerPort: port);

    public void LoadGroupExamplePreview(bool expanded)
    {
        if (!preview) return;
        nativePorts = [];
        docker = new(PreviewContainerBindings().ToList(), 1, "Docker · 1 container · förhandsvisning");
        ApplyFilter();
        SetPreviewTelemetry();
        Ports[0].IsExpanded = expanded;
        Changed(nameof(PortCount)); Changed(nameof(ContainerCount)); Changed(nameof(DockerStatus));
    }

    public async Task<string> VerifyPreviewControlsAsync()
    {
        if (!preview) throw new InvalidOperationException("UI-kontroll kräver förhandsvisning.");
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add("PASS " + name); }
        Check(Ports.Count == 5, "Five owner groups render");
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
        backend.IsExpanded = true; ApplyFilter();
        Check(ReferenceEquals(backend, Ports.Single(p => p.Key == backend.Key)) && backend.IsExpanded, "Expanded details survive refresh");
        backend.IsExpanded = false;
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
        Check(DensePortList.Padding == new Thickness(0) && DenseScroll.Padding == new Thickness(0) && ToolbarRow.Height.Value == 30, "Compact list has no padding and uses a small toolbar");
        Check(DenseScroll.MinHeight == 132 && DenseScroll.ViewportHeight >= 132 && MinHeight >= 166 && DenseScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "The port list reserves at least three dense rows and enable scrolling");
        Check(CompactDragArea.Visibility == Visibility.Visible && !WindowChrome.GetIsHitTestVisibleInChrome(CompactDragArea) && WindowChrome.GetIsHitTestVisibleInChrome(DropdownButton), "Compact search is replaced by a draggable caption area while dropdown stays interactive");
        int HitTest(FrameworkElement element)
        {
            var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            var coordinates = unchecked((int)(ushort)(short)Math.Round(point.X) | ((int)(short)Math.Round(point.Y) << 16));
            return SendPreviewWindowMessage(new WindowInteropHelper(this).Handle, 0x0084, IntPtr.Zero, new IntPtr(coordinates)).ToInt32();
        }
        Check(HitTest(CompactCaptionArea) == 2 && HitTest(DropdownButton) == 1 && HitTest(BalanceButton) == 1 && HitTest(CompactBalanceText) == 1 && HitTest(OpenAiLogo) == 1, "Balance logo and amount are clickable client areas while the adjacent blank header remains draggable");
        Check(OpenAiLogo.Data.Bounds.Width > 20 && OpenAiLogo.IsVisible && RowVisuals<System.Windows.Shapes.Path>(BalanceButton).Contains(OpenAiLogo), "OpenAI balance uses the complete Blossom vector in its interactive button");
        Check(CompactBalanceText.Text == "OpenAI · $24.50 · exempel" && CompactCaptionArea.ActualWidth >= 24, "Preview balance is explicitly marked as example data and preserves an adjacent drag area");
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
        var stops = RowVisuals<Button>(DensePorts).Where(button => button.Name == "DenseStop").ToArray();
        Check(stops.Length == Ports.Count && stops.All(button => button.ActualWidth == 36 && button.ActualHeight == 32 && button.Padding == new Thickness(10, 6, 10, 6)), "Every compact stop target has a 36 by 32 button and extra internal padding");
        balance = balance! with { State = "page_closed" }; Changed(nameof(BalanceLabel)); Changed(nameof(BalanceHint));
        Check(BalanceLabel.Contains("äldre") && balance.Hint(DateTime.UtcNow).Contains("stängd"), "Closing Billing retains the last amount but visibly marks it older");
        balance = new(0m, DateTime.UtcNow, DateTime.UtcNow, "ok"); Changed(nameof(BalanceLabel));
        Check(BalanceLabel == "OpenAI · $0.00 · exempel", "Zero balance is displayed as a real value within the clearly marked preview");
        balance = new(24.50m, DateTime.UtcNow, DateTime.UtcNow, "ok"); Changed(nameof(BalanceLabel)); Changed(nameof(BalanceHint));
        var isolatedBalancePath = Path.Combine(Path.GetTempPath(), "PortPilot-balance-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedBalancePath);
        var accountWindow = new MainWindow(new SettingsStore(isolatedBalancePath), new Settings { Compact = false, QuickStarts = [], OpenAiBalanceEnabled = true }, false);
        try
        {
            accountWindow.RefreshBalance();
            Check(accountWindow.settings.Compact && accountWindow.Width == 360 && accountWindow.MinWidth == 300,
                "An older saved standard-mode preference always opens the compact layout");
            Check(accountWindow.BalanceLabel == "OpenAI · väntar", "Production mode with no stored balance never displays the preview amount");
            new BalanceStore(isolatedBalancePath).Apply(new(1, "ok", "73.21", "USD"), DateTime.UtcNow);
            accountWindow.RefreshBalance();
            Check(accountWindow.BalanceLabel == "OpenAI · $73.21", "Production balance binding reads its own persisted USD value without an example suffix");
        }
        finally
        {
            accountWindow.Close();
            foreach (var fixtureFile in new[] { "settings.json", "settings.json.bak", "openai-balance.json" }) File.Delete(Path.Combine(isolatedBalancePath, fixtureFile));
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
        Check(settings.Compact && Width == 360 && DensePorts.IsVisible
            && !appearanceMenu.Items.OfType<MenuItem>().Any(item => item.Tag is "view"), "Only the compact layout exists and the dropdown offers no view switch");
        Check(appearanceMenu.Items.OfType<MenuItem>().Any(item => item.Tag is "quickstarts"), "Quick starts remain accessible from the compact dropdown");
        var launchesOpened = false;
        var closeLaunches = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        closeLaunches.Tick += (_, _) =>
        {
            var dialog = OwnedWindows.OfType<Window>().FirstOrDefault(window => window.Title == "DevGeek · Snabbstarter" && window.IsLoaded);
            if (dialog is null) return;
            launchesOpened = ReferenceEquals(dialog.Content, LaunchSection) && LaunchSection.IsVisible && Width == 360
                && RowVisuals<TextBlock>(LaunchSection).Any(text => text.Text == QuickStarts[0].Name);
            closeLaunches.Stop(); dialog.Close();
        };
        closeLaunches.Start(); QuickStarts_Click(this, new()); closeLaunches.Stop(); UpdateLayout();
        Check(launchesOpened && LaunchSection.Visibility == Visibility.Collapsed && DensePorts.IsVisible && ReferenceEquals(LaunchSection.DataContext, this),
            "Quick starts open in an owned dialog, keep their bindings and restore the compact window on close");
        VerifyProjectRows(Check);
        VerifyAssistantUsage(Check);
        await VerifyAppearanceAsync(Check);
        return string.Join(Environment.NewLine, checks);
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendPreviewWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
