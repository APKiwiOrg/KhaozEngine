using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void RejectedPolygonsBeginNoOperands()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "flat box"));
        using IPhysicsQueryLease lease = scene.AcquireLease();
        SupportSample cold = scene.Find(lease);
        InstalledSupportWorldFace[] faces = [.. scene.Form(scene.Owners().Single())!.Leaves.ToArray()
            .SelectMany(leaf => leaf.Faces.ToArray())];
        SupportFootprintRequest request = FootSupport.DescribeFootprint(scene.Query);
        Assert.True(SupportFootprintBounds.TryCreate(request, out SupportFootprintBounds bounds));
        Assert.True(SupportFootprintBounds.TryColumn(request, out double bottom, out double top));
        // The area and resolution checks keep their order ahead of the bounds and column reject.
        int passing = faces.Count(face => face.IsResolved && face.NormalArea == GeometrySign.Positive &&
            !(face.High[0] < bounds.Minimum.X || face.Low[0] > bounds.Maximum.X || face.High[1] < bottom ||
                face.Low[1] > top || face.High[2] < bounds.Minimum.Z || face.Low[2] > bounds.Maximum.Z));

        SupportQueryDiagnostics.Reset();
        Assert.Equal(cold.ToString(), scene.Find(lease).ToString());
        SupportQueryCounts warm = SupportQueryDiagnostics.Snapshot();
        Assert.True(warm.Polygons > 0 && warm.Polygons % faces.Length == 0, $"{warm.Polygons} polygons.");
        Assert.Equal(passing * (warm.Polygons / faces.Length), warm.PolygonOperands);
        Assert.True(warm.PolygonOperands < warm.Polygons, $"{warm.PolygonOperands} of {warm.Polygons}.");

        // The reference begins operands on every polygon before any reject.
        SupportQueryDiagnostics.Reset();
        using (SupportReferenceEvaluation.Begin()) Assert.Equal(cold.ToString(), scene.Find(lease).ToString());
        SupportQueryCounts reference = SupportQueryDiagnostics.Snapshot();
        Assert.Equal(reference.Polygons, reference.PolygonOperands);
        Assert.Equal(warm.Polygons, reference.Polygons);
    }

    [Fact]
    public void PrefilterRejectsOnlyCertifiedSeparation()
    {
        using var counters = new WorldFormCounters();
        foreach (string name in new[] { "stairs box", "hidden crate" })
        {
            using var scene = new WorldFormScene(CostRows().Single(row => row.Name == name));
            SupportQueryDiagnostics.Reset();
            SupportSample cold = scene.Find();
            int coldRejects = SupportQueryDiagnostics.Snapshot().PrefilterRejects;
            SupportQueryDiagnostics.Reset();
            SupportSample warm = scene.Find();
            int warmRejects = SupportQueryDiagnostics.Snapshot().PrefilterRejects;
            Assert.True(warmRejects > 0, $"{name}: no member was rejected before the contact kernel.");
            Assert.Equal(coldRejects, warmRejects);
            Assert.Equal(cold.ToString(), warm.ToString());
            SupportQueryDiagnostics.Reset();
            using (SupportReferenceEvaluation.Begin()) Assert.Equal(warm.ToString(), scene.Find().ToString());
            Assert.Equal(0, SupportQueryDiagnostics.Snapshot().PrefilterRejects);
        }

        // Every raw member's proposal mask records both contact verdicts, so a prefilter that turned a kernel
        // Positive or Unresolved into Negative changes a mask or the result status against the reference.
        foreach (SupportCostRow row in CostRows())
        {
            using var scene = new WorldFormScene(row);
            scene.Checked($"{row.Name} cold", out _);
            scene.Checked($"{row.Name} warm", out _);
            scene.CheckedFootprint($"{row.Name} raw members");
            if (row.Name.StartsWith("flat ", StringComparison.Ordinal) ||
                row.Name.StartsWith("stairs ", StringComparison.Ordinal))
                scene.CheckedSubstep($"{row.Name} substep");
        }
    }

    [Fact]
    public void PrefilterKeepsBandBoundaryMembers()
    {
        SupportFootprintRequest request = Request();
        double reach = (double)request.AxisProposal.Probe.Radius + request.ContactBand;

        // The contact band boundary: the last represented wall offset the reference keeps and the first it drops.
        float inside = (float)reach;
        while (Wall(request, inside, reference: true, out _) != GeometrySign.Positive) inside = MathF.BitDecrement(inside);
        while (Wall(request, MathF.BitIncrement(inside), reference: true, out _) == GeometrySign.Positive)
            inside = MathF.BitIncrement(inside);
        float outside = MathF.BitIncrement(inside);
        Assert.Equal(GeometrySign.Negative, Wall(request, outside, reference: true, out _));
        foreach (float offset in new[] { inside, outside })
        {
            Assert.Equal(Wall(request, offset, reference: true, out _), Wall(request, offset, reference: false,
                out int rejects));
            Assert.Equal(0, rejects);
        }

        // The prefilter's own boundary: the first represented offset it certifies, and the one step before it.
        int low = BitConverter.SingleToInt32Bits(outside), high = BitConverter.SingleToInt32Bits(0.5f);
        Wall(request, 0.5f, reference: false, out int far);
        Assert.Equal(1, far);
        while (low + 1 < high)
        {
            int middle = low + (high - low) / 2;
            Wall(request, BitConverter.Int32BitsToSingle(middle), reference: false, out int rejects);
            if (rejects == 0) low = middle;
            else high = middle;
        }
        foreach ((int bits, int expected) in new[] { (low, 0), (high, 1) })
        {
            float offset = BitConverter.Int32BitsToSingle(bits);
            Assert.Equal(GeometrySign.Negative, Wall(request, offset, reference: true, out _));
            Assert.Equal(GeometrySign.Negative, Wall(request, offset, reference: false, out int rejects));
            Assert.Equal(expected, rejects);
        }
    }

    [Fact]
    public void PrefilterNeverHidesAnUnresolvedKernel()
    {
        SupportFootprintRequest request = Request();
        Vector3 a = new(0.5f, -0.1f, -0.1f), b = new(0.5f, 0.1f, -0.1f), c = new(0.5f, 0.1f, 0.1f),
            d = new(0.5f, -0.1f, 0.1f);

        // The same wall, certifiably clear of the contact segment, is a prefiltered non-member.
        SupportPolygon wall = Polygon([a, b, c, d], out _, upward: false);
        Assert.Equal(GeometrySign.Negative, Member(request, wall, reference: true, out _));
        Assert.Equal(GeometrySign.Negative, Member(request, wall, reference: false, out int rejects));
        Assert.Equal(1, rejects);

        // A repeated vertex leaves a zero-length edge, which the kernel refuses wherever the segment is.
        SupportPolygon repeated = Polygon([a, b, c, c, d], out _, upward: false);
        Assert.True(repeated.IsResolved);
        Assert.Equal(GeometrySign.Unresolved, Member(request, repeated, reference: true, out _));
        Assert.Equal(GeometrySign.Unresolved, Member(request, repeated, reference: false, out rejects));
        Assert.Equal(0, rejects);

        // An edge enclosure that straddles zero cannot prove a positive edge length either.
        var uncertain = new SupportPolygon();
        uncertain.Begin(new StaticHandle(42), 7, 4);
        FeatureNumber near = FeatureNumber.Enclosed(GeometryInterval.Enclose(-1e-9, 1e-9));
        Vector3[] corners = [a, b, c, d];
        for (int i = 0; i < corners.Length; i++)
        {
            FeaturePoint vertex = FeaturePoint.Exact(corners[i]);
            uncertain.Set(i, vertex, i == 2 ? new(near, near, near)
                : FeaturePoint.Subtract(FeaturePoint.Exact(corners[(i + 1) % corners.Length]), vertex));
        }
        uncertain.Finish(wall.Normal);
        Assert.True(uncertain.IsResolved);
        Assert.Equal(GeometrySign.Unresolved, Member(request, uncertain, reference: true, out _));
        Assert.Equal(GeometrySign.Unresolved, Member(request, uncertain, reference: false, out rejects));
        Assert.Equal(0, rejects);
    }

    [Fact]
    public void PrefilterOnlyFiresWhereTheKernelIsNegative()
    {
        // Triangles and rectangles in many orientations are swept from the contact segments outward, across both
        // proposals' band boundaries and the prefilter's own margin.
        SupportFootprintRequest request = Request();
        int fired = 0;
        for (int i = 0; i < 48; i++)
        {
            float Next(int k) => (float)((i * 0.6180339887498949 + k * 0.7548776662466927) % 1.0);
            Quaternion orientation = Quaternion.Normalize(new(Next(1) - 0.5f, Next(2) - 0.5f, Next(3) - 0.5f,
                Next(4) + 0.1f));
            Vector3 toward = Vector3.Normalize(new(Next(5) - 0.5f, Next(6) - 0.5f, Next(7) - 0.5f));
            float width = 0.05f + 0.2f * Next(8), depth = 0.05f + 0.2f * Next(9);
            Vector3[] local = i % 2 == 0
                ? [Vector3.Zero, new(width, 0, 0), new(0.5f * width, 0, depth)]
                : [Vector3.Zero, new(width, 0, 0), new(width, 0, depth), new(0, 0, depth)];
            for (int step = 0; step <= 48; step++)
            {
                Vector3 shift = new Vector3(0, 0.015f, 0) + toward * (0.3f * step / 48);
                SupportPolygon polygon = Polygon([.. local.Select(v => Vector3.Transform(v, orientation) + shift)],
                    out _);
                for (int proposal = 0; proposal < 2; proposal++)
                {
                    GeometrySign reference = Member(request, polygon, reference: true, out _, proposal);
                    GeometrySign optimized = Member(request, polygon, reference: false, out int rejects, proposal);
                    Assert.True(reference == optimized, $"Case {i} step {step} proposal {proposal}: {optimized} " +
                        $"differs from reference {reference}.");
                    if (rejects == 0) continue;
                    fired++;
                    Assert.Equal(GeometrySign.Negative, reference);
                }
            }
        }
        Assert.True(fired > 0);
    }

    // A wall facing the axis proposal's contact segment across x, at the given represented offset.
    static GeometrySign Wall(in SupportFootprintRequest request, float x, bool reference, out int rejects) =>
        Member(request, Polygon([new(x, -0.1f, -0.1f), new(x, 0.1f, -0.1f), new(x, 0.1f, 0.1f),
            new(x, -0.1f, 0.1f)], out _, upward: false), reference, out rejects);

    [ThreadStatic] static SupportFootprintCollector? t_memberCollector;

    // One proposal's member verdict for one polygon, its contact capsule resting on y = 0. Begin resets the reused
    // collector.
    static GeometrySign Member(in SupportFootprintRequest request, SupportPolygon polygon, bool reference,
        out int rejects, int index = 0)
    {
        SupportFootprintCollector collector = t_memberCollector ??= new();
        Assert.True(collector.Begin(request));
        SupportFootprintProposal proposal = index == 0 ? request.AxisProposal : request.FootProposal;
        float distance = proposal.Start.Position.Y - proposal.Probe.Radius - proposal.Probe.Length / 2;
        collector.Proposal(index, true, new SweepHit(distance, Vector3.Zero, Vector3.UnitY, new StaticHandle(1)));
        bool enabled = SupportQueryDiagnostics.Enabled;
        SupportQueryDiagnostics.Enable(true);
        SupportQueryDiagnostics.Reset();
        try
        {
            GeometrySign verdict;
            if (reference)
                using (SupportReferenceEvaluation.Begin()) verdict = collector.ContactMember(polygon, index);
            else verdict = collector.ContactMember(polygon, index);
            rejects = SupportQueryDiagnostics.Snapshot().PrefilterRejects;
            return verdict;
        }
        finally
        {
            SupportQueryDiagnostics.Reset();
            SupportQueryDiagnostics.Enable(enabled);
        }
    }
}
