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

    /// <summary>
    /// Picks the voice to steal when every voice is busy, given what each one is currently playing: the LOWEST
    /// priority in <paramref name="playing"/>, with the rotation as the tie-break, so equal-priority voices are
    /// still taken oldest-first (issue #114). Pure rotation alone could cut a
    /// <see cref="SfxPriority.High"/> cue while a <see cref="SfxPriority.Low"/> footstep was playing two voices
    /// over, purely because it was that voice's turn.
    /// <para>The incoming sound's own priority is deliberately not an input: a play always gets a voice, it just
    /// takes the least valuable one. Dropping a one-shot instead would trade an audible cut for a silence that
    /// nothing anywhere reports.</para>
    /// </summary>
    /// <param name="playing">Each voice's current priority, indexed by voice. Length must be the pool's count.</param>
    public int Steal(ReadOnlySpan<SfxPriority> playing)
    {
        if (playing.Length != _count)
            throw new ArgumentException($"Expected one priority per voice ({_count}), got {playing.Length}.", nameof(playing));

        // Walk in rotation order from the cursor and keep the first STRICT minimum, which is what makes the
        // rotation the tie-break rather than a second sort key.
        int best = _cursor;
        SfxPriority bestPriority = playing[_cursor];
        for (int n = 1; n < _count; n++)
        {
            int v = (_cursor + n) % _count;
            if (playing[v] >= bestPriority) continue;
            best = v;
            bestPriority = playing[v];
        }
        _cursor = (best + 1) % _count;
        return best;
    }
}
