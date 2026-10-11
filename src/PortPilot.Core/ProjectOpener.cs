using System.ComponentModel;
using System.Diagnostics;

namespace PortPilot.Core;

public static class ProjectOpener
{
    public static string? ResolveDirectory(PortEntry entry)
    {
        if (LocalDirectory(entry.ProjectDirectory) is { } project) return project;
        if (entry.IsDocker || entry.StartedUtc is not { } started) return null;
        return LocalDirectory(ProcessEnvironmentReader.Read(entry.Pid, started)?.Directory);
    }

    public static string? LocalDirectory(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network) return null;
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static ProcessStartInfo TerminalStart(string directory, string? terminalExecutable)
    {
        directory = RequireDirectory(directory);
        // WT also parses semicolons as command separators. Such paths go through
        // WorkingDirectory only, never through WT's command language.
        if (terminalExecutable is not null && !directory.Contains(';'))
        {
            var terminal = new ProcessStartInfo(terminalExecutable) { UseShellExecute = false, WorkingDirectory = directory, CreateNoWindow = true };
            foreach (var argument in new[] { "-w", "new", "new-tab", "--startingDirectory", directory }) terminal.ArgumentList.Add(argument);
            return terminal;
        }
        var shell = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = true, WorkingDirectory = directory, WindowStyle = ProcessWindowStyle.Normal };
        foreach (var argument in new[] { "-NoLogo", "-NoExit", "-NoProfile" }) shell.ArgumentList.Add(argument);
        return shell;
    }

    public static ProcessStartInfo ZedStart(string directory, string executable)
    {
        directory = RequireDirectory(directory);
        var zed = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory, CreateNoWindow = true };
        // Respect Zed's configured behavior for opening an existing or new project.
        zed.ArgumentList.Add(directory);
        return zed;
    }

    public static void OpenTerminal(string directory)
    {
        var executable = FindExecutable("wt.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe"));
        try { using var process = Process.Start(TerminalStart(directory, executable)); }
        catch (Win32Exception) when (executable is not null) { using var process = Process.Start(TerminalStart(directory, null)); }
    }

    public static void OpenZed(string directory)
    {
        var executable = FindExecutable("zed.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Zed", "bin", "zed.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zed", "bin", "zed.exe"));
        if (executable is null) throw new InvalidOperationException("Zed hittades inte. Installera Zed eller lägg dess bin-mapp i PATH och starta om DevGeek.");
        using var process = Process.Start(ZedStart(directory, executable));
    }

    private static string RequireDirectory(string directory) => LocalDirectory(directory)
        ?? throw new DirectoryNotFoundException("Projektmappen finns inte längre eller är inte en lokal Windows-mapp.");

    private static string? FindExecutable(string name, params string[] knownPaths)
    {
        foreach (var path in knownPaths) if (File.Exists(path)) return path;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var path = folder.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(path)) continue;
                path = Path.Combine(path, name);
                if (File.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return null;
    }
}
