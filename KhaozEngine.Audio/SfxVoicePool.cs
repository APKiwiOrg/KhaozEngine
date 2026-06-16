using System;

namespace KhaozEngine.Audio;

/// <summary>
/// Pure (no OpenAL) round-robin voice allocation policy for the SFX backend. The backend prefers a genuinely
/// idle source first and only falls back to <see cref="Next"/>, which rotates through the voices so the
/// oldest-allocated one is stolen when every voice is busy. Headless-testable.
/// </summary>
internal sealed class SfxVoicePool
{
    readonly int _count;
    int _cursor;

    /// <param name="count">Number of voices. Must be &gt; 0.</param>
    public SfxVoicePool(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), count, "voice count must be > 0");
        _count = count;
    }

    /// <summary>Returns the next voice index, advancing the cursor: 0, 1, ..., count-1, 0, ... deterministically.</summary>
    public int Next()
    {
        int v = _cursor;
        _cursor = (_cursor + 1) % _count;
        return v;
    }
}
