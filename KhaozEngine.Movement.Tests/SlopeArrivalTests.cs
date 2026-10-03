// Reproduction for https://github.com/APKiwiOrg/KhaozEngine/issues/1265, deferred from the NPC movement round.
// Every fact is skipped until the follow-up lands. The attempt that turned all 15 green, and why it was not kept:
//
// 1. Core, CharacterMovement.Collision.cs SlideSubstep. The walkable push-out rule became `if (restHold || grounded)`
//    instead of `if (restHold)`, so a walkable push-out (push.Y >= cos(MaxSlopeRadians) x |push|) is vertical only
//    while moving as well as while idle. That removed the down-slope creep under command that left the probe 1.25 mm
//    (grade 0.08) to 3.77 mm (grade 0.25) short of uphill and sideways targets. It broke 7 existing Locomotion
//    tests: StairRunTangentPacingTests (3 cases, run-climb penetration 0.24 to 0.83 m),
//    LipContactShortRiserTests.ShallowTreadStaircaseBase_MountsViaRayFanFallback (3 walk cases) and
//    StairDescentGroundedHoldTests.StepDownBeyondStepHeight_StillReleasesAndFalls (riser 0.45). Stair tread edges
//    also give walkable push-outs whose horizontal part a run-climb needs.
// 2. Probe, GroundTraversalProbe.cs (ruling M18). GroundArrived and the start-node settle check both accepted today's
//    1 mm ball, or a horizontal miss within ArrivalTolerance with feet 0 to r x (1/cos(MaxSlopeRadians) - 1) +
//    ArrivalTolerance above the target. A body below the target beyond the ball was refused.
//
// The mesh winding [0, 2, 1, 0, 3, 2] faces up for raycasts. The order [0, 1, 2, 0, 2, 3] used by
// WalkableSlopeNoStepUpTests faces down, so a capture stores no surface for it.

using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

/// <summary>KhaozEngine #1265: on a smooth physics slope a resting capsule's bottom sphere touches the surface off
/// the column's down ray, so its feet rest r x (1/cos t - 1) above the captured column height. The bake must still
/// accept edges up, down and across the slope.</summary>
public class SlopeArrivalTests
{
    private const float Row = 0.125f;
    private const string Deferred = "#1265 deferred: the M19 core attempt broke 7 stair tests, see the file header";
    private static readonly PhysicsNavBakeOptions Options = new(-1.5f, -1.5f, 1.5f, 1.5f,
        0.25f, 5f, 8f, 0.8f, 256, 512);

    public enum Heading { Uphill, Downhill, Sideways }

    [Theory(Skip = Deferred)]
    [InlineData(0.08f, 0.2f, Heading.Uphill)]
    [InlineData(0.08f, 0.2f, Heading.Downhill)]
    [InlineData(0.08f, 0.2f, Heading.Sideways)]
    [InlineData(0.08f, 0.3f, Heading.Uphill)]
    [InlineData(0.08f, 0.3f, Heading.Downhill)]
    [InlineData(0.08f, 0.3f, Heading.Sideways)]
    [InlineData(0.25f, 0.2f, Heading.Uphill)]
    [InlineData(0.25f, 0.2f, Heading.Downhill)]
    [InlineData(0.25f, 0.2f, Heading.Sideways)]
    [InlineData(0.25f, 0.3f, Heading.Uphill)]
    [InlineData(0.25f, 0.3f, Heading.Downhill)]
    [InlineData(0.25f, 0.3f, Heading.Sideways)]
    public void BakeAcceptsEdgesOnASmoothSlope(float grade, float radius, Heading heading)
    {
        using BepuPhysicsWorld world = SlopeWorld(grade);
        MoveTuning tuning = GroundTraversalProbeTests.Tuning with { CapsuleRadius = radius };
        GroundMoveContext context = Context(world);
        using PhysicsNavBake bake = PhysicsNavBake.Capture(context, Options, _ => 0u);
        GroundNavigation nav = bake.BuildProfile(tuning, default);
        // Uphill is -Z. Sideways runs along +X, level across the slope.
        (float fromX, float fromZ, float toX, float toZ) = heading switch
        {
            Heading.Uphill => (Row, Row, Row, -Row),
            Heading.Downhill => (Row, -Row, Row, Row),
            _ => (Row, Row, Row + 0.25f, Row),
        };
        Vector3 from = Surface(grade, fromX, fromZ), to = Surface(grade, toX, toZ);

        Assert.True(GroundTraversalProbeTests.Probe(context, tuning, from, to), "the ground proof refused the edge");
        Assert.True(nav.AllowsSegment(from, to), "the baked graph refused the edge");
    }

    [Theory(Skip = Deferred)]
    [InlineData(0.08f)]
    [InlineData(0.25f)]
    public void RouteFollowerClimbsAndCrossesTheSlopeToItsGoal(float grade)
    {
        const float dt = 1f / 30f, range = 0.25f;
        using BepuPhysicsWorld world = SlopeWorld(grade);
        MoveTuning tuning = GroundTraversalProbeTests.Tuning with { WalkSpeed = 2f, RunSpeed = 5f };
        GroundMoveContext context = Context(world);
        using PhysicsNavBake bake = PhysicsNavBake.Capture(context, Options, _ => 0u);
        var move = new MoveToRange(bake.BuildProfile(tuning, default));
        ReachTarget target = ReachTarget.Point(Surface(grade, 1.125f, -1.125f));
        var body = new MoveState
        {
            Position = Surface(grade, -1.125f, 1.125f) + Vector3.UnitY * tuning.CapsuleHalfHeight,
            Grounded = true,
            SpeedScale = 1f,
        };
        body = NpcGroundMovement.Hold(body, dt, tuning, context);

        RangeMoveStatus status = RangeMoveStatus.Following;
        for (int tick = 0; tick < 150 && status != RangeMoveStatus.InRange; tick++)
        {
            RangeSteering steering = move.Tick(body, tuning, target, range, false, dt, context);
            status = steering.Status;
            if (status == RangeMoveStatus.InRange) break;
            Assert.True(status == RangeMoveStatus.Following, $"{status} at tick {tick}, centre {body.Position}");
            body = NpcGroundMovement.Step(body, steering, false, dt, tuning, context);
            Assert.True(body.Grounded, $"airborne at tick {tick}, centre {body.Position}");
        }

        Assert.True(status == RangeMoveStatus.InRange, $"stalled at centre {body.Position}");
        Assert.True(ReachGeometry.Within(new MovementBody(body.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight),
            target, range));
    }

    [Fact(Skip = Deferred)]
    public void ArrivalRefusesAPerchAboveTheBoundAndAnyDropBelowTheTolerance()
    {
        MoveTuning tuning = GroundTraversalProbeTests.Tuning with { CapsuleRadius = 0.3f };
        float tolerance = GroundTraversalProbe.ArrivalTolerance;
        float bound = tuning.CapsuleRadius * (1f / MathF.Cos(tuning.MaxSlopeRadians) - 1f) + tolerance;
        var grounded = new MoveState { Grounded = true };
        Vector3 target = new(1f, 2f, 3f);

        Assert.True(GroundTraversalProbe.GroundArrived(grounded, target + Vector3.UnitY * (bound - 0.0005f), target, tuning));
        Assert.False(GroundTraversalProbe.GroundArrived(grounded, target + Vector3.UnitY * (bound + 0.0005f), target, tuning));
        Assert.False(GroundTraversalProbe.GroundArrived(grounded, target - Vector3.UnitY * (tolerance + 0.0005f), target, tuning));
        Assert.False(GroundTraversalProbe.GroundArrived(grounded, target - Vector3.UnitY * 0.05f, target, tuning));
        Assert.True(GroundTraversalProbe.GroundArrived(grounded, target + new Vector3(0.0009f, 0.005f, 0f), target, tuning));
        Assert.False(GroundTraversalProbe.GroundArrived(grounded, target + new Vector3(0.0011f, 0.005f, 0f), target, tuning));
        Assert.False(GroundTraversalProbe.GroundArrived(default, target, target, tuning));
    }

    // A slope rising toward -Z, y = -grade z, as one two-triangle mesh. The analytic ground lies far below, so the mesh
    // is the only support, as for a physics-only slope.
    private static BepuPhysicsWorld SlopeWorld(float grade)
    {
        const float half = 4f;
        Vector3[] vertices =
        [
            new(-half, -grade * half, half), new(half, -grade * half, half),
            new(half, grade * half, -half), new(-half, grade * half, -half),
        ];
        var world = new BepuPhysicsWorld();
        world.AddStatic(new TriangleMeshShape(vertices, [0, 2, 1, 0, 3, 2]), Pose.At(Vector3.Zero));
        return world;
    }

    private static GroundMoveContext Context(BepuPhysicsWorld world) => new((_, _) => -10f, physics: world);

    private static Vector3 Surface(float grade, float x, float z) => new(x, -grade * z, z);
}
