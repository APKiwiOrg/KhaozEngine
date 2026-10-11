using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public static partial class ExplicitCharacterMovement
{
    internal static MovementStepOutcome WaterPlacement(in MovementBodyQuery body, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries, MovementBoundary? boundary)
    {
        if (boundary is not null)
        {
            MovementAvailability availability = boundary.Classify(body.Centre, out bool inside);
            if (availability != MovementAvailability.Known) return Outcome(availability);
            if (!inside) return MovementStepOutcome.PlacementRefused;
        }
        if (water.Mode is not (WaterTraversalMode.SurfaceSwimmer or WaterTraversalMode.WadeOnly or WaterTraversalMode.DryOnly))
            return MovementStepOutcome.Advanced;
        Span<Vector3> normals = stackalloc Vector3[MovementQueryLease.MaxDomainContacts];
        MovementAvailability status = MovementWaterBoundary.Find(body, Vector3.Zero, body.HalfHeight,
            tuning.SwimEnterDepthFraction, water.Mode, queries, normals, out _, out var hit);
        return status != MovementAvailability.Known ? Outcome(status)
            : hit.InitialOverlap ? MovementStepOutcome.PlacementRefused : MovementStepOutcome.Advanced;
    }

    static MovementBodyQuery Body(Vector3 position, in MoveTuning tuning, in MovementSelection selection) =>
        new(position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight, selection.Space, selection.Support);

    internal static MovementStepResult Hold(in FramedMovementState state, MovementStepOutcome outcome)
    {
        if (!state.IsValid) return new(state, outcome);
        MoveState held = MovementStepResult.HoldState(state.State);
        held.JumpBufferRemaining = 0;
        return new(new(held, state.Frame, null), outcome);
    }

    internal static MovementStepOutcome Outcome(MovementAvailability status) => status == MovementAvailability.Invalid
        ? MovementStepOutcome.EnvironmentInvalid : MovementStepOutcome.EnvironmentUnresolved;

    internal static bool ValidCarriedState(in MoveState state) =>
        Nonnegative(state.TimeSinceGrounded) && Nonnegative(state.JumpBufferRemaining) &&
        ValidCommitment(state.Commitment) && Nonnegative(state.ClimbRateEwma) && Nonnegative(state.SpeedScale) && float.IsFinite(state.FacingYaw) &&
        float.IsFinite(state.HorizontalVelocity.X) && float.IsFinite(state.HorizontalVelocity.Y) &&
        state.WaterExcursion is >= WaterExcursionState.None and <= WaterExcursionState.AirborneFromWater;

    internal static bool Valid(in MoveTuning tuning) =>
        Positive(tuning.CapsuleRadius) && Positive(tuning.CapsuleHalfHeight) &&
        tuning.CapsuleHalfHeight >= tuning.CapsuleRadius && Positive(tuning.Gravity) && Positive(tuning.MaxFallSpeed) &&
        Nonnegative(tuning.WalkSpeed) && Nonnegative(tuning.RunSpeed) && Nonnegative(tuning.SwimSpeed) &&
        Nonnegative(tuning.JumpSpeed) && Nonnegative(tuning.GroundedEpsilon) && Nonnegative(tuning.StepHeight) &&
        Fraction(tuning.WadeStartDepthFraction) && Fraction(tuning.WadeEndDepthFraction) &&
        tuning.WadeEndDepthFraction > tuning.WadeStartDepthFraction && Fraction(tuning.WadeMinSpeedScale) &&
        Positive(tuning.SwimEnterDepthFraction) && tuning.SwimEnterDepthFraction <= 1 &&
        Fraction(tuning.SwimExitDepthFraction) && tuning.SwimExitDepthFraction <= tuning.SwimEnterDepthFraction &&
        Fraction(tuning.SwimSurfaceSubmersionFraction) && Nonnegative(tuning.SwimBuoyancyStiffness) &&
        Nonnegative(tuning.CoyoteTime) && Nonnegative(tuning.JumpBuffer) && Fraction(tuning.AirControl) &&
        Nonnegative(tuning.AirBrakeAccel) && Nonnegative(tuning.MaxSlopeRadians) && tuning.MaxSlopeRadians < MathF.PI * 0.5f;
    static bool Fraction(float value) => Nonnegative(value) && value <= 1;
    static bool Positive(float value) => float.IsFinite(value) && value > 0;
    static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0;
}
