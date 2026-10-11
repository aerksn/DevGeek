using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PortPilot.Core;

internal static class BalanceTests
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check, Func<Func<Task>, Task> throws, string? executable)
    {
        await test("USD balance accepts zero/negative amounts and rejects invalid currency, precision and sensitive fields", async () =>
        {
            foreach (var amount in new[] { "0.00", "-0.50", "123456.78" })
                check(BalanceMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, state = "ok", amount, currency = "USD" })).DollarAmount is not null, "Valid USD rejected.");
            foreach (var json in new[] { "{}", "null", "{\"schema\":2,\"state\":\"paused\"}", "{\"schema\":1,\"state\":\"ok\",\"amount\":\"$7.20\",\"currency\":\"USD\"}", "{\"schema\":1,\"state\":\"ok\",\"amount\":\"7.20\",\"currency\":\"EUR\"}", "{\"schema\":1,\"state\":\"ok\",\"amount\":\"1,234.56\",\"currency\":\"USD\"}", "{\"schema\":1,\"state\":\"ok\",\"amount\":\"7.200\",\"currency\":\"USD\"}", "{\"schema\":1,\"state\":\"paused\",\"amount\":\"7.20\"}", "{\"schema\":1,\"state\":\"paused\",\"token\":\"fixture-only\"}" })
                await throws(() => { BalanceMessage.Parse(Encoding.UTF8.GetBytes(json)); return Task.CompletedTask; });
        });
        await test("Balance errors preserve amount/time and stale or future readings never appear current", () =>
        {
            var store = new BalanceStore(Path.Combine(root, "balance-state-" + Guid.NewGuid().ToString("N"))); var now = DateTime.UtcNow;
            var first = store.Apply(new(1, "ok", "24.50", "USD"), now);
            check(first.IsFresh(now) && !first.IsFresh(now.AddMinutes(13)) && !first.IsFresh(now.AddSeconds(-1)), "Incorrect freshness.");
            var failed = store.Apply(new(1, "page_closed"), now.AddMinutes(1));
            check(failed.Amount == 24.50m && failed.ObservedUtc == now && !failed.IsFresh(now.AddMinutes(1)) && failed.Label(now).Contains("äldre") && store.Read() == failed, "Error renewed or lost the last balance.");
            return Task.CompletedTask;
        });
        await test("Native framing handles fragmented/multiple messages and rejects truncated or oversized input", async () =>
        {
            using var input = new MemoryStream();
            await BalanceNativeHost.WriteFrameAsync(input, Encoding.UTF8.GetBytes("first"));
            await BalanceNativeHost.WriteFrameAsync(input, Encoding.UTF8.GetBytes("second")); input.Position = 0;
            using var fragmented = new FragmentedStream(input);
            check(Encoding.UTF8.GetString((await BalanceNativeHost.ReadFrameAsync(fragmented))!) == "first", "First fragmented frame lost.");
            check(Encoding.UTF8.GetString((await BalanceNativeHost.ReadFrameAsync(fragmented))!) == "second" && await BalanceNativeHost.ReadFrameAsync(fragmented) is null, "Second frame or EOF lost.");
            foreach (var bytes in new byte[][] { [1, 0], [0, 0, 0, 0], [1, 16, 0, 0], [5, 0, 0, 0, 1] })
                await throws(async () => { using var broken = new MemoryStream(bytes); await BalanceNativeHost.ReadFrameAsync(broken); });
        });
        await test("Native host is disabled by default and invalid payloads never overwrite good balance or echo input", async () =>
        {
            var directory = Path.Combine(root, "balance-protocol-" + Guid.NewGuid().ToString("N"));
            var payload = JsonSerializer.SerializeToUtf8Bytes(new BalanceMessage(1, "ok", "24.50", "USD"), BalanceStore.JsonOptions);
            using var input = new MemoryStream(); using var output = new MemoryStream();
            await BalanceNativeHost.WriteFrameAsync(input, payload); input.Position = 0;
            check(await BalanceNativeHost.RunAsync(input, output, directory) == 0 && !File.Exists(new BalanceStore(directory).FilePath), "Disabled bridge stored data.");
            output.Position = 0; using var disabled = JsonDocument.Parse((await BalanceNativeHost.ReadFrameAsync(output))!);
            check(disabled.RootElement.GetProperty("error").GetString() == "disabled", "Disabled status missing.");
            new SettingsStore(directory).Save(new Settings { OpenAiBalanceEnabled = true });
            input.SetLength(0); output.SetLength(0); input.Position = output.Position = 0;
            await BalanceNativeHost.WriteFrameAsync(input, payload);
            await BalanceNativeHost.WriteFrameAsync(input, Encoding.UTF8.GetBytes("{\"schema\":1,\"state\":\"paused\",\"secret\":\"fixture-only\"}")); input.Position = 0;
            check(await BalanceNativeHost.RunAsync(input, output, directory) == 0, "Host failed."); output.Position = 0;
            using var accepted = JsonDocument.Parse((await BalanceNativeHost.ReadFrameAsync(output))!);
            using var rejected = JsonDocument.Parse((await BalanceNativeHost.ReadFrameAsync(output))!);
            check(accepted.RootElement.GetProperty("ok").GetBoolean() && !rejected.RootElement.GetProperty("ok").GetBoolean(), "Payload acceptance incorrect.");
            check(new BalanceStore(directory).Read() is { Amount: 24.50m, State: "ok" } && !Encoding.UTF8.GetString(output.ToArray()).Contains("fixture-only"), "Invalid payload changed state or echoed input.");
        });
        await test("Malformed balance remains intact and native manifest pins the exact extension and executable", async () =>
        {
            var directory = Path.Combine(root, "balance-corrupt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var store = new BalanceStore(directory); File.WriteAllText(store.FilePath, "broken-json");
            await throws(() => { store.Apply(new(1, "ok", "24.50", "USD"), DateTime.UtcNow); return Task.CompletedTask; });
            check(File.ReadAllText(store.FilePath) == "broken-json", "Corrupt state overwritten.");
            using var manifest = JsonDocument.Parse(new BrowserBridgeInstaller(directory).CreateManifest(Environment.ProcessPath!));
            check(manifest.RootElement.GetProperty("type").GetString() == "stdio" && manifest.RootElement.GetProperty("path").GetString() == Environment.ProcessPath
                && manifest.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(p => p.GetString()).SequenceEqual([BalanceNativeHost.ExtensionOrigin]), "Unexpected caller or executable accepted.");
        });
        await test("Background navigation only reads OpenAI Billing and distinguishes sign-in from other pages", () =>
        {
            check(BillingNavigation.IsOverview(BalanceNativeHost.BillingUrl + "?fixture=1") && BillingNavigation.IsOverview("https://platform.openai.com/account/billing/"), "Overview route rejected.");
            foreach (var url in new[] { "https://platform.openai.com.evil.test/account/billing", "http://platform.openai.com/account/billing", "https://platform.openai.com:8443/account/billing", "https://platform.openai.com/settings/organization/billing/history" })
                check(!BillingNavigation.IsOverview(url), "Unrelated route accepted: " + url);
            check(BillingNavigation.StateFor("https://auth.openai.com/log-in") == "login_required" && BillingNavigation.StateFor("https://platform.openai.com/home") == "paused", "Sign-in state incorrect.");
            check(BillingNavigation.CanAutoRefresh(BalanceNativeHost.BillingUrl, false, false, false) && BillingNavigation.CanAutoRefresh(BalanceNativeHost.BillingUrl, true, true, false), "Hidden/minimized overview did not permit refresh.");
            check(!BillingNavigation.CanAutoRefresh(BalanceNativeHost.BillingUrl, true, false, false) && !BillingNavigation.CanAutoRefresh(BalanceNativeHost.BillingUrl, false, false, true)
                && !BillingNavigation.CanAutoRefresh("https://auth.openai.com/log-in", false, false, false), "Visible window, active navigation or login was refreshed.");
            return Task.CompletedTask;
        });
        await test("Extension cannot overwrite the balance while the app uses its own background session", async () =>
        {
            var directory = Path.Combine(root, "background-mode-" + Guid.NewGuid().ToString("N"));
            new SettingsStore(directory).Save(new Settings { OpenAiBalanceEnabled = true, OpenAiBackgroundEnabled = true });
            var store = new BalanceStore(directory); var original = store.Apply(new(1, "ok", "24.50", "USD"), DateTime.UtcNow);
            using var input = new MemoryStream(); using var output = new MemoryStream();
            await BalanceNativeHost.WriteFrameAsync(input, JsonSerializer.SerializeToUtf8Bytes(new BalanceMessage(1, "ok", "1.00", "USD"), BalanceStore.JsonOptions)); input.Position = 0;
            check(await BalanceNativeHost.RunAsync(input, output, directory) == 0, "Host failed."); output.Position = 0;
            using var reply = JsonDocument.Parse((await BalanceNativeHost.ReadFrameAsync(output))!);
            check(reply.RootElement.GetProperty("error").GetString() == "background_mode" && store.Read() == original, "Extension changed an independent background reading.");
        });
        if (executable is null) return;
        await test("Packaged WPF balance host exchanges real stdio frames without opening desktop UI", async () =>
        {
            var directory = Path.Combine(root, "balance-packaged-" + Guid.NewGuid().ToString("N")); new SettingsStore(directory).Save(new Settings { OpenAiBalanceEnabled = true });
            var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--balance-host-test"); start.ArgumentList.Add(directory);
            using var process = Process.Start(start)!;
            try
            {
                await BalanceNativeHost.WriteFrameAsync(process.StandardInput.BaseStream, JsonSerializer.SerializeToUtf8Bytes(new BalanceMessage(1, "ok", "24.50", "USD"), BalanceStore.JsonOptions));
                var bytes = await BalanceNativeHost.ReadFrameAsync(process.StandardOutput.BaseStream).WaitAsync(TimeSpan.FromSeconds(15));
                using var reply = JsonDocument.Parse(bytes!);
                check(reply.RootElement.GetProperty("ok").GetBoolean() && new BalanceStore(directory).Read() is { Amount: 24.50m }, "Packaged protocol failed.");
                process.Refresh(); check(process.MainWindowHandle == IntPtr.Zero, "Host opened a window.");
                process.StandardInput.Close(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await process.WaitForExitAsync(timeout.Token);
                check(process.ExitCode == 0 && (await process.StandardError.ReadToEndAsync()).Length == 0, "Host did not exit cleanly.");
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        });
    }
    private sealed class FragmentedStream(Stream stream) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, Math.Min(1, count));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
