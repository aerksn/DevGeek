using System.Globalization;
using System.Text.Json;

namespace OtterMonitor.Core;

/// <summary>Swedish source strings are stable resource keys; English is the default UI language.</summary>
public static class UiText
{
    private static readonly IReadOnlyDictionary<string, string> English = Load();
    public static string Language { get; private set; } = "en";
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "sv" ? "sv-SE" : "en-US");
    public static void SetLanguage(string? language)
    {
        Language = language == "sv" ? "sv" : "en";
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
    }
    public static string T(string text) => Language == "sv" ? text : English.GetValueOrDefault(text, text);
    public static string F(string format, params object?[] values) => string.Format(Culture, T(format), values);
    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("OtterMonitor.Core.Localization.en.json")
            ?? throw new InvalidOperationException("English localization resources are missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
