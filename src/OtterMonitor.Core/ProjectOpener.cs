using System.ComponentModel;
using System.Diagnostics;

namespace OtterMonitor.Core;

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

    public static ProcessStartInfo EditorStart(string directory, string executable)
    {
        directory = RequireDirectory(directory);
        var editor = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory, CreateNoWindow = true };
        // Respect the editor's own configured behavior for opening an existing or new project.
        editor.ArgumentList.Add(directory);
        return editor;
    }

    public static void OpenTerminal(string directory)
    {
        var executable = FindExecutable("wt.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe"));
        try { using var process = Process.Start(TerminalStart(directory, executable)); }
        catch (Win32Exception) when (executable is not null) { using var process = Process.Start(TerminalStart(directory, null)); }
    }

    public static void OpenEditor(string directory, Editor editor)
    {
        var executable = editor.Find() ?? throw new InvalidOperationException($"{editor.Name} hittades inte. Installera {editor.Name} eller välj en annan editor under ⌄ → Editor.");
        using var process = Process.Start(EditorStart(directory, executable));
    }

    private static string RequireDirectory(string directory) => LocalDirectory(directory)
        ?? throw new DirectoryNotFoundException("Projektmappen finns inte längre eller är inte en lokal Windows-mapp.");

    internal static string? FindExecutable(string name, params string[] knownPaths)
    {
        foreach (var path in knownPaths) if (File.Exists(path)) return path;
        return PathFolders().Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }

    internal static IEnumerable<string> PathFolders()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string path;
            try { path = folder.Trim().Trim('"'); if (!Path.IsPathFullyQualified(path)) continue; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { continue; }
            yield return path;
        }
    }
}

/// <summary>Editors started directly by their own .exe with the project folder as a single argument; never through a shell.</summary>
public sealed record Editor(string Id, string Name, Func<string?> Find)
{
    private static string Local(params string[] parts) => Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), .. parts]);
    private static string Programs(params string[] parts) => Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), .. parts]);

    public static readonly IReadOnlyList<Editor> All =
    [
        new("zed", "Zed", () => ProjectOpener.FindExecutable("zed.exe", Local("Programs", "Zed", "bin", "zed.exe"), Programs("Zed", "bin", "zed.exe"))),
        new("code", "VS Code", () => ProjectOpener.FindExecutable("Code.exe", Local("Programs", "Microsoft VS Code", "Code.exe"), Programs("Microsoft VS Code", "Code.exe"))
            ?? FromLauncher("code.cmd", "Code.exe")),
        new("cursor", "Cursor", () => ProjectOpener.FindExecutable("Cursor.exe", Local("Programs", "cursor", "Cursor.exe"), Programs("Cursor", "Cursor.exe"))
            ?? FromLauncher("cursor.cmd", "Cursor.exe")),
        new("rider", "Rider", () => ProjectOpener.FindExecutable("rider64.exe", Local("Programs", "Rider", "bin", "rider64.exe"))
            ?? Newest(Programs("JetBrains"), "JetBrains Rider*", "bin", "rider64.exe")),
    ];

    public static Editor? ById(string? id) => All.FirstOrDefault(editor => string.Equals(editor.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The saved choice, else the first installed editor (Zed first, as before), else Zed so the error names something.</summary>
    public static Editor Resolve(string? id) => ById(id) ?? All.FirstOrDefault(editor => editor.Find() is not null) ?? All[0];

    // VS Code and Cursor put a .cmd launcher in PATH; start the real .exe found above it instead of running a batch file.
    private static string? FromLauncher(string launcher, string executable)
    {
        foreach (var folder in ProjectOpener.PathFolders())
        {
            if (!File.Exists(Path.Combine(folder, launcher))) continue;
            var directory = new DirectoryInfo(folder);
            for (var level = 0; directory is not null && level < 4; level++, directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, executable))) return Path.Combine(directory.FullName, executable);
        }
        return null;
    }

    private static string? Newest(string root, string pattern, params string[] tail)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root, pattern).OrderDescending(StringComparer.OrdinalIgnoreCase)
                .Select(folder => Path.Combine([folder, .. tail])).FirstOrDefault(File.Exists) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
