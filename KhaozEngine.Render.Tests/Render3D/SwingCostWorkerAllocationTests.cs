using System;
using System.Threading;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class SwingCostWorkerAllocationTests
{
    [Fact]
    public void Worker_allocation_evidence_includes_work_outside_the_timed_callbacks()
    {
        const int workerCount = 3;
        const int participants = workerCount + 1;
        const int payloadBytes = 65536;
        using var workers = new WorkerJobScheduler(workerCount);
        using var allocations = new SwingCostWorkerAllocationWindow(workers, workerCount);
        using var arrived = new CountdownEvent(participants);
        WaitHandle everyone = arrived.WaitHandle;
        int caller = Environment.CurrentManagedThreadId;
        var retained = new byte[participants][];
        var profiledJobs = new TimedWearerJobs(workers, participants, measureTiming: false);

        void JoinParticipants(int index)
        {
            arrived.Signal();
            everyone.WaitOne();
        }

        void AllocateOnEveryWorker()
        {
            arrived.Reset();
            workers.For(participants, index =>
            {
                if (Environment.CurrentManagedThreadId != caller) retained[index] = new byte[payloadBytes];
                JoinParticipants(index);
            });
        }

        profiledJobs.For(participants, JoinParticipants);
        allocations.Begin();
        AllocateOnEveryWorker();
        arrived.Reset();
        profiledJobs.For(participants, JoinParticipants);
        AllocateOnEveryWorker();
        allocations.End();

        Assert.Equal(workerCount, allocations.Workers.Length);
        Assert.All(allocations.Workers, worker => Assert.True(worker.ManagedBytesAllocated >= 2 * payloadBytes));
        GC.KeepAlive(retained);
    }
}
