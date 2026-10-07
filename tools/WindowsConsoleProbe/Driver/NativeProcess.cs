using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsConsoleValidation;

internal sealed class NativeProcess(Process process, SafeFileHandle processHandle, SafeFileHandle job) : IDisposable
{
    internal Process Process { get; } = process;

    internal static NativeProcess Start(string executable, string arguments, bool inheritConsole = false, uint flags = 0)
    {
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        NativeConsole.Require(!job.IsInvalid, "CreateJobObjectW");
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        try
        {
            NativeConsole.Require(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()), "SetInformationJobObject");
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
            if (inheritConsole)
            {
                startup.Flags = 0x100; // STARTF_USESTDHANDLES
                startup.Input = NativeConsole.GetStdHandle(NativeConsole.StdInput);
                startup.Output = NativeConsole.GetStdHandle(NativeConsole.StdOutput);
                startup.Error = NativeConsole.GetStdHandle(NativeConsole.StdError);
            }
            // Assign the job before any child code runs. Driver death closes the non-inherited job
            // handle and terminates cmd plus its probe, including failures before evidence is written.
            NativeConsole.Require(CreateProcessW(executable, new StringBuilder(Quote(executable) + " " + arguments),
                IntPtr.Zero, IntPtr.Zero, inheritConsole, flags | 4, IntPtr.Zero, null, ref startup, out ProcessInfo info), "CreateProcessW");
            using var thread = new SafeFileHandle(info.Thread, ownsHandle: true);
            var child = new SafeFileHandle(info.Process, ownsHandle: true);
            try
            {
                NativeConsole.Require(AssignProcessToJobObject(job, child), "AssignProcessToJobObject");
                Process managed = Process.GetProcessById(checked((int)info.ProcessId));
                try
                {
                    NativeConsole.Require(ResumeThread(thread) != uint.MaxValue, "ResumeThread");
                    return new NativeProcess(managed, child, job);
                }
                catch { managed.Dispose(); throw; }
            }
            catch { TerminateProcess(child, 125); child.Dispose(); throw; }
        }
        catch { job.Dispose(); throw; }
    }

    internal int Wait()
    {
        uint wait = WaitForSingleObject(processHandle, 20000);
        if (wait == 258) throw new TimeoutException("Child process exceeded 20 seconds.");
        NativeConsole.Require(wait == 0, "WaitForSingleObject");
        NativeConsole.Require(GetExitCodeProcess(processHandle, out uint code), "GetExitCodeProcess");
        return unchecked((int)code);
    }

    internal static string Quote(string value)
    {
        // Inputs are private paths, GUID markers and fixed tokens. Reject cmd expansion characters.
        if (value.IndexOfAny(['"', '\r', '\n', '%', '!']) >= 0 || value.EndsWith('\\'))
            throw new ArgumentException("Path or token cannot be represented safely in the cmd proof.");
        return "\"" + value + "\"";
    }

    public void Dispose()
    {
        try
        {
            job.Dispose();
            uint wait = WaitForSingleObject(processHandle, 5000);
            if (wait == 258) throw new TimeoutException("Job cleanup did not terminate its child.");
            NativeConsole.Require(wait == 0, "WaitForSingleObject during cleanup");
        }
        finally { processHandle.Dispose(); Process.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessUserTime, JobUserTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string? directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
}
