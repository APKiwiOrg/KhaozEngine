using System;

namespace KhaozEngine.Simulation;

/// <summary>
/// A headless fixed-timestep accumulator: converts variable real-elapsed time into a whole number of
/// fixed-dt ticks, decoupling simulation rate from frame/render rate. The authoritative server host loop is
/// built on this. Deterministic - a given sequence of elapsed-time values always yields the same tick count.
/// </summary>
/// <remarks>
/// Promotes the accumulator proven in SpaceGame's <c>FixedStepRunDriver</c>, reduced to a single tick stream
/// (the lockstep-specific input-delay / dual input-vs-sim counters are dropped: an authoritative server needs
/// only one fixed tick stream).
/// </remarks>
public sealed class FixedTickHost
{
    private readonly float tickSeconds;
    private float accumulatorSeconds;

    /// <param name="tickSeconds">Fixed timestep, seconds per tick (e.g. <c>1f / 30f</c>). Must be &gt; 0.</param>
    public FixedTickHost(float tickSeconds)
    {
        if (tickSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), tickSeconds, "Tick duration must be > 0.");
        this.tickSeconds = tickSeconds;
    }

    /// <summary>Seconds per fixed tick.</summary>
    public float TickSeconds => tickSeconds;

    /// <summary>Total fixed ticks advanced since construction or the last <see cref="Reset"/>.</summary>
    public long TickCount { get; private set; }

    /// <summary>Clears the accumulator and the tick counter.</summary>
    public void Reset()
    {
        accumulatorSeconds = 0f;
        TickCount = 0;
    }

    /// <summary>
    /// Adds <paramref name="elapsedSeconds"/> (negative is clamped to 0) to the accumulator and invokes
    /// <paramref name="onTick"/> once per whole fixed step, at most <paramref name="maxTicksPerFrame"/> times,
    /// passing the running <see cref="TickCount"/>. When the cap is hit the accumulator is clamped to one
    /// tick's worth so the host sheds backlog instead of spiralling. Returns the number of ticks produced.
    /// </summary>
    public int Advance(float elapsedSeconds, Action<long> onTick, int maxTicksPerFrame = 8)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        int cap = Math.Max(1, maxTicksPerFrame);

        accumulatorSeconds += MathF.Max(0f, elapsedSeconds);
        int produced = 0;
        while (accumulatorSeconds >= tickSeconds && produced < cap)
        {
            accumulatorSeconds -= tickSeconds;
            onTick(TickCount);
            TickCount++;
            produced++;
        }

        if (produced >= cap)
            accumulatorSeconds = MathF.Min(accumulatorSeconds, tickSeconds);

        return produced;
    }
}
