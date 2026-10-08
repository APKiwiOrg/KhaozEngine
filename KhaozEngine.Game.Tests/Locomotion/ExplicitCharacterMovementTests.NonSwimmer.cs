using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Theory]
    [InlineData(WaterTraversalMode.DryOnly, 0.3f, 0.75f)]
    [InlineData(WaterTraversalMode.WadeOnly, 1f, 0.75f)]
    [InlineData(WaterTraversalMode.WadeOnly, 0.6f, 0.4f)]
    public void NonSwimmerStopsBeforeAThinForbiddenInterval(WaterTraversalMode mode, float surface, float halfHeight)
    {
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("channel", "room", new(new(1, 0, -8), new(1.05f, surface, 8)), surface)]);
        var tuning = Tuning with { CapsuleHalfHeight = halfHeight };
        var start = scene.State(new(0, halfHeight + 0.002f, 0), grounded: true);
        var result = ExplicitCharacterMovement.StepTowards(start, Vector2.UnitX, false, 1, tuning, new(mode), scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.InRange(result.State.State.Position.X, 0.73f, 0.75f);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
    }

    [Fact]
    public void WadeOnlyTraversesProvedSupportedShallows()
    {
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("shallows", "room", new(new(-8, 0, -8), new(8, 0.3f, 8)), 0.3f)]);
        var result = ExplicitCharacterMovement.StepTowards(scene.State(new(0, 0.752f, 0), grounded: true),
            Vector2.UnitX, false, 0.1f, Tuning, new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Position.X > 0);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
    }

    [Theory]
    [InlineData(WaterTraversalMode.DryOnly)]
    [InlineData(WaterTraversalMode.WadeOnly)]
    public void ForbiddenStartingOverlapIsAPlacementRefusal(WaterTraversalMode mode)
    {
        using var scene = new Scene(floor: true, wet: true);
        var state = scene.State(new(0, 0.752f, 0), grounded: true);
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.1f, Tuning, new(mode), scene.Lease);
        Assert.Equal(MovementStepOutcome.PlacementRefused, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
        Assert.Equal(Vector2.Zero, result.State.State.CommandedVelocity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonSwimmerProjectsTheWholeShoreCorner(bool reverse)
    {
        Water a = new("east", "room", new(new(1, 0, -8), new(8, 1, 8)), 1);
        Water b = new("north", "room", new(new(-8, 0, 1), new(1, 1, 8)), 1);
        using var scene = new Scene(floor: true, waterRegions: reverse ? [b, a] : [a, b]);
        var result = ExplicitCharacterMovement.StepTowards(scene.State(new(0, 0.752f, 0), grounded: true),
            Vector2.One, false, 1, Tuning, new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.InRange(result.State.State.Position.X, 0.73f, 0.75f);
        Assert.InRange(result.State.State.Position.Z, 0.73f, 0.75f);
    }

    [Fact]
    public void NonSwimmerSlidesAlongTheShoreWithoutLosingTheOtherComponent()
    {
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("lake", "room", new(new(1, 0, -8), new(8, 1, 8)), 1)]);
        var result = ExplicitCharacterMovement.StepTowards(scene.State(new(0, 0.752f, 0), grounded: true),
            Vector2.One, false, 1, Tuning, new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.InRange(result.State.State.Position.X, 0.73f, 0.75f);
        Assert.InRange(result.State.State.Position.Z, 2.82f, 2.84f);
    }
    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.75f)]
    public void WadeOnlyStopsAtItsOwnBodyDepthThresholdDuringDescent(float halfHeight)
    {
        using var scene = new Scene(wet: true);
        var tuning = Tuning with { CapsuleHalfHeight = halfHeight };
        float boundary = 1 + halfHeight * (1 - 2 * tuning.SwimEnterDepthFraction);
        var result = ExplicitCharacterMovement.StepTowards(scene.State(new(0, boundary + 0.2f, 0)),
            Vector2.UnitX, false, 0.2f, tuning, new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.InRange(result.State.State.Position.Y, boundary, boundary + 0.005f);
        Assert.Equal(result.State.State.CommandedVelocity.X * 0.2f, result.State.State.Position.X, 5);
        Assert.False(result.State.State.Swimming);
    }

    [Fact]
    public void WaterSlideRetracesIntoTheRealSolidWall()
    {
        using var scene = new Scene(floor: true, wall: true, waterRegions:
            [new Water("lake", "room", new(new(-8, 0, 1), new(8, 1, 8)), 1)]);
        var result = ExplicitCharacterMovement.StepTowards(scene.State(new(0, 0.752f, 0), grounded: true),
            Vector2.One, false, 1, Tuning, new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.InRange(result.State.State.Position.X, 1.70f, 1.71875f);
        Assert.InRange(result.State.State.Position.Z, 0.73f, 0.75f);
    }

    [Theory]
    [InlineData(MovementAvailability.Unresolved, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.CapacityExceeded, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.Invalid, MovementStepOutcome.EnvironmentInvalid)]
    public void FailedWaterCorrectionDiscardsTheEntireTentativeStep(MovementAvailability failure, MovementStepOutcome expected)
    {
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("lake", "room", new(new(1, 0, -8), new(8, 1, 8)), 1)]);
        var real = scene.Environment.Acquisition.OnCoverage!;
        bool corrected = false;
        scene.Environment.Acquisition.OnCoverage = (in MovementMediumSweepQuery query, System.Span<MovementCoverageSpan> spans,
            System.Span<MovementDomainContact> contacts) =>
        {
            if (query.Body.Centre.X > 0.1f && query.Delta.Z > 0)
            {
                corrected = true;
                return new(failure, 0, 0, 0, 0, 0, Identity);
            }
            return real(query, spans, contacts);
        };
        var state = scene.State(new(0, 0.752f, 0), grounded: true);
        var result = ExplicitCharacterMovement.StepTowards(state, Vector2.One, false, 1, Tuning,
            new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.True(corrected);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(state.State.Position, result.State.State.Position);
        Assert.Equal(Vector2.Zero, result.State.State.CommandedVelocity);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(0f)]
    [InlineData(1.1f)]
    public void InvalidDepthPolicyCannotAdmitWetMotion(float fraction)
    {
        using var scene = new Scene(wet: true);
        var result = ExplicitCharacterMovement.Step(scene.State(new(0, 0.8f, 0)), MoveCommand.Idle, 0.1f,
            Tuning with { SwimEnterDepthFraction = fraction }, new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.EnvironmentInvalid, result.Outcome);
    }

    [Theory]
    [InlineData(-1f, 0f, MovementStepOutcome.Advanced)]
    [InlineData(0f, 1f, MovementStepOutcome.Advanced)]
    [InlineData(1f, 0f, MovementStepOutcome.Blocked)]
    public void CertifiedExactTangencyAllowsAwayAndParallelButBlocksInward(float x, float z, MovementStepOutcome expected)
    {
        using var scene = new Scene(waterRegions:
            [new Water("lake", "room", new(new(1, -4, -8), new(8, 1, 8)), 1)]);
        scene.Environment.CertifyAxisSeparation = true;
        var result = ExplicitCharacterMovement.StepTowards(scene.State(new(0.75f, 0.75f, 0)),
            new(x, z), false, 0.125f, Tuning, new(WaterTraversalMode.DryOnly), scene.Lease);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(x < 0 ? 0.25f : 0.75f, result.State.State.Position.X);
        Assert.Equal(z * 0.5f, result.State.State.Position.Z);
    }

    [Fact]
    public void WadeOnlyReclassifiesCarriedSwimmingHistoryUsingItsEntryThreshold()
    {
        using var scene = new Scene(floor: true, floorY: 0.1f, wet: true, waterDepth: 0.9f);
        var result = ExplicitCharacterMovement.Step(Swimmer(scene, 0.851f), MoveCommand.Idle, 0.125f,
            Tuning, new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
    }

}
