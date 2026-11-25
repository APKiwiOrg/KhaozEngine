using System;
using System.Threading;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public sealed class WorkerJobSchedulerInterruptTests
{
    [Fact]
    public void Interrupted_caller_joins_the_active_dispatch_before_reusing_the_scheduler()
    {
        using var scheduler = new WorkerJobScheduler(1);
        using var workerReady = new ManualResetEventSlim();
        using var interruptArmed = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var stopped = new ManualResetEventSlim();
        using var blockingCall = new ManualResetEvent(false);
        int[] first = new int[2], next = new int[3];
        bool deferred = false;
        Exception? failure = null;
        var caller = new Thread(() =>
        {
            try
            {
                int callerId = Environment.CurrentManagedThreadId;
                scheduler.For(first.Length, i =>
                {
                    Interlocked.Increment(ref first[i]);
                    if (Environment.CurrentManagedThreadId != callerId)
                    {
                        workerReady.Set();
                        releaseWorker.Wait();
                    }
                    else
                    {
                        workerReady.Wait();
                        Thread.CurrentThread.Interrupt();
                        interruptArmed.Set();
                    }
                });

                try
                {
                    blockingCall.WaitOne(0);
                }
                catch (ThreadInterruptedException)
                {
                    deferred = true;
                }
                scheduler.For(next.Length, i => Interlocked.Increment(ref next[i]));
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                stopped.Set();
                interruptArmed.Set();
            }
        }) { IsBackground = true };

        caller.UnsafeStart();
        interruptArmed.Wait();
        // Keep the worker busy while the caller reaches the join, with a release if an interrupt escapes early.
        while (!stopped.IsSet && (caller.ThreadState & ThreadState.WaitSleepJoin) == 0)
            Thread.Yield();
        releaseWorker.Set();
        caller.Join();

        Assert.Null(failure);
        Assert.True(deferred);
        Assert.Equal(new[] { 1, 1 }, first);
        Assert.Equal(new[] { 1, 1, 1 }, next);
    }

    [Fact]
    public void Interrupted_dispose_waits_for_active_work_and_defers_the_interrupt()
    {
        using var scheduler = new WorkerJobScheduler(1);
        using var workerReady = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var disposerStarted = new ManualResetEventSlim();
        using var disposerStopped = new ManualResetEventSlim();
        using var blockingCall = new ManualResetEvent(false);
        bool deferred = false;
        Exception? failure = null;
        var caller = new Thread(() =>
        {
            int callerId = Environment.CurrentManagedThreadId;
            scheduler.For(2, _ =>
            {
                if (Environment.CurrentManagedThreadId == callerId)
                {
                    workerReady.Wait();
                    return;
                }
                workerReady.Set();
                releaseWorker.Wait();
            });
        }) { IsBackground = true };

        var disposer = new Thread(() =>
        {
            try
            {
                Thread.CurrentThread.Interrupt();
                disposerStarted.Set();
                scheduler.Dispose();
                scheduler.Dispose();
                try
                {
                    blockingCall.WaitOne(0);
                }
                catch (ThreadInterruptedException)
                {
                    deferred = true;
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                disposerStopped.Set();
            }
        }) { IsBackground = true };

        caller.UnsafeStart();
        workerReady.Wait();
        disposer.UnsafeStart();
        disposerStarted.Wait();
        // Dispose must acquire the held scheduler gate, even with interruption already pending.
        while (!disposerStopped.IsSet && (disposer.ThreadState & ThreadState.WaitSleepJoin) == 0)
            Thread.Yield();
        releaseWorker.Set();
        caller.Join();
        disposer.Join();

        Assert.Null(failure);
        Assert.True(deferred);
        Assert.Throws<ObjectDisposedException>(() => scheduler.For(1, static _ => { }));
    }
}
