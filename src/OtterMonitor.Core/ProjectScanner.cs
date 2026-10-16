using System.Text.Json;

namespace OtterMonitor.Core;

public sealed record ProjectCommand(string Label, string Command, bool Recommended);

/// <summary>A runnable project found on disk, with the commands that can become a quick start.</summary>
public sealed record ProjectCandidate(string Name, string Directory, string ServiceIcon, string ServiceLabel, IReadOnlyList<ProjectCommand> Commands)
{
    public ProjectCommand Recommended => Commands.FirstOrDefault(command => command.Recommended) ?? Commands[0];
    public string Location { get; init; } = Directory;
    public bool Matches(string query) => $"{Name} {Directory} {ServiceLabel} {string.Join(' ', Commands.Select(command => command.Command))}".Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Finds projects under the chosen folders by reading their manifests only: package.json, deno.json, Compose files,
/// web .csproj files and Django's manage.py. Nothing is executed and no package is installed.
/// </summary>
public static class ProjectScanner
{
    // Build output, dependencies and tool folders: never projects of their own, and often huge.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    { "node_modules", "bin", "obj", "dist", "build", "out", "target", "coverage", "vendor", "venv", "__pycache__" };
    // Long-running scripts first; the first one present is recommended.
    private static readonly string[] Preferred = ["dev", "start", "serve", "develop", "start:dev", "dev:server", "watch", "preview"];
    private static readonly string[] ServerWords = ["dev", "start", "serve", "watch", "preview"];
    private static readonly HashSet<string> Lifecycle = new(StringComparer.OrdinalIgnoreCase)
    { "preinstall", "install", "postinstall", "prepare", "prepublish", "prepublishOnly", "prepack", "postpack", "publish", "postpublish", "preversion", "version", "postversion", "dependencies" };
    private static readonly HashSet<string> TemplateNames = new(StringComparer.OrdinalIgnoreCase)
    { "nuxt-app", "nuxt-project", "vite-project", "vue-project", "react-app", "my-app", "my-project", "svelte-app", "sveltekit-app", "astro-project", "next-app", "nextjs-app", "remix-app", "app", "project" };
    private static readonly string[] ComposeFiles = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Common Windows code folders that exist on this computer, C:\repos first.</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { @"C:\repos", @"C:\src", @"C:\dev", Path.Combine(profile, "source", "repos"), Path.Combine(profile, "repos"), Path.Combine(profile, "code"), Path.Combine(profile, "Projects") }
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<ProjectCandidate> Scan(IEnumerable<string> roots, CancellationToken cancellationToken = default, int maxDepth = 4, int maxFolders = 6000)
    {
        var found = new List<ProjectCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth, string Root)>();
        foreach (var root in roots.Select(ProjectOpener.LocalDirectory).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)) queue.Enqueue((root, 0, root));
        var visited = 0;
        while (queue.Count > 0 && visited++ < maxFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, depth, root) = queue.Dequeue();
            if (!seen.Add(folder)) continue;
            foreach (var candidate in Inspect(folder))
                found.Add(candidate with { Location = Path.GetRelativePath(Path.GetDirectoryName(root) ?? root, folder) });
            if (depth >= maxDepth) continue;
            IEnumerable<DirectoryInfo> children;
            try { children = new DirectoryInfo(folder).EnumerateDirectories().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                // Hidden folders (.git, .next, .output, .venv) and links are skipped; links could loop or leave the root.
                if (child.Name.StartsWith('.') || Skipped.Contains(child.Name) || child.Attributes.HasFlag(FileAttributes.ReparsePoint) || child.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                queue.Enqueue((child.FullName, depth + 1, root));
            }
        }
        return found.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase).ThenBy(candidate => candidate.Directory, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every runnable project defined directly in this folder.</summary>
    public static IEnumerable<ProjectCandidate> Inspect(string folder)
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (Package(folder, folderName) is { } package) yield return package;
        if (Deno(folder, folderName) is { } deno) yield return deno;
        if (ComposeFiles.Any(file => File.Exists(Path.Combine(folder, file))))
            yield return new(folderName, folder, "docker", "Docker Compose",
                [new("up -d", "docker compose up -d", true), new("up", "docker compose up", false), new("up --build", "docker compose up --build", false)]);
        foreach (var web in WebProjects(folder)) yield return web;
        if (File.Exists(Path.Combine(folder, "manage.py")))
            yield return new(folderName, folder, "python", "Django", [new("runserver", "python manage.py runserver", true)]);
    }

    private static ProjectCandidate? Package(string folder, string folderName)
    {
        using var document = ReadJson(Path.Combine(folder, "package.json"));
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root) return null;
        var scripts = Scripts(root, "scripts");
        if (scripts.Count == 0) return null;
        var manager = PackageManager(folder, root);
        string Run(string script) => manager switch { "bun" => "bun run " + script, "pnpm" => "pnpm run " + script, "yarn" => "yarn " + script, _ => "npm run " + script };
        var name = root.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : folderName;
        // A starter template's default name says nothing about the app; its folder does.
        if (TemplateNames.Contains(name)) name = folderName;
        return new(name, folder, manager == "bun" ? "bun" : "node", manager switch { "bun" => "Bun", "pnpm" => "Node · pnpm", "yarn" => "Node · Yarn", _ => "Node · npm" }, Commands(scripts, Run));
    }

    private static ProjectCandidate? Deno(string folder, string folderName)
    {
        var file = new[] { "deno.json", "deno.jsonc" }.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
        if (file is null) return null;
        using var document = ReadJson(file);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root) return null;
        var tasks = Scripts(root, "tasks");
        return tasks.Count == 0 ? null : new(folderName, folder, "tcp", "Deno", Commands(tasks, task => "deno task " + task));
    }

    private static IEnumerable<ProjectCandidate> WebProjects(string folder)
    {
        string[] projects;
        try { projects = Directory.GetFiles(folder, "*.csproj"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var project in projects)
        {
            string text;
            try { text = File.ReadAllText(project); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            // Web SDKs host a server; class libraries and test projects do not.
            if (!text.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) && !text.Contains("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase)) continue;
            var file = Path.GetFileName(project);
            var single = projects.Length == 1;
            yield return new(Path.GetFileNameWithoutExtension(project), folder, "dotnet", ".NET",
                [new("watch", single ? "dotnet watch" : $"dotnet watch --project \"{file}\"", true), new("run", single ? "dotnet run" : $"dotnet run --project \"{file}\"", false)]);
        }
    }

    private static List<(string Name, string Body)> Scripts(JsonElement root, string property) =>
        root.TryGetProperty(property, out var scripts) && scripts.ValueKind == JsonValueKind.Object
            ? scripts.EnumerateObject().Where(script => script.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(script.Value.GetString())).Select(script => (script.Name, script.Value.GetString()!)).ToList()
            : [];

    private static List<ProjectCommand> Commands(List<(string Name, string Body)> scripts, Func<string, string> run)
    {
        var names = scripts.Select(script => script.Name).ToHashSet(StringComparer.Ordinal);
        // npm runs "predev"/"postdev" around "dev" by itself; they are not entry points.
        var usable = scripts.Where(script => !Lifecycle.Contains(script.Name) && !IsHook(script.Name, names) && IsSafeName(script.Name)).Select(script => script.Name).ToList();
        // Then other server-like scripts (dev:api, start:prod), then the rest in file order.
        var serverLike = usable.Where(name => !Preferred.Contains(name) && ServerWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        var ordered = Preferred.Where(usable.Contains).Concat(serverLike).Concat(usable).Distinct().ToList();
        var recommended = Preferred.FirstOrDefault(usable.Contains) ?? ordered.FirstOrDefault();
        return ordered.Select(name => new ProjectCommand(name, run(name), name == recommended)).ToList();
    }

    private static bool IsHook(string name, HashSet<string> names) =>
        name.StartsWith("pre", StringComparison.Ordinal) && names.Contains(name[3..]) || name.StartsWith("post", StringComparison.Ordinal) && names.Contains(name[4..]);

    // The name becomes part of a cmd.exe command line; keep to characters that need no quoting.
    private static bool IsSafeName(string name) => name.Length is > 0 and <= 80 && name.All(c => char.IsLetterOrDigit(c) || c is ':' or '-' or '_' or '.' or '/' or '@');

    private static string PackageManager(string folder, JsonElement root)
    {
        if (root.TryGetProperty("packageManager", out var declared) && declared.ValueKind == JsonValueKind.String && declared.GetString() is { } value)
            foreach (var manager in new[] { "bun", "pnpm", "yarn", "npm" }) if (value.StartsWith(manager + "@", StringComparison.OrdinalIgnoreCase)) return manager;
        // Workspaces keep the lockfile at the repository root, a few levels up.
        for (var directory = new DirectoryInfo(folder); directory is not null && directory.FullName.Length > 3; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "bun.lockb")) || File.Exists(Path.Combine(directory.FullName, "bun.lock"))) return "bun";
            if (File.Exists(Path.Combine(directory.FullName, "pnpm-lock.yaml"))) return "pnpm";
            if (File.Exists(Path.Combine(directory.FullName, "yarn.lock"))) return "yarn";
            if (File.Exists(Path.Combine(directory.FullName, "package-lock.json"))) return "npm";
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) break;
        }
        return "npm";
    }

    private static JsonDocument? ReadJson(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length < 1_000_000 ? JsonDocument.Parse(File.ReadAllText(path), Lenient) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
