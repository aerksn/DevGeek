using System.Text.Json.Serialization;

namespace PortPilot.Core;

[JsonConverter(typeof(JsonStringEnumConverter<BackgroundMode>))]
public enum BackgroundMode { Normal, Opaque, Clear, Blur, Acrylic }

public sealed record AppearanceSettings
{
    public BackgroundMode Mode { get; init; } = BackgroundMode.Acrylic;
    public string Color { get; init; } = "#372B53";
    public int Transparency { get; init; } = 75;
    public bool ShowBorder { get; init; } = true;
    public AppearanceSettings Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : BackgroundMode.Acrylic,
        Color = IsColor(Color) ? Color.ToUpperInvariant() : "#372B53",
        Transparency = Math.Clamp(Transparency, 0, 100)
    };
    public static bool IsColor(string? color) => color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);
    public byte TintAlpha => (byte)Math.Round((100 - Math.Clamp(Transparency, 0, 100)) * 255d / 100);
}
