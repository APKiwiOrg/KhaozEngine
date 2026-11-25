using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Cumulative user and kernel CPU nanoseconds on the current thread.</summary>
internal static partial class ThreadCpuClock
{
    // The clock ID is platform-specific. The POSIX declarations below use the 64-bit timespec layout.
    private const int LinuxThreadCpuClockId = 3;
    private const int MacThreadCpuClockId = 16;
    private static readonly Func<long?>? ReadCurrent = FindClock();

    internal static long? Read() => ReadCurrent?.Invoke();

    private static Func<long?>? FindClock()
    {
        Func<long?>? clock = OperatingSystem.IsWindows() ? ReadWindows
            : !Environment.Is64BitProcess ? null
            : OperatingSystem.IsLinux() ? ReadLinux
            : OperatingSystem.IsMacOS() ? ReadMac
            : null;
        try
        {
            return clock?.Invoke().HasValue == true ? clock : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static long? ReadLinux() => LinuxClockGetTime(LinuxThreadCpuClockId, out Timespec time) == 0
        ? time.TotalNanoseconds : null;

    private static long? ReadMac() => MacClockGetTime(MacThreadCpuClockId, out Timespec time) == 0
        ? time.TotalNanoseconds : null;

    private static long? ReadWindows() => GetThreadTimes(GetCurrentThread(), out _, out _,
        out FileTime kernel, out FileTime user)
        ? (kernel.Ticks + user.Ticks) * 100
        : null;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Timespec
    {
        private readonly long _seconds;
        private readonly long _nanoseconds;
        internal long TotalNanoseconds => _seconds * 1_000_000_000 + _nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _low;
        private readonly uint _high;
        internal long Ticks => ((long)_high << 32) | _low;
    }

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    private static partial int LinuxClockGetTime(int clockId, out Timespec time);

    [LibraryImport("libSystem.dylib", EntryPoint = "clock_gettime")]
    private static partial int MacClockGetTime(int clockId, out Timespec time);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out FileTime creation, out FileTime exit,
        out FileTime kernel, out FileTime user);
}
