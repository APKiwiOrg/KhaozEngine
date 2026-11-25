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
    private static readonly PhysicsNavBakeOptions Options = new(-1.5f, -1.5f, 1.5f, 1.5f,
        0.25f, 5f, 8f, 0.8f, 256, 512);

    public enum Heading { Uphill, Downhill, Sideways }

    [Theory]
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
    public void Issue1265_LegacyBakeAcceptsEdgesOnASmoothSlope(float grade, float radius, Heading heading)
    {
        using BepuPhysicsWorld world = SlopeWorld(grade);
        MoveTuning tuning = GroundTraversalProbeTests.Tuning with { CapsuleRadius = radius };
        using GroundMoveContext context = Context(world);
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

        Assert.True(nav.AllowsSegment(from, to), "the baked graph refused the edge");
    }

    [Theory]
    [InlineData(0.08f)]
    [InlineData(0.25f)]
    public void Issue1265_LegacyRouteFollowerClimbsAndCrossesTheSlopeToItsGoal(float grade)
    {
        const float dt = 1f / 30f, range = 0.25f;
        using BepuPhysicsWorld world = SlopeWorld(grade);
        MoveTuning tuning = GroundTraversalProbeTests.Tuning with { WalkSpeed = 2f, RunSpeed = 5f };
        using GroundMoveContext context = Context(world);
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

    [Fact]
    public void Issue1265_LegacyProbeRefusesAParallelFloorWithinTheSameMesh()
    {
        const float grade = 0.25f, separation = 0.0125f;
        using BepuPhysicsWorld world = SlopeWorld(grade, separation);
        using GroundMoveContext context = Context(world);
        MoveTuning tuning = GroundTraversalProbeTests.Tuning with { CapsuleRadius = 0.3f };
        Vector3 upper = Surface(grade, Row, -Row);
        Vector3 lower = upper - Vector3.UnitY * separation;

        Assert.True(GroundTraversalProbeTests.Probe(context, tuning, upper, upper));
        Assert.False(GroundTraversalProbeTests.Probe(context, tuning, upper, lower));
    }

    [Fact]
    public void Issue1265_LegacyProbeRefusesAClampedHorizontalMissOnASlope()
    {
        const float grade = 0.25f;
        using BepuPhysicsWorld world = SlopeWorld(grade);
        Vector3 from = Surface(grade, Row, Row), to = Surface(grade, Row + 0.25f, Row);
        Vector3 limit = to - new Vector3(0.002f, 0f, 0f);
        using var context = new GroundMoveContext((_, _) => -10f, physics: world,
            clampXz: (x, z) => new(float.Min(x, limit.X), z));

        Assert.True(GroundTraversalProbeTests.Probe(context, GroundTraversalProbeTests.Tuning, from, limit));
        Assert.False(GroundTraversalProbeTests.Probe(context, GroundTraversalProbeTests.Tuning, from, to));
    }

    // A slope rising toward -Z, y = -grade z, as one two-triangle mesh. The analytic ground lies far below, so the mesh
    // is the only support, as for a physics-only slope.
    private static BepuPhysicsWorld SlopeWorld(float grade, float separation = 0)
    {
        const float half = 4f;
        Vector3[] vertices =
        [
            new(-half, -grade * half, half), new(half, -grade * half, half),
            new(half, grade * half, -half), new(-half, grade * half, -half),
        ];
        // Reverse the usual geometry winding because Bepu's upward raycast face is clockwise.
        int[] indices = [0, 2, 1, 0, 3, 2];
        if (separation > 0)
        {
            Vector3 lower = Vector3.UnitY * separation;
            vertices = [.. vertices, vertices[0] - lower, vertices[1] - lower, vertices[2] - lower, vertices[3] - lower];
            indices = [.. indices, 4, 6, 5, 4, 7, 6];
        }
        var world = new BepuPhysicsWorld();
        world.AddStatic(new TriangleMeshShape(vertices, indices), Pose.At(Vector3.Zero));
        return world;
    }

    private static GroundMoveContext Context(BepuPhysicsWorld world) => new((_, _) => -10f, physics: world);

    private static Vector3 Surface(float grade, float x, float z) => new(x, -grade * z, z);
}
