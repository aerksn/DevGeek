using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;

namespace OtterMonitor.App;

// Only session cookies from the app's own profile, scoped separately for OpenAI, Claude and JEV.
internal sealed class BillingSessionStore(string directory, bool claude = false, bool jev = false, bool anthropic = false)
{
    internal string FilePath => Path.Combine(directory, anthropic ? "anthropic-session.bin" : jev ? "jev-session.bin" : claude ? "claude-session.bin" : "billing-session.bin");
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);
    private const int MaximumBytes = 262144;
    // Keys from when the app was called PortPilot. Renaming them would make every saved login unreadable.
    private readonly byte[] entropy = Encoding.UTF8.GetBytes(anthropic ? "OtterMonitor.AnthropicSession.v1" : jev ? "OtterMonitor.JevSession.v1" : claude ? "PortPilot.ClaudeSession.v1" : "PortPilot.BillingSession.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    internal sealed record Cookie(string Name, string Value, string Domain, string Path, bool IsSecure, bool IsHttpOnly, CoreWebView2CookieSameSiteKind SameSite);
    private sealed record Snapshot(int Version, DateTime SavedUtc, Cookie[] Cookies);

    internal static bool IsAllowedDomain(string domain) => domain.TrimStart('.').ToLowerInvariant() is "openai.com" or "platform.openai.com" or "auth.openai.com" or "auth0.openai.com";
    internal bool IsSessionDomain(string domain) => anthropic ? domain.TrimStart('.').ToLowerInvariant() is "platform.claude.com" or "claude.com" or "console.anthropic.com" or "anthropic.com"
        : jev ? domain.TrimStart('.').ToLowerInvariant() is "typesafe.ai" or "console.typesafe.ai"
        : claude ? domain.TrimStart('.').Equals("claude.ai", StringComparison.OrdinalIgnoreCase) : IsAllowedDomain(domain);
    private bool IsValid(Cookie cookie) => cookie is not null && cookie.Domain is not null && IsSessionDomain(cookie.Domain)
        && !string.IsNullOrWhiteSpace(cookie.Name) && cookie.Name.Length <= 512 && cookie.Value is not null && cookie.Value.Length <= 65536
        && cookie.Path is not null && cookie.Path.StartsWith('/') && cookie.Path.Length <= 2048 && Enum.IsDefined(cookie.SameSite);

    internal Cookie[] Read(DateTime utcNow)
    {
        if (!File.Exists(FilePath)) return [];
        if (new FileInfo(FilePath).Length > MaximumBytes + 4096) throw new InvalidDataException("Ogiltig sessionsfil.");
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), entropy, DataProtectionScope.CurrentUser);
        try
        {
            if (plain.Length > MaximumBytes) throw new InvalidDataException("Ogiltig sessionsfil.");
            var snapshot = JsonSerializer.Deserialize<Snapshot>(plain, JsonOptions) ?? throw new InvalidDataException("Ogiltig sessionsfil.");
            if (snapshot.Version != 1 || snapshot.SavedUtc.Kind != DateTimeKind.Utc || snapshot.SavedUtc > utcNow || snapshot.Cookies is null
                || snapshot.Cookies.Length > 256 || snapshot.Cookies.Any(cookie => !IsValid(cookie))
                || snapshot.Cookies.DistinctBy(cookie => (cookie.Name, cookie.Domain, cookie.Path)).Count() != snapshot.Cookies.Length)
                throw new InvalidDataException("Ogiltig sessionsfil.");
            if (utcNow - snapshot.SavedUtc > MaximumAge) { Clear(); return []; }
            return snapshot.Cookies;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal void Save(IEnumerable<CoreWebView2Cookie> cookies, DateTime utcNow)
    {
        var session = cookies.Where(cookie => cookie.IsSession && IsSessionDomain(cookie.Domain))
            .Select(cookie => new Cookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.IsSecure, cookie.IsHttpOnly, cookie.SameSite))
            .DistinctBy(cookie => (cookie.Name, cookie.Domain, cookie.Path)).ToArray();
        if (session.Length == 0) { Clear(); return; }
        if (utcNow.Kind != DateTimeKind.Utc || session.Length > 256 || session.Any(cookie => !IsValid(cookie))) throw new InvalidDataException("Ogiltig sessionsdata.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(1, utcNow, session), JsonOptions);
        byte[] encrypted;
        try
        {
            if (plain.Length > MaximumBytes) throw new InvalidDataException(UiText.T("Sessionsdata är för stor."));
            encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(directory);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(encrypted); file.Flush(flushToDisk: true); }
            File.Move(temp, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal void Clear() { if (File.Exists(FilePath)) File.Delete(FilePath); }

    internal static async Task<List<CoreWebView2Cookie>> GetOwnCookiesAsync(CoreWebView2CookieManager manager)
    {
        var cookies = new List<CoreWebView2Cookie>();
        foreach (var url in new[] { "https://platform.openai.com/", OtterMonitor.Core.BillingNavigation.OverviewUrl, "https://auth.openai.com/log-in", "https://auth0.openai.com/authorize" })
            cookies.AddRange(await manager.GetCookiesAsync(url));
        return cookies.Where(cookie => IsAllowedDomain(cookie.Domain)).DistinctBy(cookie => (cookie.Name, cookie.Domain, cookie.Path)).ToList();
    }
    internal async Task<List<CoreWebView2Cookie>> GetCookiesAsync(CoreWebView2CookieManager manager) => anthropic
        ? (await manager.GetCookiesAsync(OtterMonitor.Core.AnthropicBillingNavigation.OverviewUrl))
            .Concat(await manager.GetCookiesAsync("https://console.anthropic.com/settings/billing"))
            .Where(cookie => IsSessionDomain(cookie.Domain)).DistinctBy(cookie => (cookie.Name, cookie.Domain, cookie.Path)).ToList()
        : jev
        ? await manager.GetCookiesAsync(OtterMonitor.Core.JevBillingNavigation.OverviewUrl)
        : claude ? await manager.GetCookiesAsync("https://claude.ai/settings/usage") : await GetOwnCookiesAsync(manager);
    internal async Task RestoreAsync(CoreWebView2CookieManager manager, Func<bool>? isCurrent = null)
    {
        var saved = Read(DateTime.UtcNow);
        if (saved.Length == 0) return;
        var current = (await GetCookiesAsync(manager).WaitAsync(TimeSpan.FromSeconds(10))).Select(cookie => (cookie.Name, cookie.Domain, cookie.Path)).ToHashSet();
        if (isCurrent is not null && !isCurrent()) return;
        foreach (var item in saved)
        {
            if (current.Contains((item.Name, item.Domain, item.Path))) continue; // Never replace a newer browser cookie.
            var cookie = manager.CreateCookie(item.Name, item.Value, item.Domain, item.Path);
            cookie.IsSecure = item.IsSecure; cookie.IsHttpOnly = item.IsHttpOnly; cookie.SameSite = item.SameSite;
            cookie.Expires = DateTime.MinValue;
            // Keep it a session cookie; never extend a server-provided expiry.
            manager.AddOrUpdateCookie(cookie);
        }
    }
}
