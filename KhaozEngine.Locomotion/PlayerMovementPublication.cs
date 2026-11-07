namespace KhaozEngine.Locomotion;

/// <summary>Canonical player state, shared by certification, prediction, replay and authority.
/// Publication is part of each simulated step, never a change to only the follower's expectation.</summary>
public static class PlayerMovementPublication
{
    /// <summary>Stable semantic identity bound into every Contact bake. Change when this policy changes.</summary>
    public const string Identity = "player-publication-1/horizontal-256-clamp127/speed-offset1024-clamp8/climb20/facing-turn65536/ewma-seed/step-hint-clear/ties-even";

    /// <summary>Applies the existing wire grids without changing pose, timers, commitment or step-only outputs.</summary>
    public static MoveState Canonicalize(MoveState state)
    {
        state.HorizontalVelocity = new(
            MovementStateQuantization.DecodeHorizontalVelocity(MovementStateQuantization.QuantizeHorizontalVelocity(state.HorizontalVelocity.X)),
            MovementStateQuantization.DecodeHorizontalVelocity(MovementStateQuantization.QuantizeHorizontalVelocity(state.HorizontalVelocity.Y)));
        state.SpeedScale = MovementStateQuantization.DecodeSpeedScale(MovementStateQuantization.QuantizeSpeedScale(state.SpeedScale));
        state.FacingYaw = MovementStateQuantization.DecodeFacingYaw(MovementStateQuantization.QuantizeFacingYaw(state.FacingYaw));
        state.ClimbRate = MovementStateQuantization.DecodeClimbRate(MovementStateQuantization.QuantizeClimbRate(state.ClimbRate));
        state.ClimbRateEwma = state.ClimbRate > 0 ? state.ClimbRate : 0;
        return state;
    }

    /// <summary>Seeds a player step from its published carry. A previous discrete-step event is not carried.</summary>
    public static MoveState PrepareStep(MoveState state) => Canonicalize(state) with { StepDeltaY = 0 };

    /// <summary>Publishes a leased step while preserving its frame, selection, refusal and contact evidence.</summary>
    public static MovementStepResult Canonicalize(in MovementStepResult result) => result with
    {
        State = new(Canonicalize(result.State.State), result.State.Frame, result.State.Selection)
    };
}
