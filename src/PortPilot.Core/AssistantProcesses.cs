using System.Diagnostics;

namespace PortPilot.Core;

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
        if (codex && cli is null) cli = FindCli();
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
            || normalized.Contains("/codex/vendor/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/codex-win32-", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.local/bin/", StringComparison.OrdinalIgnoreCase);
    }
    private static string? FindCli()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var path = Path.Combine(directory.Trim('"'), "codex.exe"); if (File.Exists(path) && IsCodexCli(path)) return path; }
            catch (ArgumentException) { }
        }
        return null;
    }
}
