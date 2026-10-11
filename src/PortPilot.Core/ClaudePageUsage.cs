using System.Text.Json;

namespace PortPilot.Core;

// Accept only normalized, visible plan percentages from our own signed-in usage page.
public sealed record ClaudePageUsage(string State, UsageSnapshot? Snapshot)
{
    public const string Url = "https://claude.ai/settings/usage";
    public static bool IsUsagePage(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "claude.ai" && uri.IsDefaultPort && uri.AbsolutePath.TrimEnd('/') == "/settings/usage";

    public static ClaudePageUsage Parse(string json, DateTimeOffset now)
    {
        if (json.Length > 16384) throw new InvalidDataException("Ogiltig kvotavläsning.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Ogiltig kvotavläsning.");
        var value = state.GetString();
        if (value is "login_required" or "unreadable" or "paused") return new(value, null);
        if (value != "ok" || !root.TryGetProperty("windows", out var source) || source.ValueKind != JsonValueKind.Array || source.GetArrayLength() is < 1 or > 2)
            throw new InvalidDataException("Ogiltig kvotavläsning.");
        var windows = new List<UsageWindow>();
        foreach (var window in source.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("minutes", out var minutes) || minutes.ValueKind != JsonValueKind.Number || !minutes.TryGetInt32(out var duration) || duration is not (300 or 10080)
                || !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent is < 0 or > 100
                || windows.Any(item => item.DurationMinutes == duration)) throw new InvalidDataException("Ogiltig kvotavläsning.");
            DateTimeOffset? resets = null;
            if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var seconds) && seconds is >= 0 and <= 253402300799)
                resets = DateTimeOffset.FromUnixTimeSeconds(seconds);
            windows.Add(new("Claude Code", percent, duration, resets));
        }
        return new("ok", new(now, windows));
    }
}
