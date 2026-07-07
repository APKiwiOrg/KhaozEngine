using System;

namespace KhaozEngine.Audio;

/// <summary>
/// Pure, dt-driven state machine for a single-stream music crossfade: the old track fades out, the stream
/// switches to the new track, then the new track fades in. Because <see cref="IMusicBackend"/> models exactly
/// one active track (one source, one decoder), this is a fade-out-then-switch-then-fade-in through the single
/// stream, not two simultaneous streams. The struct only tracks a scalar <see cref="Factor"/> in [0,1] and the
/// pending switch. <see cref="AudioSystem"/> multiplies <see cref="Factor"/> into the settings-derived
/// <c>master * music</c> volume, so the fade composes with the user's music volume and never overwrites it.
/// </summary>
/// <remarks>
/// Timeline: <see cref="Factor"/> ramps from 1 -> 0 over the first half of the configured duration (fade-out),
/// the track switch fires at 0, then it ramps 0 -> 1 over the second half (fade-in). If no track is currently
/// playing, the fade-out half is skipped and only the fade-in runs. The ramp is linear on the amplitude factor
/// (chosen over equal-power because there is only ever one stream audible: there is no summed-power dip to
/// compensate, and a linear amplitude ramp is the honest "fade this one stream down/up"). Mid-fade retarget:
/// requesting another switch replaces the pending target and restarts toward 0 from the current factor (never
/// snaps), so the newest requested track always wins; documented and tested.
/// </remarks>
internal struct MusicFade
{
    private enum Phase { Idle, FadingOut, FadingIn }

    private Phase _phase;
    private float _factor;      // current amplitude factor in [0,1]
    private float _ratePerSec;  // |d factor| per second during an active fade (1 / halfDuration)
    private int _pendingIndex;  // track index to switch to when the fade-out reaches 0
    private bool _hasPending;

    /// <summary>Current amplitude factor in [0,1]. Multiplies the settings-derived music volume. 1 when idle.</summary>
    public readonly float Factor => _phase == Phase.Idle ? 1f : _factor;

    /// <summary>True while a fade is in progress (either half).</summary>
    public readonly bool Active => _phase != Phase.Idle;

    /// <summary>
    /// Begins (or retargets) a crossfade to <paramref name="trackIndex"/> over <paramref name="duration"/>
    /// seconds. <paramref name="hasCurrentTrack"/> selects whether the fade-out half runs (a track is playing)
    /// or is skipped (nothing playing yet, fade-in only). Duration is split evenly: half fade-out, half fade-in.
    /// Retargeting mid-fade keeps the current factor and restarts toward the new target (no snap).
    /// </summary>
    public void Start(int trackIndex, float duration, bool hasCurrentTrack)
    {
        // Half of the duration is spent on each side; the rate carries the factor across [0,1] in that half.
        float half = duration * 0.5f;
        _ratePerSec = half > 0f ? 1f / half : float.PositiveInfinity;
        _pendingIndex = trackIndex;
        _hasPending = true;

        if (hasCurrentTrack && _phase != Phase.FadingOut)
        {
            // Something is audible: fade it out first (from wherever the factor currently sits when retargeting
            // mid-fade-in, so a fade-in that was 70% up starts falling from 0.7 rather than snapping to 1).
            _factor = _phase == Phase.Idle ? 1f : _factor;
            _phase = Phase.FadingOut;
        }
        else if (!hasCurrentTrack)
        {
            // Nothing playing: skip the fade-out, the switch happens immediately (handled by Advance's
            // pending-switch check at factor 0) and we fade the new track in from 0.
            _factor = 0f;
            _phase = Phase.FadingOut;   // will immediately hit the switch (factor already 0) on the next Advance
        }
        // If already FadingOut, keep falling toward 0 with the new pending target (retarget: newest wins).
    }

    /// <summary>
    /// Advances the fade by <paramref name="dt"/> seconds. Returns <c>true</c> exactly once, on the frame the
    /// fade-out reaches 0 and the stream must switch to <paramref name="switchToIndex"/> (the caller then plays
    /// that track and the fade proceeds into the fade-in half). Returns <c>false</c> otherwise.
    /// </summary>
    public bool Advance(float dt, out int switchToIndex)
    {
        switchToIndex = -1;
        if (_phase == Phase.Idle) return false;
        if (dt < 0f) dt = 0f;

        float step = float.IsPositiveInfinity(_ratePerSec) ? 1f : _ratePerSec * dt;

        if (_phase == Phase.FadingOut)
        {
            _factor -= step;
            if (_factor <= 0f)
            {
                _factor = 0f;
                if (_hasPending)
                {
                    switchToIndex = _pendingIndex;
                    _hasPending = false;
                    _phase = Phase.FadingIn;
                    return true;   // caller switches the stream now; fade-in begins next Advance
                }
                _phase = Phase.Idle;   // no pending switch (defensive): settle silent-then-idle
            }
            return false;
        }

        // FadingIn
        _factor += step;
        if (_factor >= 1f)
        {
            _factor = 1f;
            _phase = Phase.Idle;
        }
        return false;
    }

    /// <summary>Cancels any in-progress fade and returns to the idle (full-volume, factor 1) state.</summary>
    public void Reset()
    {
        _phase = Phase.Idle;
        _factor = 1f;
        _hasPending = false;
        _pendingIndex = -1;
    }
}
