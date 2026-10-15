using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PortPilot.Core;

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
    [System.Text.Json.Serialization.JsonIgnore] public string Status => IsBusy ? Operation : RecoveryError is not null ? "Återanslutning misslyckades · försök igen"
        : Session is null ? "Redo att starta" : Session.IsRunning ? "Körs · " + Session.ProcessCount + " processer" + (Session.WasRestored ? " · återansluten" : "")
        : Session.WasStopped ? "Stoppad" : Session.ExitCode is not { } code ? "Avslutad · se logg" : code == 0 ? "Avslutad · kod 0" : "Misslyckades · kod " + code + " · se logg";
    [System.Text.Json.Serialization.JsonIgnore] public string ActionLabel => IsBusy ? "Vänta…" : RecoveryError is not null ? "Försök igen" : Session?.IsRunning == true ? "■  Stoppa" : "▶  Starta";
    [System.Text.Json.Serialization.JsonIgnore] public string RowStatus => IsBusy || !IsRunning ? Status : "Körs · ingen port ännu" + (Session!.WasRestored ? " · återansluten" : "");
    [System.Text.Json.Serialization.JsonIgnore] public string RowHint => $"Snabbstart · {Name}\n{Command}\n{Directory}\n\n" + (IsRunning
        ? "Kommandot körs men lyssnar inte på någon port ännu. Portarna visas här när processen öppnar dem."
        : "Klicka på ▶ för att starta. Högerklick: logg, redigera, terminal eller editor.");
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
    public string Hint => IsDocker ? $"Container {ContainerId}\n{DockerEndpoint}" : (ProjectName is null ? "" : $"{ProjectName}\n{ProjectDirectory}\n") + $"{Executable ?? Name}\nPID {Pid} · {Protocol.ToUpperInvariant()}\nStopp avslutar processen och dess underprocesser.";
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
    public bool ClaudeUsageEnabled { get; set; }
    /// <summary>Editor id from <see cref="Editors"/>; null picks the first installed editor.</summary>
    public string? Editor { get; set; }
    public bool Notifications { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public AppearanceSettings Appearance { get; set; } = new();
    public List<QuickStart> QuickStarts { get; set; } = [];
}
