namespace OtterMonitor.Core;

/// <summary>Presentation only. Hiding an account never disconnects it.</summary>
public sealed record DashboardVisibility
{
    public bool OpenAi { get; init; } = true;
    public bool Jev { get; init; } = true;
    public bool Anthropic { get; init; } = true;
    public bool Codex { get; init; } = true;
    public bool Claude { get; init; } = true;
}
