using System;

namespace KhaozEngine.Locomotion;

/// <summary>Diagnostics for the unchanged ExplicitCharacterMovement.Valid predicate, in its evaluation order.</summary>
internal static class MovementKernelValidation
{
    internal static MovementKernelFinding? FindInvalid(in MoveTuning t) =>
        Number(t.CapsuleRadius, nameof(t.CapsuleRadius), positive: true) ??
        Number(t.CapsuleHalfHeight, nameof(t.CapsuleHalfHeight), positive: true) ??
        (t.CapsuleHalfHeight < t.CapsuleRadius ? new(MovementConfigurationRefusal.KernelHalfHeightBelowRadius)
        { CapsuleRadius = t.CapsuleRadius, CapsuleHalfHeight = t.CapsuleHalfHeight } : (MovementKernelFinding?)null) ??
        Number(t.Gravity, nameof(t.Gravity), positive: true) ??
        Number(t.MaxFallSpeed, nameof(t.MaxFallSpeed), positive: true) ??
        Number(t.WalkSpeed, nameof(t.WalkSpeed)) ?? Number(t.RunSpeed, nameof(t.RunSpeed)) ??
        Number(t.SwimSpeed, nameof(t.SwimSpeed)) ?? Number(t.JumpSpeed, nameof(t.JumpSpeed)) ??
        Number(t.GroundedEpsilon, nameof(t.GroundedEpsilon)) ?? Number(t.StepHeight, nameof(t.StepHeight)) ??
        Fraction(t.WadeStartDepthFraction, nameof(t.WadeStartDepthFraction)) ??
        Fraction(t.WadeEndDepthFraction, nameof(t.WadeEndDepthFraction)) ??
        (t.WadeEndDepthFraction <= t.WadeStartDepthFraction ? new(MovementConfigurationRefusal.KernelWadeFractionsUnordered)
        { StartFraction = t.WadeStartDepthFraction, EndFraction = t.WadeEndDepthFraction } : (MovementKernelFinding?)null) ??
        Fraction(t.WadeMinSpeedScale, nameof(t.WadeMinSpeedScale)) ??
        Fraction(t.SwimEnterDepthFraction, nameof(t.SwimEnterDepthFraction), positive: true) ??
        Fraction(t.SwimExitDepthFraction, nameof(t.SwimExitDepthFraction)) ??
        (t.SwimExitDepthFraction > t.SwimEnterDepthFraction ? new(MovementConfigurationRefusal.KernelSwimFractionsUnordered)
        { ExitFraction = t.SwimExitDepthFraction, EnterFraction = t.SwimEnterDepthFraction } : (MovementKernelFinding?)null) ??
        Fraction(t.SwimSurfaceSubmersionFraction, nameof(t.SwimSurfaceSubmersionFraction)) ??
        Number(t.SwimBuoyancyStiffness, nameof(t.SwimBuoyancyStiffness)) ??
        Number(t.CoyoteTime, nameof(t.CoyoteTime)) ?? Number(t.JumpBuffer, nameof(t.JumpBuffer)) ??
        Fraction(t.AirControl, nameof(t.AirControl)) ?? Number(t.AirBrakeAccel, nameof(t.AirBrakeAccel)) ??
        Number(t.MaxSlopeRadians, nameof(t.MaxSlopeRadians)) ??
        (t.MaxSlopeRadians >= MathF.PI * 0.5f ? new(MovementConfigurationRefusal.KernelSlopeAtOrAboveHalfPi)
        { ActualSlope = t.MaxSlopeRadians, MaximumExclusive = MathF.PI * 0.5f } : (MovementKernelFinding?)null);

    static MovementKernelFinding? Number(float value, string field, bool positive = false)
    {
        if (!float.IsFinite(value))
            return new(MovementConfigurationRefusal.KernelNumberNonfinite) { FieldName = field, ActualNumber = value };
        if (positive ? value <= 0 : value < 0)
            return positive
                ? new(MovementConfigurationRefusal.KernelPositiveValueRequired) { FieldName = field, ActualNumber = value, MinimumExclusive = 0 }
                : new(MovementConfigurationRefusal.KernelNonnegativeValueRequired) { FieldName = field, ActualNumber = value, MinimumNumber = 0 };
        return null;
    }

    static MovementKernelFinding? Fraction(float value, string field, bool positive = false) =>
        Number(value, field, positive) ?? (value > 1 ? new(MovementConfigurationRefusal.KernelFractionExceeded)
        { FieldName = field, ActualNumber = value, MaximumNumber = 1 } : (MovementKernelFinding?)null);
}
