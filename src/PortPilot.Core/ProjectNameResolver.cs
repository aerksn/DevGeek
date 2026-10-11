using System.Collections.Concurrent;
using System.Text.Json;

namespace PortPilot.Core;

public sealed record ProjectIdentity(string Name, string Directory);

public sealed class ProjectNameResolver
{
    private sealed record Cached(ProjectIdentity? Project, DateTime CheckedUtc);
    private readonly ConcurrentDictionary<(int Pid, DateTime Started), Cached> cache = new();

    public ProjectIdentity? Resolve(int pid, DateTime? startedUtc)
    {
        if (startedUtc is not { } started) return null;
        var key = (pid, started);
        var now = DateTime.UtcNow;
        if (cache.TryGetValue(key, out var previous) && now - previous.CheckedUtc < TimeSpan.FromSeconds(10)) return previous.Project;
        var environment = ProcessEnvironmentReader.Read(pid, started);
        var project = environment is null ? null : FromPaths(environment.Directory, environment.Arguments);
        cache[key] = new(project, now);
        foreach (var entry in cache) if (now - entry.Value.CheckedUtc > TimeSpan.FromMinutes(1)) cache.TryRemove(entry.Key, out _);
        return project;
    }

    public static ProjectIdentity? FromPaths(string? workingDirectory, IEnumerable<string> arguments)
    {
        var cwd = NormalizeDirectory(workingDirectory);
        var candidates = new List<string>();
        if (cwd is not null) candidates.Add(cwd);
        foreach (var argument in arguments)
        {
            try
            {
                var path = argument;
                if (argument.StartsWith("--", StringComparison.Ordinal))
                {
                    var equals = argument.IndexOf('=');
                    if (equals < 0 || argument[..equals] is not ("--cwd" or "--prefix" or "--dir")) continue;
                    path = argument[(equals + 1)..];
                }
                if (!Path.IsPathFullyQualified(path))
                {
                    if (cwd is null || Path.GetExtension(path).ToLowerInvariant() is not (".js" or ".mjs" or ".cjs" or ".ts" or ".tsx")) continue;
                    path = Path.Combine(cwd, path);
                }
                var candidate = NormalizeDirectory(path);
                if (candidate is not null && !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase)) candidates.Add(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        // Prefer real package manifests over generic cwd names such as a terminal's home directory.
        foreach (var candidate in candidates)
            if (FindPackage(candidate) is { } package) return Identity(package);
        return candidates.FirstOrDefault() is { } fallback ? Identity(fallback) : null;
    }

    private static string? NormalizeDirectory(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            if (new DriveInfo(root).DriveType == DriveType.Network) return null;
            foreach (var system in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (!string.IsNullOrEmpty(system) && (full.Equals(system, StringComparison.OrdinalIgnoreCase) || full.StartsWith(system + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return null;
            if (!Directory.Exists(full)) full = Path.GetDirectoryName(full)!;
            var parts = full.Split(Path.DirectorySeparatorChar);
            var dependency = Array.FindIndex(parts, part => part.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
            if (dependency >= 0) full = string.Join(Path.DirectorySeparatorChar, parts.Take(dependency));
            return Directory.Exists(full) && !full.Equals(root, StringComparison.OrdinalIgnoreCase) ? full.TrimEnd(Path.DirectorySeparatorChar) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static string? FindPackage(string directory)
    {
        var current = new DirectoryInfo(directory);
        for (int depth = 0; depth < 12 && current.Parent is not null; depth++, current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "package.json"))) return current.FullName;
        return null;
    }

    private static ProjectIdentity Identity(string directory)
    {
        var fallback = Path.GetFileName(directory);
        try
        {
            using var stream = new FileStream(Path.Combine(directory, "package.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 0 && stream.Length <= 1024 * 1024)
            {
                var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    var value = name.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(value) && value.Length <= 120 && !value.Any(char.IsControl)) return new(value, directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new(fallback, directory);
    }
}
