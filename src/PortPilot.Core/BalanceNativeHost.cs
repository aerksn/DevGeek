using System.Buffers.Binary;
using System.Text.Json;

namespace PortPilot.Core;

public static class BalanceNativeHost
{
    public const string ExtensionId = "dpfmlcmepinoenieelepgijlkghleeld";
    public const string ExtensionOrigin = "chrome-extension://" + ExtensionId + "/";
    public const string HostName = "com.portpilot.openai_balance";
    public const string BillingUrl = "https://platform.openai.com/settings/organization/billing/overview";
    public const int MaximumMessageBytes = 4096;

    public static async Task<int> RunAsync(Stream input, Stream output, string directory)
    {
        var store = new BalanceStore(directory);
        try
        {
            while (await ReadFrameAsync(input) is { } bytes)
            {
                object response;
                try
                {
                    var settings = new SettingsStore(directory).Load();
                    if (!settings.OpenAiBalanceEnabled) response = new { ok = false, error = "disabled" };
                    else if (settings.OpenAiBackgroundEnabled) response = new { ok = false, error = "background_mode" };
                    else
                    {
                        var message = BalanceMessage.Parse(bytes);
                        var snapshot = store.Apply(message, DateTime.UtcNow);
                        response = new { ok = true, state = snapshot.State };
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                { response = new { ok = false, error = "invalid_message_or_storage" }; }
                // Never echo page content or credentials to stdout; it contains framed replies only.
                await WriteFrameAsync(output, JsonSerializer.SerializeToUtf8Bytes(response, BalanceStore.JsonOptions));
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 1; }
    }

    public static async Task<byte[]?> ReadFrameAsync(Stream input)
    {
        var header = new byte[4];
        var first = await input.ReadAsync(header.AsMemory(0, 1));
        if (first == 0) return null;
        await input.ReadExactlyAsync(header.AsMemory(1));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size == 0 || size > MaximumMessageBytes) throw new InvalidDataException("Ogiltig meddelandelängd.");
        var message = new byte[(int)size];
        await input.ReadExactlyAsync(message);
        return message;
    }
    public static async Task WriteFrameAsync(Stream output, byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Ogiltig meddelandelängd.");
        var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        await output.WriteAsync(header); await output.WriteAsync(bytes); await output.FlushAsync();
    }
}
