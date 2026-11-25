using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using KhaozEngine.Determinism;

namespace KhaozEngine.Simulation;

/// <summary>An opt-in, reusable pool of dedicated background threads for synchronous, independent jobs.</summary>
/// <remarks>
/// The caller also runs jobs. Top-level calls on this scheduler are serialized. A call made from inside a
/// <see cref="WorkerJobScheduler"/> job runs inline on that job's thread, including calls to another instance.
/// Warm calls allocate no managed memory in the scheduler. Dispose the pool from outside its jobs.
/// </remarks>
public sealed class WorkerJobScheduler : IJobScheduler, IDisposable
{
    // Unclaimed indices are ints. Leave room for every participant to race with the stop signal without wrapping.
    private const long StoppedIndex = 1L << 32;

    [ThreadStatic]
    private static bool s_insideJob;

    [ThreadStatic]
    private static bool s_isDedicatedWorker;

    private readonly object _gate = new();
    private readonly Worker[] _workers;
    private volatile bool _disposed;

    private Action<int>? _body;
    private int _count;
    private long _next;
    private int _pendingWorkers;
    private ExceptionDispatchInfo? _failure;

    /// <param name="workerCount">Number of background threads, in addition to the caller. Zero runs inline.</param>
    public WorkerJobScheduler(int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workerCount);
        _workers = new Worker[workerCount];
        int started = 0;
        try
        {
            for (; started < _workers.Length; started++)
                _workers[started] = new Worker(this, started);
        }
        catch
        {
            bool interrupted = false;
            StopWorkers(started, ref interrupted);
            if (interrupted) Thread.CurrentThread.Interrupt();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>Each invocation enters a <see cref="DeterministicFpScope"/>, and so does the caller for the fan-out.
    /// If an invocation throws, new claims stop and this call joins every running job before rethrowing the first
    /// failure with its stack.</remarks>
    public void For(int count, Action<int> body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(body);
        if (s_insideJob)
        {
            RunInline(count, body);
            return;
        }

        bool interrupted = false;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (count <= 0) return;

                s_insideJob = true;
                try
                {
                    if (count == 1 || _workers.Length == 0)
                        RunInline(count, body);
                    else
                        RunFanOut(count, body, ref interrupted);
                }
                finally
                {
                    s_insideJob = false;
                }
            }
        }
        finally
        {
            // Re-arm only after the active dispatch and the scheduler gate have both been released.
            if (interrupted) Thread.CurrentThread.Interrupt();
        }
    }

    private static void RunInline(int count, Action<int> body)
    {
        for (int index = 0; index < count; index++)
            InvokeJob(body, index);
    }

    private void RunFanOut(int count, Action<int> body, ref bool interrupted)
    {
        using var scope = DeterministicFpScope.Enter();
        _body = body;
        _count = count;
        _next = -1;
        int workers = Math.Min(count - 1, _workers.Length);
        _pendingWorkers = workers;

        // Publish the job before waking workers, then join each awakened worker before replacing it.
        for (int i = 0; i < workers; i++) _workers[i].Wake.Set();

        Drain(body, count);

        // Each worker publishes its writes with an interlocked decrement. Acquire the final one before reading the result.
        SpinWait join = default;
        while (Volatile.Read(ref _pendingWorkers) != 0)
        {
            try
            {
                join.SpinOnce(sleep1Threshold: -1);
            }
            catch (ThreadInterruptedException)
            {
                interrupted = true;
            }
        }

        ExceptionDispatchInfo? failure = _failure;
        _body = null;
        _failure = null;
        failure?.Throw();
    }

    private void Drain(Action<int> body, int count)
    {
        while (true)
        {
            long index = Interlocked.Increment(ref _next);
            if (index >= count) return;
            try
            {
                InvokeJob(body, (int)index);
            }
            catch (Exception error)
            {
                if (Interlocked.Exchange(ref _next, StoppedIndex) < StoppedIndex)
                    _failure = ExceptionDispatchInfo.Capture(error);
                return;
            }
        }
    }

    private static void InvokeJob(Action<int> body, int index)
    {
        using var scope = DeterministicFpScope.Enter();
        if (!s_isDedicatedWorker)
        {
            body(index);
            return;
        }

        SynchronizationContext? context = SynchronizationContext.Current;
        try
        {
            body(index);
        }
        finally
        {
            // Jobs must leave the dedicated worker's ambient context intact, including for its idle wait.
            if (!ReferenceEquals(context, SynchronizationContext.Current))
                SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    /// <summary>Waits for an active top-level call, then stops and joins the dedicated workers.</summary>
    /// <exception cref="InvalidOperationException">Called from inside a dedicated scheduler job.</exception>
    public void Dispose()
    {
        if (s_insideJob)
            throw new InvalidOperationException("Dispose the worker scheduler outside its jobs.");
        bool entered = false, interrupted = false;
        try
        {
            while (!entered)
            {
                try
                {
                    Monitor.Enter(_gate, ref entered);
                }
                catch (ThreadInterruptedException)
                {
                    interrupted = true;
                }
            }

            if (!_disposed) StopWorkers(_workers.Length, ref interrupted);
        }
        finally
        {
            if (entered) Monitor.Exit(_gate);
            if (interrupted) Thread.CurrentThread.Interrupt();
        }
    }

    private void StopWorkers(int count, ref bool interrupted)
    {
        _disposed = true;
        for (int i = 0; i < count; i++) _workers[i].Wake.Set();
        for (int i = 0; i < count; i++) _workers[i].Join(ref interrupted);
    }

    private sealed class Worker
    {
        private readonly WorkerJobScheduler _scheduler;
        private readonly Thread _thread;
        internal AutoResetEvent Wake { get; } = new(false);

        internal Worker(WorkerJobScheduler scheduler, int index)
        {
            _scheduler = scheduler;
            try
            {
                _thread = new Thread(Run) { IsBackground = true, Name = $"KhaozEngine job worker {index + 1}" };
                _thread.UnsafeStart();
            }
            catch
            {
                Wake.Dispose();
                throw;
            }
        }

        private void Run()
        {
            s_isDedicatedWorker = true;
            while (true)
            {
                Wake.WaitOne();
                if (_scheduler._disposed) return;
                s_insideJob = true;
                _scheduler.Drain(_scheduler._body!, _scheduler._count);
                s_insideJob = false;
                // This is the last access to this dispatch. The wake event retains a Set that arrives before WaitOne.
                Interlocked.Decrement(ref _scheduler._pendingWorkers);
            }
        }

        internal void Join(ref bool interrupted)
        {
            while (true)
            {
                try
                {
                    _thread.Join();
                    break;
                }
                catch (ThreadInterruptedException)
                {
                    interrupted = true;
                }
            }
            Wake.Dispose();
        }
    }
}
