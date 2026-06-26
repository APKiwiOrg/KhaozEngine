using System;

namespace KhaozEngine.Simulation;

/// <summary>
/// The default <see cref="IJobScheduler"/>: runs every job inline on the calling thread, in strict index order.
/// Deterministic and allocation-free - this is the single-threaded baseline (lockstep / single-player sims keep
/// using it, byte-unchanged) and the reference a parallel scheduler's output is asserted equal to in tests.
/// </summary>
public sealed class SingleThreadedJobScheduler : IJobScheduler
{
    /// <inheritdoc />
    public void For(int count, Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        for (int i = 0; i < count; i++)
            body(i);
    }
}
