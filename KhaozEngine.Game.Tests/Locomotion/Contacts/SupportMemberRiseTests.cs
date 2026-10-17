// Every expectation below is derived from the installed geometry. The footprint radius is 0.2 and the climb
// budget is MaxStepClimbSpeed * dt, 3.5 / 30 m per tick.
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>A rise across one raw support member is explained by that member's own height field up to a walkable
/// grade over the horizontal move. A steeper rise over one member, or a rise onto another member, is a step part and
/// a discrete step.</summary>
public sealed class SupportMemberRiseTests(ITestOutputHelper output)
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    const float FootRadius = 0.2f;
    static readonly float CosMaxSlope = MathF.Cos(Tuning.MaxSlopeRadians);
    static readonly double WalkableGrade = Math.Sqrt(1 - (double)CosMaxSlope * CosMaxSlope) / CosMaxSlope;

    // The face rises +Z at 0.7 and falls toward +X at 0.7, about 44.7 degrees. It is the top of a box whose -X
    // edge runs along x 0 through (0, 1, 0), a convex join to the box's steep side face beyond it.
    static FootSupportScene SlopedEdge()
    {
        Vector3 up = Vector3.Normalize(new(0.7f, 1, -0.7f)), along = Vector3.Normalize(new(0, 0.7f, 1));
        Vector3 across = Vector3.Cross(up, along);
        Quaternion orientation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            across.X, across.Y, across.Z, 0, up.X, up.Y, up.Z, 0, along.X, along.Y, along.Z, 0, 0, 0, 0, 1));
        var half = new Vector3(0.5f, 0.3f, 1.5f * new Vector2(0.7f, 1).Length());
        Vector3 edge = new(0, 1 + 0.7f * 1.5f, 1.5f);
        return new FootSupportScene(SceneVariant.Box).Add("edge", new BoxShape(half),
            new Pose(edge - half.Y * up + half.X * across, orientation));
    }

    static SupportSample Find(ContactQueryContext queries, Vector2 axis, float feetY) =>
        queries.FindSupport(new(axis, feetY, FootRadius, Tuning.StepHeight, Tuning.StepHeight, CosMaxSlope));

    // The axis walks +Z 0.18 past the edge, on the cliff side. The disc reaches 0.02 onto the face, so its highest
    // point is the edge at the far end of the disc's chord and rises 0.07 per substep, 0.14 per tick.
    static SupportSample SlopedEdgeStart(FootSupportScene scene, ContactQueryContext queries)
    {
        SupportSample start = Find(queries, new(-0.18f, 1), 1.8f);
        Assert.Equal(SupportStatus.Walkable, start.Status);
        Assert.Equal(scene["edge"], start.Static);
        double chord = Math.Sqrt(0.2 * 0.2 - 0.18 * 0.18);
        Assert.InRange(Math.Abs(start.Height - (1 + 0.7 * (1 + chord))), 0, start.HeightError + 1e-5);
        return start;
    }

    [Fact]
    public void SlopedOverhungEdgeRiseIsNotAStepPart()
    {
        using FootSupportScene scene = SlopedEdge();
        ContactQueryContext queries = DryQueries.For(null, null, scene.World, scene.Lease, Tuning);
        SupportSample start = SlopedEdgeStart(scene, queries);
        GroundSeatResult seat = GroundSeat.Resolve(queries, start, new(-0.18f, 1), start.Height, new(-0.18f, 1.1f),
            Vector2.UnitY, FootRadius, Tuning, CosMaxSlope);
        output.WriteLine($"support {start.Height:R} -> {seat.FeetY:R}, step part {seat.StepPart:R}, " +
            $"discrete step {seat.DiscreteStep:R}");
        Assert.Equal(SeatOutcome.Seated, seat.Outcome);
        Assert.Equal(start.Static, seat.Support.Static);
        Assert.Equal(start.FeatureId, seat.Support.FeatureId);
        Assert.InRange(Math.Abs(seat.FeetY - start.Height - 0.07), 0,
            start.HeightError + seat.Support.HeightError + 1e-5);
        Assert.Equal(0f, seat.StepPart);
        Assert.Equal(0f, seat.DiscreteStep);
    }

    // The edge rises above the 3.5 / 30 budget each tick. Following it costs nothing, so the feet never lag.
    [Fact]
    public void WalkingAlongASlopedOverhungEdgeIsNotThrottled()
    {
        using FootSupportScene scene = SlopedEdge();
        ContactQueryContext queries = DryQueries.For(null, null, scene.World, scene.Lease, Tuning);
        SupportSample start = SlopedEdgeStart(scene, queries);
        const float dt = 1f / 30f;
        var feet = new Vector3(-0.18f, start.Height, 1);
        for (int tick = 0; tick < 5; tick++)
        {
            GroundStepResult result = GroundCore.Step(feet, new Vector2(0, Tuning.WalkSpeed * dt), dt, Tuning,
                new GroundCoreSettings(), queries);
            output.WriteLine($"tick {tick}: {feet} -> {result.Feet}, support {result.Support.Height:R} " +
                $"feature {result.Support.FeatureId}, lag {result.Support.Height - result.Feet.Y:R}, " +
                $"step rise {result.StepRise:R}");
            Assert.Equal(GroundFooting.Walkable, result.Footing);
            Assert.Equal(start.Static, result.Support.Static);
            Assert.Equal(start.FeatureId, result.Support.FeatureId);
            Assert.Equal(-0.18f, result.Feet.X);
            // No lag: the feet stay seated on the edge they follow.
            Assert.InRange(Math.Abs((double)result.Support.Height - result.Feet.Y), 0, result.Support.HeightError);
            Assert.InRange(Math.Abs((double)result.Feet.Y - feet.Y - 0.7 * (result.Feet.Z - feet.Z)), 0,
                result.Support.HeightError + 1e-5);
            Assert.Equal(0f, result.StepRise);
            feet = result.Feet;
        }
    }

    // A 20 degree ramp's top edge at x 2 ends in a drop. Walking over it and back stays on the ramp's top face,
    // whose height field is the face up to the edge and level past it, so the feet follow it and pay only budget.
    [Theory]
    [InlineData(1.85f, 1.95f)]
    [InlineData(1.95f, 2.05f)]
    [InlineData(2.05f, 2.15f)]
    [InlineData(2.15f, 2.05f)]
    [InlineData(2.05f, 1.95f)]
    [InlineData(1.95f, 1.85f)]
    public void RampTopEdgeEndingInADropChargesNoStepPart(float startX, float x)
    {
        using FootSupportScene scene = Ramp(SceneVariant.Box, 2 * MathF.Tan(Radians(20)));
        ContactQueryContext queries = DryQueries.For(null, null, scene.World, scene.Lease, Tuning);
        const double lag = 0.05, budget = 0.02;
        SupportSample start = Find(queries, new(startX, 0), 0.7f);
        Assert.Equal(SupportStatus.Walkable, start.Status);
        Assert.Equal(scene["ramp"], start.Static);
        float feetY = (float)(start.Height - lag);
        Vector2 startAxis = new(startX, 0), axis = new(x, 0);
        GroundSeatResult seat = GroundSeat.Resolve(queries, start, startAxis, feetY, axis,
            Vector2.Normalize(axis - startAxis), FootRadius, Tuning, CosMaxSlope);
        Assert.Equal(SeatOutcome.Seated, seat.Outcome);
        Assert.Equal(start.Static, seat.Support.Static);
        Assert.Equal(start.FeatureId, seat.Support.FeatureId);
        double edge = 2 * Math.Tan(20 * Math.PI / 180), grade = Math.Tan(20 * Math.PI / 180);
        Assert.InRange(Math.Abs(seat.FeetY - (edge - grade * Math.Max(0, 2 - x))), 0, seat.Support.HeightError + 1e-6);
        Assert.Equal(0f, seat.StepPart);
        Assert.Equal(0f, seat.DiscreteStep);

        GroundPlacement placed = GroundPlacement.Try(queries, start, feetY, startAxis, axis - startAxis, seat,
            FootRadius, Tuning, CosMaxSlope, budget);
        output.WriteLine($"{startX} -> {x}: support {start.Height:R} -> {seat.FeetY:R}, feet {feetY:R} -> " +
            $"{placed.Feet.Y:R}, left {placed.Budget:R}");
        Assert.True(placed.Valid);
        Assert.Equal(0, placed.Budget);
        // The feet follow the face's own rise or drop and climb exactly the budget, rounded down.
        double excess = (double)placed.Feet.Y - feetY - ((double)seat.FeetY - start.Height) - budget;
        Assert.InRange(excess, -(MathF.BitIncrement(placed.Feet.Y) - (double)placed.Feet.Y), 0);
    }

    // One mesh static holds a floor and a tread 0.18 higher, so the two supports share a static but not a member.
    [Fact]
    public void AStepOntoAnotherMemberOfTheSameStaticChargesItsRise()
    {
        const float tread = 0.18f;
        using FootSupportScene scene = new FootSupportScene(SceneVariant.Mesh).Mesh("steps", [
            .. FootSupportScene.Quad(new(-2, 0, -1), new(0, 0, -1), new(-2, 0, 1), new(0, 0, 1)),
            .. FootSupportScene.Quad(new(0, tread, -1), new(2, tread, -1), new(0, tread, 1), new(2, tread, 1)),
        ]);
        ContactQueryContext queries = DryQueries.For(null, null, scene.World, scene.Lease, Tuning);
        // The start disc reaches x -0.1, short of the tread. The new disc reaches x 0.1, onto it.
        SupportSample start = Find(queries, new(-0.3f, 0), 0);
        GroundSeatResult seat = GroundSeat.Resolve(queries, start, new(-0.3f, 0), 0, new(-0.1f, 0), Vector2.UnitX,
            FootRadius, Tuning, CosMaxSlope);
        Assert.Equal(SeatOutcome.Seated, seat.Outcome);
        Assert.Equal(scene["steps"], start.Static);
        Assert.Equal(start.Static, seat.Support.Static);
        Assert.NotEqual(start.FeatureId, seat.Support.FeatureId);
        double errors = start.HeightError + seat.Support.HeightError;
        Assert.True(seat.StepPart >= seat.FeetY - start.Height, $"StepPart {seat.StepPart:R} below the rise");
        Assert.InRange(Math.Abs(seat.StepPart - tread), 0, errors);
        Assert.InRange(Math.Abs(seat.DiscreteStep - tread), 0, errors);
    }

    // A curved prop of radius 0.15 rests on the floor, its top at 0.3: a sphere, because the footprint refuses the
    // flank of a lying cylinder or capsule. In the plane of the walk it has the log's profile. From axis x -0.3 the
    // disc's highest point is x -0.1 on the prop, a walkable 41.8 degrees, whose tangent plane gives 0.083 at the
    // axis. From x -0.2 the disc reaches the top at 0.3. That 0.217 rise over 0.1 is steeper than walkable, so
    // 0.117 of it is a step.
    static FootSupportScene CurvedProp() => new FootSupportScene(SceneVariant.Box).Flat("floor", -2, 2, -2, 2, 0)
        .Add("prop", new SphereShape(0.15f), Pose.At(new Vector3(0, 0.15f, 0)));

    static SupportSample CurvedPropStart(FootSupportScene scene, ContactQueryContext queries)
    {
        SupportSample start = Find(queries, new(-0.3f, 0), 0.1f);
        Assert.Equal(SupportStatus.Walkable, start.Status);
        Assert.Equal(scene["prop"], start.Static);
        double side = Math.Sqrt(0.15 * 0.15 - 0.1 * 0.1);
        Assert.InRange(Math.Abs(start.Height - (0.15 + side - 0.2 * 0.1 / side)), 0, start.HeightError + 1e-5);
        return start;
    }

    [Fact]
    public void ClimbingOntoACurvedPropChargesTheRiseBeyondWalkableGrade()
    {
        using FootSupportScene scene = CurvedProp();
        ContactQueryContext queries = DryQueries.For(null, null, scene.World, scene.Lease, Tuning);
        SupportSample start = CurvedPropStart(scene, queries);
        GroundSeatResult seat = GroundSeat.Resolve(queries, start, new(-0.3f, 0), start.Height, new(-0.2f, 0),
            Vector2.UnitX, FootRadius, Tuning, CosMaxSlope);
        output.WriteLine($"support {start.Height:R} -> {seat.FeetY:R}, step part {seat.StepPart:R}, " +
            $"discrete step {seat.DiscreteStep:R}");
        Assert.Equal(SeatOutcome.Seated, seat.Outcome);
        Assert.Equal(start.Static, seat.Support.Static);
        Assert.Equal(start.FeatureId, seat.Support.FeatureId);
        Assert.InRange(Math.Abs(seat.FeetY - 0.3), 0, seat.Support.HeightError + 1e-5);
        double excess = (double)seat.FeetY - start.Height - WalkableGrade * ((double)-0.2f - -0.3f);
        Assert.True(excess > 0.1, $"Excess {excess:R}");
        // Both errors, and the float the part is rounded to.
        double errors = start.HeightError + seat.Support.HeightError + (MathF.BitIncrement(0.125f) - 0.125f);
        Assert.InRange(Math.Abs(seat.StepPart - excess), 0, errors);
        Assert.InRange(Math.Abs(seat.DiscreteStep - excess), 0, errors);
    }

    // With a 0.02 budget the feet rise at most a walkable grade over the move plus that budget, and the step is
    // reported, so the climb lags the prop's top.
    [Fact]
    public void ACurvedPropClimbIsPacedAndReportedAsAStep()
    {
        using FootSupportScene scene = CurvedProp();
        ContactQueryContext queries = DryQueries.For(null, null, scene.World, scene.Lease, Tuning);
        SupportSample start = CurvedPropStart(scene, queries);
        const double budget = 0.02;
        var feet = new Vector3(-0.3f, start.Height, 0);
        GroundStepResult result = GroundCore.Step(feet, new Vector2(0.1f, 0), 1f / 30f, Tuning,
            new GroundCoreSettings(), queries, climbBudget: budget);
        output.WriteLine($"{feet} -> {result.Feet}, support {result.Support.Height:R}, step rise {result.StepRise:R}");
        Assert.Equal(GroundFooting.Walkable, result.Footing);
        Assert.Equal(scene["prop"], result.Support.Static);
        Assert.InRange(Math.Abs(result.Support.Height - 0.3), 0, result.Support.HeightError + 1e-5);
        Assert.True(result.Feet.Y - (double)feet.Y <= WalkableGrade * (result.Feet.X - feet.X) + budget +
            start.HeightError + result.Support.HeightError, $"Feet rose to {result.Feet.Y:R}");
        Assert.True(result.StepRise > 0, $"Step rise {result.StepRise:R}");
    }

    // Walking straight at the sloped edge from the cliff side, the disc's chord along the edge grows from 0.014 to
    // 0.138 over a 0.0545 move, while the face's own plane at the axis stays above the chord's end. The edge's 0.7
    // grade along that chord rises 0.087, beyond the walkable 0.0545.
    [Fact]
    public void WalkingStraightAtASlopedOverhungEdgeChargesTheChordRise()
    {
        using FootSupportScene scene = SlopedEdge();
        ContactQueryContext queries = DryQueries.For(null, null, scene.World, scene.Lease, Tuning);
        static double Edge(double x) => 1 + 0.7 * (1 + Math.Min(-x, Math.Sqrt(0.2 * 0.2 - x * x)));
        const float x0 = -0.1995f, x1 = -0.145f;
        SupportSample start = Find(queries, new(x0, 1), 1.7f);
        Assert.Equal(SupportStatus.Walkable, start.Status);
        Assert.Equal(scene["edge"], start.Static);
        Assert.InRange(Math.Abs(start.Height - Edge(x0)), 0, start.HeightError + 1e-5);
        GroundSeatResult seat = GroundSeat.Resolve(queries, start, new(x0, 1), start.Height, new(x1, 1),
            Vector2.UnitX, FootRadius, Tuning, CosMaxSlope);
        output.WriteLine($"support {start.Height:R} -> {seat.FeetY:R}, step part {seat.StepPart:R}, " +
            $"discrete step {seat.DiscreteStep:R}");
        Assert.Equal(SeatOutcome.Seated, seat.Outcome);
        Assert.Equal(start.FeatureId, seat.Support.FeatureId);
        double errors = start.HeightError + seat.Support.HeightError;
        Assert.InRange(Math.Abs(seat.FeetY - Edge(x1)), 0, seat.Support.HeightError + 1e-5);
        double excess = Edge(x1) - Edge(x0) - WalkableGrade * ((double)x1 - x0);
        Assert.True(excess > 0.02, $"Excess {excess:R}");
        Assert.InRange(Math.Abs(seat.StepPart - excess), 0, errors + 1e-5);
        Assert.InRange(Math.Abs(seat.DiscreteStep - excess), 0, errors + 1e-5);
    }

    // The round 0 tilted box's walkable face falls toward +X at 30 degrees from its top edge at (0.25, 0.3366). From
    // axis 0.1 the disc overhangs that edge, so the start field is level. A separate slab 0.05 higher enters the
    // disc at axis 0.2. The face's own slope would add a false 0.0577 to that rise.
    [Fact]
    public void AStepFromAnOverhungTiltedEdgeOntoAnotherStaticChargesOnlyItsRise()
    {
        using var scene = new SupportFootprintScene();
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3);
        StaticHandle box = scene.Install(new BoxShape(new(0.05f / MathF.Cos(MathF.PI / 3), 0.1f, 0.2f)),
            new(new Vector3(0.2f, 0.25f, 0) + Vector3.Transform(new Vector3(0, -0.1f, 0), tilt), tilt));
        double edge = 0.25 + 0.05 * Math.Sqrt(3), slabTop = edge + 0.05;
        StaticHandle slab = scene.Install(new BoxShape(new(0.15f, 0.01f, 0.2f)),
            Pose.At(new(0.47f, (float)slabTop - 0.01f, 0)));
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        ContactQueryContext queries = DryQueries.For(null, null, scene.World, lease, Tuning);
        SupportSample start = Find(queries, new(0.1f, 0), 0.3f);
        Assert.Equal(box, start.Static);
        Assert.True(start.Normal.X > 0.4f, $"The start keeps the face normal {start.Normal}");
        Assert.InRange(Math.Abs(start.Height - edge), 0, start.HeightError + 1e-6);
        GroundSeatResult seat = GroundSeat.Resolve(queries, start, new(0.1f, 0), start.Height, new(0.2f, 0),
            Vector2.UnitX, FootRadius, Tuning, CosMaxSlope);
        output.WriteLine($"support {start.Height:R} -> {seat.FeetY:R}, step part {seat.StepPart:R}, " +
            $"discrete step {seat.DiscreteStep:R}");
        Assert.Equal(SeatOutcome.Seated, seat.Outcome);
        Assert.Equal(slab, seat.Support.Static);
        double errors = start.HeightError + seat.Support.HeightError + 1e-6;
        Assert.InRange(Math.Abs(seat.FeetY - slabTop), 0, errors);
        Assert.InRange(Math.Abs(seat.StepPart - 0.05), 0, errors);
        Assert.InRange(Math.Abs(seat.DiscreteStep - 0.05), 0, errors);
    }
}
