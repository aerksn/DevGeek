using System.Text.RegularExpressions;

namespace OtterMonitor.Core;

public enum LogLevel { Normal, Success, Warning, Error }

/// <summary>One log line split into a dimmed prefix (time or [tool] tag) and its text, with a level for color.</summary>
public sealed record LogLine(string Prefix, string Body, LogLevel Level)
{
    public string Text => Prefix + Body;
}

public static partial class LogLines
{
    public const string TruncatedMarker = "… senaste 64 kB …";

    /// <summary>Splits log text into lines. A leading truncation marker is reported instead of shown as a line.</summary>
    public static (List<LogLine> Lines, bool Truncated) Parse(string text)
    {
        var raw = text.Replace("\r\n", "\n").Split('\n').ToList();
        var truncated = raw.Count > 0 && raw[0] == TruncatedMarker;
        if (truncated) raw.RemoveAt(0);
        if (raw.Count > 0 && raw[^1].Length == 0) raw.RemoveAt(raw.Count - 1);
        return (raw.Select(Line).ToList(), truncated);
    }

    public static LogLine Line(string text)
    {
        text = text.TrimEnd('\r').Replace("\t", "    ");
        var prefix = Prefix().Match(text) is { Success: true } match ? match.Value : "";
        var body = text[prefix.Length..];
        var level = Error().IsMatch(body) ? LogLevel.Error : Warning().IsMatch(body) ? LogLevel.Warning : Success().IsMatch(body) ? LogLevel.Success : LogLevel.Normal;
        return new(prefix, body, level);
    }

    // "08:00:01  " from docker logs, ISO times, or a "[nuxt] " / "[vite] " style tool tag.
    [GeneratedRegex(@"^(\d{2}:\d{2}:\d{2}(\.\d+)?\s+|\d{4}-\d{2}-\d{2}[T ][\d:.]+Z?\s+|\[[\w@./:-]{1,24}\]\s+)")]
    private static partial Regex Prefix();
    [GeneratedRegex(@"(\berror\b|\bERR!|\bfatal\b|\bexception\b|\bfailed\b|\bunhandled\b|✖|❌)", RegexOptions.IgnoreCase)]
    private static partial Regex Error();
    [GeneratedRegex(@"(\bwarn(ing)?\b|⚠)", RegexOptions.IgnoreCase)]
    private static partial Regex Warning();
    [GeneratedRegex(@"(^\s*[✓√✔]|\bready\b|\bcompiled successfully\b|\blistening on\b|\bLocal:\s+https?://)", RegexOptions.IgnoreCase)]
    private static partial Regex Success();
}
