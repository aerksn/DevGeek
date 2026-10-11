using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace PortPilot.Core;

public sealed record ResourceReading(double? CpuPercent, long? MemoryBytes, DateTime? StartedUtc, DateTime SampledUtc, bool Docker = false)
{
    public string MemoryText => MemoryBytes is not { } bytes ? "—" : bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.#} GB" : $"{bytes / 1_000_000d:0} MB";
    public string CpuText => CpuPercent is { } cpu ? cpu.ToString("0.#", CultureInfo.CurrentCulture) + " %" : "—";
    public string Uptime(DateTime utcNow)
    {
        if (StartedUtc is not { } started || started > utcNow) return "—";
        var elapsed = utcNow - started;
        return elapsed.TotalDays >= 1 ? $"{(int)elapsed.TotalDays}d {elapsed.Hours}h" : elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m" : elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
    }
}

public sealed class ResourceMonitor
{
    private sealed record Sample(TimeSpan Cpu, long Timestamp);
    private Dictionary<string, Sample> previous = [];
    public Dictionary<string, ResourceReading> SampleProcesses(IEnumerable<PortEntry> entries)
    {
        var next = new Dictionary<string, Sample>(); var result = new Dictionary<string, ResourceReading>();
        foreach (var entry in entries.Where(entry => !entry.IsDocker).DistinctBy(PortGroup.OwnerKey))
        {
            var key = PortGroup.OwnerKey(entry); var now = DateTime.UtcNow;
            var reading = new ResourceReading(null, null, entry.StartedUtc, now);
            try
            {
                if (entry.StartedUtc is null || entry.Pid <= 4) { result[key] = reading; continue; }
                using var process = Process.GetProcessById(entry.Pid);
                if (process.StartTime.ToUniversalTime() != entry.StartedUtc) { result[key] = new(null, null, null, now); continue; }
                process.Refresh();
                var cpu = process.TotalProcessorTime; var timestamp = Stopwatch.GetTimestamp();
                double? percent = previous.TryGetValue(key, out var before)
                    ? CalculateCpu(before.Cpu, cpu, Stopwatch.GetElapsedTime(before.Timestamp, timestamp), Environment.ProcessorCount) : null;
                next[key] = new(cpu, timestamp);
                reading = new(percent, process.WorkingSet64, entry.StartedUtc, now);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { }
            result[key] = reading;
        }
        previous = next; // Drop exited/replaced owners; never reuse a baseline from another PID lifetime.
        return result;
    }
    public static double? CalculateCpu(TimeSpan before, TimeSpan after, TimeSpan elapsed, int logicalProcessors)
        => after < before || elapsed <= TimeSpan.Zero || logicalProcessors <= 0 ? null : Math.Clamp((after - before).TotalSeconds / elapsed.TotalSeconds / logicalProcessors * 100, 0, 100);
}
