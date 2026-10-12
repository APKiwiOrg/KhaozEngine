using System.Numerics;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

public static partial class ExplicitCharacterMovement
{
    public static MovementStepResult Step(in FramedMovementState state, in MoveCommand command,
        float deltaSeconds, in MoveTuning tuning, in WaterTraversalPolicy water, MovementQueryLease queries,
        MovementBoundary? boundary = null) =>
        Step(state, command, deltaSeconds, tuning, water, queries, ContactComposition.PublicExplicit(tuning), boundary);

    public static MovementStepResult StepTowards(in FramedMovementState state, Vector2 direction, bool run,
        float deltaSeconds, in MoveTuning tuning, in WaterTraversalPolicy water, MovementQueryLease queries,
        MovementBoundary? boundary = null) =>
        StepTowards(state, direction, run, deltaSeconds, tuning, water, queries, ContactComposition.PublicExplicit(tuning), boundary);

    /// <summary>Rebuilds membership and validates the exact restored/corrected pose without running
    /// a tick or changing its carried state. Retain the lease through publication of a successful basis.</summary>
    public static MovementStepResult ValidatePlacement(in FramedMovementState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries, MovementBoundary? boundary = null) =>
        ValidatePlacement(state, tuning, water, queries, ContactComposition.PublicExplicit(tuning), boundary);

    /// <summary>Classifies a placement without advancing time. A nearby eligible floor is reached
    /// only through shared contact placement. Use ValidatePlacement for an exact correction basis.</summary>
    public static MovementStepResult SettlePlacement(in FramedMovementState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries, MovementBoundary? boundary = null) =>
        SettlePlacement(state, tuning, water, queries, ContactComposition.PublicExplicit(tuning), boundary);

    /// <summary>Rebuilds destination membership for a live continuation without moving the pose,
    /// advancing time or consuming an already accepted buffered press. Footing and water mode are
    /// reclassified from the destination facts. Restore and teleport use SettlePlacement instead.</summary>
    public static MovementStepResult ReclassifyContinuation(in FramedMovementState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries, MovementBoundary? boundary = null) =>
        ReclassifyContinuation(state, tuning, water, queries, ContactComposition.PublicExplicit(tuning), boundary);
}
