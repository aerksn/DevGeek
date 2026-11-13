using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OtterMonitor.Core;

// Keep the persisted values for the original two choices.
public enum BrowserAddressMode { Localhost = 0, DetectedIp = 1, Ipv4 = 2, Ipv6 = 3, NetworkIp = 4 }

public static class WebServiceOpener
{
    public static bool IsCandidate(PortEntry entry) => !entry.IsDocker && entry.Protocol == "tcp"
        && entry.Name.ToLowerInvariant() is "node" or "bun" or "deno" or "vite" or "next-server"
        && ServiceProbe.IsHttpCandidate(entry);

    public static Uri? SelectUrl(IReadOnlyList<PortEntry> entries, ProbeReading reading, DateTime now,
        BrowserAddressMode addressMode = BrowserAddressMode.Localhost)
    {
        if (now < reading.SampledUtc || now - reading.SampledUtc > TimeSpan.FromSeconds(30)) return null;
        var selected = reading.WebUrls?.Where(url => IsLocalWebUrl(url) && IPAddress.TryParse(url.Host.Trim('[', ']'), out var address)
            && entries.Any(entry => IsCandidate(entry) && entry.Port == url.Port && SupportsAddress(entry.Address, address)))
            .OrderBy(url => url.Port).ThenBy(url => AddressRank(IPAddress.Parse(url.Host.Trim('[', ']')), addressMode))
            .ThenBy(url => url.Host.Contains(':')).ThenBy(url => url.Host, StringComparer.Ordinal).FirstOrDefault();
        // Keep specific network-interface bindings intact: localhost only resolves to loopback.
        return selected is not null && addressMode == BrowserAddressMode.Localhost
            && IPAddress.TryParse(selected.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)
            ? new UriBuilder(selected) { Host = "localhost" }.Uri : selected;
    }

    public static IReadOnlyList<IPAddress> NetworkAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses).Select(info => info.Address)
                .Where(ip => !IPAddress.IsLoopback(ip) && !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast
                    && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
                .Distinct().OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .ThenBy(ip => ip.ToString(), StringComparer.Ordinal).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    // Expand wildcard listeners only to addresses on this machine and in the same address family.
    // The probe must confirm an HTTP response before SelectUrl can offer an expanded address.
    public static IReadOnlyList<PortEntry> ProbeEntries(IReadOnlyList<PortEntry> entries, BrowserAddressMode mode)
    {
        var network = mode == BrowserAddressMode.NetworkIp ? NetworkAddresses() : [];
        var expanded = entries.Concat(entries.Where(IsCandidate).SelectMany(entry => network
            .Where(ip => SupportsAddress(entry.Address, ip)).Select(ip => entry with { Address = ip.ToString() })));
        return expanded.Distinct().OrderBy(entry => AddressRank(ServiceProbe.LocalAddress(entry.Address), mode))
            .ThenBy(entry => entry.Address.Contains(':')).ThenBy(entry => entry.Address is not "127.0.0.1" and not "::1")
            .ThenBy(entry => entry.Address, StringComparer.Ordinal).ToArray();
    }

    public static bool CanUseAddress(IReadOnlyList<PortEntry> entries, BrowserAddressMode mode) =>
        mode is BrowserAddressMode.Localhost or BrowserAddressMode.DetectedIp
        || ProbeEntries(entries, mode).Any(entry => IsCandidate(entry) && AddressRank(ServiceProbe.LocalAddress(entry.Address), mode) == 0);

    private static int AddressRank(IPAddress? ip, BrowserAddressMode mode) => ip is not null && (mode switch
    {
        BrowserAddressMode.Ipv4 => ip.Equals(IPAddress.Loopback),
        BrowserAddressMode.Ipv6 => ip.Equals(IPAddress.IPv6Loopback),
        BrowserAddressMode.NetworkIp => !IPAddress.IsLoopback(ip),
        _ => true
    }) ? 0 : 1;

    private static bool SupportsAddress(string binding, IPAddress address) => IPAddress.TryParse(binding, out var ip)
        && (ip.Equals(address) || (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) && ip.AddressFamily == address.AddressFamily);

    private static bool IsLocalWebUrl(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https"
        && uri.UserInfo.Length == 0 && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any)
                && ServiceProbe.LocalAddress(ip.ToString()) is not null);

    public static ProcessStartInfo BrowserStart(Uri uri)
    {
        if (!IsLocalWebUrl(uri)) throw new InvalidOperationException(UiText.T("Ingen verifierad lokal webbadress är vald."));
        return new(uri.AbsoluteUri) { UseShellExecute = true };
    }

    public static void Open(Uri uri) => Process.Start(BrowserStart(uri));
}
