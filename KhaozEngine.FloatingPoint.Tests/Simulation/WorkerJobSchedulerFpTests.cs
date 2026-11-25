using System;
using System.Threading;
using KhaozEngine.Determinism;
using KhaozEngine.Simulation;
using KhaozEngine.Tests.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public sealed class WorkerJobSchedulerFpTests
{
    [Fact]
    public void Each_body_gets_canonical_rounding_on_both_threads()
    {
        if (!DeterministicFp.IsSupported) return;

        using var scheduler = new WorkerJobScheduler(1);
        using var started = new CountdownEvent(2);
        int caller = Environment.CurrentManagedThreadId;
        int callerObserved = -1, workerObserved = -1;
        try
        {
            FpRounding.SetTowardZero();
            scheduler.For(2, _ =>
            {
                FpRounding.SetTowardZero();
                started.Signal();
                started.Wait();
            });

            // No other code uses the dedicated worker. A missing per-job scope leaves the changed mode there.
            started.Reset();
            scheduler.For(2, _ =>
            {
                int rounding = FpRounding.Get();
                if (Environment.CurrentManagedThreadId == caller) callerObserved = rounding;
                else workerObserved = rounding;
                started.Signal();
                started.Wait();
            });

            Assert.Equal(FpRounding.ToNearest, callerObserved);
            Assert.Equal(FpRounding.ToNearest, workerObserved);
            Assert.Equal(FpRounding.TowardZero, FpRounding.Get());
        }
        finally
        {
            FpRounding.SetToNearest();
        }
    }
}
