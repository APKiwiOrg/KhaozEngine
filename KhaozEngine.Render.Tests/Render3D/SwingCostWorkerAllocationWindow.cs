using System;
using System.Linq;
using System.Threading;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

internal sealed class SwingCostWorkerAllocationWindow : IDisposable
{
    readonly WorkerJobScheduler scheduler;
    readonly CountdownEvent arrived;
    readonly WaitHandle everyone;
    readonly ThreadCounter[] counters;
    readonly Action<int> sampleThread;
    ThreadCounter[] before = [];

    internal SwingCostWorkerAllocationWindow(WorkerJobScheduler scheduler, int workerCount)
    {
        this.scheduler = scheduler;
        int participants = workerCount + 1;
        arrived = new CountdownEvent(participants);
        everyone = arrived.WaitHandle;
        counters = new ThreadCounter[participants];
        sampleThread = SampleThread;
    }

    internal WorkerBytes[] Workers { get; private set; } = [];

    internal void Begin() => before = Capture();

    internal void End()
    {
        ThreadCounter[] after = Capture();
        Assert.Equal(before.Select(sample => sample.ThreadId), after.Select(sample => sample.ThreadId));
        int caller = Environment.CurrentManagedThreadId;
        Workers = before.Zip(after)
            .Where(pair => pair.First.ThreadId != caller)
            .Select(pair => new WorkerBytes(pair.First.ThreadId, pair.Second.AllocatedBytes - pair.First.AllocatedBytes))
            .ToArray();
    }

    ThreadCounter[] Capture()
    {
        arrived.Reset();
        scheduler.For(counters.Length, sampleThread);
        Assert.Equal(counters.Length, counters.Select(sample => sample.ThreadId).Distinct().Count());
        return counters.OrderBy(sample => sample.ThreadId).ToArray();
    }

    void SampleThread(int index)
    {
        // Park each participant in its first job until every thread has a slot for its own counter.
        arrived.Signal();
        everyone.WaitOne();
        counters[index] = new ThreadCounter(Environment.CurrentManagedThreadId, GC.GetAllocatedBytesForCurrentThread());
    }

    public void Dispose() => arrived.Dispose();

    readonly record struct ThreadCounter(int ThreadId, long AllocatedBytes);

    internal sealed record WorkerBytes(int ThreadId, long ManagedBytesAllocated);
}
