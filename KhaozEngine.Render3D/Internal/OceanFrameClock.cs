using System;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>What one <see cref="OceanFrameClock.Advance"/> decided for a frame.</summary>
    /// <param name="Delta">Seconds since the previous ocean frame, clamped to
    /// <see cref="OceanFrameClock.MaxFrameDelta"/> and never negative. What the foam accumulator integrates over.
    /// </param>
    /// <param name="RowTime">The wave-clock time this frame's row pass evolves the spectrum to. In the steady
    /// state that is the frame's own time plus ONE predicted frame, because the row output is consumed by the NEXT
    /// frame's column pass.</param>
    /// <param name="Prime">True when the pending row output cannot be used for this frame, so the caller has to
    /// produce this frame's rows itself (the one drain) before consuming them.</param>
    internal readonly record struct OceanFrameTick(float Delta, float RowTime, bool Prime);

    /// <summary>
    /// The FFT ocean's per-frame clock: the frame delta the foam integrates over, the time the row pass is
    /// dispatched with, and whether the pending row output is usable at all.
    /// <para>
    /// <b>Why the row pass runs one frame ahead.</b> The two FFT passes are a read-after-write chain and the
    /// compute seam has no cross-dispatch barrier (issue #311), so consuming the row output in the frame that
    /// produced it costs a full device drain mid-frame, every frame
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/398">#398</see>, measured at 0.93 ms/frame on a
    /// consumer's Metal machine). Ping-ponging the intermediate removes the within-frame dependency: frame N's
    /// column pass consumes the rows frame N-1 wrote, and the frame boundary IS the ordering.
    /// </para>
    /// <para>
    /// <b>Why that does not shift the surface.</b> Only the ROW pass carries time: it evolves <c>h0(k)</c> by
    /// <c>e^{-i omega t}</c> and transforms along X, after which the per-<c>k</c> phase is gone and no later stage
    /// can put one back (the column pass reads the delta, the choppiness and the foam knobs, and never
    /// <c>Timing.x</c>). So the only lever is which <c>t</c> the row pass is handed, and the compensation is
    /// exactly one frame of it: the row pass dispatched during frame N is given <c>t_N + dt</c>, the predicted time
    /// of frame N+1, so the column pass that consumes it during frame N+1 assembles the surface at <c>t_{N+1}</c> -
    /// the same time the pre-ping-pong code fed the same math. Under a steady frame delta the produced maps are
    /// bitwise what they were, and the extrapolation is over ONE frame with no accumulation, since each frame
    /// re-derives its prediction from the current wave clock rather than from the last prediction. A frame delta
    /// that changes mid-run leaves the surface off by the CHANGE in the delta (a 16 ms frame followed by a 50 ms
    /// one renders 34 ms of wave motion early), which is sub-frame and self-correcting.
    /// </para>
    /// <para>
    /// <b>Priming, and why it is not per frame.</b> The first frame of an ocean has no pending rows, so the caller
    /// produces them the old way, with the one drain, and the frame renders exactly what it always did. The same
    /// applies whenever the pending rows stop describing this frame: a re-bake replaces the spectrum they came from
    /// (<see cref="Invalidate"/>), and a wave clock that jumps means they are for a time nobody is rendering. Both
    /// are rare by construction. A wide jump is told from a normal frame by <see cref="MaxRowDrift"/>: a gap wider
    /// than the frame-delta clamp is not a frame, it is a discontinuity (the ocean was not drawn for a while, the
    /// clock was scrubbed), and re-priming there costs one drain rather than one frame of a stale sea.
    /// </para>
    /// </summary>
    internal sealed class OceanFrameClock
    {
        /// <summary>Upper bound on a frame delta, seconds. A paused frame, a first frame or a step backwards must
        /// not inject a foam spike or run the dissipation backwards, so the delta is clamped into
        /// <c>[0, MaxFrameDelta]</c>.</summary>
        public const float MaxFrameDelta = 0.1f;

        /// <summary>How far the pending row output's time may sit from the frame's own time before it is treated as
        /// stale, seconds. Deliberately the same bound as <see cref="MaxFrameDelta"/>: a gap the delta clamp would
        /// have to truncate is not a frame at all, and everything inside it is at most one frame of phase error,
        /// which is what the ping-pong is documented to cost.</summary>
        public const float MaxRowDrift = MaxFrameDelta;

        float _lastTime;
        bool _hasLastTime;
        float _rowTime;
        bool _hasRows;

        /// <summary>Advance to <paramref name="timeSeconds"/> and decide the frame.</summary>
        public OceanFrameTick Advance(float timeSeconds)
        {
            float delta = _hasLastTime ? Math.Clamp(timeSeconds - _lastTime, 0f, MaxFrameDelta) : 0f;
            _lastTime = timeSeconds;
            _hasLastTime = true;

            // Negated so a NaN wave clock primes rather than silently consuming rows for a time nobody asked for.
            bool prime = !_hasRows || !(MathF.Abs(timeSeconds - _rowTime) <= MaxRowDrift);

            _rowTime = timeSeconds + delta;
            _hasRows = true;
            return new OceanFrameTick(delta, _rowTime, prime);
        }

        /// <summary>Drop the pending row output, so the next <see cref="Advance"/> primes. Called when the rows
        /// stop describing the sea: a re-bake changed the spectrum they were evolved from, or the buffers holding
        /// them were rebuilt. The frame delta is deliberately NOT reset - the foam integrates over real elapsed
        /// time either way, and a re-bake is not a discontinuity in the clock.</summary>
        public void Invalidate() => _hasRows = false;
    }
}
