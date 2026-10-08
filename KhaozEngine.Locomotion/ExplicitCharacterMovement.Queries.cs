using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public static partial class ExplicitCharacterMovement
{
    static MovementAvailability Move(in MovementBodyQuery body, Vector3 delta, MovementQueryLease queries,
        in WaterTraversalPolicy water, in MoveTuning tuning, out Vector3 position, out bool blocked)
    {
        position = body.Centre;
        Span<Vector3> path = stackalloc Vector3[MovementCapsuleResolver.MaximumEndpoints];
        MovementAvailability status = MovementCapsuleResolver.TryResolvePolicy(body, delta, queries, water.Mode, tuning.SwimEnterDepthFraction, path, out int count, out blocked);
        if (status == MovementAvailability.Known) position = path[count - 1];
        return status;
    }

    static MovementStepOutcome WaterPlacement(in MovementBodyQuery body, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries)
    {
        if (water.Mode is not (WaterTraversalMode.WadeOnly or WaterTraversalMode.DryOnly))
            return MovementStepOutcome.Advanced;
        Span<Vector3> normals = stackalloc Vector3[MovementQueryLease.MaxDomainContacts];
        MovementAvailability status = MovementWaterBoundary.Find(body, Vector3.Zero, body.HalfHeight,
            tuning.SwimEnterDepthFraction, water.Mode, queries, normals, out _, out var hit);
        return status != MovementAvailability.Known ? Outcome(status)
            : hit.InitialOverlap ? MovementStepOutcome.PlacementRefused : MovementStepOutcome.Advanced;
    }

    static MovementAvailability Settle(ref MoveState state, ref MovementSelection selection, float drop,
        in MoveTuning tuning, in WaterTraversalPolicy water, MovementQueryLease queries, out bool supported)
    {
        supported = false;
        MovementBodyQuery body = Body(state.Position, tuning, selection);
        var request = new MovementSupportRequest(body, 0, drop, tuning.MaxSlopeRadians,
            new(selection.Space, body.Feet));
        MovementAvailability status = MovementSupportResolver.Select(request, queries, out var placement);
        if (status != MovementAvailability.Known || placement is null) return status;
        MovementSupportPlacement support = placement.Value;
        status = Move(body, support.Centre - body.Centre, queries, water, tuning, out Vector3 position, out _);
        if (status != MovementAvailability.Known) return status;
        // A cleared endpoint is not permission to skip a blocked approach to that endpoint.
        if (position != support.Centre) return MovementAvailability.Known;
        state.Position = position;
        selection = new(support.Candidate.Space, support.Candidate.Owner, queries.Identity);
        supported = true;
        return MovementAvailability.Known;
    }

    static MovementBodyQuery Body(Vector3 position, in MoveTuning tuning, in MovementSelection selection) =>
        new(position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight, selection.Space, selection.Support);

    static MovementStepResult Hold(in FramedMovementState state, MovementStepOutcome outcome)
    {
        if (!state.IsValid) return new(state, outcome);
        MoveState held = MovementStepResult.HoldState(state.State);
        held.JumpBufferRemaining = 0;
        return new(new(held, state.Frame, null), outcome);
    }

    static MovementStepOutcome Outcome(MovementAvailability status) => status == MovementAvailability.Invalid
        ? MovementStepOutcome.EnvironmentInvalid : MovementStepOutcome.EnvironmentUnresolved;

    static bool Valid(in MoveTuning tuning) =>
        Positive(tuning.CapsuleRadius) && Positive(tuning.CapsuleHalfHeight) &&
        tuning.CapsuleHalfHeight >= tuning.CapsuleRadius && Positive(tuning.Gravity) && Positive(tuning.MaxFallSpeed) &&
        Nonnegative(tuning.WalkSpeed) && Nonnegative(tuning.RunSpeed) && Nonnegative(tuning.SwimSpeed) &&
        Nonnegative(tuning.JumpSpeed) && Nonnegative(tuning.GroundedEpsilon) && Nonnegative(tuning.StepHeight) &&
        Positive(tuning.SwimEnterDepthFraction) && tuning.SwimEnterDepthFraction <= 1 &&
        Nonnegative(tuning.CoyoteTime) && Nonnegative(tuning.MaxSlopeRadians) && tuning.MaxSlopeRadians < MathF.PI * 0.5f;
    static bool Positive(float value) => float.IsFinite(value) && value > 0;
    static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0;
}
