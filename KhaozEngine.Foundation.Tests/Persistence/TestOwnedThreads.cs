using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Tests;

// Test-owned threads keep controlled pauses and hang guards independent of pool scheduling.
// A pool thread that completes an awaited task queues the awaiter on its own local
// work-stealing queue, and other workers steal from it only after every global queue is empty. A
// pool thread that then blocks until that awaiter releases it can stay parked until a watchdog fires
// on a loaded runner. That was #1317.

/// <summary>Runs <see cref="KhaozEngine.Persistence.PersistenceQueue"/> drain passes on dedicated threads. Passes scheduled before <see cref="Start"/> wait in arrival order, which keeps the queue's drain latch held without blocking any thread. After Start every pass runs at once.</summary>
internal sealed class DedicatedDrainScheduler
{
    private readonly object sync = new();
    private readonly List<Action> held = new();
    private bool started;

    public void Schedule(Action pass)
    {
        lock (sync)
        {
            if (!started)
            {
                held.Add(pass);
                return;
            }
        }

        StartThread(pass);
    }

    public void Start()
    {
        Action[] passes;
        lock (sync)
        {
            started = true;
            passes = held.ToArray();
            held.Clear();
        }

        foreach (Action pass in passes)
        {
            StartThread(pass);
        }
    }

    private static void StartThread(Action pass)
        => new Thread(() => pass()) { IsBackground = true, Name = "persistence-test-drain" }.Start();
}

/// <summary>Runs an action on a new dedicated thread and reports it as a task, faulted with any exception it throws.</summary>
internal static class DedicatedThread
{
    // Thread.Start flows ExecutionContext. The context keeps awaits, including Task.Yield, on owned
    // threads too, so a timed handshake never depends on a pool continuation getting scheduled.
    public static Task Run(Func<Task> action)
        => Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new OwnedContinuationContext());
            action().GetAwaiter().GetResult();
        });

    public static Task Run(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        { IsBackground = true, Name = "persistence-test-actor" }.Start();
        return completion.Task;
    }

    private sealed class OwnedContinuationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
            => _ = Run(() =>
            {
                SetSynchronizationContext(this);
                callback(state);
            });
    }
}

/// <summary>Runs each queued task on a new dedicated thread instead of the pool. It still lets a task run inline
/// when the caller asks, so a continuation that would run synchronously on the completing thread still does.</summary>
internal sealed class OwnedThreadScheduler : TaskScheduler
{
    protected override void QueueTask(Task task)
        => new Thread(() => TryExecuteTask(task)) { IsBackground = true, Name = "persistence-test-continuation" }.Start();

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        => !taskWasPreviouslyQueued && TryExecuteTask(task);

    protected override IEnumerable<Task> GetScheduledTasks() => Array.Empty<Task>();
}

/// <summary>A pause that a test-owned thread holds until another test-owned thread releases it. It refuses a thread-pool thread, so a pause can never park the work that would release it.</summary>
internal sealed class ControlledHold(string timeoutMessage, TimeSpan timeout) : IDisposable
{
    private readonly ManualResetEventSlim released = new(false);

    public void Hold()
    {
        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            throw new InvalidOperationException("A controlled hold must not block a thread-pool thread.");
        }

        if (!released.Wait(timeout))
        {
            throw new TimeoutException(timeoutMessage);
        }
    }

    public void Release() => released.Set();

    public void Dispose() => released.Dispose();
}
