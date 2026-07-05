using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Netcode;

/// <summary>
/// Client-side prediction with authoritative reconciliation. Each command is predicted locally and
/// retained until the host acknowledges it by sequence number. On every authoritative snapshot the
/// predicted state is rebased to the server basis and the unacknowledged commands are re-simulated on
/// top, so with matching physics no per-snapshot drift accumulates; only a genuine misprediction
/// produces a correction, smoothed via a decaying render offset so it never pops on screen.
///
/// The render smoothing is 3D: both the inter-tick interpolation and the reconciliation offset carry the
/// vertical axis (<see cref="IPredictedState{TSelf}.Vertical"/>), so a jump/fall eases instead of stair-stepping
/// or popping. Reconcile is C1-continuous: it does NOT collapse the in-flight inter-tick interpolation onto the
/// new basis. It keeps the inter-tick phase (previous position + elapsed) flowing at the steady velocity and only
/// rebases the target plus folds any genuine misprediction into the decaying render offset, so a matching (loopback)
/// rebase - which fires every tick - perturbs neither the rendered position nor its velocity. Collapsing frac to 1
/// each tick (the old behaviour) pinned the inter-tick contribution at zero and left only the offset decay to carry
/// motion for the rest of the tick, a per-tick velocity dip that read as a 30 Hz camera sawtooth. A hard snap still
/// collapses (an intentional teleport).
/// </summary>
public sealed class ClientPrediction<TState, TCommand>
    where TState : struct, IPredictedState<TState>
{
    private readonly ITickSimulator<TState, TCommand> simulator;
    private readonly PredictionSettings settings;
    private readonly SortedList<int, TCommand> pendingCommands = new();
    private TState predictedState;
    // Reconciliation smoothing offset, split into the planar (XZ) and vertical axes so a vertical-only
    // misprediction (a mispredicted jump/fall) eases too. Decays toward zero in AdvancePresentation.
    private Vector2 renderOffset;
    private float verticalRenderOffset;
    // Velocity state for the planar offset's critically-damped decay. A first-order (exponential) decay faithfully
    // tracks the target, so when the authoritative basis dips backward for a tick or two at a velocity transition
    // (the client stops instantly in its prediction while the server, an input-RTT behind, is still applying the
    // pre-stop moves - then catches up), the planar offset chases that transient dip and the rendered avatar visibly
    // shakes back-and-forth as it settles. A critically-damped second-order decay carries inertia: a brief
    // dip-and-recover barely moves it (so the shake is gone), while a SUSTAINED correction still fully resolves
    // (unlike a hard "never move backward" clamp, which would strand the avatar on a genuine backward misprediction).
    private Vector2 renderOffsetVelocity;
    private int nextSeq;
    // Inter-tick render interpolation: the predicted position only steps once per tick (60Hz). At higher frame
    // rates the render would snap each tick, so the rendered position eases from the previous tick's position to
    // the current one across the tick duration. Frame-rate independent (time-based fraction). Carries the vertical
    // axis alongside the planar one.
    private Vector2 previousPredictedPosition;
    private float previousPredictedVertical;
    private float secondsSinceLastPredict;
    // The local player's predicted horizontal (planar) speed, recomputed each Predict from the per-tick position
    // delta. Computed ONLY in Predict (the commanded path), never in Reconcile, so a reconciliation rebase/snap is
    // not mistaken for movement: a consumer HUD/audio/locomotion gets a clean steady value under lag instead of the
    // wobble from differencing RenderedState.Position (which carries the decaying reconciliation render offset).
    private float predictedHorizontalSpeed;

    public ClientPrediction(ITickSimulator<TState, TCommand> simulator, PredictionSettings? settings = null)
    {
        this.simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        this.settings = settings ?? PredictionSettings.Default;
    }

    /// <summary>The current predicted (authority-tracking) state.</summary>
    public TState PredictedState => predictedState;

    /// <summary>
    /// The local player's predicted horizontal (planar) speed in units/sec, taken from the most recent
    /// <see cref="Predict"/> tick (the commanded move, collision-clamped in the simulator step). Unlike differencing
    /// <see cref="RenderedState"/>.Position - which carries the decaying reconciliation render offset, so a steady run
    /// wobbles under lag - this is the clean source for a consumer HUD / audio / locomotion blend: it is computed only
    /// on the commanded path and is therefore immune to reconciliation snaps. Zero until the first
    /// <see cref="Predict"/>, and reset to zero by <see cref="Reset"/> / <see cref="Reseed"/>.
    /// </summary>
    public float PredictedHorizontalSpeed => predictedHorizontalSpeed;

    /// <summary>
    /// The state to draw: the predicted position (planar AND vertical) eased from the previous tick toward the
    /// current one over the tick duration (so it stays smooth above the tick rate), plus the decaying
    /// reconciliation offset on both axes.
    /// </summary>
    public TState RenderedState
    {
        get
        {
            float frac = InterTickFraction;
            Vector2 planar = Vector2.Lerp(previousPredictedPosition, predictedState.Position, frac) + renderOffset;
            float vertical = Lerp(previousPredictedVertical, predictedState.Vertical, frac) + verticalRenderOffset;
            return predictedState.WithRenderState(planar, vertical);
        }
    }

    private float InterTickFraction => settings.TickSeconds > 0f
        ? MathF.Min(1f, secondsSinceLastPredict / settings.TickSeconds)
        : 1f;

    public void Reset(in TState initialState)
    {
        predictedState = initialState;
        previousPredictedPosition = initialState.Position;
        previousPredictedVertical = initialState.Vertical;
        secondsSinceLastPredict = settings.TickSeconds; // start fully on the current state (frac = 1)
        pendingCommands.Clear();
        renderOffset = Vector2.Zero;
        renderOffsetVelocity = Vector2.Zero;
        verticalRenderOffset = 0f;
        predictedHorizontalSpeed = 0f;
        nextSeq = 0;
    }

    /// <summary>
    /// Re-seeds the predicted state onto <paramref name="basis"/> after a mid-session RECONNECT, WITHOUT resetting
    /// the command sequence counter. Unlike <see cref="Reset"/>, this keeps <c>nextSeq</c> monotonic across the
    /// reconnect: a fresh server starts its per-connection acknowledgement watermark at -1, but the client kept
    /// sending commands (with the continuing high seq) in the gap between the new session joining and its first
    /// snapshot, so that watermark has already advanced to a high value. Zeroing the counter (as <see cref="Reset"/>
    /// does) would make every post-reconnect command land at or below that watermark, the server would reject them
    /// all as stale, and the player would be pinned at the authoritative position forever. Pending commands are
    /// kept on purpose: the very next <see cref="Reconcile"/> drops the ones the new server has acknowledged and
    /// replays the rest on top of <paramref name="basis"/>, so the local avatar snaps cleanly to the reconnect
    /// position with no render glide and no lost input.
    /// </summary>
    public void Reseed(in TState basis)
    {
        predictedState = basis;
        previousPredictedPosition = basis.Position;
        previousPredictedVertical = basis.Vertical;
        secondsSinceLastPredict = settings.TickSeconds; // start fully on the current state (frac = 1)
        renderOffset = Vector2.Zero;
        renderOffsetVelocity = Vector2.Zero;
        verticalRenderOffset = 0f;
        predictedHorizontalSpeed = 0f;
        // nextSeq intentionally preserved (monotonic across the reconnect); pendingCommands intentionally kept
        // (the following Reconcile drops acked / replays unacked against the new server's ack).
    }

    /// <summary>Predicts one command forward and retains it for reconciliation. Returns its seq.</summary>
    public int Predict(in TCommand command)
    {
        int seq = nextSeq++;
        pendingCommands[seq] = command;
        // The position before this step becomes the interpolation start; the render eases toward the new step.
        previousPredictedPosition = predictedState.Position;
        previousPredictedVertical = predictedState.Vertical;
        secondsSinceLastPredict = 0f;
        predictedState = simulator.Step(predictedState, command, settings.TickSeconds);
        // Planar speed for this tick: the distance the predicted position actually moved (after the simulator's
        // collision clamp) over the tick duration. IPredictedState.Position is planar, so this is horizontal for free.
        Vector2 step = predictedState.Position - previousPredictedPosition;
        predictedHorizontalSpeed = settings.TickSeconds > 0f ? step.Length() / settings.TickSeconds : 0f;
        if (pendingCommands.Count > settings.MaxPendingCommands)
        {
            // Bound memory if acknowledgements stop arriving (sustained loss); drop the oldest.
            pendingCommands.RemoveAt(0);
        }

        return seq;
    }

    /// <summary>
    /// Rebases to <paramref name="authoritativeBasis"/>, drops commands the host has acknowledged
    /// (seq &lt;= <paramref name="lastAcknowledgedSeq"/>), replays the rest, and smooths any visible
    /// correction. Large errors hard-snap; everything else glides via the render offset, which carries the
    /// ACTUAL on-screen position across the rebase so a mid-tick snapshot causes no discontinuous jump.
    /// </summary>
    public ReconciliationResult Reconcile(int authoritativeTick, in TState authoritativeBasis, int lastAcknowledgedSeq)
    {
        // Sample the actual on-screen position (inter-tick interpolated + current offset) BEFORE the rebase; it is
        // what the continuity-preserving render offset is anchored to. Capture the pre-rebase predicted position too
        // (without the smoothing offset) - that is the clean prediction-divergence metric the gate uses.
        float frac = InterTickFraction;
        Vector2 oldPlanar = predictedState.Position;
        float oldVertical = predictedState.Vertical;
        Vector2 renderedPlanar = Vector2.Lerp(previousPredictedPosition, oldPlanar, frac) + renderOffset;
        float renderedVertical = Lerp(previousPredictedVertical, oldVertical, frac) + verticalRenderOffset;

        while (pendingCommands.Count > 0 && pendingCommands.Keys[0] <= lastAcknowledgedSeq)
        {
            pendingCommands.RemoveAt(0);
        }

        TState replayed = authoritativeBasis;
        for (int i = 0; i < pendingCommands.Count; i++)
        {
            replayed = simulator.Step(replayed, pendingCommands.Values[i], settings.TickSeconds);
        }

        predictedState = replayed;

        // Gate on the pure prediction-divergence magnitude (3D): how far the pre-rebase predicted state sat from the
        // rebased authoritative state. This is independent of the in-flight render smoothing offset and of where in
        // the inter-tick interpolation the snapshot landed, so a residual smoothing glide never spuriously hard-snaps.
        Vector2 planarError = oldPlanar - predictedState.Position;
        float verticalError = oldVertical - predictedState.Vertical;
        float positionError = new Vector3(planarError.X, verticalError, planarError.Y).Length();
        bool hardSnapApplied = positionError >= settings.HardSnapDistance;

        if (hardSnapApplied)
        {
            // A hard snap teleports on screen: collapse the inter-tick lerp onto the new basis (previous == current,
            // frac = 1) and drop the offset so rendered == predicted immediately.
            previousPredictedPosition = predictedState.Position;
            previousPredictedVertical = predictedState.Vertical;
            secondsSinceLastPredict = settings.TickSeconds;
            renderOffset = Vector2.Zero;
            renderOffsetVelocity = Vector2.Zero;
            verticalRenderOffset = 0f;
        }
        else
        {
            // C1-continuous reconcile: do NOT collapse the inter-tick interpolation. Instead of leaving
            // previousPredictedPosition pinned while the target jumps to the new basis, TRANSLATE the whole inter-tick
            // segment (previous -> predicted) by the rebase delta, so the remaining (1 - frac) of the interpolation
            // keeps flowing in the SAME direction and at the SAME velocity it had, and the entire target jump is
            // absorbed into the decaying render offset. This makes the inter-tick lerp C1 across ANY rebase, not only a
            // steady (matching) one:
            //  - Matching rebase (loopback steady run): the delta is zero, so previous is unchanged and the offset is
            //    unchanged - identical to before, so the fixed 30 Hz camera-sawtooth stays fixed.
            //  - Backward rebase at a velocity transition (decel-to-stop): the client stops instantly in its
            //    prediction while the authority, an input-RTT behind, is momentarily still at the pre-stop position and
            //    then catches up, so the basis dips backward for a tick or two. WITHOUT the translation, previous
            //    stayed ahead of the rebased-back target and the inter-tick lerp dragged the render BACKWARD to the
            //    dipped target within half a tick (fast, and the visible shake). WITH it, the inter-tick segment has
            //    zero velocity for a stopped player (previous == predicted), so it no longer drags; the transient dip
            //    lives entirely in the render offset, which critically-damps it away without a reversal.
            // Continuity holds either way: rendered_after == rendered_before (the offset re-anchors to whatever the
            // translation left).
            Vector2 planarRebase = predictedState.Position - oldPlanar;
            float verticalRebase = predictedState.Vertical - oldVertical;
            previousPredictedPosition += planarRebase;
            previousPredictedVertical += verticalRebase;
            renderOffset = renderedPlanar - (Vector2.Lerp(previousPredictedPosition, predictedState.Position, frac));
            verticalRenderOffset = renderedVertical - Lerp(previousPredictedVertical, predictedState.Vertical, frac);
            // The offset just jumped discontinuously (re-anchored to preserve rendered continuity against the new
            // target), so its carried smoothing velocity is stale. Zero it: keeping it lets the decay's momentum
            // overshoot when the target recovers and the offset flips sign (a small secondary backward creep after the
            // stop). Starting each re-anchor from rest keeps the critical damping's early-inertia (which holds the
            // render steady through the transient authority dip) without that overshoot.
            renderOffsetVelocity = Vector2.Zero;
        }

        return new ReconciliationResult(authoritativeTick, positionError, hardSnapApplied);
    }

    /// <summary>Advances the inter-tick interpolation clock and decays the smoothing offset toward zero;
    /// frame-rate independent within clamping.</summary>
    public void AdvancePresentation(float elapsedSeconds)
    {
        float dt = MathF.Max(0f, elapsedSeconds);
        // Advance toward the current tick (clamped at one tick so a stalled tick stream holds, not overshoots).
        secondsSinceLastPredict = MathF.Min(secondsSinceLastPredict + dt, settings.TickSeconds);

        if (renderOffset == Vector2.Zero && verticalRenderOffset == 0f)
        {
            return;
        }

        // Planar offset: critically-damped (velocity-carrying) decay toward zero. The inertia is what suppresses the
        // decel-to-stop shake - it will not chase a one-to-two-tick backward dip of the authoritative basis - while a
        // sustained correction still converges. smoothTime is the ~time-to-settle, mapped from the first-order
        // CorrectionRate so the tuning knob keeps its meaning. The vertical axis stays first-order: a jump/fall
        // correction has no comparable transient-dip failure mode, and keeping it unchanged bounds the blast radius.
        float smoothTime = settings.CorrectionRate > 0f ? 1f / settings.CorrectionRate : 0f;
        renderOffset = SmoothDampToZero(renderOffset, ref renderOffsetVelocity, smoothTime, dt);
        float blend = MathF.Min(1f, settings.CorrectionRate * dt);
        verticalRenderOffset = Lerp(verticalRenderOffset, 0f, blend);

        float dz = settings.CorrectionDeadZone;
        if (renderOffset.LengthSquared() + verticalRenderOffset * verticalRenderOffset <= dz * dz)
        {
            // Settled within the dead-zone: snap exactly onto the predicted state instead of chasing float jitter.
            renderOffset = Vector2.Zero;
            renderOffsetVelocity = Vector2.Zero;
            verticalRenderOffset = 0f;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// Critically-damped (no-overshoot) decay of <paramref name="current"/> toward zero, carrying
    /// <paramref name="velocity"/> across calls. Closed-form and stable at any frame dt (60/120/uncapped fps), so the
    /// settle is frame-rate independent. <paramref name="smoothTime"/> is the approximate time to reach zero; a value
    /// at or below zero decays instantly. Standard critically-damped smoothing (damping ratio 1): a transient impulse
    /// produces little displacement (inertia), a sustained offset fully resolves.
    /// </summary>
    private static Vector2 SmoothDampToZero(Vector2 current, ref Vector2 velocity, float smoothTime, float dt)
    {
        if (smoothTime <= 0f || dt <= 0f)
        {
            velocity = Vector2.Zero;
            return smoothTime <= 0f ? Vector2.Zero : current;
        }

        float omega = 2f / smoothTime;
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        // change = current - target, target = 0.
        Vector2 temp = (velocity + omega * current) * dt;
        velocity = (velocity - omega * temp) * exp;
        return (current + temp) * exp;
    }
}
