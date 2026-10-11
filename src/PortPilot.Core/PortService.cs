using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace PortPilot.Core;

public sealed class PortService
{
    private readonly ProjectNameResolver projects = new();
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool ordered, int family, int tableClass, uint reserved);

    public List<PortEntry> Scan()
    {
        var raw = ReadTable(2).Concat(ReadTable(23)).ToList();
        var owners = new Dictionary<int, (string Name, DateTime? Started, string? Path, ProjectIdentity? Project)>();
        foreach (var pid in raw.Select(r => r.Pid).Distinct())
        {
            var name = pid is 0 or 4 ? "System" : "Okänd process";
            DateTime? started = null;
            string? path = null;
            try
            {
                using var process = Process.GetProcessById(pid);
                name = process.ProcessName;
                started = process.StartTime.ToUniversalTime();
                try { path = process.MainModule?.FileName; } catch (Win32Exception) { }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
            var project = name.ToLowerInvariant() is "node" or "bun" or "deno" ? projects.Resolve(pid, started) : null;
            owners[pid] = (name, started, path, project);
        }
        return raw.Select(r =>
        {
            var p = owners[r.Pid];
            var kind = p.Name.ToLowerInvariant() switch { "node" => "node", "bun" => "bun", "deno" => "deno", "dotnet" => ".NET", "python" or "python3" => "python", _ => "TCP" };
            return new PortEntry(r.Port, r.Address, r.Pid, p.Name, kind, p.Started, p.Path,
                ProjectName: p.Project?.Name, ProjectDirectory: p.Project?.Directory);
        }).OrderBy(r => r.Port).ThenBy(r => r.Address).ToList();
    }

    private static List<(int Port, string Address, int Pid)> ReadTable(int family)
    {
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, 3, 0);
        if (result != 122 && result != 0) throw new Win32Exception((int)result);
        for (int retry = 0; retry < 5; retry++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, true, family, 3, 0);
                if (result == 122) continue;
                if (result != 0) throw new Win32Exception((int)result);
                var count = Marshal.ReadInt32(buffer);
                var rows = new List<(int, string, int)>();
                var stride = family == 2 ? 24 : 56;
                for (int i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + i * stride);
                    var portOffset = family == 2 ? 8 : 20;
                    var port = (Marshal.ReadByte(row, portOffset) << 8) + Marshal.ReadByte(row, portOffset + 1);
                    var bytes = new byte[family == 2 ? 4 : 16];
                    Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), bytes, 0, bytes.Length);
                    var address = family == 2 ? new IPAddress(bytes) : new IPAddress(bytes, (uint)Marshal.ReadInt32(row, 16));
                    var pid = Marshal.ReadInt32(row, family == 2 ? 20 : 52);
                    rows.Add((port, address.ToString(), pid));
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Portlistan ändrades för snabbt. Försök igen.");
    }

    public async Task StopAsync(PortEntry entry)
    {
        if (entry.IsDocker || !entry.CanStop) throw new InvalidOperationException("Den här processen kan inte stoppas från DevGeek.");
        using var process = Process.GetProcessById(entry.Pid);
        // Hold the original kernel handle while validating and terminating, protecting against PID reuse.
        _ = process.Handle;
        if (process.StartTime.ToUniversalTime() != entry.StartedUtc || !string.Equals(process.ProcessName, entry.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Processen har ändrats. Uppdatera portlistan och försök igen.");
        if (!Scan().Any(p => p.Pid == entry.Pid && p.Port == entry.Port && p.Address == entry.Address))
            throw new InvalidOperationException("Processen lyssnar inte längre på den här porten.");
        process.Kill(entireProcessTree: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await process.WaitForExitAsync(timeout.Token);
    }
}
