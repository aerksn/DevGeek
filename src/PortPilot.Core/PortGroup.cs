namespace PortPilot.Core;

/// <summary>A single stoppable owner; never merge unrelated processes by display name.</summary>
public sealed class PortGroup : Observable
{
    private List<PortEntry> entries;
    private bool isExpanded;
    private bool isStopping;
    private ResourceReading? resources;
    private ProbeReading probe = ProbeReading.Waiting;
    public string Key { get; }
    public IReadOnlyList<PortEntry> Entries => entries;
    public string Name => entries[0].DisplayName;
    public string Badge => entries[0].Badge;
    public string ServiceIcon => IsDocker ? "docker" : entries[0].Kind.ToLowerInvariant() switch { "node" => "node", "bun" => "bun", ".net" => "dotnet", "python" => "python", _ => "tcp" };
    public string ServiceLabel => IsDocker ? "Docker" : entries[0].Kind;
    public string StatusText => DateTime.UtcNow - probe.SampledUtc > TimeSpan.FromSeconds(30) ? "Äldre" : probe.Text;
    public ServiceTone StatusTone => DateTime.UtcNow - probe.SampledUtc > TimeSpan.FromSeconds(30) ? ServiceTone.Neutral : probe.Tone;
    public string ResourceSummary => $"RAM {resources?.MemoryText ?? "—"}  ·  CPU {resources?.CpuText ?? "—"}  ·  ↑ {resources?.Uptime(DateTime.UtcNow) ?? new ResourceReading(null, null, entries[0].StartedUtc, DateTime.UtcNow).Uptime(DateTime.UtcNow)}";
    public void SetResources(ResourceReading? value) { resources = value; RefreshTelemetry(); }
    public void SetProbe(ProbeReading value) { probe = value; RefreshTelemetry(); }
    public void RefreshTelemetry() { Changed(nameof(StatusText)); Changed(nameof(StatusTone)); Changed(nameof(ResourceSummary)); Changed(nameof(Hint)); }
    public bool IsDevelopment => entries.Any(p => p.IsDevelopment);
    public bool CanStop => !IsStopping && StopTarget is not null;
    public bool IsDocker => entries[0].IsDocker;
    public bool CanRestart => CanStop && IsDocker;
    public bool IsStopping { get => isStopping; set { if (isStopping == value) return; isStopping = value; Changed(); Changed(nameof(CanStop)); Changed(nameof(CanRestart)); } }
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
    public IEnumerable<string> BindingDetails => entries.Select(p =>
    {
        var address = p.Address.Contains(':') ? $"[{p.Address}]" : p.Address;
        return p.IsDocker ? $"{address}:{p.Port} → {p.ContainerPort}/{p.Protocol}" : $"{address}:{p.Port}/{p.Protocol}";
    });
    public string Hint => entries[0].Hint + "\n" + Description + "\n\n" + probe.Hint + "\n" + ResourceSummary
        + (IsDocker ? "\nCPU och RAM enligt docker stats. CPU kan överstiga 100 % när flera kärnor används."
            : "\nRAM är processens arbetsminne. CPU är andelen av datorns totala logiska processorkapacitet. Gäller portens ägarprocess.")
        + "\nKörtid räknas från processens eller containerns start. — betyder att ett värde inte är tillgängligt ännu."
        + (IsDocker && entries[0].ProjectDirectory is { } directory ? "\nProjektmapp: " + directory : "")
        + "\n\nDubbelklick eller Enter: terminal i projektmappen. Högerklick: terminal eller Zed.";
    public bool IsExpanded { get => isExpanded; set { if (isExpanded == value) return; isExpanded = value; Changed(); } }

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
    public bool Matches(string query) => entries.Any(p =>
        $"{p.PortLabel} {p.Pid} {p.Name} {p.DisplayName} {p.ProjectDirectory} {p.Kind} {p.Address} {p.Protocol}".Contains(query, StringComparison.OrdinalIgnoreCase));
}
