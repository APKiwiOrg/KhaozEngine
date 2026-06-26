using System;
using System.Collections.Generic;
using System.Threading;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class JobSchedulerTests
{
    [Fact]
    public void SingleThreaded_RunsEveryIndexInStrictOrder()
    {
        var order = new List<int>();
        new SingleThreadedJobScheduler().For(5, order.Add);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, order);
    }

    [Fact]
    public void SingleThreaded_NonPositiveCount_DoesNothing()
    {
        int calls = 0;
        var s = new SingleThreadedJobScheduler();
        s.For(0, _ => calls++);
        s.For(-3, _ => calls++);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ThreadPool_RunsEachIndexExactlyOnce()
    {
        const int n = 2000;
        var hits = new int[n];
        new ThreadPoolJobScheduler().For(n, i => Interlocked.Increment(ref hits[i]));
        Assert.All(hits, h => Assert.Equal(1, h));
    }

    [Fact]
    public void ThreadPool_CountOne_RunsBodyOnce()
    {
        int calls = 0;
        new ThreadPoolJobScheduler().For(1, _ => Interlocked.Increment(ref calls));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ThreadPool_NonPositiveCount_DoesNothing()
    {
        int calls = 0;
        var s = new ThreadPoolJobScheduler();
        s.For(0, _ => Interlocked.Increment(ref calls));
        s.For(-1, _ => Interlocked.Increment(ref calls));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ThreadPool_RespectsMaxDegreeOfParallelism()
    {
        // With a degree of 1, observed concurrency must never exceed 1.
        int active = 0, maxObserved = 0;
        var gate = new object();
        new ThreadPoolJobScheduler(maxDegreeOfParallelism: 1).For(50, _ =>
        {
            int now = Interlocked.Increment(ref active);
            lock (gate) maxObserved = Math.Max(maxObserved, now);
            Thread.Sleep(1);
            Interlocked.Decrement(ref active);
        });
        Assert.Equal(1, maxObserved);
    }

    [Fact]
    public void NullBody_Throws_OnBothSchedulers()
    {
        Assert.Throws<ArgumentNullException>(() => new SingleThreadedJobScheduler().For(1, null!));
        Assert.Throws<ArgumentNullException>(() => new ThreadPoolJobScheduler().For(1, null!));
    }

    [Fact]
    public void ThreadPool_InvalidMaxDegree_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThreadPoolJobScheduler(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThreadPoolJobScheduler(-2));
    }
}
