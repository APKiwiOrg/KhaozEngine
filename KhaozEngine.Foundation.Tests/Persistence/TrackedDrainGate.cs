using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Tests;

// Scenario-local controlled gate for the tracked drain scenario. It keeps the shared WriteGate's
// behavior (signal entry, block until released or the timeout passes, then throw the given timeout
// message) and adds trace entries for entry, release request, release observation and timeout. The
// payload filter stays at the call site, so the shared WriteGate itself is unchanged.
internal sealed class TrackedDrainGate(string name, TimeSpan timeout, string timeoutMessage, TrackedDrainTrace trace) : IDisposable
{
    private readonly ManualResetEventSlim release = new(false);

    public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Hold()
    {
        trace.Record("gate-entered", name);
        Entered.TrySetResult(true);
        if (!release.Wait(timeout))
        {
            trace.Record("gate-timeout", name);
            throw new TimeoutException(timeoutMessage);
        }

        trace.Record("gate-released", name);
    }

    public void Release(string requester)
    {
        trace.Record("gate-release-requested", $"{name} by {requester}");
        release.Set();
    }

    public void Dispose() => release.Dispose();
}
