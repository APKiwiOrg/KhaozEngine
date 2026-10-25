using System;
using System.Threading;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfUnitBudget(RendererPerfClock Clock, long ColdStart, long Deadline,
    CancellationToken Cancellation, Action<string>? Boundary = null)
{
    public void Check(string stage)
    {
        Boundary?.Invoke(stage);
        Cancellation.ThrowIfCancellationRequested();
        long now = Clock.Now();
        if (now < ColdStart) throw new InvalidOperationException("The monotonic clock moved backwards at " + stage + ".");
        if (now >= Deadline) throw new TimeoutException("UnitTimeout at " + stage + ".");
    }
    public double ElapsedMilliseconds() => checked(Clock.Now() - ColdStart) * 1000d / Clock.Frequency;
}
