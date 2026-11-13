using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OtterMonitor.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class QuickStart : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Command { get; set; } = "";
    private bool isBusy, ownsPorts;
    [System.Text.Json.Serialization.JsonIgnore] public LaunchSession? Session { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? RecoveryError { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string Operation { get; set; } = "Arbetar…";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsBusy { get => isBusy; set { isBusy = value; Refresh(); } }
    /// <summary>True while at least one listed port group belongs to this quick start's job.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool OwnsPorts { get => ownsPorts; set { if (ownsPorts == value) return; ownsPorts = value; Changed(); } }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsRunning => Session?.IsRunning == true;
    [System.Text.Json.Serialization.JsonIgnore] public bool HasFailed => !IsBusy && RecoveryError is null && Session is { IsRunning: false, WasStopped: false, ExitCode: { } code } && code != 0;
    [System.Text.Json.Serialization.JsonIgnore] public bool CanAct => !IsBusy;
    [System.Text.Json.Serialization.JsonIgnore] public bool CanRestart => !IsBusy && Session?.IsRunning == true;
    [System.Text.Json.Serialization.JsonIgnore] public bool CanEdit => !IsBusy && Session?.IsRunning != true && RecoveryError is null;
    [System.Text.Json.Serialization.JsonIgnore] public string Status => IsBusy ? Operation : RecoveryError is not null ? UiText.T("Återanslutning misslyckades · försök igen")
        : Session is null ? UiText.T("Redo att starta") : Session.IsRunning ? UiText.T("Körs · ") + Session.ProcessCount + UiText.T(" processer") + (Session.WasRestored ? UiText.T(" · återansluten") : "")
        : Session.WasStopped ? UiText.T("Stoppad") : Session.ExitCode is not { } code ? UiText.T("Avslutad · se logg") : code == 0 ? UiText.T("Avslutad · kod 0") : UiText.T("Misslyckades · kod ") + code + UiText.T(" · se logg");
    [System.Text.Json.Serialization.JsonIgnore] public string ActionLabel => IsBusy ? UiText.T("Vänta…") : RecoveryError is not null ? UiText.T("Försök igen") : Session?.IsRunning == true ? UiText.T("■  Stoppa") : UiText.T("▶  Starta");
    [System.Text.Json.Serialization.JsonIgnore] public string RowStatus => IsBusy || !IsRunning ? Status : UiText.T("Körs · ingen port ännu") + (Session!.WasRestored ? UiText.T(" · återansluten") : "");
    [System.Text.Json.Serialization.JsonIgnore] public string RowHint => UiText.F("Snabbstart \u00B7 {0}\n{1}\n{2}\n\n", Name, Command, Directory) + (IsRunning
        ? UiText.T("Kommandot körs men lyssnar inte på någon port ännu. Portarna visas här när processen öppnar dem.")
        : UiText.T("Klicka på ▶ för att starta. Högerklick: logg, redigera, terminal eller editor."));
    public bool Matches(string query) => $"{Name} {Command} {Directory}".Contains(query, StringComparison.OrdinalIgnoreCase);
    public void Refresh() => Changed(null);
}

public sealed record PortEntry(int Port, string Address, int Pid, string Name, string Kind,
    DateTime? StartedUtc, string? Executable = null, string? ContainerId = null,
    string? DockerEndpoint = null, int? ContainerPort = null, string Protocol = "tcp",
    string? ProjectName = null, string? ProjectDirectory = null, string? ContainerHealth = null)
{
    public string DisplayName => ProjectName ?? Name;
    public bool IsDocker => ContainerId is not null;
    public bool IsDevelopment => IsDocker || DevelopmentNames.Contains(Name);
    public bool CanStop => IsDocker || (StartedUtc is not null && Pid > 4 && Pid != Environment.ProcessId && !ProtectedNames.Contains(Name));
    public string PortLabel => ":" + Port;
    public string Hint => IsDocker ? $"Container {ContainerId}\n{DockerEndpoint}" : (ProjectName is null ? "" : $"{ProjectName}\n{ProjectDirectory}\n") + UiText.F("{0}\nPID {1} \u00B7 {2}\nStopp avslutar processen och dess underprocesser.", Executable ?? Name, Pid, Protocol.ToUpperInvariant());
    public string Key => $"{ContainerId ?? Pid.ToString()}|{Address}|{Port}|{Protocol}";

    public static readonly HashSet<string> DevelopmentNames = new(StringComparer.OrdinalIgnoreCase)
    { "node", "bun", "deno", "dotnet", "python", "python3", "pythonw", "uv", "java", "javaw", "ruby", "php", "go", "postgres", "mysqld", "redis-server", "nginx", "httpd", "caddy", "vite", "next-server" };
    public static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    { "System", "Registry", "svchost", "services", "lsass", "csrss", "wininit", "winlogon", "smss", "MsMpEng", "com.docker.backend", "com.docker.proxy", "Docker Desktop", "wslrelay", "wslhost", "wslservice", "vpnkit" };
}

public sealed class Settings
{
    public int Version { get; set; } = 1;
    public bool Topmost { get; set; }
    public bool OpenAiBalanceEnabled { get; set; }
    public bool JevBalanceEnabled { get; set; }
    public bool AnthropicBalanceEnabled { get; set; }
    public string Language { get; set; } = "en";
    public bool ClaudeUsageEnabled { get; set; }
    /// <summary>Editor id from <see cref="Editors"/>; null picks the first installed editor.</summary>
    public string? Editor { get; set; }
    public BrowserAddressMode BrowserAddress { get; set; } = BrowserAddressMode.Localhost;
    public bool Notifications { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    /// <summary>Folders searched for projects in "Ny snabbstart"; null uses <see cref="ProjectScanner.DefaultRoots"/>.</summary>
    public List<string>? ProjectRoots { get; set; }
    public AppearanceSettings Appearance { get; set; } = new();
    public DashboardVisibility Visibility { get; set; } = new();
    public List<QuickStart> QuickStarts { get; set; } = [];
}
