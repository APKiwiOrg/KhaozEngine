using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed partial class ClothContactTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void A_rotating_capsule_requires_a_bounded_clearance_certificate(int refinementLimit)
    {
        const float angle = MathF.PI / 6, radius = .001f, reach = .025f, plane = .0255f;
        Vector3[] rest = [new(plane, -.036f, -.022f), new(plane, .036f, -.022f), new(plane, 0, .022f)];
        var simulation = new ClothSimulation(Triangle(rest), new ClothWorkLimits(4, 2, 64, 256)
        {
            MaxSweepRefinements = refinementLimit,
        });
        simulation.Reset(rest);
        var tip = reach * Vector3.UnitX;
        var capsule = new ColliderCapsule(tip, tip, radius);
        ClothSupport first = ClothSupport.Capsule(capsule, Pose(0), Vector3.Zero);
        ClothSupport last = ClothSupport.Capsule(capsule, Pose(1), Vector3.Zero);

        Assert.True(WorldTip(0).X + radius + Material.HalfThickness < plane);
        Assert.True(WorldTip(1).X + radius + Material.HalfThickness < plane);
        Assert.True(WorldTip(.5f).X + radius > plane);
        ClothStepResult step = simulation.Advance(new ClothSubstep([], [first], [last], Seconds, Vector3.Zero));

        if (refinementLimit == 0)
        {
            Assert.Equal(ClothStepStatus.SweepWork, step.Status);
            Assert.Equal(0, step.SweepRefinements);
            Assert.Equal(rest, simulation.Positions.ToArray());
            return;
        }

        Assert.Equal(ClothStepStatus.Accepted, step.Status);
        for (int sample = 0; sample <= 32; sample++)
        {
            float time = sample / 32f;
            Vector3 a = Vector3.Lerp(rest[0], simulation.Positions[0], time);
            Vector3 b = Vector3.Lerp(rest[1], simulation.Positions[1], time);
            Vector3 c = Vector3.Lerp(rest[2], simulation.Positions[2], time);
            Vector3 tipAtTime = WorldTip(time);
            float clearance = DistanceToTriangle(tipAtTime, a, b, c) - radius - Material.HalfThickness;
            Assert.True(clearance >= 0, $"Sample {sample}, clearance {clearance:R} m.");
            Assert.True(Vector3.Dot(tipAtTime - a, Vector3.Cross(b - a, c - a)) < 0);
        }

        static Quaternion Pose(float t) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, float.Lerp(-angle, angle, t));
        Vector3 WorldTip(float t) => Vector3.Transform(tip, Pose(t));
    }
    [Fact]
    public void A_finite_angular_sweep_can_clear_without_moving_distant_pins()
    {
        const float angle = MathF.PI / 6, radius = .001f, reach = .025f, plane = .0255f;
        Vector3[] rest = [new(plane, -.036f, -.022f), new(plane, .036f, -.022f), new(plane, 0, .022f)];
        var simulation = new ClothSimulation(Triangle(rest, [0, 1]), ClothWorkLimits.LayeredContact);
        simulation.Reset(rest);
        var corner = reach * Vector3.UnitX;
        var sphere = new ColliderCapsule(corner, corner, radius);
        ClothSupport first = ClothSupport.Capsule(sphere, Rotation(0), Vector3.Zero);
        ClothSupport last = ClothSupport.Capsule(sphere, Rotation(1), Vector3.Zero);
        Assert.True(DistanceToTriangle(corner, rest[0], rest[1], rest[2]) < radius);

        ClothStepResult step = simulation.Advance(new ClothSubstep(rest.AsSpan(0, 2), [first], [last], Seconds, Vector3.Zero));

        Assert.Equal(ClothStepStatus.Accepted, step.Status);
        Assert.Equal(rest[0], simulation.Positions[0]);
        Assert.Equal(rest[1], simulation.Positions[1]);
        for (int sample = 0; sample <= 64; sample++)
        {
            float time = sample / 64f;
            Vector3 center = Vector3.Transform(corner, Rotation(time));
            Vector3 apex = Vector3.Lerp(rest[2], simulation.Positions[2], time);
            Assert.True(DistanceToTriangle(center, rest[0], rest[1], apex) >= radius + Material.HalfThickness);
        }

        static Quaternion Rotation(float time) =>
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, float.Lerp(-angle, angle, time));
    }

    [Fact]
    public void A_moving_lower_segment_with_an_independent_radius_reaches_a_thin_edge()
    {
        Vector3[] rest = [new(0, -.02f, 0), new(0, .02f, 0), new(0, 0, .0008f)];
        var simulation = new ClothSimulation(Triangle(rest), ClothWorkLimits.LayeredContact);
        simulation.Reset(rest);
        var openSegment = new ColliderCapsule(new Vector3(-.006f, 0, -.008f), new Vector3(-.006f, 0, -.0002f), .0007f);
        var closeSegment = new ColliderCapsule(new Vector3(-.0003f, 0, -.008f), new Vector3(-.0003f, 0, -.0002f), .0012f);
        ClothSupport first = ClothSupport.LowerSegment(openSegment, Identity);
        ClothSupport last = ClothSupport.LowerSegment(closeSegment, Identity);
        Assert.True(DistanceToTriangle(openSegment.End, rest[0], rest[1], rest[2]) > openSegment.Radius + Material.HalfThickness);
        Assert.True(DistanceToTriangle(closeSegment.End, rest[0], rest[1], rest[2]) < closeSegment.Radius);

        ClothStepResult step = simulation.Advance(new ClothSubstep([], [first], [last], Seconds, Vector3.Zero));

        Assert.Equal(ClothStepStatus.Accepted, step.Status);
        Assert.True(Vector3.Distance(closeSegment.End, simulation.Positions[0]) > closeSegment.Radius + Material.HalfThickness);
        Assert.True(Vector3.Distance(closeSegment.End, simulation.Positions[1]) > closeSegment.Radius + Material.HalfThickness);
        Assert.True(DistanceToTriangle(closeSegment.End, simulation.Positions[0], simulation.Positions[1],
            simulation.Positions[2]) >= closeSegment.Radius + Material.HalfThickness - .00025f);
    }

    [Theory]
    [InlineData(false, ClothStepStatus.Accepted)]
    [InlineData(true, ClothStepStatus.UnresolvedContact)]
    public void Zero_skin_capsule_sweep_accepts_tangency_but_refuses_sub_micrometre_penetration(bool rotating,
        ClothStepStatus expected)
    {
        const float reach = 1f / 32, radius = 1f / 1024, depth = 1f / 16_777_216;
        float angle = rotating ? 1f / 64 : 0;
        float x = reach + radius - (rotating ? depth : 0);
        var face = new ClothFacet(new Vector3(x, -.01f, -.01f), new Vector3(x, .01f, -.01f), new Vector3(x, 0, .01f));
        Vector3 tip = reach * Vector3.UnitX;
        var capsule = new ColliderCapsule(tip, tip, radius);
        ClothSupport opening = ClothSupport.Capsule(capsule, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -angle), Vector3.Zero);
        ClothSupport closing = ClothSupport.Capsule(capsule, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle), Vector3.Zero);
        ClothCapsuleSweep sweep = new ClothCapsuleSweep(opening, closing, default).WithFacets(face, face);
        if (rotating)
        {
            Assert.True(sweep.Sample(0).Distance > 0);
            Assert.True(sweep.Sample(1).Distance > 0);
            Assert.InRange(sweep.Sample(.5f).Distance, -2 * depth, -.5f * depth);
        }
        else
            Assert.Equal(0, sweep.Sample(.5f).Distance);

        Assert.Equal(expected, ClothContactSweep.Check(sweep, 0, 8, out _, out _, requireClearOpening: true));
    }

    [Fact]
    public void Zero_skin_convex_sweep_refuses_a_face_crossing_between_clear_endpoints()
    {
        const float reach = 1f / 32, angle = 1f / 64, depth = 1f / 16_777_216;
        var size = new Vector3(1f / 1024, 1f / 32_768, 1f / 512);
        float x = reach + size.X - depth;
        var face = new ClothFacet(new Vector3(x, -.01f, -.01f), new Vector3(x, .01f, -.01f), new Vector3(x, 0, .01f));
        var hull = new ClothPosedHull(Identity, Box(reach * Vector3.UnitX, size),
            new ClothHullPose(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -angle), Vector3.Zero, 1),
            new ClothHullPose(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle), Vector3.Zero, 1));
        var sweep = new ClothHullSweep(face, face, hull);
        Assert.True(sweep.Sample(0).Distance > 0);
        Assert.True(sweep.Sample(1).Distance > 0);
        Assert.True(sweep.Gap(.5f, Vector3.UnitX) < 0);

        Assert.NotEqual(ClothStepStatus.Accepted, ClothContactSweep.Check(sweep, 0, 8, out _, out _, requireClearOpening: true));
    }

    [Fact]
    public void Zero_skin_sweep_declines_an_opening_inside_a_capsule()
    {
        const float radius = 1f / 1024, depth = 1f / 16_777_216;
        var support = ClothSupport.Capsule(new ColliderCapsule(Vector3.Zero, Vector3.Zero, radius),
            Quaternion.Identity, Vector3.Zero);
        var sweep = new ClothCapsuleSweep(support, support, default).WithFacets(Face(radius - depth), Face(radius + depth));

        Assert.Equal(ClothStepStatus.UnresolvedContact, ClothContactSweep.Check(sweep, 0, 8, out _, out _));

        static ClothFacet Face(float x) =>
            new(new Vector3(x, -.01f, -.01f), new Vector3(x, .01f, -.01f), new Vector3(x, 0, .01f));
    }

    private static float DistanceToTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        float distance = Vector3.Dot(point - a, normal);
        Vector3 projected = point - distance * normal;
        if (InsideEdge(a, b) && InsideEdge(b, c) && InsideEdge(c, a))
            return MathF.Abs(distance);

        return MathF.Min(DistanceToEdge(a, b), MathF.Min(DistanceToEdge(b, c), DistanceToEdge(c, a)));

        bool InsideEdge(Vector3 start, Vector3 end) =>
            Vector3.Dot(Vector3.Cross(end - start, projected - start), normal) >= 0;

        float DistanceToEdge(Vector3 start, Vector3 end)
        {
            Vector3 edge = end - start;
            float fraction = Math.Clamp(Vector3.Dot(point - start, edge) / edge.LengthSquared(), 0, 1);
            return Vector3.Distance(point, start + fraction * edge);
        }
    }
}
