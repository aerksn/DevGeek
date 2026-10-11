using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PortPilot.Core;

internal sealed record ProcessEnvironment(string? Directory, string[] Arguments);

/// <summary>Read-only process parameters. A denied, exited or unsupported process yields no metadata.</summary>
internal static class ProcessEnvironmentReader
{
    public static ProcessEnvironment? Read(int pid, DateTime startedUtc)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return null;
        try
        {
            using var handle = OpenProcess(0x0410, false, pid); // QUERY_INFORMATION | VM_READ, never write/terminate.
            if (handle.IsInvalid || !GetProcessTimes(handle, out long created, out _, out _, out _)
                || DateTime.FromFileTimeUtc(created) != startedUtc) return null;
            if (NtQueryInformationProcess(handle, 0, out BasicInformation info, Marshal.SizeOf<BasicInformation>(), out _) < 0) return null;
            if (NtQueryWow64Information(handle, 26, out IntPtr wow64Peb, IntPtr.Size, out _) < 0) return null;
            int pointerSize = wow64Peb == IntPtr.Zero ? 8 : 4;
            var peb = wow64Peb == IntPtr.Zero ? info.Peb : wow64Peb;
            var parameters = ReadPointer(handle, IntPtr.Add(peb, pointerSize == 8 ? 0x20 : 0x10), pointerSize);
            if (parameters == IntPtr.Zero) return null;
            // RTL_USER_PROCESS_PARAMETERS: CURDIR.DosPath and CommandLine, for x64 and WOW64.
            var directory = ReadString(handle, IntPtr.Add(parameters, pointerSize == 8 ? 0x38 : 0x24), pointerSize);
            var command = ReadString(handle, IntPtr.Add(parameters, pointerSize == 8 ? 0x70 : 0x40), pointerSize);
            return new(directory, Arguments(command));
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or ArgumentException or OverflowException)
        { return null; }
    }

    private static IntPtr ReadPointer(SafeProcessHandle handle, IntPtr address, int size)
    {
        var data = ReadBytes(handle, address, size);
        return data is null ? IntPtr.Zero : size == 8 ? new IntPtr(BitConverter.ToInt64(data)) : new IntPtr((long)BitConverter.ToUInt32(data));
    }

    private static string? ReadString(SafeProcessHandle handle, IntPtr address, int pointerSize)
    {
        var header = ReadBytes(handle, address, pointerSize == 8 ? 16 : 8);
        if (header is null) return null;
        var length = BitConverter.ToUInt16(header);
        var maximum = BitConverter.ToUInt16(header, 2);
        if (length == 0 || length > maximum || length > 32766 || length % 2 != 0) return null;
        var buffer = pointerSize == 8 ? new IntPtr(BitConverter.ToInt64(header, 8)) : new IntPtr((long)BitConverter.ToUInt32(header, 4));
        var data = ReadBytes(handle, buffer, length);
        return data is null ? null : Encoding.Unicode.GetString(data).TrimEnd('\0');
    }

    private static byte[]? ReadBytes(SafeProcessHandle handle, IntPtr address, int size)
    {
        if (address == IntPtr.Zero) return null;
        var bytes = new byte[size];
        return ReadProcessMemory(handle, address, bytes, size, out var read) && read.ToInt64() == size ? bytes : null;
    }

    private static string[] Arguments(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];
        var buffer = CommandLineToArgvW(command, out int count);
        if (buffer == IntPtr.Zero) return [];
        try
        {
            if (count < 1 || count > 4096) return [];
            // The executable itself is not evidence of a project directory.
            return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size)) ?? "").ToArray();
        }
        finally { LocalFree(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation { public IntPtr ExitStatus, Peb, Affinity, Priority, Pid, ParentPid; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(SafeProcessHandle handle, IntPtr address, [Out] byte[] bytes, int size, out IntPtr read);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle handle, int infoClass, out BasicInformation information, int size, out int returned);
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")] private static extern int NtQueryWow64Information(SafeProcessHandle handle, int infoClass, out IntPtr information, int size, out int returned);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
