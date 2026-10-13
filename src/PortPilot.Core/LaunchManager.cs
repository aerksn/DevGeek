using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PortPilot.Core;

public sealed record LaunchRecord(Guid QuickStartId, Guid RunId, string JobName, string LogPath, int Pid, int Version = 1);

/// <summary>Durable ownership of launches started by PortPilot; never guesses ownership from names or ports.</summary>
public sealed class LaunchManager(string directory, bool shadowCopyHost = false, string? hostExecutable = null)
{
    private readonly string root = Path.GetFullPath(directory);
    private string? hostPath;
    public string LogDirectory => Path.Combine(root, "logs");
    public string RecordPath(Guid id) => Path.Combine(root, "sessions", id.ToString("N") + ".json");

    public LaunchSession? Restore(QuickStart entry)
    {
        string json;
        try { json = File.ReadAllText(RecordPath(entry.Id)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var record = JsonSerializer.Deserialize<LaunchRecord>(json);
        if (record is null || record.Version != 1 || record.QuickStartId != entry.Id || record.RunId == Guid.Empty || record.Pid <= 0 ||
            record.JobName != $"Local\\PortPilot.Launch.{entry.Id:N}.{record.RunId:N}" ||
            !string.Equals(record.LogPath, Path.Combine(LogDirectory, $"{entry.Id:N}-{record.RunId:N}.log"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Snabbstartens sparade körning kunde inte läsas. Ingen ny körning har startats.");
        return LaunchSession.Restore(record);
    }

    public LaunchSession Start(QuickStart entry)
    {
        LaunchSession.Validate(entry);
        // Also serialize starts from separate processes; the saved record is checked while holding this lock.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        using var gate = new Mutex(false, $"Local\\PortPilot.Start.{key}.{entry.Id:N}");
        bool acquired;
        try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("Snabbstarten håller redan på att startas.");
        try
        {
            using var existing = Restore(entry);
            if (existing?.IsRunning == true) throw new InvalidOperationException("Snabbstarten körs redan. Återanslut eller använd Starta om.");
            var executable = PrepareHost();
            return LaunchSession.Start(entry, LogDirectory, record => { Save(record); StartHost(executable, record); });
        }
        finally { gate.ReleaseMutex(); }
    }

    private string PrepareHost()
    {
        if (hostPath is not null) return hostPath;
        var source = hostExecutable ?? Environment.ProcessPath ?? throw new InvalidOperationException("Kunde inte hitta Otter Monitors programfil.");
        if (!shadowCopyHost) return hostPath = source;
        // Keep the portable app replaceable while environments run. One cached host per app build, shared by launches.
        using var file = File.OpenRead(source);
        var hash = Convert.ToHexString(SHA256.HashData(file));
        var destination = Path.Combine(root, "runtime", hash, "PortPilot.LaunchHost.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination))
        {
            var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(source, temp); File.Move(temp, destination, overwrite: true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return hostPath = destination;
    }

    private static void StartHost(string executable, LaunchRecord record)
    {
        var eventName = $"Local\\PortPilot.Ready.{record.RunId:N}";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--keep-launch", record.JobName, eventName }) start.ArgumentList.Add(arg);
        using var host = Process.Start(start) ?? throw new InvalidOperationException("Kunde inte bevara snabbstartens processkoppling.");
        if (!ready.WaitOne(TimeSpan.FromSeconds(10)))
        {
            if (!host.HasExited) host.Kill();
            throw new TimeoutException("Snabbstartens hjälpprocess svarade inte. Kommandot har inte körts.");
        }
    }

    public async Task RestartAsync(QuickStart entry)
    {
        LaunchSession.Validate(entry); // Keep the current run alive when the replacement configuration is invalid.
        if (entry.Session is not { IsRunning: true } previous) throw new InvalidOperationException("Snabbstarten körs inte längre. Välj Starta.");
        await previous.StopAsync();
        entry.Session = null;
        previous.Dispose();
        entry.Session = Start(entry);
    }

    private void Save(LaunchRecord record)
    {
        var path = RecordPath(record.QuickStartId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, record);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
