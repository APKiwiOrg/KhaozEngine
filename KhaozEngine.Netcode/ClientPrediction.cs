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

    public ClientPrediction(ITickSimulator<TState, TCommand> simulator, PredictionSettings? settings = null)
    {
        this.simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        this.settings = settings ?? PredictionSettings.Default;
    }

    /// <summary>The current predicted (authority-tracking) state.</summary>
    public TState PredictedState => predictedState;

    /// <summary>The predicted state with the smoothing offset applied (what to draw).</summary>
    public TState RenderedState => predictedState.WithPosition(predictedState.Position + renderOffset);

    public void Reset(in TState initialState)
    {
        predictedState = initialState;
        pendingCommands.Clear();
        renderOffset = Vector2.Zero;
        nextSeq = 0;
    }

    /// <summary>Predicts one command forward and retains it for reconciliation. Returns its seq.</summary>
    public int Predict(in TCommand command)
    {
        int seq = nextSeq++;
        pendingCommands[seq] = command;
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

        Vector2 error = previousRenderedPosition - predictedState.Position;
        float positionError = error.Length();
        bool hardSnapApplied = positionError >= settings.HardSnapDistance;
        renderOffset = (hardSnapApplied || positionError <= settings.CorrectionDeadZone) ? Vector2.Zero : error;

        return new ReconciliationResult(authoritativeTick, positionError, hardSnapApplied);
    }

    /// <summary>Decays the smoothing offset toward zero; frame-rate independent within clamping.</summary>
    public void AdvancePresentation(float elapsedSeconds)
    {
        if (renderOffset == Vector2.Zero)
        {
            return;
        }

        float dt = MathF.Max(0f, elapsedSeconds);
        float blend = MathF.Min(1f, settings.CorrectionRate * dt);
        renderOffset = Vector2.Lerp(renderOffset, Vector2.Zero, blend);
        if (renderOffset.LengthSquared() <= settings.CorrectionDeadZone * settings.CorrectionDeadZone)
        {
            renderOffset = Vector2.Zero;
        }
    }
}
