using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using KhaozEngine.Movement;

namespace KhaozEngine.Tests.Movement.Research;

internal static class ProfileMeasurement
{
    internal static (GroundNavigation Navigation, ProfileMetrics Metrics) Once(PhysicsNavBake capture, bool stable)
    {
        var execution = new NavBakeExecution(1) { UseStableLegacyReadScope = stable };
        var tuning = ProofFixture.Tuning;
        using var process = Process.GetCurrentProcess();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long locks = Monitor.LockContentionCount;
        TimeSpan cpu = process.TotalProcessorTime;
        double threadCpu = LinuxThreadCpu.Seconds();
        long processBytes = GC.GetTotalAllocatedBytes(precise: true);
        long threadBytes = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();

        // The only BuildProfile invocation in this executable. No warm-up or retry path exists.
        GroundNavigation navigation = capture.BuildProfile(tuning, default, GroundProfileOptions.Default, execution);

        long stopped = Stopwatch.GetTimestamp();
        long allocatedThread = GC.GetAllocatedBytesForCurrentThread() - threadBytes;
        long allocatedProcess = GC.GetTotalAllocatedBytes(precise: true) - processBytes;
        double mainCpu = LinuxThreadCpu.Seconds() - threadCpu;
        process.Refresh();
        double processCpu = (process.TotalProcessorTime - cpu).TotalSeconds;
        return (navigation, new ProfileMetrics(Stopwatch.GetElapsedTime(started, stopped).TotalSeconds,
            processCpu, mainCpu, allocatedProcess, allocatedThread, GC.CollectionCount(0) - gen0,
            GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2, Monitor.LockContentionCount - locks));
    }
}

internal static class LinuxThreadCpu
{
    // Linux CLOCK_THREAD_CPUTIME_ID measures the calling OS thread, including user and system CPU.
    // https://man7.org/linux/man-pages/man3/clock_gettime.3.html
    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ReadClock(int clock, out Timespec time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        internal long Seconds;
        internal long Nanoseconds;
    }

    internal static double Seconds()
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("The thread clock requires 64-bit Linux.");
        if (ReadClock(3, out Timespec time) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return time.Seconds + time.Nanoseconds / 1_000_000_000d;
    }
}
