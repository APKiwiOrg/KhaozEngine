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

    private readonly object _gate = new();
    private readonly Worker[] _workers;
    private readonly ManualResetEvent _workersDone;
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
        _workersDone = new ManualResetEvent(false);
        int started = 0;
        try
        {
            for (; started < _workers.Length; started++)
                _workers[started] = new Worker(this, started);
        }
        catch
        {
            StopWorkers(started);
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
                    RunFanOut(count, body);
            }
            finally
            {
                s_insideJob = false;
            }
        }
    }

    private static void RunInline(int count, Action<int> body)
    {
        for (int index = 0; index < count; index++)
        {
            using var scope = DeterministicFpScope.Enter();
            body(index);
        }
    }

    private void RunFanOut(int count, Action<int> body)
    {
        using var scope = DeterministicFpScope.Enter();
        _workersDone.Reset();
        _body = body;
        _count = count;
        _next = -1;
        int workers = Math.Min(count - 1, _workers.Length);
        _pendingWorkers = workers;

        // Publish the job before waking workers, then join each awakened worker before replacing it.
        for (int i = 0; i < workers; i++) _workers[i].Wake.Set();

        Drain(body, count);
        _workersDone.WaitOne();

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
                using var scope = DeterministicFpScope.Enter();
                body((int)index);
            }
            catch (Exception error)
            {
                if (Interlocked.Exchange(ref _next, StoppedIndex) < StoppedIndex)
                    _failure = ExceptionDispatchInfo.Capture(error);
                return;
            }
        }
    }

    /// <summary>Waits for an active top-level call, then stops and joins the dedicated workers.</summary>
    /// <exception cref="InvalidOperationException">Called from inside a dedicated scheduler job.</exception>
    public void Dispose()
    {
        if (s_insideJob)
            throw new InvalidOperationException("Dispose the worker scheduler outside its jobs.");
        lock (_gate)
        {
            if (!_disposed) StopWorkers(_workers.Length);
        }
    }

    private void StopWorkers(int count)
    {
        _disposed = true;
        for (int i = 0; i < count; i++) _workers[i].Wake.Set();
        for (int i = 0; i < count; i++) _workers[i].Join();
        _workersDone.Dispose();
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
            while (true)
            {
                Wake.WaitOne();
                if (_scheduler._disposed) return;
                s_insideJob = true;
                _scheduler.Drain(_scheduler._body!, _scheduler._count);
                s_insideJob = false;
                if (Interlocked.Decrement(ref _scheduler._pendingWorkers) == 0)
                    _scheduler._workersDone.Set();
            }
        }

        internal void Join()
        {
            _thread.Join();
            Wake.Dispose();
        }
    }
}
