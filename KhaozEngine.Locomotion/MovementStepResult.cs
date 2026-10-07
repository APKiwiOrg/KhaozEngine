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
public readonly record struct MovementStepResult(FramedMovementState State, MovementStepOutcome Outcome);
