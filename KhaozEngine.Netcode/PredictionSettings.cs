namespace KhaozEngine.Netcode;

/// <summary>Tunables for <see cref="ClientPrediction{TState,TCommand}"/>.</summary>
/// <param name="TickSeconds">Fixed simulation timestep in seconds.</param>
/// <param name="MaxPendingCommands">Maximum retained unacknowledged commands before the oldest is dropped.</param>
/// <param name="HardSnapDistance">Position error (world units) at or above which the correction snaps instantly.</param>
/// <param name="CorrectionRate">Per-second blend rate at which the smoothing offset decays toward zero.</param>
/// <param name="CorrectionDeadZone">Smoothing-offset magnitude (world units) at or below which the decaying render
/// offset snaps exactly onto the predicted state, so it settles instead of chasing float jitter forever. This is a
/// presentation-side cleanup threshold, not a reconcile gate: every non-hard-snap correction is smoothed however
/// small, so corrections glide rather than pop.</param>
public readonly record struct PredictionSettings(
    float TickSeconds,
    int MaxPendingCommands,
    float HardSnapDistance,
    float CorrectionRate,
    float CorrectionDeadZone)
{
    /// <summary>Reasonable defaults for a 60 Hz action game: 60 Hz tick, 256-command buffer, 100u snap, rate 8,
    /// 0.03u (3 cm) dead-zone. The dead-zone is small so every human-scale latency misprediction smooths instead of
    /// snapping (a 1.5u dead-zone popped them all, which read as jitter while moving).</summary>
    public static PredictionSettings Default => new(
        TickSeconds: 1f / 60f,
        MaxPendingCommands: 256,
        HardSnapDistance: 100f,
        CorrectionRate: 8f,
        CorrectionDeadZone: 0.03f);
}

/// <summary>Outcome of a <see cref="ClientPrediction{TState,TCommand}.Reconcile"/> call.</summary>
/// <param name="AuthoritativeTick">The server tick this reconciliation was against.</param>
/// <param name="PositionError">Prediction-divergence magnitude (world units, 3D): the distance between the pre-rebase
/// predicted state and the rebased authoritative state (the in-flight render smoothing offset is excluded).</param>
/// <param name="HardSnapApplied">True if the error met <see cref="PredictionSettings.HardSnapDistance"/> and snapped instantly.</param>
public readonly record struct ReconciliationResult(
    int AuthoritativeTick,
    float PositionError,
    bool HardSnapApplied);
