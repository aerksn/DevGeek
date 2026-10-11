namespace PortPilot.Core;

public static class BillingNavigation
{
    public const string OverviewUrl = "https://platform.openai.com/settings/organization/billing/overview";
    public static bool CanAutoRefresh(string url, bool visible, bool minimized, bool navigating) => IsOverview(url) && (!visible || minimized) && !navigating;
    public static bool IsOverview(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.Host == "platform.openai.com" && uri.Port == 443 && uri.AbsolutePath.TrimEnd('/') is "/settings/organization/billing/overview" or "/settings/organization/billing" or "/account/billing" or "/account/billing/overview";
    public static string StateFor(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "unreadable";
        if (uri.Host == "auth.openai.com" || uri.Host == "auth0.openai.com" || uri.AbsolutePath.StartsWith("/login", StringComparison.Ordinal)) return "login_required";
        return IsOverview(url) ? "ok" : "paused";
    }
    public static bool IsPlatformHome(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.Host == "platform.openai.com" && uri.Port == 443 && uri.AbsolutePath.TrimEnd('/') is "" or "/home";
}
