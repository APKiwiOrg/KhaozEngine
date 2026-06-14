namespace KhaozEngine.Netcode;

/// <summary>Tunables for <see cref="ClientPrediction{TState,TCommand}"/>.</summary>
public readonly record struct PredictionSettings(
    float TickSeconds,
    int MaxPendingCommands,
    float HardSnapDistance,
    float CorrectionRate,
    float CorrectionDeadZone)
{
    /// <summary>SpaceGame's defaults: 60 Hz tick, 256-command buffer, 100u snap, rate 8, 1.5u dead-zone.</summary>
    public static PredictionSettings Default => new(
        TickSeconds: 1f / 60f,
        MaxPendingCommands: 256,
        HardSnapDistance: 100f,
        CorrectionRate: 8f,
        CorrectionDeadZone: 1.5f);
}

/// <summary>Outcome of a <see cref="ClientPrediction{TState,TCommand}.Reconcile"/> call.</summary>
public readonly record struct ReconciliationResult(
    int AuthoritativeTick,
    float PositionError,
    bool HardSnapApplied);
