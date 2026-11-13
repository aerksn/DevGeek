using System.Diagnostics;

namespace OtterMonitor.Core;

public sealed record AssistantActivity(bool CodexRunning, bool ClaudeRunning, string? CodexExecutable, bool ClaudeDesktopRunning = false, bool ClaudeCliRunning = false);

public static class AssistantProcesses
{
    public static AssistantActivity Scan(int excludedPid = 0)
    {
        var codex = false; var claude = false; var claudeDesktop = false; string? cli = null;
        using var current = Process.GetCurrentProcess();
        var sessionId = current.SessionId;
        foreach (var name in new[] { "codex", "claude", "node" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (process.Id == excludedPid || process.SessionId != sessionId) continue;
                    var path = process.MainModule?.FileName;
                    if (path is null) continue;
                    if (name == "codex")
                    {
                        codex = true;
                        // Desktop's bundled native app-server. Electron's Codex.exe cannot serve stdio.
                        if (IsCodexCli(path)) cli ??= path;
                    }
                    else if (name == "claude")
                    {
                        if (IsClaudeCli(path)) claude = true;
                        if (IsClaudeDesktop(path)) claudeDesktop = true;
                    }
                    else if (name == "node")
                    {
                        // Only inspect the entry script; never persist or display command arguments.
                        var args = ProcessEnvironmentReader.Read(process.Id, process.StartTime.ToUniversalTime())?.Arguments;
                        var script = args?.FirstOrDefault(arg => !arg.StartsWith('-'))?.Replace('\\', '/');
                        if (script?.Contains("/@anthropic-ai/claude-code/", StringComparison.OrdinalIgnoreCase) == true) claude = true;
                        if (script?.Contains("/@openai/codex/", StringComparison.OrdinalIgnoreCase) == true) codex = true;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { }
            }
        }
        cli ??= FindCodexCli();
        return new(codex, claude || claudeDesktop, cli, claudeDesktop, claude);
    }

    public static bool IsClaudeDesktop(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (!normalized.EndsWith("/Claude.exe", StringComparison.OrdinalIgnoreCase)) return false;
        return normalized.Contains("/WindowsApps/Claude_", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/AnthropicClaude/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/AppData/Local/Anthropic/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/AppData/Local/Programs/Claude/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsClaudeCli(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/.local/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.local/share/claude/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/claude-code/", StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsCodexCli(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/Codex/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/WindowsApps/OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
                && normalized.EndsWith("/app/resources/codex.exe", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.t3/tools/codex/", StringComparison.OrdinalIgnoreCase)
                && normalized.EndsWith("/bin/codex.exe", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/codex/vendor/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/codex-win32-", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.local/bin/", StringComparison.OrdinalIgnoreCase);
    }
    public static string? FindCodexCli(IEnumerable<string>? searchRoots = null, string? searchPath = null)
    {
        foreach (var directory in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var path = Path.Combine(directory.Trim('"'), "codex.exe"); if (File.Exists(path) && IsCodexCli(path)) return path; }
            catch (ArgumentException) { }
        }
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 8 };
        foreach (var root in searchRoots ?? CodexSearchRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var path = Directory.EnumerateFiles(root, "codex.exe", options).Where(IsCodexCli)
                    .OrderByDescending(InstallationVersion).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (path is not null) return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    private static IEnumerable<string> CodexSearchRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(home, ".t3", "tools", "codex");
        yield return Path.Combine(home, ".local", "bin");
        yield return Path.Combine(local, "Programs", "OpenAI", "Codex", "bin");
        yield return Path.Combine(local, "OpenAI", "Codex", "bin");
        yield return Path.Combine(roaming, "npm", "node_modules", "@openai", "codex");
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        string[] packages;
        try { packages = Directory.GetDirectories(windowsApps, "OpenAI.Codex_*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var package in packages.OrderByDescending(InstallationVersion)) yield return Path.Combine(package, "app", "resources");
    }

    private static Version InstallationVersion(string path)
    {
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            var value = segment.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ? segment.Split('_')[1] : segment;
            if (Version.TryParse(value, out var version)) return version;
        }
        return new(0, 0);
    }
}
