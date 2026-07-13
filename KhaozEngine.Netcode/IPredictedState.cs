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
    /// across reconciliations: an ADVANCE marks a hard teleport - an intentional discontinuity (join/reconnect
    /// placement, respawn, admin or fast-travel move) that must CUT instantly rather than glide, regardless of the
    /// <see cref="PredictionSettings.HardSnapDistance"/> gate. Defaults to 0 for states with no teleport concept, so
    /// an ordinary predicted state keeps its distance-only cut-vs-glide behaviour with no change required.
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

    /// <summary>Returns a copy of this state with the planar <paramref name="position"/> applied (vertical unchanged).</summary>
    TSelf WithPosition(Vector2 position);

    /// <summary>
    /// Returns a copy with both the smoothed planar <paramref name="position"/> and the <paramref name="vertical"/>
    /// axis applied, used to build the rendered (presentation) state. Defaults to <see cref="WithPosition"/> (the
    /// vertical is ignored) so a purely planar state needs no extra implementation.
    /// </summary>
    TSelf WithRenderState(Vector2 position, float vertical) => WithPosition(position);
}
