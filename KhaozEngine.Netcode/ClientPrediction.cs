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
    // Velocity state for the offset's critically-damped decay, one carrier per axis (planar XZ + vertical). A
    // first-order (exponential) decay faithfully tracks the target, so when the authoritative basis dips backward for
    // a tick or two at a velocity transition (the client stops instantly in its prediction while the server, an
    // input-RTT behind, is still applying the pre-stop moves - then catches up), the offset chases that transient dip
    // and the rendered avatar visibly shakes back-and-forth as it settles. A critically-damped second-order decay
    // carries inertia: a brief dip-and-recover barely moves it (so the shake is gone), while a SUSTAINED correction
    // still fully resolves (unlike a hard "never move backward" clamp, which would strand the avatar on a genuine
    // backward misprediction). The vertical axis carries its own velocity for the same reason: the surface-swim
    // buoyancy spring emits a CONTINUOUS stream of small vertical corrections, and a first-order vertical decay chased
    // each one into a fast, jerky camera bob (10.7.0 left the vertical first-order only because corrections there were
    // then rare one-off jumps/falls).
    private Vector2 renderOffsetVelocity;
    private float verticalRenderOffsetVelocity;
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
    // A client-local, session-monotonic running sum of the DISCRETE-STEP impulses the sim committed on the PREDICTED
    // (real forward) ticks: incremented in Predict by predictedState.StepDeltaY (positive for an isolated step-up seat /
    // the first riser of a run, negative for a step-down seat), and NEVER touched by Reconcile. A render-side mesh
    // smoother DIFFS it frame-to-frame to detect each new isolated step exactly once and fold it into a render-time-
    // decaying vertical MESH offset (UE-style step-event smoothing - the continuous stair glide renders such singles
    // raw, so they would otherwise pop). Keying the accumulation to the Predict boundary is precisely what makes it
    // EXACTLY-ONCE across reconciliation: a reconcile replays the pending command window (re-running Step, re-committing
    // the same StepDeltaY) but does NOT add to this sum, so a step is counted once when its command is first predicted
    // and never re-counted on a replay. It grows only on real discrete steps (occasional doorsteps/curbs; a continuous
    // climb exports ClimbRate and leaves StepDeltaY 0), so float precision stays ample over any realistic session.
    private float stepCumulativeY;
    // The authoritative teleport epoch, tracked as a HIGH-WATER MARK rather than a last-seen value: within a session
    // it only ever rises. An ADVANCE past it forces an unconditional hard cut, independent of HardSnapDistance (see
    // IPredictedState.TeleportEpoch). Re-baselined on (re)seed - a reseed lands on a fresh authoritative entity whose
    // epoch counts from its own zero - so the seed's own epoch is never mistaken for an in-session advance.
    //
    // The watermark is what makes the compare monotonic in practice, and a plain "greater than the last value" test is
    // NOT enough on its own. The epoch reads 0 whenever the authoritative host serves a state with its movement
    // component momentarily absent, so a real stream can dip 5 -> 0 -> 5: with a last-seen store the dip is silent but
    // the RECOVERY reads as a fresh advance and cuts, which is the every-snapshot flip-flop. With the watermark held
    // at 5, neither edge fires.
    private uint lastTeleportEpoch;
    // Set by Reset/Reseed so the FIRST reconcile after a (re)seed does not additionally count the epoch that seed
    // captured as an in-session advance.
    private bool justSeeded;
    // Whether the pending (re)seed's own placement is itself a teleport worth reporting. Always true for Reset (a
    // first-ever join has no prior state, so the placement IS the discontinuity) and true for Reseed only when the
    // resume position is genuinely discontinuous - see Reseed.
    private bool seedReportsTeleport;

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
    /// The client-local, session-monotonic running sum of DISCRETE-STEP vertical impulses committed on the predicted
    /// ticks (see <see cref="IPredictedState{TSelf}.StepDeltaY"/>): a step-up seat / first riser adds its rise, a
    /// step-down seat subtracts its drop. A render-side mesh smoother DIFFS this across frames to pick up each new
    /// isolated step EXACTLY ONCE and ease it with a render-time-decaying vertical offset (the continuous stair glide
    /// renders such singles raw, so they would otherwise pop as a mini-teleport). Incremented only on the commanded
    /// <see cref="Predict"/> path and NEVER on a reconciliation replay, so replaying the pending window across a step tick
    /// does not double-count it. Zero until the first step, and reset to zero by <see cref="Reset"/> / <see cref="Reseed"/>
    /// (paired with the teleport signal a consumer uses to re-baseline the smoother, so a reset is never read as a step).
    /// </summary>
    public float StepCumulativeY => stepCumulativeY;

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
        verticalRenderOffsetVelocity = 0f;
        predictedHorizontalSpeed = 0f;
        stepCumulativeY = 0f;
        nextSeq = 0;
        // Capture the seed epoch (so it is not re-counted as an in-session advance) and arm the join teleport signal.
        // A first-ever join always reports one: the client has no prior state, so there is nothing for the placement
        // to be continuous WITH, and the consumer has a camera to place and a world to stream in around it.
        lastTeleportEpoch = initialState.TeleportEpoch;
        justSeeded = true;
        seedReportsTeleport = true;
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
    /// replays the rest on top of <paramref name="basis"/>, so no input is lost across the reconnect.
    /// <para><b>A reseed is only reported as a teleport when the world position is genuinely discontinuous.</b> The
    /// resume displacement - the 3D distance from the state this client was carrying to <paramref name="basis"/>,
    /// measured in absolute space so an island re-anchor across the reconnect counts as zero - is compared against
    /// <see cref="PredictionSettings.HardSnapDistance"/>. Below it the session simply resumes: prediction is still
    /// reseeded (the render offsets, the inter-tick phase and the authoritative basis all have to be rebuilt against
    /// the new session), but <see cref="ReconciliationResult.Teleported"/> stays false, so a consumer that answers a
    /// teleport with an expensive world-scale reaction does not pay it every time a lossy link drops. At or above it
    /// the player really did move while disconnected and the teleport is reported as it always was.</para>
    /// <para><b>The same verdict decides whether the avatar cuts or glides, because the consumer's camera hangs off
    /// it.</b> A reported teleport CUTS: the render offsets drop, so the avatar is on the resume position the frame
    /// the seed lands, and the consumer warps its follow camera to meet it. A quiet resume must therefore NOT cut,
    /// because nothing tells that camera to warp: the sub-threshold displacement is re-anchored into the decaying
    /// render offset instead and glides away like any ordinary correction, so the avatar and the eased camera stay
    /// together. Zeroing the offsets on both verdicts put the avatar on the new position instantly while the camera
    /// eased the whole way behind it, which is the camera-flying artifact the teleport signal exists to prevent, just
    /// inverted.</para>
    /// <para>Position is the ONLY sound signal here, because the epoch cannot be compared across a reconnect: the
    /// server allocates a fresh net id and a fresh entity per join, whose <see cref="IPredictedState{TSelf}.TeleportEpoch"/>
    /// counts from its own zero, so it bears no relation to the epoch the previous session ended on.</para>
    /// </summary>
    public void Reseed(in TState basis)
    {
        // Decide the resume verdict BEFORE the basis overwrites the state this client was carrying - that carried
        // state is the only record of where the player was when the link dropped.
        seedReportsTeleport = ResumeDisplacement(basis) >= settings.HardSnapDistance;
        // Sample the ACTUAL on-screen position (inter-tick interpolated + the in-flight offset) before the rebase, in
        // ABSOLUTE space for the same reason ResumeDisplacement measures there: an island re-anchor across the
        // reconnect moved nothing, so it must contribute nothing to the glide. renderOffset is a delta and therefore
        // frame-invariant, which is what lets it be re-anchored across the frame change at all.
        float frac = InterTickFraction;
        Vector2 renderedAbsolute = predictedState.FrameAnchor
            + Vector2.Lerp(previousPredictedPosition, predictedState.Position, frac) + renderOffset;
        float renderedVertical = Lerp(previousPredictedVertical, predictedState.Vertical, frac) + verticalRenderOffset;

        predictedState = basis;
        previousPredictedPosition = basis.Position;
        previousPredictedVertical = basis.Vertical;
        secondsSinceLastPredict = settings.TickSeconds; // start fully on the current state (frac = 1)
        if (seedReportsTeleport)
        {
            // A reported teleport CUTS: drop the offsets so rendered == predicted the frame the seed lands, matched by
            // the camera warp the consumer runs off the signal.
            renderOffset = Vector2.Zero;
            verticalRenderOffset = 0f;
        }
        else
        {
            // A quiet resume GLIDES: re-anchor the offsets so the rendered position is exactly where it already was,
            // and let the sub-threshold displacement decay away as an ordinary correction. Nothing fires the
            // consumer's camera warp on this path, so cutting here would strand the avatar ahead of its camera.
            renderOffset = renderedAbsolute - (basis.FrameAnchor + basis.Position);
            verticalRenderOffset = renderedVertical - basis.Vertical;
        }
        // Either way the carried smoothing velocity is stale (the offsets just moved discontinuously), so both axes
        // restart from rest - the same rule Reconcile's C1 branch applies whenever it re-anchors.
        renderOffsetVelocity = Vector2.Zero;
        verticalRenderOffsetVelocity = 0f;
        predictedHorizontalSpeed = 0f;
        stepCumulativeY = 0f;
        // nextSeq intentionally preserved (monotonic across the reconnect); pendingCommands intentionally kept
        // (the following Reconcile drops acked / replays unacked against the new server's ack).
        // Re-baseline the epoch watermark onto the new session's counter (see lastTeleportEpoch), so the reseed's own
        // epoch is never read as an in-session advance whichever way it moved.
        lastTeleportEpoch = basis.TeleportEpoch;
        justSeeded = true;
    }

    // The 3D distance from the currently carried predicted state to a resume basis, measured in ABSOLUTE space
    // (FrameAnchor + Position) so an island re-anchor across the reconnect - a no-op in world space - measures zero
    // rather than a frame width. Deliberately arithmetic on the anchors rather than a WithFrameAnchor conversion:
    // the wither's default body throws, and a state that left FrameAnchor at its default must not be able to reach it
    // through a path that only measures a distance.
    private float ResumeDisplacement(in TState basis)
    {
        Vector2 planar = (basis.FrameAnchor + basis.Position) - (predictedState.FrameAnchor + predictedState.Position);
        float vertical = basis.Vertical - predictedState.Vertical;
        return new Vector3(planar.X, vertical, planar.Y).Length();
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
        // Fold this tick's discrete-step impulse into the step-smoothing accumulator, EXACTLY ONCE per real forward tick.
        // Reconcile deliberately never touches stepCumulativeY, so replaying the pending window (which re-runs Step and
        // re-commits the same StepDeltaY) never re-counts a step. StepDeltaY is 0 on all but a discrete-step commit tick,
        // so this is a no-op on almost every tick.
        stepCumulativeY += predictedState.StepDeltaY;
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
        // FIRST, above every capture below: convert the carried presentation state into the INCOMING basis's frame.
        // An island re-anchor moves the whole world by an exact multiple of the frame grid and is a no-op in world
        // space, but the predicted state is still stamped with the old anchor while the basis carries the new one, so
        // without this every quantity differenced further down is differencing two spaces. Two separate bugs come out
        // of that, and both are fixed here rather than at their own sites: `planarError` below would measure the whole
        // anchor delta and trip the HardSnapDistance gate (a hard cut on a shift that moved nothing), and the C1
        // branch's renderOffset would re-anchor against a captured-in-the-old-frame rendered position and glide the
        // avatar a frame-width across the screen while it decayed. Placing the conversion above the captures is what
        // covers both: oldPlanar, renderedPlanar and previousPredictedPosition all end up in one frame.
        //
        // renderOffset and renderOffsetVelocity are DELTAS, so they are frame-invariant and untouched. So are the
        // vertical axis and its offset, because Y is never framed.
        Vector2 frameDelta = predictedState.FrameAnchor - authoritativeBasis.FrameAnchor;
        if (frameDelta != Vector2.Zero)
        {
            predictedState = predictedState.WithFrameAnchor(
                authoritativeBasis.FrameAnchor, predictedState.Position + frameDelta);
            previousPredictedPosition += frameDelta;
        }

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

        // Authoritative teleport marker: an ADVANCE of the epoch past the high-water mark is an intentional
        // discontinuity that CUTS regardless of distance. Strictly an advance, never any inequality: the epoch reads 0
        // whenever the host serves a state with its movement component momentarily absent, and treating that dip - or
        // the recovery back off it - as a teleport fired the cut on ordinary snapshots. justSeeded suppresses counting
        // the (re)seed's own captured epoch as an advance; whether the seed ITSELF reports a teleport is
        // seedReportsTeleport (always for a join, only for a discontinuous resume - see Reset / Reseed). A seed that
        // does report one still does not force the hard-snap branch: it already placed the avatar with no glide.
        uint epoch = authoritativeBasis.TeleportEpoch;
        bool epochAdvanced = !justSeeded && epoch > lastTeleportEpoch;
        bool teleported = seedReportsTeleport || epochAdvanced;
        justSeeded = false;
        seedReportsTeleport = false;
        if (epoch > lastTeleportEpoch) lastTeleportEpoch = epoch;

        bool hardSnapApplied = positionError >= settings.HardSnapDistance || epochAdvanced;

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
            verticalRenderOffsetVelocity = 0f;
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
            // Both axes' offsets just jumped discontinuously (re-anchored to preserve rendered continuity against the
            // new target), so their carried smoothing velocity is stale. Zero both: keeping it lets the decay's
            // momentum overshoot when the target recovers and the offset flips sign (a small secondary backward creep
            // after the stop). Starting each re-anchor from rest keeps the critical damping's early-inertia (which
            // holds the render steady through the transient authority dip) without that overshoot.
            renderOffsetVelocity = Vector2.Zero;
            verticalRenderOffsetVelocity = 0f;
        }

        return new ReconciliationResult(authoritativeTick, positionError, hardSnapApplied, teleported);
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

        // Both axes: critically-damped (velocity-carrying) decay toward zero. The inertia is what suppresses the
        // decel-to-stop planar shake - it will not chase a one-to-two-tick backward dip of the authoritative basis -
        // while a sustained correction still converges. The vertical axis gets the same filter (was first-order
        // through 10.7.0): the surface-swim buoyancy spring emits a continuous stream of small vertical corrections,
        // and a first-order vertical decay chased each into a fast, jerky camera bob; the inertia rides over that
        // stream while a sustained vertical bias still resolves. smoothTime is the ~time-to-settle, mapped from the
        // first-order CorrectionRate so the tuning knob keeps its meaning on both axes.
        float smoothTime = settings.CorrectionRate > 0f ? 1f / settings.CorrectionRate : 0f;
        renderOffset = SmoothDampToZero(renderOffset, ref renderOffsetVelocity, smoothTime, dt);
        verticalRenderOffset = SmoothDampToZero(verticalRenderOffset, ref verticalRenderOffsetVelocity, smoothTime, dt);

        float dz = settings.CorrectionDeadZone;
        if (renderOffset.LengthSquared() + verticalRenderOffset * verticalRenderOffset <= dz * dz)
        {
            // Settled within the dead-zone: snap exactly onto the predicted state instead of chasing float jitter.
            renderOffset = Vector2.Zero;
            renderOffsetVelocity = Vector2.Zero;
            verticalRenderOffset = 0f;
            verticalRenderOffsetVelocity = 0f;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// Critically-damped (no-overshoot) decay of <paramref name="current"/> toward zero, carrying
    /// <paramref name="velocity"/> across calls. Closed-form and stable at any frame dt (60/120/uncapped fps), so the
    /// settle is frame-rate independent. <paramref name="smoothTime"/> is the approximate time to reach zero; a value
    /// at or below zero decays instantly. Standard critically-damped smoothing (damping ratio 1): a transient impulse
    /// produces little displacement (inertia), a sustained offset fully resolves. Drives both the vertical axis and,
    /// componentwise, the planar axis.
    /// </summary>
    private static float SmoothDampToZero(float current, ref float velocity, float smoothTime, float dt)
    {
        if (smoothTime <= 0f || dt <= 0f)
        {
            velocity = 0f;
            return smoothTime <= 0f ? 0f : current;
        }

        float omega = 2f / smoothTime;
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        // change = current - target, target = 0.
        float temp = (velocity + omega * current) * dt;
        velocity = (velocity - omega * temp) * exp;
        return (current + temp) * exp;
    }

    /// <summary>Vector form of <see cref="SmoothDampToZero(float, ref float, float, float)"/>. omega/exp depend only on
    /// <paramref name="smoothTime"/> and <paramref name="dt"/>, so the decay is fully component-separable: each axis is
    /// decayed independently, bit-identical to the per-axis scalar call.</summary>
    private static Vector2 SmoothDampToZero(Vector2 current, ref Vector2 velocity, float smoothTime, float dt)
    {
        float vx = velocity.X, vy = velocity.Y;
        float rx = SmoothDampToZero(current.X, ref vx, smoothTime, dt);
        float ry = SmoothDampToZero(current.Y, ref vy, smoothTime, dt);
        velocity = new Vector2(vx, vy);
        return new Vector2(rx, ry);
    }
}
