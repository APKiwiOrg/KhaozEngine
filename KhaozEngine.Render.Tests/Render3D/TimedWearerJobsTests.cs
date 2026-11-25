using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class TimedWearerJobsTests
{
    [Fact]
    public void Caller_allocation_is_attributed_to_the_exact_work_windows()
    {
        var scheduler = new AllocatingInlineScheduler();
        var probes = new TimedWearerJobs(scheduler, wearerCount: 1, measureTiming: false);
        long jobBytes = 0;
        Action<int> job = _ => jobBytes = Allocate();
        probes.For(1, job);
        Allocate();

        TimedWearerJobs.CallerAllocation result;
        long beforeScheduler, afterScheduler, gross;
        using (AllocationRegion.Start("wearer allocation attribution"))
        {
            long beforeBatch = GC.GetAllocatedBytesForCurrentThread();
            beforeScheduler = Allocate();
            probes.For(1, job);
            afterScheduler = Allocate();
            long afterBatch = GC.GetAllocatedBytesForCurrentThread();

            gross = afterBatch - beforeBatch;
            result = probes.SplitCallerAllocation(beforeBatch, afterBatch);
        }

        Assert.True(beforeScheduler > 0 && afterScheduler > 0 && scheduler.Bytes > 0 && jobBytes > 0);
        Assert.Equal(beforeScheduler + afterScheduler, result.BatchOutsideSchedulerBytes);
        Assert.Equal(scheduler.Bytes, result.SchedulerBytes);
        Assert.Equal(jobBytes, result.CallerJobBytes);
        Assert.Equal(0, result.FirstAllocatingJob);
        Assert.Equal(gross, result.BatchOutsideSchedulerBytes + result.SchedulerBytes + result.CallerJobBytes + result.ProbeBytes);
        Assert.Null(probes.SumJobTimes().CpuNanoseconds);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Allocate()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(new byte[128]);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private sealed class AllocatingInlineScheduler : IJobScheduler
    {
        internal long Bytes { get; private set; }

        public void For(int count, Action<int> body)
        {
            Bytes = Allocate();
            for (int index = 0; index < count; index++) body(index);
        }
    }
}
