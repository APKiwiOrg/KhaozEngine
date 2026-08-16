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
/// <param name="HardSnapApplied">True if the correction cut instantly (rather than glided): the error met
/// <see cref="PredictionSettings.HardSnapDistance"/>, OR the authoritative <see cref="IPredictedState{T}.TeleportEpoch"/>
/// advanced (an in-session teleport cuts regardless of distance).</param>
/// <param name="Teleported">True when the local player's world position changed DISCONTINUOUSLY this reconciliation.
/// Three things set it, and nothing else does: the first reconcile after a
/// <see cref="ClientPrediction{TState,TCommand}.Reset"/> (a first-ever join, which has no prior position to be
/// continuous with), the first reconcile after a <see cref="ClientPrediction{TState,TCommand}.Reseed"/> whose resume
/// position sits at or beyond <see cref="PredictionSettings.HardSnapDistance"/> from where this client was, and an
/// advance of the authoritative <see cref="IPredictedState{T}.TeleportEpoch"/> (an in-session server teleport:
/// respawn, admin move, fast travel). A transport reconnect that resumes the same position is NOT a teleport, even
/// though it reseeds prediction, and an ordinary smoothed correction never sets it either.
/// <para>The contract is deliberately expensive to honour and therefore deliberately rare: a consumer answers it by
/// snapping a follow camera, running a screen transition, and re-centring anything keyed to the player's position
/// (a terrain streamer's ring, a spatial audio bed, an occlusion cache). Treat it as "the player is somewhere else
/// now", not as "the session changed".</para>
/// <para>Note a (re)seed that reports a teleport sets this but not necessarily <see cref="HardSnapApplied"/>, since
/// the seed already places the avatar with no glide.</para></param>
public readonly record struct ReconciliationResult(
    int AuthoritativeTick,
    float PositionError,
    bool HardSnapApplied,
    bool Teleported = false);
