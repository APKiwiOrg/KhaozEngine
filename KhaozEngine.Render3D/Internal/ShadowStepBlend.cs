using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure bookkeeping for the temporal shadow cross-fade (<see cref="ShadowSettings.ShadowStepBlendSeconds"/>): given
    /// the quantized key-light direction each frame, decides WHEN a quantized-direction step begins, picks the fade
    /// duration ADAPTIVELY from the observed inter-step cadence, ramps a 0..1 blend weight across that window, and retires
    /// the outgoing set when the window ends. No GPU, no engine state, so the headless <c>ShadowStepBlendTests</c> can
    /// pin the interval observation, duration selection, weight ramp, retirement, and the per-frame bypass.
    /// </summary>
    /// <remarks>
    /// <para><b>Adaptive duration (issue #227).</b> A fixed-seconds window under-fills a slow sun (slide-then-hold: the
    /// fade covers only the first slice of a long inter-step gap, then the edge holds still until the next step) and
    /// truncates under a fast one (steps arrive before the fade lands). So the fade duration is now
    /// <c>min(observed inter-step interval, clamp)</c>, where the clamp is <see cref="ShadowSettings.ShadowStepBlendSeconds"/>
    /// reinterpreted as the CLAMP MAX (<c>0</c> still means off entirely). With <c>clamp &gt;= the step interval</c> each
    /// new atlas starts fading on arrival and lands exactly as the next step is due, so the edge is in continuous motion,
    /// one step latent. The FIRST step after a scene start (or a <see cref="Reset"/>) has no observed interval yet, so it
    /// falls back to the clamp as its duration.</para>
    /// <para><b>Per-frame bypass (issue #227).</b> When the sun outruns the frame rate the fade cannot chain (a step every
    /// frame or two), so quantizing buys nothing: <see cref="BypassQuantization"/> latches on when the observed interval
    /// drops below <see cref="BypassEngageFrames"/> frames' worth of scene time and the caller then fits per frame from
    /// the RAW direction (continuous sub-texel motion), with blending suppressed. It releases only once the interval
    /// climbs back past <see cref="BypassReleaseFrames"/> frames, so it does not flap at the boundary. "Frames' worth of
    /// scene time" is <c>interval / dt</c> (both share the <see cref="Scene3D.EffectTimeSeconds"/> unit, so the ratio is
    /// the number of render frames the interval spanned, invariant to any clock scaling the consumer applies). The caller
    /// keeps feeding the QUANTIZED direction here even while bypassing, so the cadence keeps being measured and the
    /// release threshold is well-defined.</para>
    /// <para>The atlas re-renders on both a camera pan and a sun step, but only a SUN STEP (the quantized direction
    /// changing) has an "old direction" to cross-fade from, so the trigger is keyed on the quantized direction, not the
    /// fitted matrices. On a step the caller freezes the outgoing atlas + receiver matrices (the live set becomes the
    /// frozen set) and renders the incoming step; while <see cref="Blending"/>, the receiver lerps the two PCF results by
    /// <see cref="Weight"/> (0 = fully outgoing, 1 = fully incoming). A step arriving mid-blend lands the in-flight fade
    /// instantly and restarts: the current live set becomes the new frozen set and the newest step renders, so only ever
    /// ONE frozen set is alive (bounded memory). The window is captured at the step, so tuning the clamp mid-fade does
    /// not rescale the fade in flight (it applies to the next step).</para>
    /// </remarks>
    internal struct ShadowStepBlend
    {
        // Bypass hysteresis, in render frames' worth of scene time (interval / dt). Engage well under the ~2-frame floor
        // where a fade can no longer chain; release only once steps slow back past a comfortably higher threshold so the
        // decision does not flap frame to frame at the boundary.
        const float BypassEngageFrames = 2f;
        const float BypassReleaseFrames = 4f;
        const float Epsilon = 1e-6f;

        Vector3 _committedDir;    // the quantized light direction the LIVE atlas currently holds
        bool _hasCommitted;       // false until the first Advance seeds _committedDir
        bool _hasStepped;         // at least one step has happened (so the NEXT step's gap is a real inter-step interval)
        bool _blending;           // a cross-fade is in flight
        float _elapsed;           // seconds since the current step began
        float _window;            // the fade duration captured at the step (immune to a mid-fade clamp change)
        float _sinceStep;         // scene-time accrued since the last step (the running inter-step gap)
        float _observedInterval;  // the last completed inter-step interval (the adaptive duration source)
        bool _hasInterval;        // a real inter-step interval has been measured (>= 2 steps seen)
        bool _bypass;             // quantization bypass latched on (fit per frame from the raw direction, no blend)

        /// <summary>A cross-fade is in flight (the frozen set must be kept alive + sampled).</summary>
        public readonly bool Blending => _blending;

        /// <summary>Cross-fade weight the receiver lerps by: <c>0</c> = fully the OUTGOING (frozen) set, <c>1</c> = fully
        /// the INCOMING (live) set. <c>1</c> whenever no cross-fade is in flight (the receiver samples only the live
        /// atlas), which is the byte-stable default-off state.</summary>
        public readonly float Weight =>
            _blending && _window > 0f ? Math.Clamp(_elapsed / _window, 0f, 1f) : 1f;

        /// <summary>Quantization is bypassed this frame: the steps outran the frame rate, so the caller should fit the
        /// cascades from the RAW (un-quantized) light direction (continuous per-frame refit) instead of the quantized
        /// lattice, and no cross-fade runs. Latched with hysteresis (see the type remarks). Read by the caller BEFORE it
        /// fits, so it reflects the PREVIOUS <see cref="Advance"/>'s decision (a one-frame lag, harmless for a
        /// hysteretic latch). <c>false</c> in the common case (a normal moving sun chains fades and never bypasses).</summary>
        public readonly bool BypassQuantization => _bypass;

        /// <summary>
        /// Advance the blend one frame. <paramref name="quantizedDir"/> is the quantized fit-lattice direction this frame
        /// (already snapped when <see cref="ShadowSettings.ShadowLightQuantizeDegrees"/> &gt; 0); pass it EVEN while
        /// <see cref="BypassQuantization"/> is set, so the cadence keeps being observed. <paramref name="dt"/> is the
        /// elapsed <see cref="Scene3D.EffectTimeSeconds"/> this frame, <paramref name="clampSeconds"/> the fade-duration
        /// CLAMP MAX (<see cref="ShadowSettings.ShadowStepBlendSeconds"/>; pass <c>0</c> to disable blending, e.g. when
        /// quantization is off or the second atlas was not provisioned). The chosen fade duration is
        /// <c>min(observed inter-step interval, clampSeconds)</c>, or <c>clampSeconds</c> on the first step (no interval
        /// observed yet). Returns <c>true</c> IFF a cross-fade step BEGINS this frame, so the caller freezes the outgoing
        /// atlas + receiver matrices before rendering the incoming step (never while bypassing). On a fresh step the
        /// weight is <c>0</c> (fully outgoing) and does not advance until the next frame, so no incoming detail leaks in
        /// on the transition frame itself.
        /// </summary>
        public bool Advance(Vector3 quantizedDir, float dt, float clampSeconds)
        {
            dt = MathF.Max(0f, dt);

            if (!_hasCommitted)
            {
                _committedDir = quantizedDir;
                _hasCommitted = true;
                _sinceStep = 0f;
                return false;
            }

            // Accrue the running inter-step gap BEFORE the step test, so the frame the step is detected on counts toward
            // the interval that step just closed (otherwise the measured interval is short by one frame's dt).
            _sinceStep += dt;

            if (quantizedDir != _committedDir)
            {
                float interval = _sinceStep;   // the just-closed gap since the previous step (or the commit, first time)
                _committedDir = quantizedDir;
                _sinceStep = 0f;

                if (_hasStepped)
                {
                    // A real inter-step interval. Update the bypass latch off how many render frames it spanned, and
                    // record it as the adaptive duration source. (Skipped on the FIRST step: the commit->step gap is not
                    // a step-to-step cadence, per the type remarks, so that step falls back to the clamp.)
                    if (dt > Epsilon)
                    {
                        float framesPerStep = interval / dt;
                        if (_bypass) { if (framesPerStep > BypassReleaseFrames) _bypass = false; }
                        else if (framesPerStep < BypassEngageFrames) _bypass = true;
                    }
                    _observedInterval = interval;
                    _hasInterval = true;
                }
                _hasStepped = true;

                // Start (or restart) the cross-fade, unless bypassing or blending is off. Duration = min(interval, clamp)
                // once an interval is known, else the clamp alone (first step). A restart lands the in-flight fade
                // instantly: the live set becomes the new frozen set and the weight resets to the outgoing end.
                if (!_bypass && clampSeconds > 0f)
                {
                    _window = _hasInterval ? MathF.Min(_observedInterval, clampSeconds) : clampSeconds;
                    _blending = true;
                    _elapsed = 0f;
                    return true;
                }
                _blending = false;   // bypassing, or blending disabled: a plain re-render step, no cross-fade
            }
            else if (_bypass && dt > Epsilon && _sinceStep > BypassReleaseFrames * dt)
            {
                // No step for a comfortably long stretch while bypassing (the sun slowed or stopped): release now rather
                // than waiting on a step that may never come. Uses the growing gap so the exit is prompt.
                _bypass = false;
            }

            if (_blending)
            {
                _elapsed += dt;
                if (_elapsed >= _window) { _blending = false; _elapsed = _window; }   // window elapsed: retire the frozen set
            }
            return false;
        }

        /// <summary>Cancel any in-flight cross-fade and forget the committed direction + observed cadence (the shadow
        /// tier turned off, or a scene reset). The next <see cref="Advance"/> re-seeds without a step, and the following
        /// step falls back to the clamp again (no interval observed yet).</summary>
        public void Reset() => this = default;
    }
}
