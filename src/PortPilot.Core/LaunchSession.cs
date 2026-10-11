using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PortPilot.Core;

// Start suspended, assign to a Windows job, then resume. Children cannot escape between start and assignment.
// Deliberately no KILL_ON_JOB_CLOSE: quitting the monitor leaves development servers running.
public sealed class LaunchSession : IDisposable
{
    private readonly SafeFileHandle? job;
    private readonly SafeFileHandle? process;
    public LaunchRecord Record { get; }
    public bool WasRestored { get; }
    public string LogPath { get; }
    public int Pid { get; }
    public bool WasStopped { get; private set; }
    public uint ProcessCount
    {
        get
        {
            if (job is null) return 0;
            if (!Native.QueryInformationJobObject(job, 1, out var info, Marshal.SizeOf<Native.Accounting>(), IntPtr.Zero)) throw new Win32Exception();
            return info.ActiveProcesses;
        }
    }
    public bool IsRunning => ProcessCount > 0;
    public uint? ExitCode => process is not null && Native.GetExitCodeProcess(process, out var exit) ? exit : null;

    private LaunchSession(SafeFileHandle? job, SafeFileHandle? process, LaunchRecord record, bool restored = false)
    { this.job = job; this.process = process; Record = record; LogPath = record.LogPath; Pid = record.Pid; WasRestored = restored; }

    public static void Validate(QuickStart entry)
    {
        var directory = Environment.ExpandEnvironmentVariables(entry.Directory.Trim());
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new DirectoryNotFoundException("Arbetsmappen finns inte. Redigera snabbstarten och välj en befintlig, absolut sökväg.");
        if (string.IsNullOrWhiteSpace(entry.Command) || entry.Command.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("Ange ett kommando på en rad.");
    }

    public static LaunchSession Start(QuickStart entry, string logDirectory, Action<LaunchRecord>? beforeResume = null)
    {
        Validate(entry);
        var directory = Environment.ExpandEnvironmentVariables(entry.Directory.Trim());
        Directory.CreateDirectory(logDirectory);
        var runId = Guid.NewGuid();
        var path = Path.GetFullPath(Path.Combine(logDirectory, $"{entry.Id:N}-{runId:N}.log"));
        var jobName = $"Local\\PortPilot.Launch.{entry.Id:N}.{runId:N}";
        var attributes = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = true };
        using var output = Native.CreateFile(path, 0x40000000, 3, ref attributes, 2, 0x80, IntPtr.Zero);
        using var input = Native.CreateFile("NUL", 0x80000000, 3, ref attributes, 3, 0x80, IntPtr.Zero);
        if (output.IsInvalid || input.IsInvalid) throw new Win32Exception();
        var job = Native.CreateJobObject(IntPtr.Zero, jobName);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(); }
        var info = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Flags = 0x100, StdOutput = output.DangerousGetHandle(), StdError = output.DangerousGetHandle(), StdInput = input.DangerousGetHandle() };
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var commandLine = new StringBuilder($"\"{shell}\" /d /s /c \"{entry.Command}\"");
        if (!Native.CreateProcess(shell, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x08000004, IntPtr.Zero, directory, ref info, out var created))
        { var error = new Win32Exception(); job.Dispose(); throw error; }
        var process = new SafeFileHandle(created.Process, true);
        using var thread = new SafeFileHandle(created.Thread, true);
        try
        {
            if (!Native.AssignProcessToJobObject(job, process)) throw new Win32Exception();
            var record = new LaunchRecord(entry.Id, runId, jobName, path, created.ProcessId);
            // Persist ownership before user code runs; a failed write must never leave a server running untracked.
            beforeResume?.Invoke(record);
            if (Native.ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            return new(job, process, record);
        }
        catch
        {
            Native.TerminateProcess(process, 1);
            process.Dispose(); job.Dispose(); throw;
        }
    }

    internal static LaunchSession Restore(LaunchRecord record)
    {
        var job = Native.OpenJobObject(0x0004 | 0x0008, false, record.JobName); // QUERY | TERMINATE
        if (!job.IsInvalid) return new(job, null, record, restored: true);
        var error = Marshal.GetLastWin32Error(); job.Dispose();
        if (error == 2) return new(null, null, record, restored: true); // Previous run finished; keep its log.
        throw new Win32Exception(error, "Kunde inte återansluta snabbstarten. Ingen ny körning har startats.");
    }

    // The kernel keeps the job alive for its processes, but removes its NAME when the last handle closes.
    // A detached host must keep a real handle until every descendant exits, even after the UI closes.
    public static int HoldJob(string jobName, string readyEvent)
    {
        using var job = Native.OpenJobObject(0x0004, false, jobName);
        if (job.IsInvalid) return 1;
        using var ready = EventWaitHandle.OpenExisting(readyEvent);
        ready.Set();
        while (true)
        {
            if (!Native.QueryInformationJobObject(job, 1, out var info, Marshal.SizeOf<Native.Accounting>(), IntPtr.Zero)) return 1;
            if (info.ActiveProcesses == 0) return 0;
            Thread.Sleep(500);
        }
    }

    public void Stop()
    {
        if (job is null) return;
        if (!Native.TerminateJobObject(job, 1)) throw new Win32Exception();
        WasStopped = true;
    }

    public async Task StopAsync()
    {
        Stop();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (IsRunning)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Processerna har inte avslutats ännu. Ingen ny körning har startats.");
            await Task.Delay(50);
        }
    }

    public string ReadLog()
    {
        using var file = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int maxBytes = 65536;
        var truncated = file.Length > maxBytes;
        if (truncated) file.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(file, Encoding.UTF8, true);
        var text = reader.ReadToEnd();
        // Strip terminal color codes for the native text viewer.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        return (truncated ? "… senaste 64 kB …\n" : "") + text;
    }

    public void Dispose() { process?.Dispose(); job?.Dispose(); }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
        {
            public int Size; public string? Reserved; public string? Desktop; public string? Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public ushort ShowWindow, ReservedSize; public IntPtr ReservedBytes, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct Accounting
        { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaultCount, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle OpenJobObject(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out Accounting info, int length, IntPtr returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
    }
}
