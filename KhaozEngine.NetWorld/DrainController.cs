namespace KhaozEngine.NetWorld;

/// <summary>A deterministic, tick-driven grace countdown shared by <see cref="WorldServer"/> and
/// <see cref="ShardedWorldServer"/> for a graceful drain. No wall clock: the host advances it by dt each tick.</summary>
public sealed class DrainController
{
    private float remaining;

    /// <summary>True between <see cref="Begin"/> and the grace elapsing.</summary>
    public bool IsDraining { get; private set; }

    /// <summary>True once the grace period has elapsed (the host should then flush + close).</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Starts the grace countdown. A non-positive grace completes on the next <see cref="Advance"/>.</summary>
    public void Begin(float graceSeconds)
    {
        remaining = graceSeconds;
        IsDraining = true;
        IsComplete = false;
    }

    /// <summary>Advances the countdown by dt; flips <see cref="IsComplete"/> when the grace elapses.</summary>
    public void Advance(float dt)
    {
        if (!IsDraining || IsComplete) return;
        remaining -= dt;
        if (remaining <= 0f) { IsComplete = true; IsDraining = false; }
    }
}
