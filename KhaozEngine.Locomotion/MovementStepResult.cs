using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>A query refusal is distinct from a proved obstruction and never proves unreachable navigation.</summary>
public enum MovementStepOutcome
{
    EnvironmentUnresolved = 0,
    Advanced,
    Blocked,
    EnvironmentInvalid,
    PlacementRefused,
    FrameMismatch
}

/// <summary>Pure state to publish while the caller still owns the MovementQueryLease.</summary>
public readonly record struct MovementStepResult(FramedMovementState State, MovementStepOutcome Outcome)
{
    /// <summary>Holds carried pose/velocity while consuming the refused press and clearing step-only effects.</summary>
    public static MoveState HoldState(MoveState state)
    {
        state.JumpBufferRemaining = 0;
        state.StepDeltaY = 0;
        state.ClimbRate = 0;
        state.LandingImpactSpeed = 0;
        state.SupportGranted = false;
        state.CommandedVelocity = Vector2.Zero;
        return state;
    }
}
