namespace OtterMonitor.Core;

public static class AnthropicBillingNavigation
{
    public const string OverviewUrl = "https://platform.claude.com/settings/billing";
    private static bool IsConsole(Uri uri) => uri.Scheme == "https" && uri.Port == 443
        && uri.Host is "platform.claude.com" or "console.anthropic.com";
    public static bool IsOverview(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsConsole(uri)
        && uri.AbsolutePath.TrimEnd('/') == "/settings/billing";
    public static bool IsHome(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsConsole(uri)
        && uri.AbsolutePath.TrimEnd('/') is "" or "/dashboard";
    public static string StateFor(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsConsole(uri)
        && (uri.AbsolutePath.StartsWith("/login", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/auth", StringComparison.Ordinal))
        ? "login_required" : IsOverview(url) ? "ok" : "paused";
    public static bool CanAutoRefresh(string url, bool visible, bool minimized, bool navigating) => IsOverview(url) && (!visible || minimized) && !navigating;
}
