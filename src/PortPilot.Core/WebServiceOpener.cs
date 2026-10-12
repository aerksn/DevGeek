using System.Diagnostics;

namespace PortPilot.Core;

public static class WebServiceOpener
{
    public static bool IsCandidate(PortEntry entry) => !entry.IsDocker && entry.Protocol == "tcp"
        && entry.Name.ToLowerInvariant() is "node" or "bun" or "deno" or "vite" or "next-server"
        && ServiceProbe.IsHttpCandidate(entry);

    public static Uri? SelectUrl(IReadOnlyList<PortEntry> entries, ProbeReading reading, DateTime now)
    {
        if (now < reading.SampledUtc || now - reading.SampledUtc > TimeSpan.FromSeconds(30)) return null;
        return reading.WebUrls?.Where(url => IsLocalWebUrl(url) && entries.Any(entry => IsCandidate(entry)
                && entry.Port == url.Port && ServiceProbe.LocalAddress(entry.Address)?.Equals(ServiceProbe.LocalAddress(url.Host.Trim('[', ']'))) == true))
            .OrderBy(url => url.Port).ThenBy(url => url.Host.Contains(':')).FirstOrDefault();
    }

    private static bool IsLocalWebUrl(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https"
        && uri.UserInfo.Length == 0 && ServiceProbe.LocalAddress(uri.Host.Trim('[', ']')) is not null;

    public static ProcessStartInfo BrowserStart(Uri uri)
    {
        if (!IsLocalWebUrl(uri)) throw new InvalidOperationException("Ingen verifierad lokal webbadress är vald.");
        return new(uri.AbsoluteUri) { UseShellExecute = true };
    }

    public static void Open(Uri uri) => Process.Start(BrowserStart(uri));
}
