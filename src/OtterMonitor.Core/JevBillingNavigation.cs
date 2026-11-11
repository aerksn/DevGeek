namespace OtterMonitor.Core;

public static class JevBillingNavigation
{
    public const string OverviewUrl = "https://console.typesafe.ai/settings/billing";
    private static bool IsConsole(Uri uri) => uri.Scheme == "https" && uri.Host == "console.typesafe.ai" && uri.Port == 443;
    public static bool IsOverview(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsConsole(uri)
        && uri.AbsolutePath.TrimEnd('/') is "/settings/billing" or "/billing";
    public static bool IsHome(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsConsole(uri)
        && uri.AbsolutePath.TrimEnd('/') is "" or "/home";
    public static string StateFor(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsConsole(uri)
        && uri.AbsolutePath.TrimEnd('/') == "/login" ? "login_required" : IsOverview(url) ? "ok" : "paused";
    public static bool CanAutoRefresh(string url, bool visible, bool minimized, bool navigating) => IsOverview(url) && (!visible || minimized) && !navigating;
}
