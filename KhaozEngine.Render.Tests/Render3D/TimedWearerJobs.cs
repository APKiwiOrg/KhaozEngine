using System;
using System.Collections.Generic;
using System.Diagnostics;
using KhaozEngine.Attachments;
using KhaozEngine.Simulation;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Samples wall time, thread CPU time and caller allocations around the individual wearer jobs.</summary>
internal sealed class TimedWearerJobs : IJobScheduler
{
    private readonly IJobScheduler _scheduler;
    private readonly JobSample[] _samples;
    private readonly bool _measureTiming;
    private readonly bool _disableSheet;
    private readonly Action<int> _timeJob;
    private Action<int>? _body;
    private int _callerThread;
    private int _count;
    private long _entryBytes;
    private long _exitBytes;
    private long _schedulerCallBytes;

    internal TimedWearerJobs(IJobScheduler scheduler, int wearerCount, bool measureTiming, bool disableSheet = false)
    {
        _scheduler = scheduler;
        _measureTiming = measureTiming;
        _disableSheet = disableSheet;
        _samples = new JobSample[wearerCount];
        _timeJob = TimeJob;
    }

    internal long StartedAt { get; private set; }
    internal long? StartedCpu { get; private set; }

    public void For(int count, Action<int> body)
    {
        _entryBytes = GC.GetAllocatedBytesForCurrentThread();
        StartedAt = _measureTiming ? Stopwatch.GetTimestamp() : 0;
        StartedCpu = _measureTiming ? ThreadCpuClock.Read() : null;
        _body = body;
        _callerThread = Environment.CurrentManagedThreadId;
        _count = count;

        long beforeScheduler = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            _scheduler.For(count, _timeJob);
        }
        finally
        {
            _schedulerCallBytes = GC.GetAllocatedBytesForCurrentThread() - beforeScheduler;
            _body = null;
            _exitBytes = GC.GetAllocatedBytesForCurrentThread();
        }
    }

    private void TimeJob(int index)
    {
        long beforeWrapper = GC.GetAllocatedBytesForCurrentThread();
        long startedAt = _measureTiming ? Stopwatch.GetTimestamp() : 0;
        long? cpuBefore = _measureTiming ? ThreadCpuClock.Read() : null;
        long beforeBody = GC.GetAllocatedBytesForCurrentThread();
        bool previous = ChainSheetContact.DisabledForTests;
        ChainSheetContact.DisabledForTests = _disableSheet;
        try
        {
            _body!(index);
        }
        finally
        {
            ChainSheetContact.DisabledForTests = previous;
        }
        long bodyBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBody;
        long? cpuNanoseconds = _measureTiming ? ThreadCpuClock.Read() - cpuBefore : null;
        long wallTicks = _measureTiming ? Stopwatch.GetTimestamp() - startedAt : 0;
        int threadId = Environment.CurrentManagedThreadId;
        bool onCaller = threadId == _callerThread;
        long wrapperBytes = GC.GetAllocatedBytesForCurrentThread() - beforeWrapper;
        _samples[index] = new JobSample(wallTicks, cpuNanoseconds, onCaller, threadId, bodyBytes, wrapperBytes);
    }

    internal (long WallTicks, long? CpuNanoseconds) SumJobTimes()
    {
        long wallTicks = 0;
        long? cpuNanoseconds = 0;
        foreach (ref readonly JobSample sample in _samples.AsSpan(0, _count))
        {
            wallTicks += sample.WallTicks;
            cpuNanoseconds += sample.CpuNanoseconds;
        }
        return (wallTicks, cpuNanoseconds);
    }

    internal void AddWorkerBytes(Dictionary<int, long> bytesByThread)
    {
        foreach (ref readonly var sample in _samples.AsSpan(0, _count))
            if (!sample.OnCaller)
                bytesByThread[sample.ThreadId] = bytesByThread.GetValueOrDefault(sample.ThreadId) + sample.BodyBytes;
    }

    internal CallerAllocation SplitCallerAllocation(long beforeBatch, long afterBatch)
    {
        long callerJobBytes = 0, callerJobWrapperBytes = 0;
        int firstAllocatingJob = -1;
        for (int index = 0; index < _count; index++)
        {
            ref readonly JobSample sample = ref _samples[index];
            if (!sample.OnCaller) continue;
            callerJobBytes += sample.BodyBytes;
            callerJobWrapperBytes += sample.WrapperBytes;
            if (firstAllocatingJob < 0 && sample.BodyBytes != 0) firstAllocatingJob = index;
        }

        long batchOutsideSchedulerBytes = (_entryBytes - beforeBatch) + (afterBatch - _exitBytes);
        long schedulerBytes = _schedulerCallBytes - callerJobWrapperBytes;
        long probeBytes = (_exitBytes - _entryBytes - _schedulerCallBytes) + (callerJobWrapperBytes - callerJobBytes);
        return new CallerAllocation(batchOutsideSchedulerBytes, schedulerBytes, callerJobBytes, probeBytes, firstAllocatingJob);
    }

    private readonly record struct JobSample(long WallTicks, long? CpuNanoseconds, bool OnCaller, int ThreadId, long BodyBytes,
        long WrapperBytes);

    internal readonly record struct CallerAllocation(long BatchOutsideSchedulerBytes, long SchedulerBytes,
        long CallerJobBytes, long ProbeBytes, int FirstAllocatingJob);
}
