using System.Text.Json;

namespace OtterMonitor.Core;

/// <summary>Moves the data folder from the time the app was called PortPilot to %LOCALAPPDATA%\OtterMonitor.</summary>
public static class DataDirectory
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Default => Path.Combine(LocalAppData, "OtterMonitor");
    public static string Legacy => Path.Combine(LocalAppData, "PortPilot");

    /// <summary>
    /// Returns the folder to use for this run. The old folder is renamed in one step, so settings, logins, logs and
    /// quick start records move together. While a file in it is in use (a running quick start or a WebView process)
    /// the rename fails, the old folder keeps being used, and the move is tried again on the next start.
    /// </summary>
    public static string Resolve(string current, string legacy, string claudeSettingsFile)
    {
        if (!Directory.Exists(legacy)) return current;
        if (Directory.Exists(current))
        {
            if (Directory.EnumerateFileSystemEntries(current).Any()) return current;
            try { Directory.Delete(current); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return current; }
        }
        try { Directory.Move(legacy, current); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return legacy; }
        RebaseLaunchRecords(current, legacy);
        try { new ClaudeUsageInstaller(current, claudeSettingsFile, legacy).Relocate(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { /* Clicking the Claude row reconnects it. */ }
        return current;
    }

    // Quick start records store their log's full path; point finished runs at the moved logs so they still open.
    private static void RebaseLaunchRecords(string current, string legacy)
    {
        var sessions = Path.Combine(current, "sessions");
        if (!Directory.Exists(sessions)) return;
        foreach (var path in Directory.EnumerateFiles(sessions, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<LaunchRecord>(File.ReadAllText(path));
                if (record is null || !record.LogPath.StartsWith(legacy + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                File.WriteAllText(path, JsonSerializer.Serialize(record with { LogPath = current + record.LogPath[legacy.Length..] }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* Restore reports an unreadable record. */ }
        }
    }
}
