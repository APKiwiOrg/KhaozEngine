using System;
using System.Diagnostics;
using KhaozEngine.Simulation;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Times the batch's per-wearer jobs when the cost diagnostic runs a full measurement.</summary>
internal sealed class TimedWearerJobs : IJobScheduler
{
    private readonly IJobScheduler _scheduler;
    private readonly long[] _ticks;
    private readonly Action<int> _timeJob;
    private Action<int>? _body;

    internal TimedWearerJobs(IJobScheduler scheduler, int wearerCount)
    {
        _scheduler = scheduler;
        _ticks = new long[wearerCount];
        _timeJob = TimeJob;
    }

    internal long StartedAt { get; private set; }

    public void For(int count, Action<int> body)
    {
        StartedAt = Stopwatch.GetTimestamp();
        _body = body;
        try
        {
            _scheduler.For(count, _timeJob);
        }
        finally
        {
            _body = null;
        }
    }

    private void TimeJob(int index)
    {
        long start = Stopwatch.GetTimestamp();
        _body!(index);
        _ticks[index] = Stopwatch.GetTimestamp() - start;
    }

    internal long SumJobTicks()
    {
        long total = 0;
        foreach (long ticks in _ticks) total += ticks;
        return total;
    }
}
