namespace KhaozEngine.Particles;

/// <summary>
/// Turns a per-second emission rate into a whole-number count to emit each frame, carrying the fractional
/// remainder so the long-run average matches the rate exactly. For continuous emitters; bursts call
/// <see cref="ParticleSystem.Emit"/> directly.
/// </summary>
public struct RateAccumulator
{
    private float _carry;

    /// <summary>
    /// Advance by <paramref name="dt"/> seconds at <paramref name="ratePerSec"/> particles/second and return
    /// the integer count to emit this frame. Fractional remainder accumulates across calls.
    /// </summary>
    public int Advance(float dt, float ratePerSec)
    {
        if (ratePerSec <= 0f || dt <= 0f)
        {
            return 0;
        }

        _carry += ratePerSec * dt;
        int count = (int)_carry;
        _carry -= count;
        return count;
    }

    /// <summary>Reset the fractional carry.</summary>
    public void Reset() => _carry = 0f;
}
