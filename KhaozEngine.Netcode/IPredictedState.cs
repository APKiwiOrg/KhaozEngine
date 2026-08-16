using System;
using System.Numerics;

namespace KhaozEngine.Netcode;

/// <summary>
/// A predicted local state whose position participates in reconciliation error smoothing.
/// </summary>
/// <typeparam name="TSelf">The implementing state type (CRTP), so WithPosition stays strongly typed.</typeparam>
public interface IPredictedState<TSelf>
{
    /// <summary>Planar (ground-plane) world position used to measure and gate reconciliation error.</summary>
    Vector2 Position { get; }

    /// <summary>
    /// Vertical axis (height) carried through render smoothing alongside the planar <see cref="Position"/>, so a
    /// jump/fall eases instead of stair-stepping or popping. Defaults to 0 for purely planar states that have no
    /// vertical axis - those keep their old behaviour with no change required.
    /// </summary>
    float Vertical => 0f;

    /// <summary>
    /// A monotonic teleport epoch stamped by the authoritative host onto this state. Client prediction compares it
    /// across reconciliations: an ADVANCE marks a hard teleport - an intentional discontinuity (respawn, admin or
    /// fast-travel move) that must CUT instantly rather than glide, regardless of the
    /// <see cref="PredictionSettings.HardSnapDistance"/> gate. Defaults to 0 for states with no teleport concept, so
    /// an ordinary predicted state keeps its distance-only cut-vs-glide behaviour with no change required.
    /// <para><b>Advance means strictly greater than the highest value observed so far</b>, not merely different, and
    /// the client holds that highest value as a watermark. A host that momentarily cannot read the component this
    /// epoch lives on serves a default 0, so a real stream dips and recovers; only a genuine advance past the
    /// watermark is a teleport, and the dip and the recovery are both ignored.</para>
    /// <para>The epoch is per-session and NOT comparable across a reconnect: a rejoining client is a fresh
    /// authoritative entity whose epoch counts from its own zero. The join and reconnect placements are decided by
    /// <see cref="ClientPrediction{TState,TCommand}.Reset"/> / <see cref="ClientPrediction{TState,TCommand}.Reseed"/>
    /// instead, on prior state and resume distance.</para>
    /// </summary>
    uint TeleportEpoch => 0;

    /// <summary>
    /// The signed vertical delta a DISCRETE step committed on THIS predicted tick (positive = an isolated step-up seat or
    /// the first riser of a run; negative = an isolated step-down seat; 0 = not a discrete step this tick). It is a per-tick
    /// EVENT, read by <see cref="ClientPrediction{TState,TCommand}.Predict"/> exactly once per real forward tick and folded
    /// into a client-side render-time-decaying mesh offset (UE-style step-event mesh smoothing). <see cref="ClientPrediction{TState,TCommand}.Reconcile"/>
    /// never reads it, so a reconciliation replay of the pending window never re-counts the step (exactly-once by
    /// construction). Defaults to 0 for states with no discrete-step concept, so an ordinary predicted state accumulates no
    /// mesh offset and behaves exactly as before.
    /// </summary>
    float StepDeltaY => 0f;

    /// <summary>
    /// The planar (XZ) anchor this state's <see cref="Position"/> is expressed against - the simulation island's
    /// frame, stamped onto the state by the step that produced it. <see cref="Vector2.Zero"/> (the default) means
    /// absolute world coordinates, so a state with no frame concept behaves exactly as it always did.
    /// <para><see cref="ClientPrediction{TState,TCommand}.Reconcile"/> differences this against the incoming
    /// authoritative basis's anchor and converts the pre-rebase presentation state into the basis's frame BEFORE any
    /// error is measured, so an island re-anchor - a no-op in world space - measures as zero prediction error, does
    /// not trip the hard-snap gate, and glides nothing.</para>
    /// </summary>
    Vector2 FrameAnchor => Vector2.Zero;

    /// <summary>
    /// Returns a copy of this state re-expressed against <paramref name="anchor"/>, with <paramref name="position"/>
    /// as the ALREADY-converted planar position (the caller differences the two anchors). Y is never framed, so the
    /// vertical axis is carried through unchanged.
    /// <para>The default THROWS, deliberately. A wither has to construct a <typeparamref name="TSelf"/> and nothing
    /// else on this interface can carry a new anchor, so there is no default body that could be correct. Making it
    /// abstract instead would break every existing implementer, which is the whole thing this default-member pattern
    /// exists to avoid. It is unreachable unless the two anchors actually differ, which is impossible for a state
    /// that left <see cref="FrameAnchor"/> at its default - so a state that reaches it is one whose author opted into
    /// frames on one side and not the other, and that should say so loudly rather than silently drop the
    /// conversion.</para>
    /// </summary>
    TSelf WithFrameAnchor(Vector2 anchor, Vector2 position) => throw new NotSupportedException(
        $"{typeof(TSelf).Name} does not implement WithFrameAnchor, so it cannot be reconciled across a frame change. "
      + "Implement it, or leave FrameAnchor at Vector2.Zero on both the predicted state and the authoritative basis.");

    /// <summary>Returns a copy of this state with the planar <paramref name="position"/> applied (vertical unchanged).</summary>
    TSelf WithPosition(Vector2 position);

    /// <summary>
    /// Returns a copy with both the smoothed planar <paramref name="position"/> and the <paramref name="vertical"/>
    /// axis applied, used to build the rendered (presentation) state. Defaults to <see cref="WithPosition"/> (the
    /// vertical is ignored) so a purely planar state needs no extra implementation.
    /// </summary>
    TSelf WithRenderState(Vector2 position, float vertical) => WithPosition(position);
}
