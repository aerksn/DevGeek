using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PortPilot.Core;

public sealed class ClaudeUsageInstaller(string dataDirectory, string settingsFile)
{
    public static string DefaultSettingsFile => Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "settings.json");
    public string CachePath => Path.Combine(dataDirectory, "claude-usage.json");
    public string ScriptPath => Path.Combine(dataDirectory, "claude-usage.ps1");
    // EncodedCommand keeps quotes/spaces/$ in a Windows profile path literal under both Bash and PowerShell.
    public string Command => "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
        + Convert.ToBase64String(Encoding.Unicode.GetBytes("& '" + Path.GetFullPath(ScriptPath).Replace("'", "''") + "'"));
    private JsonObject ReadSettings() => File.Exists(settingsFile)
        ? JsonNode.Parse(File.ReadAllText(settingsFile)) as JsonObject ?? throw new IOException("Claude-inställningarna är inte ett JSON-objekt.") : new();

    public bool IsInstalled()
    {
        try { return ReadSettings()["statusLine"] is JsonObject line && line["command"]?.GetValue<string>() == Command && File.Exists(ScriptPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
    }

    public void Install()
    {
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsFile))!);
        // Lock the settings file itself while reading/preserving unrelated fields. A concurrent editor fails instead of losing data.
        using var file = new FileStream(settingsFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (file.Length > 4 * 1024 * 1024) throw new IOException("Claude-inställningarna är för stora.");
        var bytes = new byte[file.Length]; file.ReadExactly(bytes);
        var settings = bytes.Length == 0 ? new JsonObject() : ParseSettings(bytes);
        var existing = settings["statusLine"];
        if (existing is not null && (existing is not JsonObject line || line["command"]?.GetValue<string>() != Command))
            throw new IOException("Claude Code har redan en egen statusrad. Otter Monitor har inte ändrat den. Kopplingen kan kombineras manuellt enligt README.");
        using var stream = typeof(ClaudeUsageInstaller).Assembly.GetManifestResourceStream("PortPilot.Core.claude-usage.ps1")!;
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd();
        if (!File.Exists(ScriptPath) || File.ReadAllText(ScriptPath) != script) File.WriteAllText(ScriptPath, script, new UTF8Encoding(false));
        if (existing is not null) return;
        settings["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = Command };
        if (bytes.Length > 0) File.WriteAllBytes(settingsFile + ".portpilot-" + Guid.NewGuid().ToString("N") + ".bak", bytes);
        var output = Encoding.UTF8.GetBytes(settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        try { file.Position = 0; file.Write(output); file.SetLength(output.Length); file.Flush(true); }
        catch { file.Position = 0; file.Write(bytes); file.SetLength(bytes.Length); file.Flush(true); throw; }
    }

    public void Uninstall()
    {
        if (!File.Exists(settingsFile)) return;
        using var file = new FileStream(settingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (file.Length > 4 * 1024 * 1024) throw new IOException("Claude-inställningarna är för stora.");
        var bytes = new byte[file.Length]; file.ReadExactly(bytes);
        var settings = ParseSettings(bytes);
        if (settings["statusLine"] is not JsonObject line || line["command"]?.GetValue<string>() != Command) return;
        settings.Remove("statusLine");
        var output = Encoding.UTF8.GetBytes(settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        try { file.Position = 0; file.Write(output); file.SetLength(output.Length); file.Flush(true); }
        catch { file.Position = 0; file.Write(bytes); file.SetLength(bytes.Length); file.Flush(true); throw; }
    }

    private static JsonObject ParseSettings(byte[] bytes) => JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) as JsonObject
        ?? throw new IOException("Ogiltiga Claude-inställningar.");
}
