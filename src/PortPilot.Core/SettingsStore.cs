using System.Text.Json;

namespace PortPilot.Core;

public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortPilot");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Settings Load()
    {
        if (!File.Exists(FilePath)) return new Settings();
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Inställningsfilen är tom.");
        if (settings.Version != 1 || settings.QuickStarts is null || settings.QuickStarts.Any(q => q is null || q.Id == Guid.Empty || string.IsNullOrWhiteSpace(q.Name) || q.Directory is null || q.Command is null) || settings.QuickStarts.Select(q => q.Id).Distinct().Count() != settings.QuickStarts.Count)
            throw new InvalidDataException("Inställningsfilen har ett ogiltigt format eller en nyare version.");
        settings.Appearance = (settings.Appearance ?? new()).Normalize();
        return settings;
    }

    public void Save(Settings settings)
    {
        System.IO.Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
