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
/// or popping. Reconcile preserves the ACTUAL on-screen position (the inter-tick interpolated one, not the
/// full-tick one): a snapshot that lands mid inter-tick therefore does not jump the avatar forward by the
/// un-played remainder of the interpolation - the source of the moving/jumping jitter on a remote server.
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
    private int nextSeq;
    // Inter-tick render interpolation: the predicted position only steps once per tick (60Hz). At higher frame
    // rates the render would snap each tick, so the rendered position eases from the previous tick's position to
    // the current one across the tick duration. Frame-rate independent (time-based fraction). Carries the vertical
    // axis alongside the planar one.
    private Vector2 previousPredictedPosition;
    private float previousPredictedVertical;
    private float secondsSinceLastPredict;

    public ClientPrediction(ITickSimulator<TState, TCommand> simulator, PredictionSettings? settings = null)
    {
        this.simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        this.settings = settings ?? PredictionSettings.Default;
    }

    /// <summary>The current predicted (authority-tracking) state.</summary>
    public TState PredictedState => predictedState;

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
        verticalRenderOffset = 0f;
        nextSeq = 0;
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
        // The rebase collapses the inter-tick interpolation onto the new basis (previous == current, frac = 1); the
        // visible glide is carried entirely by the render offset below, so the inter-tick remainder is never double
        // counted and the next Predict starts a fresh ease.
        previousPredictedPosition = predictedState.Position;
        previousPredictedVertical = predictedState.Vertical;
        secondsSinceLastPredict = settings.TickSeconds;

        // Gate on the pure prediction-divergence magnitude (3D): how far the pre-rebase predicted state sat from the
        // rebased authoritative state. This is independent of the in-flight render smoothing offset and of where in
        // the inter-tick interpolation the snapshot landed, so a residual smoothing glide never spuriously hard-snaps.
        Vector2 planarError = oldPlanar - predictedState.Position;
        float verticalError = oldVertical - predictedState.Vertical;
        float positionError = new Vector3(planarError.X, verticalError, planarError.Y).Length();
        bool hardSnapApplied = positionError >= settings.HardSnapDistance;

        if (hardSnapApplied)
        {
            renderOffset = Vector2.Zero;
            verticalRenderOffset = 0f;
        }
        else
        {
            // Continuity: keep the avatar exactly where it was being drawn, then let the offset decay toward the
            // corrected basis. rendered_after = predicted_new + offset == renderedBefore, so no pop on screen.
            renderOffset = renderedPlanar - predictedState.Position;
            verticalRenderOffset = renderedVertical - predictedState.Vertical;
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

        float blend = MathF.Min(1f, settings.CorrectionRate * dt);
        renderOffset = Vector2.Lerp(renderOffset, Vector2.Zero, blend);
        verticalRenderOffset = Lerp(verticalRenderOffset, 0f, blend);

        float dz = settings.CorrectionDeadZone;
        if (renderOffset.LengthSquared() + verticalRenderOffset * verticalRenderOffset <= dz * dz)
        {
            // Settled within the dead-zone: snap exactly onto the predicted state instead of chasing float jitter.
            renderOffset = Vector2.Zero;
            verticalRenderOffset = 0f;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
