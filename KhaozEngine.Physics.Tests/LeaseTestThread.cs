using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>A single coordinated operation, with a timeout only to fail a stranded test.</summary>
internal sealed class LeaseTestThread
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    readonly Thread _thread;
    Exception? _error;

    LeaseTestThread(Action action)
    {
        _thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { _error = error; }
        }) { IsBackground = true };
        _thread.Start();
    }

    internal static LeaseTestThread Start(Action action) => new(action);

    internal void Join()
    {
        Exception? error = JoinFailure();
        if (error is not null) ExceptionDispatchInfo.Throw(error);
    }

    Exception? JoinFailure()
    {
        Assert.True(_thread.Join(Timeout), "The coordinated physics operation did not complete.");
        return _error;
    }

    internal static void Await(ManualResetEventSlim signal) =>
        Assert.True(signal.Wait(Timeout), "The coordinated physics operation did not signal completion.");

    /// <summary>An interrupt is delivered at the attempted gate wait, including when it is pending before
    /// entry. Successful admission would complete without that exception and fail this assertion.</summary>
    internal static void AssertWaits(Action action)
    {
        using var attempting = new ManualResetEventSlim();
        LeaseTestThread thread = Start(() =>
        {
            attempting.Set();
            action();
        });
        Await(attempting);
        thread._thread.Interrupt();
        Assert.IsType<ThreadInterruptedException>(thread.JoinFailure());
    }
}
