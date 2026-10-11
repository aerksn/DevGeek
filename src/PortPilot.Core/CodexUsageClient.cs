using System.Diagnostics;
using System.Text.Json;

namespace PortPilot.Core;

/// <summary>Read-only account RPC through the user's installed, authenticated Codex CLI.</summary>
public sealed class CodexUsageClient
{
    public int HelperPid { get; private set; }

    public async Task<UsageSnapshot> ReadAsync(string executable, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(18));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
        using var process = Process.Start(start) ?? throw new IOException("Codex kunde inte starta kvotläsaren.");
        HelperPid = process.Id;
        // Drain diagnostics without storing messages, paths, or authentication data.
        var diagnostics = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await SendAsync(process, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "portpilot", version = "1.0.0" } } }, timeout.Token);
            await ReceiveAsync(process, 1, timeout.Token);
            await SendAsync(process, new { method = "initialized", @params = new { } }, timeout.Token);
            await SendAsync(process, new { id = 2, method = "account/rateLimits/read" }, timeout.Token);
            return AssistantUsage.ParseCodex(await ReceiveAsync(process, 2, timeout.Token), DateTimeOffset.UtcNow);
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(shutdown.Token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                await diagnostics.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { }
            HelperPid = 0;
        }
    }

    private static async Task SendAsync(Process process, object message, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
    }

    private static async Task<JsonElement> ReceiveAsync(Process process, int id, CancellationToken token)
    {
        for (var count = 0; count < 100; count++)
        {
            var line = await process.StandardOutput.ReadLineAsync(token) ?? throw new IOException("Codex-kopplingen stängdes.");
            if (line.Length > 1_000_000) throw new IOException("Oväntat kvotsvar från Codex.");
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (!message.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var value) || value != id) continue;
            if (message.TryGetProperty("error", out _)) throw new IOException("Codex kunde inte läsa kvoten. Kontrollera inloggningen i Codex.");
            return message.GetProperty("result").Clone();
        }
        throw new IOException("Codex skickade inget kvotsvar.");
    }
}
