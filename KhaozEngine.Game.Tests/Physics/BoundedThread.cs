using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>A dedicated test thread. Every coordination wait is bounded, and a hang fails on its timeout.</summary>
internal sealed class BoundedThread
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    readonly Thread _thread;
    Exception? _error;

    BoundedThread(Action body)
    {
        _thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception error) { _error = error; }
        })
        { IsBackground = true };
    }

    internal static BoundedThread Start(Action body)
    {
        var thread = new BoundedThread(body);
        thread._thread.Start();
        return thread;
    }

    internal bool IsAlive => _thread.IsAlive;

    /// <summary>Interrupts the thread's current or next blocking wait.</summary>
    internal void Interrupt() => _thread.Interrupt();

    /// <summary>Joins within the timeout and rethrows the body's failure on the test thread.</summary>
    internal void Join()
    {
        Exception? error = JoinFailure();
        if (error is not null) ExceptionDispatchInfo.Throw(error);
    }

    /// <summary>Joins within the timeout and returns the body's failure, if any.</summary>
    internal Exception? JoinFailure()
    {
        Assert.True(_thread.Join(Timeout), "A coordinated test thread did not finish within its timeout.");
        return _error;
    }

    /// <summary>
    /// Joins every started thread within the timeout and reports every failure together: the
    /// coordinator's own first, then each body failure or missed join. Returns only when all of them
    /// stopped cleanly, so the caller may then release what the threads shared.
    /// </summary>
    internal static void JoinAll(Exception? coordinatorFailure, params BoundedThread?[] threads)
    {
        var failures = new List<Exception>();
        if (coordinatorFailure is not null) failures.Add(coordinatorFailure);
        foreach (BoundedThread? thread in threads)
        {
            if (thread is null) continue;
            if (!thread._thread.Join(Timeout))
                failures.Add(new TimeoutException("A coordinated test thread did not finish within its timeout."));
            else if (thread._error is not null)
                failures.Add(thread._error);
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    internal static void Arrive(Barrier barrier)
    {
        if (!barrier.SignalAndWait(Timeout))
            throw new TimeoutException("A coordinated test thread did not reach the barrier.");
    }

    internal static void Await(ManualResetEventSlim signal)
    {
        if (!signal.Wait(Timeout))
            throw new TimeoutException("A coordinated test signal was not raised.");
    }

    internal static void Until(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(condition, Timeout))
            throw new TimeoutException("A coordinated test condition was not reached.");
    }
}
