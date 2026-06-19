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
/// </summary>
public sealed class ClientPrediction<TState, TCommand>
    where TState : struct, IPredictedState<TState>
{
    private readonly ITickSimulator<TState, TCommand> simulator;
    private readonly PredictionSettings settings;
    private readonly SortedList<int, TCommand> pendingCommands = new();
    private TState predictedState;
    private Vector2 renderOffset;
    private int nextSeq;
    // Inter-tick render interpolation: the predicted position only steps once per tick (60Hz). At higher frame
    // rates the render would snap each tick, so the rendered position eases from the previous tick's position to
    // the current one across the tick duration. Frame-rate independent (time-based fraction).
    private Vector2 previousPredictedPosition;
    private float secondsSinceLastPredict;

    public ClientPrediction(ITickSimulator<TState, TCommand> simulator, PredictionSettings? settings = null)
    {
        this.simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        this.settings = settings ?? PredictionSettings.Default;
    }

    /// <summary>The current predicted (authority-tracking) state.</summary>
    public TState PredictedState => predictedState;

    /// <summary>
    /// The state to draw: the predicted position eased from the previous tick toward the current one over the
    /// tick duration (so it stays smooth above the tick rate), plus the decaying reconciliation offset.
    /// </summary>
    public TState RenderedState
    {
        get
        {
            float frac = settings.TickSeconds > 0f
                ? MathF.Min(1f, secondsSinceLastPredict / settings.TickSeconds)
                : 1f;
            Vector2 interpolated = Vector2.Lerp(previousPredictedPosition, predictedState.Position, frac);
            return predictedState.WithPosition(interpolated + renderOffset);
        }
    }

    public void Reset(in TState initialState)
    {
        predictedState = initialState;
        previousPredictedPosition = initialState.Position;
        secondsSinceLastPredict = settings.TickSeconds; // start fully on the current state (frac = 1)
        pendingCommands.Clear();
        renderOffset = Vector2.Zero;
        nextSeq = 0;
    }

    /// <summary>Predicts one command forward and retains it for reconciliation. Returns its seq.</summary>
    public int Predict(in TCommand command)
    {
        int seq = nextSeq++;
        pendingCommands[seq] = command;
        // The position before this step becomes the interpolation start; the render eases toward the new step.
        previousPredictedPosition = predictedState.Position;
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
    /// correction. Large errors hard-snap; sub-dead-zone errors are ignored as float jitter.
    /// </summary>
    public ReconciliationResult Reconcile(int authoritativeTick, in TState authoritativeBasis, int lastAcknowledgedSeq)
    {
        Vector2 previousRenderedPosition = predictedState.Position + renderOffset;

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
        // The rebase moves predictedState to a new basis; collapse the inter-tick interpolation onto it (the
        // visible jump is carried by renderOffset below, not by a stale previous-position lerp).
        previousPredictedPosition = predictedState.Position;

        Vector2 error = previousRenderedPosition - predictedState.Position;
        float positionError = error.Length();
        bool hardSnapApplied = positionError >= settings.HardSnapDistance;
        renderOffset = (hardSnapApplied || positionError <= settings.CorrectionDeadZone) ? Vector2.Zero : error;

        return new ReconciliationResult(authoritativeTick, positionError, hardSnapApplied);
    }

    /// <summary>Advances the inter-tick interpolation clock and decays the smoothing offset toward zero;
    /// frame-rate independent within clamping.</summary>
    public void AdvancePresentation(float elapsedSeconds)
    {
        float dt = MathF.Max(0f, elapsedSeconds);
        // Advance toward the current tick (clamped at one tick so a stalled tick stream holds, not overshoots).
        secondsSinceLastPredict = MathF.Min(secondsSinceLastPredict + dt, settings.TickSeconds);

        if (renderOffset == Vector2.Zero)
        {
            return;
        }

        float blend = MathF.Min(1f, settings.CorrectionRate * dt);
        renderOffset = Vector2.Lerp(renderOffset, Vector2.Zero, blend);
        if (renderOffset.LengthSquared() <= settings.CorrectionDeadZone * settings.CorrectionDeadZone)
        {
            renderOffset = Vector2.Zero;
        }
    }
}
