namespace KhaozEngine.NetWorld;

public sealed partial class WorldClient
{
    /// <summary>Advances render-time smoothing (call once per render frame): the local avatar's inter-tick
    /// prediction smoothing, plus - when <see cref="WorldClientConfig.InterpolateRemotes"/> is set - remote
    /// interpolation. Remotes render on a fixed delay (<see cref="WorldClientConfig.InterpolationDelayTicks"/> ticks
    /// behind the newest snapshot): the monotonic render clock advances by <paramref name="dt"/>, and the two buffered
    /// snapshots bracketing (clock - delay) are lerped by their true timestamps, so between the discrete ~tick-rate
    /// snapshots they glide rather than teleport. A snapshot stall past the buffer holds the remote at the newest value
    /// (no extrapolation) until the next arrives. Idempotent within a frame: it rewrites the same interpolated state,
    /// so multiple <see cref="Snapshot"/> reads per frame are consistent.</summary>
    /// <param name="dt">Seconds since the last render frame. Anything that is not a finite positive number of
    /// seconds (negative, zero, infinite, or not a number) is treated as zero and advances nothing.</param>
    public void AdvancePresentation(float dt)
    {
        // A frame that took no valid amount of time advances nothing. The render clock ACCUMULATES, so an infinite
        // dt (which the old dt > 0f test accepted) pinned it at infinity and parked every remote on its newest
        // sample for the session, and the raw dt went to the prediction layer, where one NaN frame poisoned the
        // local rendered position just as permanently. One sanitized value covers both clocks.
        float step = float.IsFinite(dt) && dt > 0f ? dt : 0f;
        prediction.AdvancePresentation(step);                 // local avatar (unaffected by remote interpolation)
        UpdateNetStatsWindow(step);
        presentationClock += step;                            // monotonic render clock (snapshots are stamped against it)
        if (interpolateRemotes)
            // Render remotes on a fixed delay: pick the render time interpolationDelaySeconds behind the newest snapshot
            // (which arrived at ~presentationClock), then lerp the two buffered snapshots bracketing it. Because
            // renderTime advances smoothly with the render dt - not by ramping alpha off an estimated interval - there
            // is no phase drift, so no hold frames and no catch-up snaps at a non-integer render:tick ratio. The LOCAL
            // avatar (LocalNetId) is excluded: it renders from prediction, and its client-world ReplicatedPosition must
            // stay the last-received authoritative value (the reconcile basis), not a fixed-delay interpolated one.
            view.InterpolateAt(world, presentationClock - interpolationDelaySeconds, LocalNetId);
        PresentServerTick();
        if (presentationTrace is not null) RecordTraceFrame(step);
    }
}
