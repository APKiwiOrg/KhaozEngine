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
    public void AdmissionCertificateMatchesTheReferenceOnEveryCostRow()
    {
        using var counters = new WorldFormCounters();
        foreach (string name in new[] { "stairs box", "stairs hull", "hidden crate" })
        {
            using var scene = new WorldFormScene(CostRows().Single(row => row.Name == name));
            SupportQueryDiagnostics.Reset();
            SupportSample cold = scene.Find();
            int coldCertified = SupportQueryDiagnostics.Snapshot().AdmissionCertificates;
            SupportQueryDiagnostics.Reset();
            SupportSample warm = scene.Find();
            int warmCertified = SupportQueryDiagnostics.Snapshot().AdmissionCertificates;
            Assert.True(warmCertified > 0, $"{name}: no vertical face was admitted by the segment certificate.");
            Assert.Equal(coldCertified, warmCertified);
            Assert.Equal(cold.ToString(), warm.ToString());
            SupportQueryDiagnostics.Reset();
            using (SupportReferenceEvaluation.Begin()) Assert.Equal(warm.ToString(), scene.Find().ToString());
            Assert.Equal(0, SupportQueryDiagnostics.Snapshot().AdmissionCertificates);
        }

        // Every raw member keeps its plane, normal and witnesses, so a certificate that admitted a face the kernel
        // refuses or drops, or the reverse, changes a member, the member count or the status against the reference.
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
    public void StairsFindCertifiesEveryAdmittedRiser()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "stairs box"));
        using IPhysicsQueryLease lease = scene.AcquireLease();
        SupportSample cold = scene.Find(lease);
        SupportFootprintRequest request = FootSupport.DescribeFootprint(scene.Query);

        // Each face once: the risers the bounds and column reject keeps are certified, and nothing else is.
        int passing = 0, risers = 0, riserExtrema = 0;
        var polygon = new SupportPolygon();
        foreach (StaticHandle owner in scene.Owners())
            foreach (InstalledSupportWorldFace face in scene.Form(owner) is { } form
                ? form.Leaves.ToArray().SelectMany(leaf => leaf.Faces.ToArray()) : [])
            {
                polygon.Bind(owner, face);
                bool certified = AdmitsAsReference(polygon, request, $"owner {owner.Value} face {face.ElementId}",
                    out _);
                Admit(polygon, request, reference: true, out _, out SupportQueryCounts kernel);
                Admit(polygon, request, reference: false, out _, out SupportQueryCounts optimized);
                passing += optimized.PolygonOperands;
                bool riser = face.Orientation == GeometrySign.Zero && optimized.PolygonOperands == 1;
                Assert.True(riser == certified, $"owner {owner.Value} face {face.ElementId}: certified {certified}.");
                if (!riser) continue;
                risers++;
                riserExtrema += kernel.Extrema;
                Assert.Equal(0, optimized.Extrema);
            }
        Assert.Equal(4, risers);
        Assert.True(riserExtrema > 0);

        // A Find repeats the polygon pass once per collection, so its counters are whole multiples of one pass.
        SupportQueryDiagnostics.Reset();
        Assert.Equal(cold.ToString(), scene.Find(lease).ToString());
        SupportQueryCounts warm = SupportQueryDiagnostics.Snapshot();
        SupportQueryDiagnostics.Reset();
        using (SupportReferenceEvaluation.Begin()) Assert.Equal(cold.ToString(), scene.Find(lease).ToString());
        SupportQueryCounts reference = SupportQueryDiagnostics.Snapshot();
        Assert.True(warm.PolygonOperands > 0 && warm.PolygonOperands % passing == 0,
            $"{warm.PolygonOperands} operands for {passing} passing faces.");
        int passes = warm.PolygonOperands / passing;
        Assert.Equal(risers * passes, warm.AdmissionCertificates);
        Assert.Equal(riserExtrema * passes, reference.Extrema - warm.Extrema);
        Assert.Equal(0, reference.AdmissionCertificates);
    }

    [Fact]
    public void AdmissionCertificateKeepsUnresolvedReachable()
    {
        SupportFootprintRequest request = Request();
        double reach = (double)request.Radius + request.ContactBand;

        // A riser across x = 0.1 from -c to c, with both far corners moved along z through the expanded circle in
        // represented steps. At the crossing the kernel's root enclosure cannot place the corners, its exact fallback
        // needs an exact discriminant, which the rounded square of the radius is not, and with no source it refuses.
        // A margin too small to cover that enclosure would certify those refusals.
        const double x = 0.1;
        double crossing = Math.Sqrt(reach * reach - x * x);
        int refused = 0, certified = 0;
        long closest = long.MaxValue;
        foreach (int direction in new[] { -1, 1 })
            for (long steps = 0; steps <= 1L << 44; steps = steps == 0 ? 1 : steps * 2)
            {
                double corner = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(crossing) +
                    direction * steps);
                SupportPolygon riser = Riser(x, -corner, x, corner, -0.05, 0.05);
                Assert.Equal(SupportVerticalDomain.Complete, riser.VerticalDomain);
                bool decided = AdmitsAsReference(riser, request, $"corner {corner:R}", out GeometrySign verdict);
                if (verdict == GeometrySign.Unresolved) refused++;
                if (!decided) continue;
                certified++;
                closest = Math.Min(closest, steps);
            }
        Assert.True(refused > 0, "The sweep reached no kernel refusal.");
        Assert.True(certified > 0, "The sweep certified no admission.");
        Assert.True(closest > 1 << 8, $"The certificate decided {closest} steps from the crossing.");

        // A riser whose line moves through tangency. The kernel's exact discriminant decides every step, and the
        // certificate leaves the steps within its margin to it.
        int tangent = 0;
        foreach (int direction in new[] { -1, 1 })
            for (long steps = 0; steps <= 1L << 44; steps = steps == 0 ? 1 : steps * 2)
            {
                double offset = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(reach) + direction * steps);
                if (AdmitsAsReference(Riser(offset, -0.25, offset, 0.25, -0.05, 0.05), request,
                    $"offset {offset:R}", out _)) tangent++;
            }
        Assert.True(tangent > 0, "The tangency sweep certified no admission.");

        // An edge enclosure that straddles zero leaves the face outside the domain, and the kernel refuses it.
        var uncertain = new SupportPolygon();
        uncertain.Begin(new StaticHandle(42), 7, 4);
        FeatureNumber near = FeatureNumber.Enclosed(GeometryInterval.Enclose(-1e-9, 1e-9));
        Vector3[] corners = [new(0.1f, -0.05f, -0.1f), new(0.1f, -0.05f, 0.1f), new(0.1f, 0.05f, 0.1f),
            new(0.1f, 0.05f, -0.1f)];
        for (int i = 0; i < corners.Length; i++)
        {
            FeaturePoint vertex = FeaturePoint.Exact(corners[i]);
            uncertain.Set(i, vertex, i == 2 ? new(near, near, near)
                : FeaturePoint.Subtract(FeaturePoint.Exact(corners[(i + 1) % corners.Length]), vertex));
        }
        uncertain.Finish(new(FeatureNumber.Exact(-1), FeatureNumber.Exact(0), FeatureNumber.Exact(0)));
        Assert.True(uncertain.IsResolved);
        Assert.Equal(SupportVerticalDomain.None, uncertain.VerticalDomain);
        Assert.False(AdmitsAsReference(uncertain, request, "uncertain edge", out GeometrySign refusal));
        Assert.Equal(GeometrySign.Unresolved, refusal);
    }

    [Fact]
    public void AdmissionCertificateAgreesWithTheKernelAcrossTheDisc()
    {
        // Vertical rectangles and triangles at many yaws are moved from the axis outward in 6.25 mm steps, across the
        // expanded disc's boundary. Rectangles keep horizontal and vertical edges and take the complete domain. A
        // right triangle's sloped edge leaves it the separation domain, which certifies only a disc no edge meets.
        SupportFootprintRequest request = Request();
        int certified = 0, separated = 0, admitted = 0;
        for (int i = 0; i < 48; i++)
        {
            float Next(int k) => (float)((i * 0.6180339887498949 + k * 0.7548776662466927) % 1.0);
            float yaw = 2 * MathF.PI * Next(1);
            Vector2 along = (i % 6) switch
            {
                0 => Vector2.UnitX,
                1 => Vector2.UnitY,
                _ => new(MathF.Cos(yaw), MathF.Sin(yaw)),
            };
            Vector2 across = new(-along.Y, along.X);
            float half = 0.05f + 0.3f * Next(2), slide = 0.6f * (Next(3) - 0.5f);
            float low = -0.3f + 0.2f * Next(4), high = low + 0.05f + 0.4f * Next(5);
            for (int step = 0; step <= 48; step++)
            {
                Vector2 centre = across * (0.3f * step / 48) + along * slide;
                Vector2 a = centre - along * half, b = centre + along * half, top = Next(6) < 0.5f ? a : b;
                SupportPolygon polygon = i % 2 == 0
                    ? Polygon([new(a.X, low, a.Y), new(b.X, low, b.Y), new(b.X, high, b.Y), new(a.X, high, a.Y)],
                        out _, upward: false)
                    : Polygon([new(a.X, low, a.Y), new(b.X, low, b.Y), new(top.X, high, top.Y)], out _, upward: false);
                Assert.Equal(GeometrySign.Zero, polygon.Orientation);
                bool decided = AdmitsAsReference(polygon, request, $"Case {i} step {step}", out GeometrySign verdict);
                if (verdict == GeometrySign.Positive) admitted++;
                if (!decided) continue;
                certified++;
                if (verdict == GeometrySign.Negative) separated++;
            }
        }
        Assert.True(certified > 0 && separated > 0 && certified > separated,
            $"{certified} certified, {separated} of them separated.");
        Assert.True(admitted > certified - separated, $"{admitted} admitted, {certified - separated} by certificate.");
    }

    // A vertical rectangle over the XZ segment from (ax, az) to (bx, bz) between two heights, on exact doubles.
    static SupportPolygon Riser(double ax, double az, double bx, double bz, double low, double high)
    {
        FeaturePoint[] corners =
        [
            Point(ax, low, az), Point(bx, low, bz), Point(bx, high, bz), Point(ax, high, az),
        ];
        var polygon = new SupportPolygon();
        polygon.Begin(new StaticHandle(42), 7, corners.Length);
        for (int i = 0; i < corners.Length; i++)
            polygon.Set(i, corners[i], FeaturePoint.Subtract(corners[(i + 1) % corners.Length], corners[i]));
        polygon.Finish(FeaturePoint.Cross(polygon.Edges[0], polygon.Edges[1]));
        return polygon;
    }

    static FeaturePoint Point(double x, double y, double z) =>
        new(FeatureNumber.Exact(x), FeatureNumber.Exact(y), FeatureNumber.Exact(z));

    // Asserts the optimized admission of one polygon matches the reference, verdict and member. Returns whether the
    // segment certificate decided it.
    static bool AdmitsAsReference(SupportPolygon polygon, in SupportFootprintRequest request, string context,
        out GeometrySign verdict)
    {
        GeometrySign reference = Admit(polygon, request, reference: true, out SupportFootprintMember expected, out _);
        verdict = Admit(polygon, request, reference: false, out SupportFootprintMember member,
            out SupportQueryCounts counts);
        Assert.True(reference == verdict, $"{context}: {verdict} differs from reference {reference}.");
        Assert.True(expected.Equals(member), $"{context}: member {member} differs from reference {expected}.");
        Assert.InRange(counts.AdmissionCertificates, 0, 1);
        return counts.AdmissionCertificates == 1;
    }

    // One polygon's footprint admission and member, with the support counters it recorded. The caller's counters
    // are reset.
    static GeometrySign Admit(SupportPolygon polygon, in SupportFootprintRequest request, bool reference,
        out SupportFootprintMember member, out SupportQueryCounts counts)
    {
        Assert.True(SupportFootprintBounds.TryCreate(request, out SupportFootprintBounds bounds));
        Assert.True(SupportFootprintBounds.TryColumn(request, out double bottom, out double top));
        bool enabled = SupportQueryDiagnostics.Enabled;
        SupportQueryDiagnostics.Enable(true);
        SupportQueryDiagnostics.Reset();
        try
        {
            GeometrySign verdict;
            if (reference)
                using (SupportReferenceEvaluation.Begin())
                    verdict = SupportFootprintPolygon.Measure(polygon, request, bounds, bottom, top, out member);
            else verdict = SupportFootprintPolygon.Measure(polygon, request, bounds, bottom, top, out member);
            counts = SupportQueryDiagnostics.Snapshot();
            return verdict;
        }
        finally
        {
            SupportQueryDiagnostics.Reset();
            SupportQueryDiagnostics.Enable(enabled);
        }
    }
}
