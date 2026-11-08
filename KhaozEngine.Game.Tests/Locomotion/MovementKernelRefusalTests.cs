using System;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementKernelRefusalTests
{
    static MoveTuning Valid => MoveTuning.Default with { Water = new(WaterTraversalMode.WadeOnly) };

    [Theory]
    [InlineData(nameof(MoveTuning.Gravity), 0, MovementConfigurationRefusal.KernelPositiveValueRequired, 0.25f)]
    [InlineData(nameof(MoveTuning.WalkSpeed), -1, MovementConfigurationRefusal.KernelNonnegativeValueRequired, 0)]
    [InlineData(nameof(MoveTuning.WalkSpeed), float.NaN, MovementConfigurationRefusal.KernelNumberNonfinite, 0)]
    [InlineData(nameof(MoveTuning.SwimEnterDepthFraction), float.PositiveInfinity, MovementConfigurationRefusal.KernelNumberNonfinite, 1)]
    [InlineData(nameof(MoveTuning.SwimEnterDepthFraction), 1.25f, MovementConfigurationRefusal.KernelFractionExceeded, 1)]
    [InlineData(nameof(MoveTuning.AirControl), 1.25f, MovementConfigurationRefusal.KernelFractionExceeded, 1)]
    public void NumericRefusalsNameTheFieldValueAndLimit(string field, float actual,
        MovementConfigurationRefusal reason, float repaired)
    {
        var tuning = Set(Valid, field, actual);
        var failure = Assert.Throws<MovementConfigurationException>(() =>
            MovementConfigurationValidation.ValidateExplicit(tuning, tuning.Water));
        Assert.Equal(reason, failure.Reason);
        Assert.Equal(field, failure.FieldName);
        Assert.Equal((double)actual, failure.ActualNumber);
        Assert.Equal("tuning", failure.ParamName);
        if (reason == MovementConfigurationRefusal.KernelPositiveValueRequired) Assert.Equal(0, failure.MinimumExclusive);
        if (reason == MovementConfigurationRefusal.KernelNonnegativeValueRequired) Assert.Equal(0, failure.MinimumNumber);
        if (reason == MovementConfigurationRefusal.KernelFractionExceeded) Assert.Equal(1, failure.MaximumNumber);
        tuning = Set(Valid, field, repaired);
        MovementConfigurationValidation.ValidateExplicit(tuning, tuning.Water);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FractionOrderingNamesBothValues(bool swim)
    {
        var tuning = swim ? Valid with { SwimEnterDepthFraction = 0.5f, SwimExitDepthFraction = 0.75f }
            : Valid with { WadeStartDepthFraction = 0.5f, WadeEndDepthFraction = 0.5f };
        var failure = Assert.Throws<MovementConfigurationException>(() =>
            MovementConfigurationValidation.ValidateExplicit(tuning, tuning.Water));
        Assert.Equal(swim ? MovementConfigurationRefusal.KernelSwimFractionsUnordered
            : MovementConfigurationRefusal.KernelWadeFractionsUnordered, failure.Reason);
        if (swim)
        {
            Assert.Equal(0.75f, failure.ExitFraction);
            Assert.Equal(0.5f, failure.EnterFraction);
            tuning = tuning with { SwimExitDepthFraction = 0.5f };
        }
        else
        {
            Assert.Equal(0.5f, failure.StartFraction);
            Assert.Equal(0.5f, failure.EndFraction);
            tuning = tuning with { WadeEndDepthFraction = MathF.BitIncrement(0.5f) };
        }
        MovementConfigurationValidation.ValidateExplicit(tuning, tuning.Water);
    }

    [Fact]
    public void KernelGeometryFindingsRetainTheFirstFailedRuleBeforeLaterFields()
    {
        // The independent kernel predicate also serves placement. Shell admission can mask its first rules.
        var tuning = Valid with { CapsuleHalfHeight = 0.25f, CapsuleRadius = 0.5f };
        var finding = MovementKernelValidation.FindInvalid(tuning)!.Value;
        Assert.Equal(MovementConfigurationRefusal.KernelHalfHeightBelowRadius, finding.Reason);
        Assert.Equal(0.25f, finding.CapsuleHalfHeight);
        Assert.Equal(0.5f, finding.CapsuleRadius);
        // With a negative step and pi slope, the shell passes: span is 2.5 and clearance is 1.5.
        // Nonnegative steps cannot pass shell admission with half height below radius.
        var shellAdmitted = tuning with { StepHeight = -2, MaxSlopeRadians = MathF.PI, TractionHysteresisRadians = 0 };
        var failure = Assert.Throws<MovementConfigurationException>(() =>
            MovementConfigurationValidation.ValidateExplicit(shellAdmitted, shellAdmitted.Water));
        Assert.Equal(MovementConfigurationRefusal.KernelHalfHeightBelowRadius, failure.Reason);
        Assert.Equal(0.25f, failure.CapsuleHalfHeight);
        Assert.Equal(0.5f, failure.CapsuleRadius);
        tuning = tuning with { CapsuleHalfHeight = 0.5f };
        Assert.Null(MovementKernelValidation.FindInvalid(tuning));
        Assert.True(ExplicitCharacterMovement.Valid(tuning));
    }

    [Fact]
    public void AFullTurnSlopePassesTheShellButNotTheKernelSlopeDomain()
    {
        var tuning = Valid with { MaxSlopeRadians = 2 * MathF.PI, TractionHysteresisRadians = 0 };
        var failure = Assert.Throws<MovementConfigurationException>(() =>
            MovementConfigurationValidation.ValidateExplicit(tuning, tuning.Water));
        Assert.Equal(MovementConfigurationRefusal.KernelSlopeAtOrAboveHalfPi, failure.Reason);
        Assert.Equal(2 * MathF.PI, failure.ActualSlope);
        Assert.Equal(1.5707964f, failure.MaximumExclusive);
        // At the exclusive boundary's predecessor, a tall shell clears the nearly vertical plane.
        tuning = tuning with { MaxSlopeRadians = MathF.BitDecrement(MathF.PI * 0.5f), StepHeight = 16_777_216, CapsuleHalfHeight = 16_777_216 };
        MovementConfigurationValidation.ValidateExplicit(tuning, tuning.Water);
    }

    [Theory]
    [InlineData(nameof(MoveTuning.CapsuleRadius), 0, MovementConfigurationRefusal.KernelPositiveValueRequired)]
    [InlineData(nameof(MoveTuning.CapsuleHalfHeight), 0, MovementConfigurationRefusal.KernelPositiveValueRequired)]
    [InlineData(nameof(MoveTuning.MaxFallSpeed), 0, MovementConfigurationRefusal.KernelPositiveValueRequired)]
    [InlineData(nameof(MoveTuning.RunSpeed), -1, MovementConfigurationRefusal.KernelNonnegativeValueRequired)]
    [InlineData(nameof(MoveTuning.SwimSpeed), -1, MovementConfigurationRefusal.KernelNonnegativeValueRequired)]
    [InlineData(nameof(MoveTuning.JumpSpeed), -1, MovementConfigurationRefusal.KernelNonnegativeValueRequired)]
    [InlineData(nameof(MoveTuning.GroundedEpsilon), -1, MovementConfigurationRefusal.KernelNonnegativeValueRequired)]
    [InlineData(nameof(MoveTuning.StepHeight), -1, MovementConfigurationRefusal.KernelNonnegativeValueRequired)]
    [InlineData(nameof(MoveTuning.WadeStartDepthFraction), 1.25f, MovementConfigurationRefusal.KernelFractionExceeded)]
    [InlineData(nameof(MoveTuning.WadeEndDepthFraction), 1.25f, MovementConfigurationRefusal.KernelFractionExceeded)]
    [InlineData(nameof(MoveTuning.WadeMinSpeedScale), 1.25f, MovementConfigurationRefusal.KernelFractionExceeded)]
    [InlineData(nameof(MoveTuning.SwimExitDepthFraction), 1.25f, MovementConfigurationRefusal.KernelFractionExceeded)]
    [InlineData(nameof(MoveTuning.SwimSurfaceSubmersionFraction), 1.25f, MovementConfigurationRefusal.KernelFractionExceeded)]
    [InlineData(nameof(MoveTuning.SwimBuoyancyStiffness), -1, MovementConfigurationRefusal.KernelNonnegativeValueRequired)]
    [InlineData(nameof(MoveTuning.CoyoteTime), -1, MovementConfigurationRefusal.KernelNonnegativeValueRequired)]
    [InlineData(nameof(MoveTuning.JumpBuffer), -1, MovementConfigurationRefusal.KernelNonnegativeValueRequired)]
    [InlineData(nameof(MoveTuning.AirBrakeAccel), -1, MovementConfigurationRefusal.KernelNonnegativeValueRequired)]
    [InlineData(nameof(MoveTuning.MaxSlopeRadians), -1, MovementConfigurationRefusal.KernelNonnegativeValueRequired)]
    public void ScalarFindingsAgreeWithTheIndependentKernelPredicate(string field, float actual,
        MovementConfigurationRefusal reason)
    {
        var tuning = Set(Valid, field, actual);
        var finding = MovementKernelValidation.FindInvalid(tuning)!.Value;
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(field, finding.FieldName);
        Assert.Equal((double)actual, finding.ActualNumber);
        Assert.False(ExplicitCharacterMovement.Valid(tuning));
        Assert.Null(MovementKernelValidation.FindInvalid(Valid));
        Assert.True(ExplicitCharacterMovement.Valid(Valid));
    }

    static MoveTuning Set(MoveTuning tuning, string field, float value) => field switch
    {
        nameof(MoveTuning.Gravity) => tuning with { Gravity = value },
        nameof(MoveTuning.WalkSpeed) => tuning with { WalkSpeed = value },
        nameof(MoveTuning.SwimEnterDepthFraction) => tuning with { SwimEnterDepthFraction = value },
        nameof(MoveTuning.AirControl) => tuning with { AirControl = value },
        nameof(MoveTuning.CapsuleRadius) => tuning with { CapsuleRadius = value },
        nameof(MoveTuning.CapsuleHalfHeight) => tuning with { CapsuleHalfHeight = value },
        nameof(MoveTuning.MaxFallSpeed) => tuning with { MaxFallSpeed = value },
        nameof(MoveTuning.RunSpeed) => tuning with { RunSpeed = value },
        nameof(MoveTuning.SwimSpeed) => tuning with { SwimSpeed = value },
        nameof(MoveTuning.JumpSpeed) => tuning with { JumpSpeed = value },
        nameof(MoveTuning.GroundedEpsilon) => tuning with { GroundedEpsilon = value },
        nameof(MoveTuning.StepHeight) => tuning with { StepHeight = value },
        nameof(MoveTuning.WadeStartDepthFraction) => tuning with { WadeStartDepthFraction = value },
        nameof(MoveTuning.WadeEndDepthFraction) => tuning with { WadeEndDepthFraction = value },
        nameof(MoveTuning.WadeMinSpeedScale) => tuning with { WadeMinSpeedScale = value },
        nameof(MoveTuning.SwimExitDepthFraction) => tuning with { SwimExitDepthFraction = value },
        nameof(MoveTuning.SwimSurfaceSubmersionFraction) => tuning with { SwimSurfaceSubmersionFraction = value },
        nameof(MoveTuning.SwimBuoyancyStiffness) => tuning with { SwimBuoyancyStiffness = value },
        nameof(MoveTuning.CoyoteTime) => tuning with { CoyoteTime = value },
        nameof(MoveTuning.JumpBuffer) => tuning with { JumpBuffer = value },
        nameof(MoveTuning.AirBrakeAccel) => tuning with { AirBrakeAccel = value },
        nameof(MoveTuning.MaxSlopeRadians) => tuning with { MaxSlopeRadians = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
}
