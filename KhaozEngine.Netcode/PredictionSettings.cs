namespace KhaozEngine.Netcode;

/// <summary>Tunables for <see cref="ClientPrediction{TState,TCommand}"/>.</summary>
/// <param name="TickSeconds">Fixed simulation timestep in seconds.</param>
/// <param name="MaxPendingCommands">Maximum retained unacknowledged commands before the oldest is dropped.</param>
/// <param name="HardSnapDistance">Position error (world units) at or above which the correction snaps instantly.</param>
/// <param name="CorrectionRate">Per-second blend rate at which the smoothing offset decays toward zero.</param>
/// <param name="CorrectionDeadZone">Position error (world units) at or below which a correction is ignored as jitter.</param>
public readonly record struct PredictionSettings(
    float TickSeconds,
    int MaxPendingCommands,
    float HardSnapDistance,
    float CorrectionRate,
    float CorrectionDeadZone)
{
    /// <summary>Reasonable defaults for a 60 Hz action game: 60 Hz tick, 256-command buffer, 100u snap, rate 8, 1.5u dead-zone.</summary>
    public static PredictionSettings Default => new(
        TickSeconds: 1f / 60f,
        MaxPendingCommands: 256,
        HardSnapDistance: 100f,
        CorrectionRate: 8f,
        CorrectionDeadZone: 1.5f);
}

/// <summary>Outcome of a <see cref="ClientPrediction{TState,TCommand}.Reconcile"/> call.</summary>
/// <param name="AuthoritativeTick">The server tick this reconciliation was against.</param>
/// <param name="PositionError">Distance (world units) between the pre- and post-reconcile rendered position.</param>
/// <param name="HardSnapApplied">True if the error met <see cref="PredictionSettings.HardSnapDistance"/> and snapped instantly.</param>
public readonly record struct ReconciliationResult(
    int AuthoritativeTick,
    float PositionError,
    bool HardSnapApplied);
