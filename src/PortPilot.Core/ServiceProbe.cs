using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PortPilot.Core;

public enum ServiceTone { Neutral, Good, Warning, Bad }
public sealed record ProbeReading(string Label, ServiceTone Tone, double? Milliseconds, string Hint, DateTime SampledUtc)
{
    public static ProbeReading Waiting => new("Väntar", ServiceTone.Neutral, null, "Kontrollerar tjänstens lokala portar…", DateTime.UtcNow);
    public string Text => Milliseconds is { } ms ? Label + " · " + (ms < 1 ? "<1" : Math.Round(ms).ToString()) + " ms" : Label;
}

public sealed class ServiceProbe : IDisposable
{
    private readonly SemaphoreSlim slots = new(6);
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, MaxResponseDrainSize = 0, ConnectTimeout = TimeSpan.FromMilliseconds(900) }) { Timeout = Timeout.InfiniteTimeSpan };
    private sealed record Result(int Port, bool? Reachable, int? HttpCode, double? Milliseconds, string Detail);

    public static IPAddress? LocalAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return null;
        if (ip.Equals(IPAddress.Any)) return IPAddress.Loopback;
        if (ip.Equals(IPAddress.IPv6Any)) return IPAddress.IPv6Loopback;
        if (IPAddress.IsLoopback(ip)) return ip;
        try { return NetworkInterface.GetAllNetworkInterfaces().SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses).Any(local => local.Address.Equals(ip)) ? ip : null; }
        catch (NetworkInformationException) { return null; }
    }
    public static bool IsHttpCandidate(PortEntry entry)
    {
        if (entry.Port is >= 24678 and <= 24700 or 9229 or 9230) return false; // HMR and debugger sockets.
        if (entry.IsDocker) return entry.ContainerPort is 80 or 443 or 3000 or 3001 or 3002 or 3210 or 3211 or 5000 or 5173 or 8000 or 8001 or 8080 or 8443 or 9000 or 11434;
        return entry.Name.ToLowerInvariant() is "node" or "bun" or "deno" or "dotnet" or "python" or "python3" or "nginx" or "httpd" or "caddy" or "vite" or "next-server";
    }
    public async Task<ProbeReading> MeasureAsync(IReadOnlyList<PortEntry> entries, CancellationToken cancellationToken = default)
    {
        var tcp = entries.Where(entry => entry.Protocol == "tcp").GroupBy(entry => entry.Port)
            .Select(group => group.OrderBy(entry => entry.Address.Contains(':')).ThenBy(entry => entry.Address is not "127.0.0.1" and not "::1").ToList()).ToList();
        if (tcp.Count == 0)
        {
            var udpHealth = entries.FirstOrDefault()?.ContainerHealth;
            return new(udpHealth switch { "unhealthy" => "Ej frisk", "starting" => "Startar", "paused" => "Pausad", "healthy" => "Frisk · UDP", _ => "UDP" },
                udpHealth switch { "unhealthy" => ServiceTone.Bad, "starting" or "paused" => ServiceTone.Warning, "healthy" => ServiceTone.Good, _ => ServiceTone.Neutral },
                null, "UDP-port visas. Ingen TCP- eller HTTP-svarstid kan mätas för den." + (udpHealth is null ? "" : "\nDocker health: " + udpHealth), DateTime.UtcNow);
        }
        var results = await Task.WhenAll(tcp.Select(async bindings =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                Result? result = null;
                foreach (var entry in bindings)
                {
                    result = await CheckAsync(entry, cancellationToken);
                    if (result.Reachable == true) return result;
                }
                return result!;
            }
            finally { slots.Release(); }
        }));
        cancellationToken.ThrowIfCancellationRequested();
        var hint = string.Join("\n", results.Select(result => $":{result.Port} · {result.Detail}"));
        var reached = results.Count(result => result.Reachable == true);
        var latency = results.Where(result => result.Reachable == true).Select(result => result.Milliseconds).Max();
        string label; ServiceTone tone;
        if (results.All(result => result.Reachable is null)) { label = "Ej kontrollerad"; tone = ServiceTone.Neutral; }
        else if (reached != results.Length) { label = reached == 0 ? "Svarar inte" : $"{reached}/{results.Length} svarar"; tone = reached == 0 ? ServiceTone.Bad : ServiceTone.Warning; }
        else
        {
            var code = results.Where(result => result.HttpCode is not null).Select(result => result.HttpCode).Max();
            label = code is not null ? "HTTP " + code : "TCP";
            tone = code >= 500 ? ServiceTone.Bad : code >= 400 ? ServiceTone.Warning : ServiceTone.Good;
        }
        var health = entries[0].ContainerHealth;
        if (health is "unhealthy" or "starting" or "paused")
        {
            label = health switch { "unhealthy" => "Ej frisk", "paused" => "Pausad", _ => "Startar" };
            tone = health == "unhealthy" ? ServiceTone.Bad : ServiceTone.Warning;
        }
        if (health is not null) hint += "\nDocker health: " + health;
        return new(label, tone, latency, hint + "\nSvarstiden är den långsammaste kontrollerade porten. HTTP mäts till svarshuvuden; TCP mäter anslutningen.", DateTime.UtcNow);
    }
    private async Task<Result> CheckAsync(PortEntry entry, CancellationToken cancellationToken)
    {
        var ip = LocalAddress(entry.Address);
        if (ip is null) return new(entry.Port, null, null, null, "Bindningen är inte en lokal IP-adress.");
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(700);
            using var client = new TcpClient(ip.AddressFamily);
            await client.ConnectAsync(ip, entry.Port, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentOutOfRangeException)
        { cancellationToken.ThrowIfCancellationRequested(); return new(entry.Port, false, null, null, "TCP-anslutningen misslyckades eller fick timeout."); }
        var tcpTime = watch.Elapsed.TotalMilliseconds;
        if (!IsHttpCandidate(entry)) return new(entry.Port, true, null, tcpTime, "TCP ansluten. Detta verifierar porten, inte applikationens hälsa.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(1100);
            var scheme = entry.Port is 443 or 8443 || entry.ContainerPort is 443 or 8443 ? "https" : "http";
            var uri = new UriBuilder(scheme, ip.ToString(), entry.Port, "/").Uri;
            watch.Restart();
            using var head = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, uri);
                watch.Restart();
                using var fallback = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                return new(entry.Port, true, (int)fallback.StatusCode, watch.Elapsed.TotalMilliseconds, $"HTTP {(int)fallback.StatusCode} från {uri}. Ingen omdirigering följs.");
            }
            return new(entry.Port, true, (int)response.StatusCode, watch.Elapsed.TotalMilliseconds, $"HTTP {(int)response.StatusCode} från {uri}. Ingen omdirigering följs.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or ObjectDisposedException)
        { cancellationToken.ThrowIfCancellationRequested(); return new(entry.Port, true, null, tcpTime, "TCP ansluten; inget verifierat HTTP-svar. HTTPS-certifikat kringgås inte."); }
    }
    public void Dispose() => http.Dispose();
}
