using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PortPilot.Core;

public sealed record DockerSnapshot(List<PortEntry> Ports, int ContainerCount, string Status, IReadOnlyDictionary<string, ResourceReading>? Resources = null);

public sealed class DockerService
{
    public async Task<DockerSnapshot> ScanAsync()
    {
        try
        {
            var endpoint = await ResolveLocalEndpointAsync();
            if (endpoint is null) return new([], 0, "Docker · fjärranslutning hoppas över");
            var ids = (await RunAsync(["--host", endpoint, "ps", "--quiet", "--no-trunc"], 5)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0) return new([], 0, "Docker · 0 containrar");
            var json = await RunAsync(["--host", endpoint, "container", "inspect", .. ids], 6);
            var ports = ParseInspect(json, endpoint);
            Dictionary<string, ResourceReading>? resources = null;
            try { resources = await ReadStatsAsync(ports); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException or JsonException or IOException) { }
            return new(ports, ids.Length, $"Docker · {ids.Length} containrar", resources);
        }
        catch (System.ComponentModel.Win32Exception) { return new([], 0, "Docker · saknas"); }
        catch (TimeoutException) { return new([], 0, "Docker · svarar inte"); }
        catch (Exception e) when (e is InvalidOperationException or JsonException or IOException) { return new([], 0, "Docker · inte tillgänglig"); }
    }

    public static bool IsLocalEndpoint(string endpoint) =>
        endpoint.StartsWith("npipe://./pipe/", StringComparison.OrdinalIgnoreCase) ||
        endpoint.StartsWith("npipe:////./pipe/", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> ResolveLocalEndpointAsync()
    {
        // DOCKER_CONTEXT overrides DOCKER_HOST. Pin the resolved pipe for scan AND stop.
        var context = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
        var host = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (string.IsNullOrEmpty(context) && !string.IsNullOrEmpty(host)) return IsLocalEndpoint(host) ? host : null;
        var args = new List<string> { "context", "inspect" };
        if (!string.IsNullOrWhiteSpace(context)) args.Add(context);
        using var document = JsonDocument.Parse(await RunAsync(args, 4));
        var endpoint = document.RootElement[0].GetProperty("Endpoints").GetProperty("docker").GetProperty("Host").GetString() ?? "";
        return IsLocalEndpoint(endpoint) ? endpoint : null;
    }

    public static List<PortEntry> ParseInspect(string json, string endpoint)
    {
        using var document = JsonDocument.Parse(json);
        var entries = new List<PortEntry>();
        foreach (var container in document.RootElement.EnumerateArray())
        {
            if (!container.GetProperty("State").GetProperty("Running").GetBoolean()) continue;
            var id = container.GetProperty("Id").GetString()!;
            var name = (container.GetProperty("Name").GetString() ?? id[..12]).TrimStart('/');
            var state = container.GetProperty("State");
            var projectDirectory = container.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object
                && config.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object
                && labels.TryGetProperty("com.docker.compose.project.working_dir", out var directory) && directory.ValueKind == JsonValueKind.String
                ? directory.GetString() : null;
            DateTime? started = state.TryGetProperty("StartedAt", out var time) && DateTime.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) && parsed.Year > 1 ? parsed : null;
            var health = state.TryGetProperty("Paused", out var paused) && paused.ValueKind == JsonValueKind.True ? "paused"
                : state.TryGetProperty("Health", out var healthState) && healthState.TryGetProperty("Status", out var status) ? status.GetString() : null;
            var ports = container.GetProperty("NetworkSettings").GetProperty("Ports");
            if (ports.ValueKind != JsonValueKind.Object) continue;
            foreach (var mapping in ports.EnumerateObject())
            {
                var parts = mapping.Name.Split('/');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var containerPort) || mapping.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var binding in mapping.Value.EnumerateArray())
                {
                    if (!int.TryParse(binding.GetProperty("HostPort").GetString(), out var hostPort)) continue;
                    var address = binding.GetProperty("HostIp").GetString() ?? "0.0.0.0";
                    entries.Add(new(hostPort, address, 0, name, "Docker", started, ContainerId: id, DockerEndpoint: endpoint, ContainerPort: containerPort, Protocol: parts[1], ProjectDirectory: projectDirectory, ContainerHealth: health));
                }
            }
        }
        return entries.DistinctBy(p => p.Key).ToList();
    }

    public static async Task<Dictionary<string, ResourceReading>> ReadStatsAsync(IEnumerable<PortEntry> entries)
    {
        var result = new Dictionary<string, ResourceReading>();
        foreach (var endpoint in entries.Where(entry => entry.IsDocker && entry.DockerEndpoint is not null && IsLocalEndpoint(entry.DockerEndpoint)).GroupBy(entry => entry.DockerEndpoint!))
        {
            var owners = endpoint.DistinctBy(entry => entry.ContainerId).ToList();
            var output = await RunAsync(["--host", endpoint.Key, "stats", "--no-stream", "--no-trunc", "--format", "{{json .}}", .. owners.Select(entry => entry.ContainerId!)], 5);
            var values = ParseStats(output);
            foreach (var owner in owners)
                if (values.TryGetValue(owner.ContainerId!, out var value)) result[PortGroup.OwnerKey(owner)] = value with { StartedUtc = owner.StartedUtc };
        }
        return result;
    }
    public static Dictionary<string, ResourceReading> ParseStats(string jsonLines)
    {
        var result = new Dictionary<string, ResourceReading>(StringComparer.Ordinal);
        foreach (var line in jsonLines.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line); var value = document.RootElement;
            if (!value.TryGetProperty("ID", out var idField) || string.IsNullOrWhiteSpace(idField.GetString())) continue;
            double? cpu = value.TryGetProperty("CPUPerc", out var cpuField) && double.TryParse(cpuField.GetString()?.Trim().TrimEnd('%'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent)
                && double.IsFinite(percent) && percent >= 0 ? percent : null;
            var memory = value.TryGetProperty("MemUsage", out var memoryField) ? ParseMemory(memoryField.GetString()?.Split('/')[0]) : null;
            result[idField.GetString()!] = new(cpu, memory, null, DateTime.UtcNow, Docker: true);
        }
        return result;
    }
    public static long? ParseMemory(string? text)
    {
        var match = Regex.Match(text ?? "", @"^\s*(\d+(?:\.\d+)?)\s*(B|kB|KB|KiB|MB|MiB|GB|GiB|TB|TiB)\s*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return null;
        var factor = match.Groups[2].Value switch { "B" => 1d, "kB" or "KB" => 1000d, "KiB" => 1024d, "MB" => 1e6, "MiB" => 1048576d, "GB" => 1e9, "GiB" => 1073741824d, "TB" => 1e12, "TiB" => 1099511627776d, _ => 0 };
        var bytes = amount * factor; return double.IsFinite(bytes) && bytes >= 0 && bytes < long.MaxValue ? (long)Math.Round(bytes) : null;
    }

    public static async Task StopAsync(PortEntry entry)
    {
        if (!entry.IsDocker || entry.DockerEndpoint is null || !IsLocalEndpoint(entry.DockerEndpoint)) throw new InvalidOperationException("Ingen lokal Docker-container är vald.");
        await RunAsync(["--host", entry.DockerEndpoint, "stop", "--time", "10", entry.ContainerId!], 18);
    }

    public static async Task RestartAsync(PortEntry entry)
    {
        if (!entry.IsDocker || string.IsNullOrWhiteSpace(entry.ContainerId) || entry.DockerEndpoint is null || !IsLocalEndpoint(entry.DockerEndpoint)) throw new InvalidOperationException("Ingen lokal Docker-container är vald.");
        await RunAsync(["--host", entry.DockerEndpoint, "container", "restart", "--timeout", "10", entry.ContainerId], 30);
    }

    private static async Task<string> RunAsync(IEnumerable<string> args, int seconds)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Remove("DOCKER_CONTEXT");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker kunde inte startas.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Docker tog för lång tid på sig. Uppdatera för att se aktuell status.");
        }
        var result = await output;
        var errorText = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText) ? "Docker-kommandot misslyckades." : errorText.Trim());
        return result;
    }
}
