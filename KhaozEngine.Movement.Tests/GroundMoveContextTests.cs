using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class GroundMoveContextTests
{
    private static readonly Vector3 RebasedOrigin = new(100f, 20f, -80f);
    private static readonly MoveTuning Tuning = MoveTuning.Default with
    {
        WalkSpeed = 4f,
        RunSpeed = 8f,
        CapsuleRadius = 0.25f,
        CapsuleHalfHeight = 0.75f,
    };

    [Fact]
    public void ConstructorRejectsMissingGroundProvider()
    {
        Assert.Throws<ArgumentNullException>(() => { _ = new GroundMoveContext(null!); });
    }

    [Fact]
    public void BridgeWalkAndHoldUseCurrentPhysicsOriginBetweenSteps()
    {
        using var absoluteWorld = BridgeWorld(Vector3.Zero);
        using var localWorld = BridgeWorld(RebasedOrigin);
        var absolute = new GroundMoveContext((_, _) => 1f, physics: absoluteWorld);
        var rebased = new GroundMoveContext((_, _) => 1f, physics: localWorld);
        MoveState zeroState = Standing(new Vector3(103f, 4.75f, -76f));
        MoveState localState = zeroState;

        zeroState = absolute.Step(zeroState, Vector2.UnitX, false, 0.125f, Tuning);
        localState = rebased.Step(localState, Vector2.UnitX, false, 0.125f, Tuning);
        AssertEquivalent(zeroState, localState);
        Assert.InRange(zeroState.Position.X, 103.45f, 103.55f);
        Assert.InRange(zeroState.Position.Y, 4.74f, 4.78f);
        Assert.True(zeroState.Grounded);

        Vector3 beforeHold = localState.Position;
        zeroState = absolute.Step(zeroState, Vector2.Zero, false, 0.125f, Tuning);
        localState = rebased.Step(localState, Vector2.Zero, false, 0.125f, Tuning);
        AssertEquivalent(zeroState, localState);
        Assert.Equal(beforeHold.X, localState.Position.X);
        Assert.Equal(beforeHold.Z, localState.Position.Z);
        Assert.True(localState.Grounded);

        localWorld.Rebase(new Vector3(96f, -12f, -72f));
        float beforeRebasedWalk = localState.Position.X;
        zeroState = absolute.Step(zeroState, Vector2.UnitX, false, 0.125f, Tuning);
        localState = rebased.Step(localState, Vector2.UnitX, false, 0.125f, Tuning);
        AssertEquivalent(zeroState, localState);
        Assert.InRange(localState.Position.X - beforeRebasedWalk, 0.3f, 0.55f);
        Assert.InRange(localState.Position.Y, 4.74f, 4.78f);
        Assert.True(localState.Grounded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProvidersReceiveAbsoluteCoordinatesAndFeet(bool rebase)
    {
        using var world = BridgeWorld(rebase ? RebasedOrigin : Vector3.Zero);
        var heights = new List<Vector2>();
        var normals = new List<Vector2>();
        var clamps = new List<Vector2>();
        Vector3 sampledFeet = default;
        var context = new GroundMoveContext(
            (x, z) => { heights.Add(new Vector2(x, z)); return 1f; },
            (x, z) => { normals.Add(new Vector2(x, z)); return Vector3.UnitY; },
            world,
            (x, z) => { clamps.Add(new Vector2(x, z)); return new Vector2(x, z); },
            (x, z, feet) => { sampledFeet = new Vector3(x, feet, z); return MovementMedium.Dry; });

        MoveState result = context.Step(Standing(new Vector3(103f, 4.75f, -76f)),
            Vector2.UnitX, false, 0.125f, Tuning);

        Assert.Equal(new Vector3(103f, 4f, -76f), sampledFeet);
        Assert.NotEmpty(heights);
        Assert.NotEmpty(normals);
        Assert.NotEmpty(clamps);
        foreach (Vector2 sample in heights) AssertAbsoluteColumn(sample);
        foreach (Vector2 sample in normals) AssertAbsoluteColumn(sample);
        foreach (Vector2 sample in clamps) AssertAbsoluteColumn(sample);
        Assert.True(result.Grounded);
        Assert.InRange(result.Position.Y, 4.74f, 4.78f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsoluteMediumSurfaceSlowsWadingWithoutFalseSwimming(bool rebase)
    {
        using var world = BridgeWorld(rebase ? RebasedOrigin : Vector3.Zero);
        var dry = new GroundMoveContext((_, _) => 1f, physics: world);
        var wet = new GroundMoveContext((_, _) => 1f, physics: world,
            medium: (_, _, _) => new MovementMedium(4.5f, true, 0.5f));
        MoveState body = Standing(new Vector3(103f, 4.75f, -76f));

        MoveState dryResult = dry.Step(body, Vector2.UnitX, false, 0.125f, Tuning);
        MoveState wetResult = wet.Step(body, Vector2.UnitX, false, 0.125f, Tuning);

        float dryTravel = dryResult.Position.X - body.Position.X;
        float wetTravel = wetResult.Position.X - body.Position.X;
        Assert.InRange(dryTravel, 0.45f, 0.55f);
        Assert.InRange(wetTravel, 0.1f, 0.24f);
        Assert.True(wetTravel < dryTravel);
        Assert.False(wetResult.Swimming);
        Assert.True(wetResult.Grounded);
    }

    [Fact]
    public void AbsoluteMediumSurfaceSupportsSwimInRebasedWorld()
    {
        using var world = BridgeWorld(RebasedOrigin);
        var context = new GroundMoveContext((_, _) => 1f, physics: world,
            medium: (_, _, _) => new MovementMedium(6f, true));
        MoveState body = Standing(new Vector3(103f, 4.75f, -76f));

        MoveState result = context.Step(body, Vector2.UnitX, false, 0.125f, Tuning);

        Assert.True(result.Swimming);
        Assert.False(result.Grounded);
        Assert.True(result.Position.Y > body.Position.Y);
        Assert.InRange(result.Position.X, 103.2f, 103.4f);
    }

    [Fact]
    public void AbsoluteBoundsClampBothAxesAfterRebase()
    {
        using var world = new BepuPhysicsWorld();
        world.Rebase(RebasedOrigin);
        var context = new GroundMoveContext((_, _) => 1f, physics: world,
            clampXz: (x, z) => new Vector2(Math.Clamp(x, 102f, 103.25f), Math.Clamp(z, -77f, -75.75f)));

        MoveState result = context.Step(Standing(new Vector3(103f, 1.75f, -76f)),
            Vector2.One, true, 0.125f, Tuning);

        Assert.Equal(103.25f, result.Position.X);
        Assert.Equal(-75.75f, result.Position.Z);
        Assert.True(result.Grounded);
        Assert.Equal(1.75f, result.Position.Y);
    }

    [Fact]
    public void AbsoluteNormalCanRefuseUnwalkableTerrain()
    {
        using var world = new BepuPhysicsWorld();
        world.Rebase(RebasedOrigin);
        var context = new GroundMoveContext((x, _) => x > 103.25f ? 4f : 1f,
            (x, _) => x > 103.25f ? Vector3.UnitX : Vector3.UnitY, world);

        MoveState result = context.Step(Standing(new Vector3(103f, 1.75f, -76f)),
            Vector2.UnitX, false, 0.125f, Tuning);

        Assert.Equal(103f, result.Position.X);
        Assert.InRange(result.Position.Y, 1.74f, 1.76f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CarriedAirMomentumAndFacingSurviveCoordinateTranslation(bool rebase)
    {
        using var world = new BepuPhysicsWorld();
        if (rebase) world.Rebase(RebasedOrigin);
        var context = new GroundMoveContext((_, _) => 1f, physics: world);
        MoveTuning tuning = Tuning with { AirMomentum = true, AirControl = 0f, FacingTurnSpeed = 0f };
        var body = new MoveState
        {
            Position = new Vector3(103f, 10f, -76f),
            VerticalVelocity = -2f,
            HorizontalVelocity = new Vector2(2f, -1f),
            FacingYaw = 1.25f,
            TimeSinceGrounded = 0.5f,
            JumpBufferRemaining = 0.08f,
            SpeedScale = 0.5f,
        };

        MoveState result = context.Step(body, Vector2.Zero, false, 0.125f, tuning);

        Assert.InRange(Vector3.Distance(new Vector3(103.25f, 9.359375f, -76.125f), result.Position),
            0f, 0.00003f);
        Assert.Equal(new Vector2(2f, -1f), result.HorizontalVelocity);
        Assert.Equal(-5.125f, result.VerticalVelocity);
        Assert.Equal(1.25f, result.FacingYaw);
        Assert.Equal(0.5f, result.SpeedScale);
        Assert.Equal(0.625f, result.TimeSinceGrounded);
        Assert.Equal(0f, result.JumpBufferRemaining);
        Assert.False(result.Grounded);
    }

    [Fact]
    public void CarriedCommitmentAdvancesThroughCharacterMovement()
    {
        using var world = new BepuPhysicsWorld();
        world.Rebase(RebasedOrigin);
        var context = new GroundMoveContext((_, _) => 1f, physics: world);
        MoveState body = Standing(new Vector3(103f, 1.75f, -76f));
        body.Commitment = new MovementCommitment(7, Vector2.UnitX, 4f, 3f, 25f,
            preparationSeconds: 0.5f, recoverySeconds: 0.2f, timeoutSeconds: 5f);
        body.FacingYaw = 1.25f;

        MoveState result = context.Step(body, Vector2.UnitY, true, 0.125f, Tuning);

        Assert.Equal(body.Position, result.Position);
        Assert.Equal(7u, result.Commitment.Sequence);
        Assert.Equal(MovementCommitmentPhase.Preparing, result.Commitment.Phase);
        Assert.Equal(0.375f, result.Commitment.PreparationRemaining);
        Assert.Equal(1.25f, result.FacingYaw);
    }

    [Fact]
    public void TerrainOnlyUsesZeroOriginAndPreciseDirectionWithEntitySpeedScale()
    {
        Vector3 feet = default;
        var context = new GroundMoveContext((_, _) => 5f,
            medium: (x, z, y) => { feet = new Vector3(x, y, z); return MovementMedium.Dry; });
        MoveState body = Standing(new Vector3(0f, 5.75f, -2f));
        body.SpeedScale = 0.5f;

        MoveState result = context.Step(body, new Vector2(0.0001f, 0f), true, 0.125f, Tuning);

        Assert.Null(context.Physics);
        Assert.Null(context.MovementQueries);
        Assert.Equal(new Vector3(0f, 5f, -2f), feet);
        Assert.Equal(0.00005f, result.Position.X, 8);
        Assert.Equal(5.75f, result.Position.Y);
        Assert.Equal(-2f, result.Position.Z);
        Assert.Equal(0.0004f, result.CommandedVelocity.X, 8);
        Assert.True(result.Grounded);
    }

    [Fact]
    public void GroundTuningRejectsInconsistentCapsuleDimensions()
    {
        var context = new GroundMoveContext((_, _) => 0f);
        void validateWideShortBody() => context.Step(Standing(Vector3.UnitY), Vector2.Zero,
            false, 0.125f, Tuning with { CapsuleRadius = 1f, CapsuleHalfHeight = 0.75f });

        Assert.Throws<ArgumentOutOfRangeException>(validateWideShortBody);
    }

    [Theory]
    [InlineData(0.05f, 0.1f)]
    [InlineData(0.25f, 0.255f)]
    public void GroundTuningAcceptsDeclaredMinimumDimensions(float radius, float halfHeight)
    {
        var context = new GroundMoveContext((_, _) => 0f);
        MoveTuning tuning = Tuning with { CapsuleRadius = radius, CapsuleHalfHeight = halfHeight };

        context.ValidateTuning(tuning);
        MoveState result = context.Step(Standing(new Vector3(0f, halfHeight, 0f)),
            Vector2.Zero, false, 0.125f, tuning);

        Assert.Equal(halfHeight, result.Position.Y);
        Assert.True(result.Grounded);
    }

    [Fact]
    public void GroundTuningAllowsZeroPaceAndGravity()
    {
        var context = new GroundMoveContext((_, _) => 0f);
        MoveTuning tuning = Tuning with { WalkSpeed = 0f, RunSpeed = 0f, Gravity = 0f, MaxFallSpeed = 0f };
        var body = new MoveState { Position = new Vector3(0f, 10f, 0f) };

        MoveState result = context.Step(body, Vector2.UnitX, true, 0.125f, tuning);

        Assert.Equal(body.Position, result.Position);
        Assert.Equal(0f, result.VerticalVelocity);
        Assert.False(result.Grounded);
    }

    [Theory]
    [MemberData(nameof(InvalidGroundTuning))]
    public void GroundTuningRejectsInvalidControlsBeforeSampling(MoveTuning tuning)
    {
        bool sampled = false;
        var context = new GroundMoveContext((_, _) => { sampled = true; return 0f; });

        Assert.Throws<ArgumentOutOfRangeException>(() => { context.ValidateTuning(tuning); });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            context.Step(Standing(Vector3.UnitY), Vector2.Zero, false, 0.125f, tuning);
        });
        Assert.False(sampled);
    }

    public static IEnumerable<object[]> InvalidGroundTuning()
    {
        MoveTuning[] invalid =
        [
            Tuning with { WalkSpeed = -1f }, Tuning with { WalkSpeed = float.NaN },
            Tuning with { RunSpeed = float.PositiveInfinity }, Tuning with { RunSpeed = -1f },
            Tuning with { CapsuleRadius = 0f }, Tuning with { CapsuleRadius = float.NaN },
            Tuning with { CapsuleHalfHeight = float.PositiveInfinity },
            Tuning with { CapsuleRadius = 0.05f, CapsuleHalfHeight = 0.099f },
            Tuning with { CapsuleRadius = 0.25f, CapsuleHalfHeight = 0.254f },
            Tuning with { MaxSlopeRadians = float.NaN }, Tuning with { MaxSlopeRadians = -0.1f },
            Tuning with { MaxSlopeRadians = MathF.PI / 2f },
            Tuning with { StepHeight = -0.1f }, Tuning with { StepHeight = float.PositiveInfinity },
            Tuning with { Gravity = float.NaN }, Tuning with { Gravity = -1f },
            Tuning with { MaxFallSpeed = -1f }, Tuning with { GroundedEpsilon = -0.1f },
            Tuning with { MaxStepClimbSpeed = float.NaN }, Tuning with { MaxStepClimbSpeed = -1f },
            Tuning with { AirControl = -0.1f }, Tuning with { AirControl = 1.1f },
            Tuning with { CoyoteTime = float.NaN }, Tuning with { JumpBuffer = -0.1f },
            Tuning with { JumpSpeed = float.NaN }, Tuning with { AirBrakeAccel = -1f },
            Tuning with { FacingTurnSpeed = float.NaN }, Tuning with { FacingTurnSpeed = -1f },
            Tuning with { TractionHysteresisRadians = -0.1f },
            Tuning with { TractionHysteresisRadians = float.PositiveInfinity },
            Tuning with { SlideFrictionRampRadians = float.NaN },
            Tuning with { WadeStartDepthFraction = -0.1f },
            Tuning with { WadeEndDepthFraction = 0.1f },
            Tuning with { WadeMinSpeedScale = 1.1f },
            Tuning with { SwimExitDepthFraction = 0.7f },
            Tuning with { SwimEnterDepthFraction = float.NaN },
            Tuning with { SwimSpeed = -1f }, Tuning with { SwimSurfaceSubmersionFraction = 1.1f },
            Tuning with { SwimBuoyancyStiffness = float.PositiveInfinity },
            default,
        ];
        foreach (MoveTuning tuning in invalid) yield return [tuning];
    }

    [Fact]
    public void OriginMutationDuringProviderIsRejectedAndNextStepCanRecover()
    {
        using var world = BridgeWorld(RebasedOrigin);
        bool mutate = true;
        var context = new GroundMoveContext((_, _) =>
        {
            if (mutate) world.Rebase(new Vector3(96f, -12f, -72f));
            return 1f;
        }, physics: world);
        MoveState body = Standing(new Vector3(103f, 4.75f, -76f));

        Assert.Throws<InvalidOperationException>(() => { context.Step(body, Vector2.UnitX, false, 0.125f, Tuning); });
        mutate = false;
        MoveState result = context.Step(body, Vector2.UnitX, false, 0.125f, Tuning);

        Assert.True(result.Grounded);
        Assert.InRange(result.Position.X, 103.45f, 103.55f);
        Assert.InRange(result.Position.Y, 4.74f, 4.78f);
    }

    [Fact]
    public void RecursiveStepCannotReplaceTheFrozenOrigin()
    {
        GroundMoveContext context = null!;
        MoveState body = Standing(new Vector3(0f, 0.75f, 0f));
        context = new GroundMoveContext((_, _) =>
        {
            context.Step(body, Vector2.Zero, false, 0.125f, Tuning);
            return 0f;
        });

        Assert.Throws<InvalidOperationException>(() => { context.Step(body, Vector2.Zero, false, 0.125f, Tuning); });
    }

    [Fact]
    public void ContextDoesNotAdvanceCallerOwnedPhysics()
    {
        using var world = new BepuPhysicsWorld();
        world.Rebase(RebasedOrigin);
        DynamicBodyHandle dynamic = world.AddDynamic(new BoxShape(new Vector3(0.5f)),
            Pose.At(new Vector3(30f, 30f, 30f)), DynamicBodyDescription.WithMass(1f));
        Pose before = world.GetDynamicPose(dynamic);
        var context = new GroundMoveContext((_, _) => 1f, physics: world);

        context.Step(Standing(new Vector3(103f, 1.75f, -76f)), Vector2.UnitX, false, 0.125f, Tuning);

        Assert.Equal(before, world.GetDynamicPose(dynamic));
        Assert.Equal(RebasedOrigin, world.Origin);
    }

    private static BepuPhysicsWorld BridgeWorld(Vector3 origin)
    {
        var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(8f, 0.5f, 8f)), Pose.At(new Vector3(104f, 0.5f, -76f)));
        world.AddStatic(new BoxShape(new Vector3(4f, 0.25f, 4f)), Pose.At(new Vector3(104f, 3.75f, -76f)));
        if (origin != Vector3.Zero) world.Rebase(origin);
        return world;
    }

    private static MoveState Standing(Vector3 position) => new() { Position = position, Grounded = true };

    private static void AssertAbsoluteColumn(Vector2 sample)
    {
        Assert.InRange(sample.X, 102f, 105f);
        Assert.InRange(sample.Y, -77f, -75f);
    }

    private static void AssertEquivalent(MoveState expected, MoveState actual)
    {
        Assert.InRange(Vector3.Distance(expected.Position, actual.Position), 0f, 0.00003f);
        Assert.Equal(expected.Grounded, actual.Grounded);
        Assert.Equal(expected.VerticalVelocity, actual.VerticalVelocity);
        Assert.Equal(expected.FacingYaw, actual.FacingYaw);
        Assert.Equal(expected.SpeedScale, actual.SpeedScale);
        Assert.Equal(expected.CommandedVelocity, actual.CommandedVelocity);
    }
}
