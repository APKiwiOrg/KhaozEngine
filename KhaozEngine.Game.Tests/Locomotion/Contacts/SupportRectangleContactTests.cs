using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;
using Xunit;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed class SupportRectangleContactTests
{
    // The represented riser and step top of the second stair, as the box and hull stairs capture them.
    const double Riser = 0.7000000178813934, StepEnd = 1.0499999821186066;
    static readonly double Radius = 0.2f, Band = SupportJoinIncidence.ContactBand, Length = 0.01f;

    public static TheoryData<string, double, double, double, bool> SignCases() => new()
    {
        // The box and hull stairs probes rest on the riser nosing at about the probe radius.
        { "box nosing", 0.5249999761581421, 0.8468244029209018, 0, true },
        { "hull nosing", 0.5249999761581421, 0.8468245817348361, 0, true },
        { "touching", Riser - Radius, 0, 0, true },
        { "grazing within the band", Riser - (Radius + Band / 2), 0, 0, true },
        { "at the band", Riser - (Radius + Band), 0, 0, false },
        { "just beyond the band", Riser - (Radius + Band) - 1e-7, 0, 0, false },
        { "behind the face", Riser + 0.1, 0, 0, true },
        { "in the face plane", Riser, 0, 0, true },
        { "past the side within reach", Riser - 0.1, 0, 2.15, true },
        { "below the face within the band", Riser - 0.1, -1.25 - Length - Band / 2, 0, true },
        { "below the face beyond the band", Riser - 0.05, -1.34 - Length, 0, true },
    };

    [Theory]
    [MemberData(nameof(SignCases))]
    public void RectangleContactSignMatchesKernel(string name, double x, double y, double z, bool decided)
    {
        SupportPolygon riser = Rectangle(Corners(0, Riser, (-1.25, 0.75), (-2, 2)));
        GeometrySign sign = AssertSign(riser, Point(x, y, z), name);
        if (decided) Assert.NotEqual(GeometrySign.Unresolved, sign);
        // The same configuration against a face across z instead of x.
        SupportPolygon across = Rectangle(Corners(2, Riser, (-1.25, 0.75), (-2, 2)));
        GeometrySign mirrored = AssertSign(across, Point(z, y, x), name + " across z");
        if (decided) Assert.NotEqual(GeometrySign.Unresolved, mirrored);
    }

    [Fact]
    public void RectangleContactSignDecidesBothVerdicts()
    {
        SupportPolygon riser = Rectangle(Corners(0, Riser, (-1.25, 0.75), (-2, 2)));
        Assert.Equal(GeometrySign.Positive, AssertSign(riser, Point(0.5249999761581421, 0.8468244029209018, 0), "nosing"));
        // The face is above the whole segment by more than the band, so no closest pair approaches it.
        Assert.Equal(GeometrySign.Negative, AssertSign(riser, Point(Riser - 0.05, -1.34 - Length, 0), "below"));
        // A downward face over the probe, as the underside of a box, keeps the same certificate.
        SupportPolygon underside = Rectangle(Corners(1, 1, (-1, 1), (-1, 1)).Reverse().ToArray());
        Assert.Equal(GeometrySign.Positive, AssertSign(underside, Point(0, 1 - Length - Band / 2, 0), "underside"));
    }

    [Fact]
    public void RectangleContactSignFallsThroughOffExactDomain()
    {
        FeaturePoint[] corners = Corners(0, Riser, (-1.25, 0.75), (-2, 2));
        FeaturePoint start = Point(Riser - Radius, 0, 0);
        // A represented face one ulp off vertical is not its own bounding box.
        FeaturePoint[] tilted = [.. corners];
        tilted[2] = tilted[2] with { X = FeatureNumber.Exact(Math.BitIncrement(Riser)) };
        tilted[3] = tilted[3] with { X = FeatureNumber.Exact(Math.BitIncrement(Riser)) };
        FeaturePoint[] enclosed = [.. corners];
        enclosed[0] = enclosed[0] with { Y = FeatureNumber.Enclosed(GeometryInterval.Enclose(-1.2500001, -1.2499999)) };
        FeaturePoint[] yawed = [.. corners.Select(p => new FeaturePoint(p.X.Add(p.Z.Multiply(FeatureNumber.Exact(0.25))), p.Y, p.Z))];
        FeaturePoint[] distant = Corners(0, Math.ScaleB(1, 41), (-1.25, 0.75), (-2, 2));
        foreach (SupportPolygon polygon in new[] { Rectangle(tilted), Rectangle(enclosed), Rectangle(yawed),
            Rectangle(corners[..3]), Rectangle(distant) })
            Assert.Equal(GeometrySign.Unresolved, Sign(polygon, start, Direction()));
        SupportPolygon riser = Rectangle(corners);
        FeaturePoint inexact = start with { Y = FeatureNumber.Enclosed(GeometryInterval.Enclose(-0.0000001, 0.0000001)) };
        Assert.Equal(GeometrySign.Unresolved, Sign(riser, inexact, Direction()));
        Assert.Equal(GeometrySign.Unresolved, Sign(riser, start, Point(0.001, Length, 0)));
        Assert.Equal(GeometrySign.Unresolved, Sign(riser, start, Point(0, -Length, 0)));
        Assert.Equal(GeometrySign.Unresolved, SupportRectangleContact.Sign(riser, start, Direction(), Radius, double.NaN));
        Assert.Equal(GeometrySign.Positive, Sign(riser, start, Direction()));
    }

    public static TheoryData<string, float, float, float, bool> LowestCases() => new()
    {
        { "inside", 0, 0, 0.25f, false },
        { "on the edge", 1, 0, 0.25f, false },
        { "straddling an edge", 1.1f, 0, 0.25f, true },
        { "straddling with vertices", 1.1f, 0, 1.5f, true },
        { "tangent to an edge", 1.25f, 0, 0.25f, true },
        { "tangent to a vertex", 1.75f, 2, 1.25f, true },
        { "beside a corner", 1.1f, 1.1f, 0.25f, true },
        { "missing", 3, 0, 0.25f, true },
        { "missing within the skip margin", 1.25f, 1.5f, 0.2499999f, true },
    };

    [Theory]
    [MemberData(nameof(LowestCases))]
    public void RectangleLowestMatchesExhaustive(string name, float x, float z, float radius, bool decided)
    {
        foreach (SupportPolygon polygon in new[] { Rectangle(Corners(1, 0.5, (-1, 1), (-1, 1))),
            Installed(Quaternion.Identity, new(0.00000001f, 0, 0)), Installed(Quaternion.Identity, new(0, 0.25f, 0)) })
            Assert.Equal(decided, AssertLowest(polygon, Proposal(x, z, radius), name));
    }

    [Fact]
    public void RectangleLowestMatchesExhaustiveOnTheStepTop()
    {
        // The second step top the stairs probes straddle at its riser edge.
        SupportPolygon top = Rectangle(Corners(1, 0.75, (Riser, StepEnd), (-2, 2)));
        Assert.True(AssertLowest(top, Proposal(0.525f, 0, 0.2f), "step top"));
        FeaturePoint[] tilted = Corners(1, 0.75, (Riser, StepEnd), (-2, 2));
        tilted[2] = tilted[2] with { Y = FeatureNumber.Exact(Math.BitIncrement(0.75)) };
        tilted[3] = tilted[3] with { Y = FeatureNumber.Exact(Math.BitIncrement(0.75)) };
        Assert.False(AssertLowest(Rectangle(tilted), Proposal(0.525f, 0, 0.2f), "tilted step top"));
        Assert.False(AssertLowest(Installed(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.37f), Vector3.Zero),
            Proposal(1.1f, 0, 0.25f), "rotated"));
    }

    [Fact]
    public void ReferenceEvaluationBypassesRectangleContact()
    {
        SupportPolygon riser = Rectangle(Corners(0, Riser, (-1.25, 0.75), (-2, 2)));
        SupportPolygon top = Rectangle(Corners(1, 0.75, (Riser, StepEnd), (-2, 2)));
        SupportFootprintProposal proposal = Proposal(0.525f, 0, 0.2f);
        FeaturePoint start = Point(Riser - Radius, 0, 0);
        Assert.Equal(GeometrySign.Positive, Sign(riser, start, Direction()));
        Assert.True(SupportRectangleContact.TryLowest(top, proposal, out _, out _));
        using (SupportReferenceEvaluation.Begin())
        {
            Assert.Equal(GeometrySign.Unresolved, Sign(riser, start, Direction()));
            Assert.False(SupportRectangleContact.TryLowest(top, proposal, out _, out _));
        }
        SupportCostRow row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == "stairs box");
        using SupportFootprintScene scene = SupportGateCostScenes.Build(row);
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        Assert.True(FindCounts(scene, lease, row, reference: false).RectangleContact > 0);
        Assert.Equal(0, FindCounts(scene, lease, row, reference: true).RectangleContact);
    }

    [Fact]
    public void RectangleContactMatchesReferenceOnEveryCostScene()
    {
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            foreach (SupportCostRow row in SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]))
            {
                using SupportFootprintScene scene = SupportGateCostScenes.Build(row);
                using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
                Func<float, float, float>? height = row.Analytic ? (x, _) => row.HeightAt(x) : null;
                Func<float, float, Vector3>? normal = row.Analytic ? (x, _) => row.NormalAt(x) : null;
                SupportQueryCounts before = SupportQueryDiagnostics.Snapshot();
                SupportFootprintEquivalence.AssertMatchesReference(() =>
                    FootSupport.Find(height, normal, scene.World, lease, row.Query), row.Name);
                long hits = (SupportQueryDiagnostics.Snapshot() - before).RectangleContact;
                if (row.Name is "stairs box" or "stairs hull") Assert.True(hits > 0, row.Name);
                // Footprint members carry each proposal's contact mask, and the result carries both contact
                // heights, so this compares every kernel-visible contact verdict bit for bit.
                SupportFootprintEquivalence.AssertMatchesReference(scene.World, lease,
                    FootSupport.DescribeFootprint(row.Query), row.Name);
            }
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    static SupportQueryCounts FindCounts(SupportFootprintScene scene, IPhysicsQueryLease lease, SupportCostRow row,
        bool reference)
    {
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            if (reference)
                using (SupportReferenceEvaluation.Begin()) FootSupport.Find(null, null, scene.World, lease, row.Query);
            else FootSupport.Find(null, null, scene.World, lease, row.Query);
            return SupportQueryDiagnostics.Snapshot();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    // The certificate may only return the kernel path's own resolved verdict, and counts each decision.
    static GeometrySign AssertSign(SupportPolygon polygon, FeaturePoint start, string context)
    {
        GeometrySign kernel = Kernel(polygon, start, Direction());
        Assert.NotEqual(GeometrySign.Unresolved, kernel);
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            GeometrySign sign = Sign(polygon, start, Direction());
            Assert.True(sign == GeometrySign.Unresolved || sign == kernel, $"{context}: {sign} against kernel {kernel}");
            Assert.Equal(sign == GeometrySign.Unresolved ? 0 : 1, SupportQueryDiagnostics.Snapshot().RectangleContact);
            return sign;
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    // The collector's kernel path after every earlier certificate leaves the member unresolved.
    static GeometrySign Kernel(SupportPolygon polygon, FeaturePoint start, FeaturePoint direction)
    {
        SupportDistance distance = new SupportSegmentPolygon().MeasureFootprintContact(start, direction, polygon, Band);
        if (!distance.IsResolved) return GeometrySign.Unresolved;
        GeometryInterval separation = GeometryInterval.Enclose(distance.Lower, distance.Upper)
            .Subtract(GeometryInterval.Exact(Radius));
        if (!separation.IsResolved) return GeometrySign.Unresolved;
        return separation.Lower <= Band && distance.Front ? GeometrySign.Positive : GeometrySign.Negative;
    }

    static GeometrySign Sign(SupportPolygon polygon, FeaturePoint start, FeaturePoint direction) =>
        SupportRectangleContact.Sign(polygon, start, direction, Radius, Band);

    // TryLowest decides only where it returns the complete evaluator's sign and every bit of its bounds.
    static bool AssertLowest(SupportPolygon polygon, in SupportFootprintProposal proposal, string context)
    {
        GeometrySign expected = SupportFootprintProposalGeometry.Lowest(polygon, proposal, out GeometryInterval bounds,
            sourceHeight: true);
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            bool decided = SupportRectangleContact.TryLowest(polygon, proposal, out GeometrySign sign,
                out GeometryInterval actual);
            Assert.Equal(decided ? 1 : 0, SupportQueryDiagnostics.Snapshot().RectangleContact);
            if (!decided) return false;
            Assert.Equal(expected, sign);
            Assert.True(expected != GeometrySign.Unresolved, context);
            Assert.Equal(bounds.IsResolved, actual.IsResolved);
            Assert.Equal(BitConverter.DoubleToInt64Bits(bounds.Lower), BitConverter.DoubleToInt64Bits(actual.Lower));
            Assert.Equal(BitConverter.DoubleToInt64Bits(bounds.Upper), BitConverter.DoubleToInt64Bits(actual.Upper));
            return true;
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    static SupportFootprintProposal Proposal(float x, float z, float radius) =>
        new(new CapsuleShape(radius, 0.01f), Pose.At(new(x, 2, z)), 3);

    static FeaturePoint Direction() => Point(0, Length, 0);

    static FeaturePoint Point(double x, double y, double z) =>
        new(FeatureNumber.Exact(x), FeatureNumber.Exact(y), FeatureNumber.Exact(z));

    // An axis-aligned rectangle where coordinate <paramref name="axis"/> equals <paramref name="plane"/>. A face across
    // x spans y then z, a horizontal face spans x then z with upward winding, and a face across z spans y then x.
    static FeaturePoint[] Corners(int axis, double plane, (double Low, double High) first, (double Low, double High) second)
    {
        FeaturePoint At(double a, double b) => axis switch
        {
            0 => Point(plane, a, b),
            1 => Point(a, plane, b),
            _ => Point(b, a, plane),
        };
        return [At(first.Low, second.Low), At(first.Low, second.High), At(first.High, second.High), At(first.High, second.Low)];
    }

    static SupportPolygon Rectangle(FeaturePoint[] points)
    {
        var polygon = new SupportPolygon();
        polygon.Begin(new StaticHandle(42), 7, points.Length);
        for (int i = 0; i < points.Length; i++)
            polygon.Set(i, points[i], FeaturePoint.Subtract(points[(i + 1) % points.Length], points[i]));
        polygon.Finish(FeaturePoint.Cross(polygon.Edges[0], FeaturePoint.Subtract(points[2], points[0]), true));
        return polygon;
    }

    static SupportPolygon Installed(Quaternion rotation, Vector3 translation)
    {
        Assert.True(InstalledPoseOperator.TryCreateSupport(new BepuPhysics.RigidPose(translation, rotation),
            out InstalledPoseOperator map));
        Vector3[] local = [new(-1, 0.5f, -1), new(-1, 0.5f, 1), new(1, 0.5f, 1), new(1, 0.5f, -1)];
        var polygon = new SupportPolygon();
        polygon.Begin(new StaticHandle(42), 7, 4);
        polygon.BeginSource(map);
        for (int i = 0; i < 4; i++)
        {
            polygon.SetSource(i, local[i]);
            polygon.Set(i, map.Point(local[i], true), map.Edge(local[i], local[(i + 1) % 4], true));
        }
        polygon.Finish(FeaturePoint.Cross(polygon.Edges[0],
            FeaturePoint.Subtract(polygon.Vertices[2], polygon.Vertices[0]), true));
        Assert.True(polygon.HasSource);
        return polygon;
    }
}
