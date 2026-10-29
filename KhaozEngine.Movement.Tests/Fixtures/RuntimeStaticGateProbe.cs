using System;
using System.Threading;
using Xunit;

namespace KhaozEngine.Tests.Movement.Fixtures;

/// <summary>One operation whose only wait is the physics gate. An interrupt proves attempted exclusion.</summary>
internal static class RuntimeStaticGateProbe
{
    internal static void AssertWaits(Action operation)
    {
        using var attempt = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { attempt.Set(); operation(); }
            catch (Exception error) { failure = error; }
        })
        { IsBackground = true };
        thread.Start();
        Assert.True(attempt.Wait(TimeSpan.FromSeconds(30)));
        thread.Interrupt();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.IsType<ThreadInterruptedException>(failure);
    }
}
