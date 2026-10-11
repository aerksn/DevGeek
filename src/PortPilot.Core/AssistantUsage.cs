using System.Globalization;
using System.Text.Json;

namespace PortPilot.Core;

public sealed record UsageWindow(string Name, double UsedPercent, int? DurationMinutes, DateTimeOffset? ResetsAt)
{
    public double Remaining => Math.Clamp(100 - UsedPercent, 0, 100);
    public string Period => DurationMinutes switch
    {
        10080 => "7d", 1440 => "24h", > 0 when DurationMinutes % 60 == 0 => $"{DurationMinutes / 60}h",
        > 0 => $"{DurationMinutes}m", _ => Name
    };
    public string Label(DateTimeOffset now) => ResetsAt <= now ? $"{Period} —" : $"{Period} {Remaining.ToString("0.#", CultureInfo.InvariantCulture)}%";
}

public sealed record UsageSnapshot(DateTimeOffset ObservedAt, IReadOnlyList<UsageWindow> Windows);

public static class AssistantUsage
{
    public static UsageSnapshot ParseCodex(JsonElement result, DateTimeOffset now)
    {
        var windows = new List<UsageWindow>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object && buckets.EnumerateObject().Any())
        {
            foreach (var bucket in buckets.EnumerateObject()) ReadBucket(bucket.Value, bucket.Name, windows);
        }
        else if (result.TryGetProperty("rateLimits", out var legacy)) ReadBucket(legacy, "codex", windows);
        return new(now, windows);
    }

    private static void ReadBucket(JsonElement bucket, string id, List<UsageWindow> windows)
    {
        if (bucket.ValueKind != JsonValueKind.Object) return;
        var name = bucket.TryGetProperty("limitName", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : null;
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!bucket.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object
                || !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) continue;
            int? minutes = window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var value) && value > 0 ? value : null;
            windows.Add(new(name ?? id, Math.Clamp(percent, 0, 100), minutes, ReadReset(window, "resetsAt")));
        }
    }

    public static UsageSnapshot? ReadClaude(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 16384) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("observedAt", out var observed) || !observed.TryGetDateTimeOffset(out var at)
                || !root.TryGetProperty("windows", out var source) || source.ValueKind != JsonValueKind.Array) return null;
            var windows = new List<UsageWindow>();
            foreach (var window in source.EnumerateArray().Take(2))
            {
                if (!window.TryGetProperty("minutes", out var minutes) || minutes.ValueKind != JsonValueKind.Number || !minutes.TryGetInt32(out var duration) || duration is not (300 or 10080)
                    || !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) continue;
                windows.Add(new("Claude Code", Math.Clamp(percent, 0, 100), duration, ReadReset(window, "resetsAt")));
            }
            return new(at, windows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    private static DateTimeOffset? ReadReset(JsonElement window, string key)
    {
        if (!window.TryGetProperty(key, out var reset) || reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out var seconds) || seconds < 0 || seconds > 253402300799) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
