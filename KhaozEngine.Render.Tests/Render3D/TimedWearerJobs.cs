using System;
using System.Diagnostics;
using KhaozEngine.Simulation;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Times the batch's per-wearer jobs when the cost diagnostic runs a full measurement.</summary>
/// <remarks>It also splits the measuring thread's allocation inside the scheduler call between the jobs that ran
/// on that thread and the scheduler around them, so an allocating frame names its source.</remarks>
internal sealed class TimedWearerJobs : IJobScheduler
{
    private readonly IJobScheduler _scheduler;
    private readonly long[] _ticks;
    private readonly long[] _jobBytes;
    private readonly bool[] _ranOnCaller;
    private readonly Action<int> _timeJob;
    private Action<int>? _body;
    private int _callerThread;

    internal TimedWearerJobs(IJobScheduler scheduler, int wearerCount)
    {
        _scheduler = scheduler;
        _ticks = new long[wearerCount];
        _jobBytes = new long[wearerCount];
        _ranOnCaller = new bool[wearerCount];
        _timeJob = TimeJob;
    }

    internal long StartedAt { get; private set; }

    /// <summary>Measuring-thread bytes allocated inside the last scheduler call, jobs on that thread included.</summary>
    internal long SchedulerCallBytes { get; private set; }

    public void For(int count, Action<int> body)
    {
        StartedAt = Stopwatch.GetTimestamp();
        _body = body;
        _callerThread = Environment.CurrentManagedThreadId;
        Array.Clear(_jobBytes);
        Array.Clear(_ranOnCaller);
        long before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            _scheduler.For(count, _timeJob);
        }
        finally
        {
            SchedulerCallBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            _body = null;
        }
    }

    private void TimeJob(int index)
    {
        long start = Stopwatch.GetTimestamp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        _body!(index);
        _jobBytes[index] = GC.GetAllocatedBytesForCurrentThread() - before;
        _ranOnCaller[index] = Environment.CurrentManagedThreadId == _callerThread;
        _ticks[index] = Stopwatch.GetTimestamp() - start;
    }

    internal long SumJobTicks()
    {
        long total = 0;
        foreach (long ticks in _ticks) total += ticks;
        return total;
    }

    /// <summary>Bytes the last call's jobs allocated on the measuring thread, and the first such job's index.</summary>
    internal (long Bytes, int FirstJob) CallerJobAllocation()
    {
        long total = 0;
        int first = -1;
        for (int index = 0; index < _jobBytes.Length; index++)
        {
            if (!_ranOnCaller[index] || _jobBytes[index] == 0) continue;
            total += _jobBytes[index];
            if (first < 0) first = index;
        }
        return (total, first);
    }
}
