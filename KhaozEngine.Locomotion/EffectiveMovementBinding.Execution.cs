using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public sealed partial class EffectiveMovementBinding
{
    public MovementStepResult Step(in FramedMovementState state, in MoveCommand command,
        float dt, MovementQueryLease queries, MovementBoundary? boundary = null) =>
        ExplicitCharacterMovement.Step(state, command, dt, Tuning, Water, queries, _composition, Boundary(queries, boundary));

    public MovementStepResult StepTowards(in FramedMovementState state, Vector2 direction, bool run,
        float dt, MovementQueryLease queries, MovementBoundary? boundary = null) =>
        ExplicitCharacterMovement.StepTowards(state, direction, run, dt, Tuning, Water, queries, _composition, Boundary(queries, boundary));

    public MovementStepResult ValidatePlacement(in FramedMovementState state,
        MovementQueryLease queries, MovementBoundary? boundary = null) =>
        ExplicitCharacterMovement.ValidatePlacement(state, Tuning, Water, queries, _composition, Boundary(queries, boundary));

    public MovementStepResult SettlePlacement(in FramedMovementState state,
        MovementQueryLease queries, MovementBoundary? boundary = null) =>
        ExplicitCharacterMovement.SettlePlacement(state, Tuning, Water, queries, _composition, Boundary(queries, boundary));

    public MovementStepResult ReclassifyContinuation(in FramedMovementState state,
        MovementQueryLease queries, MovementBoundary? boundary = null) =>
        ExplicitCharacterMovement.ReclassifyContinuation(state, Tuning, Water, queries, _composition, Boundary(queries, boundary));

    MovementBoundary? Boundary(MovementQueryLease queries, MovementBoundary? supplied)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(queries);
        if (!UsesLease) throw new InvalidOperationException("A Legacy binding does not execute leased movement.");
        if (_context is null || !_context.OwnsLease(queries) ||
            _view is not null && !queries.UsesPhysicsView(_view))
            throw new ArgumentException("The movement lease belongs to another binding environment context or view.", nameof(queries));
        return supplied ?? _ordinary?.CaptureBoundary(queries.Frame);
    }
}
