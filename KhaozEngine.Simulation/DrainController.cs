namespace KhaozEngine.Simulation.Hosting;

/// <summary>
/// A deterministic elapsed-time grace countdown for an authoritative host. No wall clock is read. The host starts
/// a countdown with <see cref="Begin"/> and advances it from its own frame or tick clock.
/// </summary>
public sealed class DrainController
{
    private float remaining;

    /// <summary>True after the first <see cref="Begin"/>, including after the countdown completes.</summary>
    public bool HasBegun { get; private set; }

    /// <summary>True between <see cref="Begin"/> and the grace elapsing.</summary>
    public bool IsDraining { get; private set; }

    /// <summary>True once the grace period has elapsed.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>
    /// Starts or restarts the grace countdown. A non-positive grace completes on the next <see cref="Advance"/>.
    /// </summary>
    public void Begin(float graceSeconds)
    {
        remaining = graceSeconds;
        HasBegun = true;
        IsDraining = true;
        IsComplete = false;
    }

    /// <summary>Advances the countdown by <paramref name="dt"/> and completes it when the grace elapses.</summary>
    public void Advance(float dt)
    {
        if (!IsDraining || IsComplete) return;
        remaining -= dt;
        if (remaining <= 0f)
        {
            IsComplete = true;
            IsDraining = false;
        }
    }
}
