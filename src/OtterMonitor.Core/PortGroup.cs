namespace OtterMonitor.Core;

/// <summary>A single stoppable owner; never merge unrelated processes by display name.</summary>
public sealed class PortGroup : Observable
{
    private List<PortEntry> entries;
    private bool isStopping;
    private QuickStart? launch;
    private ResourceReading? resources;
    private ProbeReading probe = ProbeReading.Waiting;
    public string Key { get; }
    public IReadOnlyList<PortEntry> Entries => entries;
    public string Name => entries[0].DisplayName;
    public string ServiceIcon => IsDocker ? "docker" : entries[0].Kind.ToLowerInvariant() switch { "node" => "node", "bun" => "bun", ".net" => "dotnet", "python" => "python", _ => "tcp" };
    public string ServiceLabel => IsDocker ? "Docker" : entries[0].Kind;
    public string StatusText => DateTime.UtcNow - probe.SampledUtc > TimeSpan.FromSeconds(30) ? "Äldre" : probe.Text;
    public ServiceTone StatusTone => DateTime.UtcNow - probe.SampledUtc > TimeSpan.FromSeconds(30) ? ServiceTone.Neutral : probe.Tone;
    public string ResourceSummary => $"RAM {resources?.MemoryText ?? "—"}  ·  CPU {resources?.CpuText ?? "—"}  ·  ↑ {resources?.Uptime(DateTime.UtcNow) ?? new ResourceReading(null, null, entries[0].StartedUtc, DateTime.UtcNow).Uptime(DateTime.UtcNow)}";
    public void SetResources(ResourceReading? value) { resources = value; RefreshTelemetry(); }
    public void SetProbe(ProbeReading value) { probe = value; RefreshTelemetry(); }
    public Uri? BrowserUrl => WebServiceOpener.SelectUrl(entries, probe, DateTime.UtcNow);
    public void RefreshTelemetry() { Changed(nameof(StatusText)); Changed(nameof(StatusTone)); Changed(nameof(ResourceSummary)); Changed(nameof(Hint)); }
    public bool IsDevelopment => entries.Any(p => p.IsDevelopment);
    public bool CanStop => !IsStopping && StopTarget is not null;
    public bool IsDocker => entries[0].IsDocker;
    public bool CanRestart => IsDocker ? CanStop : !IsStopping && launch?.CanRestart == true;
    public bool IsStopping { get => isStopping; set { if (isStopping == value) return; isStopping = value; Changed(); Changed(nameof(CanStop)); Changed(nameof(CanRestart)); } }
    /// <summary>The quick start whose Windows job owns this process; set only from verified job membership.</summary>
    public QuickStart? Launch { get => launch; set { if (ReferenceEquals(launch, value)) return; launch = value; Changed(); Changed(nameof(IsLaunch)); Changed(nameof(LaunchHint)); Changed(nameof(CanRestart)); Changed(nameof(CanShowLog)); Changed(nameof(Hint)); } }
    public bool IsLaunch => launch is not null;
    public string LaunchHint => launch is null ? "" : "Startad av snabbstarten " + launch.Name;
    public bool CanShowLog => IsDocker || launch is not null;
    public bool CanBrowse => entries.Any(WebServiceOpener.IsCandidate);
    public string CopyIdLabel => IsDocker ? "Kopiera container-ID" : "Kopiera PID";
    public string CopyId => IsDocker ? entries[0].ContainerId ?? "" : entries[0].Pid.ToString();
    /// <summary>The verified web address when known; otherwise the first TCP port, written as a URL only for web runtimes.</summary>
    public string Address => BrowserUrl?.ToString() ?? (CanBrowse ? "http://localhost:" : "localhost:") + (entries.FirstOrDefault(p => p.Protocol == "tcp") ?? entries[0]).Port;
    public PortEntry? StopTarget => entries.FirstOrDefault(p => p.CanStop);
    public int PortCount => entries.Select(p => (p.Port, p.Protocol)).Distinct().Count();
    public string PortSummary => string.Join("  ·  ", entries.Select(p => (p.Port, p.Protocol)).Distinct()
        .Select(p => $":{p.Port}" + (p.Protocol == "tcp" ? "" : "/" + p.Protocol)));
    public string InlinePorts => string.Join(" · ", entries.Select(p => (p.Port, p.Protocol)).Distinct()
        .Select(p => $":{p.Port}" + (p.Protocol == "tcp" ? "" : "/" + p.Protocol)));
    public string Description
    {
        get
        {
            var families = entries.Select(p => p.Address.Contains(':') ? "IPv6" : "IPv4").Distinct().Order().ToArray();
            var owner = entries[0].IsDocker ? "" : $"PID {entries[0].Pid} · ";
            return $"{owner}{PortCount} {(PortCount == 1 ? "port" : "portar")} · {string.Join(" + ", families)}";
        }
    }
    public string Hint => (launch is null ? "" : LaunchHint + "\n") + entries[0].Hint + "\n" + Description + "\n\n" + probe.Hint + "\n" + ResourceSummary
        + (IsDocker ? "\nCPU och RAM enligt docker stats. CPU kan överstiga 100 % när flera kärnor används."
            : "\nRAM är processens arbetsminne. CPU är andelen av datorns totala logiska processorkapacitet. Gäller portens ägarprocess.")
        + "\nKörtid räknas från processens eller containerns start. — betyder att ett värde inte är tillgängligt ännu."
        + (IsDocker && entries[0].ProjectDirectory is { } directory ? "\nProjektmapp: " + directory : "")
        + (entries.Any(WebServiceOpener.IsCandidate)
            ? "\n\nDubbelklick eller Enter: öppna webbporten i standardwebbläsaren om HTTP svarar; annars terminal."
            : "\n\nDubbelklick eller Enter: terminal i projektmappen.")
        + "\nHåll musen över raden för snabbknappar. Högerklick för fler val.";

    private PortGroup(List<PortEntry> entries) { this.entries = entries; Key = OwnerKey(entries[0]); }

    public static string OwnerKey(PortEntry entry) => entry.IsDocker
        ? $"docker|{entry.DockerEndpoint}|{entry.ContainerId}"
        : $"process|{entry.Pid}|{entry.StartedUtc?.Ticks}";

    public static List<PortGroup> Create(IEnumerable<PortEntry> entries) => entries
        .GroupBy(OwnerKey)
        .Select(group => new PortGroup(group.Distinct().OrderBy(p => p.Port).ThenBy(p => p.Protocol).ThenBy(p => p.Address).ToList()))
        .OrderBy(group => group.entries[0].Port).ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public void Update(PortGroup current)
    {
        if (current.Key != Key) throw new InvalidOperationException("Gruppens process eller container får inte bytas ut.");
        if (entries.SequenceEqual(current.entries)) return;
        entries = current.entries;
        Changed(null);
    }

    // Match the entire owner, keeping every affected port visible when a single port is searched for.
    public bool Matches(string query) => launch?.Matches(query) == true || entries.Any(p =>
        $"{p.PortLabel} {p.Pid} {p.Name} {p.DisplayName} {p.ProjectDirectory} {p.Kind} {p.Address} {p.Protocol}".Contains(query, StringComparison.OrdinalIgnoreCase));
}
