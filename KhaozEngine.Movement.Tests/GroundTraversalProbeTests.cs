using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class GroundTraversalProbeTests
{
    internal static readonly MoveTuning Tuning = MoveTuning.Default with
    {
        CapsuleRadius = 0.2f,
        CapsuleHalfHeight = 0.75f,
        MaxSlopeRadians = 0.8f,
        StepHeight = 0.4f,
        WalkSpeed = 9f,
        RunSpeed = 18f,
    };

    [Fact]
    public void UnderSolidReportedFloorCannotAcceptABody()
    {
        using var world = FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(0.5f, 1f, 0.5f)), Pose.At(new Vector3(0f, 1f, 0f)));
        var context = new GroundMoveContext((_, _) => 0f, physics: world);

        Assert.False(Probe(context, Tuning, Vector3.Zero, Vector3.Zero));
    }

    [Fact]
    public void HoldRequiresRealGroundedSupportAtTheReportedHeight()
    {
        using var world = FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);

        Assert.True(Probe(context, Tuning, Vector3.Zero, Vector3.Zero));
        Assert.False(Probe(context, Tuning, new Vector3(0f, 2f, 0f), new Vector3(0f, 2f, 0f)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThinWallBetweenPassableEndpointsCannotBeCrossed(bool reverse)
    {
        using var world = FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(0.05f, 1f, 2f)), Pose.At(new Vector3(0.5f, 1f, 0f)));
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        Vector3 from = new(reverse ? 1f : 0f, 0f, 0f);
        Vector3 to = new(reverse ? 0f : 1f, 0f, 0f);

        Assert.True(Probe(context, Tuning, from, from));
        Assert.True(Probe(context, Tuning, to, to));
        Assert.False(Probe(context, Tuning, from, to));
    }

    [Fact]
    public void ProbeUsesUnitPaceAndDryMediumWithoutMutatingCallerState()
    {
        using var world = FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world,
            medium: (_, _, _) => throw new InvalidOperationException("A geometric probe must be dry."));
        MoveTuning zeroPace = Tuning with { WalkSpeed = 0f, RunSpeed = 0f, AirMomentum = true };

        Assert.True(Probe(context, zeroPace, Vector3.Zero, new Vector3(1f, 0f, 0f)));
        Assert.False(Probe(context, zeroPace, Vector3.Zero, new Vector3(3f, 0f, 0f)));
        Assert.Equal(0f, zeroPace.WalkSpeed);
        Assert.True(zeroPace.AirMomentum);
    }

    [Fact]
    public void EveryIntermediateFootprintMustRemainEligible()
    {
        using var world = FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);

        Assert.False(GroundTraversalProbe.TryEdge(context, Tuning, Vector3.Zero, Vector3.UnitX,
            feet => feet.X < 0.4f || feet.X > 0.6f, 1f / 30f, 64));
    }

    [Fact]
    public void SlowStepClimbIsDirectedAndZeroRetainsInstantMountSemantics()
    {
        using var world = StepWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        Vector3 lower = new(-0.5f, 0f, 0f);
        Vector3 upper = new(0.5f, 0.3f, 0f);
        MoveTuning slow = Tuning with { MaxStepClimbSpeed = 0.001f };

        Assert.False(Probe(context, slow, lower, upper));
        Assert.True(Probe(context, slow, upper, lower));
        Assert.True(Probe(context, slow with { MaxStepClimbSpeed = 0f }, lower, upper));
        Assert.True(Probe(context, Tuning, lower, upper));
    }

    [Fact]
    public void AHighRiseCannotBeAuthorizedByJumpTuning()
    {
        using var world = FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 0.5f, 2f)), Pose.At(new Vector3(2f, 0.5f, 0f)));
        var context = new GroundMoveContext((_, _) => 0f, physics: world);

        Assert.False(Probe(context, Tuning with { JumpSpeed = 100f }, new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 1f, 0f)));
    }

    [Fact]
    public void SlopeAndGravityRemainTheCallersRules()
    {
        var steep = new GroundMoveContext((x, _) => x, (_, _) => Vector3.Normalize(new Vector3(-1f, 1f, 0f)));
        MoveTuning tuning = Tuning with { MaxSlopeRadians = 0.2f, TractionHysteresisRadians = 0f };

        Assert.False(Probe(steep, tuning, Vector3.Zero, new Vector3(1f, 1f, 0f)));
        var noSupport = new GroundMoveContext((_, _) => -2f);
        Assert.False(Probe(noSupport, Tuning with { Gravity = 0f }, Vector3.Zero, Vector3.UnitX));
    }

    [Fact]
    public void FinalFractionIsResolvedByTheCoreWithoutEndpointSnap()
    {
        using var world = FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        Vector3 endpoint = new(0.5005f, 0f, 0f);
        Vector3 lastSample = default;

        Assert.True(GroundTraversalProbe.TryEdge(context, Tuning, Vector3.Zero, endpoint, feet =>
        {
            lastSample = feet;
            return true;
        }, 1f / 30f, 64));
        Assert.InRange(Vector3.Distance(endpoint, lastSample), 0f, 0.001f);
    }

    [Theory]
    [InlineData(0f, 64)]
    [InlineData(float.NaN, 64)]
    [InlineData(1f / 30f, 0)]
    public void InvalidProbeBudgetsAreRefused(float dt, int steps)
    {
        var context = new GroundMoveContext((_, _) => 0f);

        Assert.Throws<ArgumentOutOfRangeException>(() => GroundTraversalProbe.TryEdge(context, Tuning,
            Vector3.Zero, Vector3.UnitX, _ => true, dt, steps));
    }

    internal static bool Probe(GroundMoveContext context, MoveTuning tuning, Vector3 from, Vector3 to)
        => GroundTraversalProbe.TryEdge(context, tuning, from, to, _ => true, 1f / 30f, 64);

    internal static BepuPhysicsWorld FlatWorld()
    {
        var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(8f, 0.1f, 8f)), Pose.At(new Vector3(0f, -0.1f, 0f)));
        return world;
    }

    internal static BepuPhysicsWorld StepWorld()
    {
        BepuPhysicsWorld world = FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 0.15f, 2f)), Pose.At(new Vector3(2f, 0.15f, 0f)));
        return world;
    }
}
