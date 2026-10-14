using System;
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
