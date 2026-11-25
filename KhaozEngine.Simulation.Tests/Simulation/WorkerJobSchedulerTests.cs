using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

[Collection("AllocSensitive")]
public sealed class WorkerJobSchedulerTests
{
    [Fact]
    public void Reused_scheduler_runs_each_index_once()
    {
        using var scheduler = new WorkerJobScheduler(3);
        for (int count = 2; count <= 32; count++)
        {
            int[] hits = new int[count];
            scheduler.For(count, index => Interlocked.Increment(ref hits[index]));
            Assert.All(hits, hit => Assert.Equal(1, hit));
        }
    }

    [Theory]
    [InlineData(3, -1)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(0, 4)]
    public void Empty_and_inline_cases_run_on_the_caller(int workers, int count)
    {
        int caller = Environment.CurrentManagedThreadId;
        using var scheduler = new WorkerJobScheduler(workers);
        int[] threads = new int[Math.Max(0, count)];
        scheduler.For(count, i => threads[i] = Environment.CurrentManagedThreadId);
        Assert.All(threads, thread => Assert.Equal(caller, thread));
    }

    [Fact]
    public void Worker_failure_is_rethrown_with_its_stack_after_the_other_job_finishes()
    {
        using var scheduler = new WorkerJobScheduler(1);
        using var started = new CountdownEvent(2);
        int caller = Environment.CurrentManagedThreadId;
        bool callerFinished = false;
        var failure = new InvalidOperationException();
        var error = Assert.Throws<InvalidOperationException>(() => scheduler.For(2, _ =>
        {
            started.Signal();
            started.Wait();
            if (Environment.CurrentManagedThreadId != caller)
                ThrowJobFailure(failure);
            callerFinished = true;
        }));

        Assert.Same(failure, error);
        Assert.Contains(nameof(ThrowJobFailure), error.StackTrace);
        Assert.True(callerFinished);

        int[] recovery = new int[2];
        scheduler.For(recovery.Length, i => recovery[i] = i + 1);
        Assert.Equal(new[] { 1, 2 }, recovery);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowJobFailure(InvalidOperationException error) => throw error;

    [Fact]
    public void A_body_can_call_For_again_on_its_own_thread()
    {
        using var scheduler = new WorkerJobScheduler(1);
        using var started = new CountdownEvent(2);
        int[] outerThreads = new int[2];
        int[,] innerThreads = new int[2, 3];
        scheduler.For(outerThreads.Length, i =>
        {
            started.Signal();
            started.Wait();
            outerThreads[i] = Environment.CurrentManagedThreadId;
            scheduler.For(innerThreads.GetLength(1), j => innerThreads[i, j] = Environment.CurrentManagedThreadId);
        });

        Assert.NotEqual(outerThreads[0], outerThreads[1]);
        for (int i = 0; i < outerThreads.Length; i++)
            for (int j = 0; j < innerThreads.GetLength(1); j++)
                Assert.Equal(outerThreads[i], innerThreads[i, j]);
    }

    [Fact]
    public async Task Concurrent_callers_keep_their_job_sets_separate()
    {
        using var scheduler = new WorkerJobScheduler(2);
        using var ready = new CountdownEvent(2);
        int[] first = new int[15], second = new int[19];

        Task Run(int[] hits) => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            ready.Wait();
            scheduler.For(hits.Length, i => Interlocked.Increment(ref hits[i]));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await Task.WhenAll(Run(first), Run(second));

        Assert.All(first, hit => Assert.Equal(1, hit));
        Assert.All(second, hit => Assert.Equal(1, hit));
    }

    [Fact]
    public void Disposed_scheduler_refuses_work_even_for_inline_counts()
    {
        var scheduler = new WorkerJobScheduler(1);
        scheduler.Dispose();
        scheduler.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scheduler.For(1, static _ => { }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warm_fanout_can_wait_for_worker_completion_without_allocating(bool notifyWaits)
    {
        using var scheduler = new WorkerJobScheduler(1);
        using var workerReady = new ManualResetEventSlim();
        using var callerJobFinishing = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        SynchronizationContext? context = notifyWaits ? new WaitNotifyingContext() : null;
        SynchronizationContext? afterWait = null;
        ExceptionDispatchInfo? failure = null;
        long bytes = -1;
        int workerFinished = 0;
        int completedWhenReturned = 0;

        using var measured = new ManualResetEventSlim();
        var caller = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                int callerId = Environment.CurrentManagedThreadId;
                Action<int> job = _ =>
                {
                    if (Environment.CurrentManagedThreadId == callerId)
                    {
                        while (!workerReady.IsSet)
                            Thread.Yield();
                        callerJobFinishing.Set();
                    }
                    else
                    {
                        workerReady.Set();
                        releaseWorker.Wait();
                        Volatile.Write(ref workerFinished, 1);
                    }
                };

                // Warm the same scheduler and delegate, with the worker release already open.
                releaseWorker.Set();
                scheduler.For(2, job);
                releaseWorker.Reset();
                workerReady.Reset();
                callerJobFinishing.Reset();
                Volatile.Write(ref workerFinished, 0);
                measured.Set();
                long before = GC.GetAllocatedBytesForCurrentThread();
                scheduler.For(2, job);
                bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                completedWhenReturned = Volatile.Read(ref workerFinished);
                afterWait = SynchronizationContext.Current;
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
                measured.Set();
                callerJobFinishing.Set();
            }
        }) { IsBackground = true };

        // Use a separate controller for the measured dispatch. The caller's job signals without waiting.
        using (AllocationRegion.Start("fresh caller completion wait"))
        {
            caller.UnsafeStart();
            measured.Wait();
            callerJobFinishing.Wait();
            releaseWorker.Set();
            caller.Join();
        }
        failure?.Throw();

        Assert.Equal(1, completedWhenReturned);
        Assert.Equal(0, bytes);
        Assert.Same(context, afterWait);
    }

    sealed class WaitNotifyingContext : SynchronizationContext
    {
        internal WaitNotifyingContext() => SetWaitNotificationRequired();
    }

    [Fact]
    public void Warm_fanout_allocates_zero_bytes_on_the_caller_and_every_worker()
    {
        const int workers = 3, participants = workers + 1, rounds = 32;
        using var scheduler = new WorkerJobScheduler(workers);
        using var all = new CountdownEvent(participants);
        WaitHandle ready = all.WaitHandle;
        int[] threads = new int[participants];
        long[] samples = new long[participants];
        int callerThread = Environment.CurrentManagedThreadId;
        var workerContext = new WaitNotifyingContext();
        var inheritedContexts = new SynchronizationContext?[participants];
        Action<int> body = i =>
        {
            threads[i] = Environment.CurrentManagedThreadId;
            samples[i] = GC.GetAllocatedBytesForCurrentThread();

            // One pending job per participant. Each is held here until every thread has sampled itself.
            all.Signal();
            ready.WaitOne();
            if (threads[i] != callerThread)
            {
                inheritedContexts[i] = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(workerContext);
            }
        };

        void RunRound()
        {
            all.Reset();
            scheduler.For(participants, body);
        }

        for (int round = 0; round < rounds; round++) RunRound();
        var baseline = threads.Zip(samples).ToDictionary(pair => pair.First, pair => pair.Second);
        long callerBytes;
        using (AllocationRegion.Start("dedicated job workers"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < rounds; round++) RunRound();
            callerBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(participants, baseline.Count);
        Assert.Equal(participants, threads.Distinct().Count());
        Assert.All(inheritedContexts, context => Assert.Null(context));
        int caller = Environment.CurrentManagedThreadId;
        Assert.Contains(caller, threads);
        Assert.Equal(0, callerBytes);
        for (int i = 0; i < participants; i++)
        {
            // The caller's interval excludes the test's allocation-region and baseline setup.
            if (threads[i] != caller)
                Assert.Equal(0, samples[i] - baseline[threads[i]]);
        }
    }
}
