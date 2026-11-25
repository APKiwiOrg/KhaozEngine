using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public sealed class WorkerJobSchedulerInterruptTests
{
    // A hang guard for the fixture, never an expected scheduling or completion time.
    private static readonly TimeSpan FixtureTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void Interrupted_caller_joins_the_active_dispatch_before_reusing_the_scheduler()
    {
        using var workerReady = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var firstWait = new ManualResetEvent(false);
        using var secondWait = new ManualResetEvent(true);
        int[] first = new int[2], next = new int[3];
        bool interruptArmed = false;
        var caller = new TestThread(() =>
        {
            using var scheduler = new WorkerJobScheduler(1);
            int callerId = Environment.CurrentManagedThreadId;
            scheduler.For(first.Length, i =>
            {
                Interlocked.Increment(ref first[i]);
                if (Environment.CurrentManagedThreadId != callerId)
                {
                    workerReady.Set();
                    Assert.True(releaseWorker.Wait(FixtureTimeout));
                }
                else
                {
                    Assert.True(workerReady.Wait(FixtureTimeout));
                    Thread.CurrentThread.Interrupt();
                    Volatile.Write(ref interruptArmed, true);
                }
            });

            AssertInterruptDeliveredOnce(firstWait, secondWait);
            scheduler.For(next.Length, i => Interlocked.Increment(ref next[i]));
        });

        caller.Start();
        ReleaseWhenArmed(ref interruptArmed, releaseWorker);
        caller.Join();

        Assert.Equal(new[] { 1, 1 }, first);
        Assert.Equal(new[] { 1, 1, 1 }, next);
    }

    [Fact]
    public void Interrupted_dispose_waits_for_active_work_and_defers_the_interrupt()
    {
        var scheduler = new WorkerJobScheduler(1);
        using var workerReady = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var firstWait = new ManualResetEvent(false);
        using var secondWait = new ManualResetEvent(true);
        bool interruptArmed = false;
        var caller = new TestThread(() =>
        {
            int callerId = Environment.CurrentManagedThreadId;
            scheduler.For(2, _ =>
            {
                if (Environment.CurrentManagedThreadId == callerId)
                {
                    Assert.True(workerReady.Wait(FixtureTimeout));
                    return;
                }
                workerReady.Set();
                Assert.True(releaseWorker.Wait(FixtureTimeout));
            });
        });

        var disposer = new TestThread(() =>
        {
            Thread.CurrentThread.Interrupt();
            Volatile.Write(ref interruptArmed, true);
            scheduler.Dispose();

            AssertInterruptDeliveredOnce(firstWait, secondWait);
            scheduler.Dispose();
        });

        caller.Start();
        try
        {
            Assert.True(workerReady.Wait(FixtureTimeout));
            disposer.Start();
            ReleaseWhenArmed(ref interruptArmed, releaseWorker);
        }
        finally
        {
            releaseWorker.Set();
        }
        caller.Join();
        disposer.Join();

        Assert.Throws<ObjectDisposedException>(() => scheduler.For(1, static _ => { }));
    }

    [Fact]
    public void Dispose_joins_without_entering_the_callers_wait_notifying_context()
    {
        var disposer = new TestThread(() =>
        {
            var scheduler = new WorkerJobScheduler(1);
            var context = new ThrowingWaitContext();
            SynchronizationContext? previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                scheduler.Dispose();
                scheduler.Dispose();
                Assert.Same(context, SynchronizationContext.Current);
                Assert.Throws<ObjectDisposedException>(() => scheduler.For(1, static _ => { }));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });

        disposer.Start();
        disposer.Join();
    }

    [Fact]
    public void Interrupt_left_by_worker_job_does_not_remove_the_worker_or_prevent_shutdown()
    {
        int[] first = new int[2], next = new int[2];
        var caller = new TestThread(() =>
        {
            using var firstRunning = new CountdownEvent(first.Length);
            using var bothRunning = new CountdownEvent(next.Length);
            var scheduler = new WorkerJobScheduler(1);
            int callerId = Environment.CurrentManagedThreadId;

            scheduler.For(first.Length, i =>
            {
                Interlocked.Increment(ref first[i]);
                firstRunning.Signal();
                Assert.True(firstRunning.Wait(FixtureTimeout));
                if (Environment.CurrentManagedThreadId != callerId)
                    Thread.CurrentThread.Interrupt();
            });
            scheduler.For(next.Length, i =>
            {
                Interlocked.Increment(ref next[i]);
                // The second dispatch requires both participants, including the interrupted worker.
                bothRunning.Signal();
                Assert.True(bothRunning.Wait(FixtureTimeout));
            });

            scheduler.Dispose();
            Assert.Throws<ObjectDisposedException>(() => scheduler.For(1, static _ => { }));
        });

        caller.Start();
        caller.Join();

        Assert.Equal(new[] { 1, 1 }, first);
        Assert.Equal(new[] { 1, 1 }, next);
    }

    private static void ReleaseWhenArmed(ref bool armed, ManualResetEventSlim releaseWorker)
    {
        bool observed;
        try
        {
            // The armed thread must not enter a fixture wait just to signal readiness.
            long start = Stopwatch.GetTimestamp();
            var spin = new SpinWait();
            while (!(observed = Volatile.Read(ref armed)) && Stopwatch.GetElapsedTime(start) < FixtureTimeout)
                spin.SpinOnce();
        }
        finally
        {
            releaseWorker.Set();
        }
        Assert.True(observed);
    }

    private static void AssertInterruptDeliveredOnce(WaitHandle first, WaitHandle second)
    {
        bool interrupted = false;
        try
        {
            first.WaitOne(FixtureTimeout);
        }
        catch (ThreadInterruptedException)
        {
            interrupted = true;
        }

        bool secondCompleted = second.WaitOne(FixtureTimeout);
        Assert.True(interrupted);
        Assert.True(secondCompleted);
    }

    private sealed class ThrowingWaitContext : SynchronizationContext
    {
        internal ThrowingWaitContext() => SetWaitNotificationRequired();

        public override int Wait(nint[] waitHandles, bool waitAll, int millisecondsTimeout) =>
            throw new InvalidOperationException("The scheduler passed an internal join to the application's wait callback.");
    }

    private sealed class TestThread
    {
        private readonly Thread _thread;
        private ExceptionDispatchInfo? _failure;

        internal TestThread(Action body)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception failure)
                {
                    _failure = ExceptionDispatchInfo.Capture(failure);
                }
            }) { IsBackground = true };
        }

        internal void Start() => _thread.UnsafeStart();

        internal void Join()
        {
            Assert.True(_thread.Join(FixtureTimeout));
            _failure?.Throw();
        }
    }
}
