using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
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
            long coldCertified = SupportQueryDiagnostics.Snapshot().AdmissionCertificates;
            SupportQueryDiagnostics.Reset();
            SupportSample warm = scene.Find();
            long warmCertified = SupportQueryDiagnostics.Snapshot().AdmissionCertificates;
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
        long passing = 0, riserExtrema = 0;
        int risers = 0;
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
        long passes = warm.PolygonOperands / passing;
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

    [Fact]
    public void ReferenceAdmissionDoesNoCertificateWork()
    {
        // The reference evaluator never derives or caches the certificate's domain, so a reference Measure leaves a
        // fresh polygon as it found it. The optimized Measure then certifies it as before.
        SupportFootprintRequest request = Request();
        SupportPolygon riser = Riser(0.1, -0.1, 0.1, 0.1, -0.05, 0.05);
        FieldInfo certified = typeof(SupportPolygon).GetField("_hasVerticalDomain",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        GeometrySign reference = Admit(riser, request, reference: true, out SupportFootprintMember expected, out _);
        Assert.False((bool)certified.GetValue(riser)!);
        Assert.Equal(reference, Admit(riser, request, reference: false, out SupportFootprintMember member,
            out SupportQueryCounts counts));
        Assert.Equal(expected, member);
        Assert.True((bool)certified.GetValue(riser)!);
        Assert.Equal(1, counts.AdmissionCertificates);
    }

    [Fact]
    public void AdmissionCertificateThresholdIsItsDocumentedMargin()
    {
        // Each sweep bisects the represented values between the undecided crossing or tangency and a decided value
        // 2^44 steps away, to two adjacent values where the certificate first decides. The decided value clears the
        // documented margin exactly, 2^-14 D^2 for a vertex gap or 2^-14 a D^2 for a discriminant, and the undecided
        // one lies within a relative 2^-30 of it, so a looser or tighter margin moves the bracket.
        SupportFootprintRequest request = Request();
        double reach = (double)request.Radius + request.ContactBand;
        const double x = 0.1;
        double crossing = Math.Sqrt(reach * reach - x * x);
        // The tilted line through (s, -0.25) along (0.125, 0.5) is tangent where 0.5 s + 0.03125 = R |(0.125, 0.5)|.
        double tilted = 2 * (reach * Math.Sqrt(0.125 * 0.125 + 0.5 * 0.5) - 0.03125);
        // Beyond an axis-aligned tangency the bounds reject decides before the certificate, so that sweep runs inward.
        var sweeps = new (string Name, double Start, Func<double, SupportPolygon> Make, bool Edges, int[] Directions)[]
        {
            ("corner", crossing, c => Riser(x, -c, x, c, -0.05, 0.05), false, [-1, 1]),
            ("tangent", reach, o => Riser(o, -0.25, o, 0.25, -0.05, 0.05), true, [-1]),
            ("tilted tangent", tilted, t => Riser(t, -0.25, t + 0.125, 0.25, -0.05, 0.05), true, [-1, 1]),
        };
        foreach (var (name, start, make, edges, directions) in sweeps)
            foreach (int direction in directions)
            {
                double At(long steps) =>
                    BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(start) + direction * steps);
                bool Decides(long steps)
                {
                    SupportPolygon polygon = make(At(steps));
                    Assert.Equal(SupportVerticalDomain.Complete, polygon.VerticalDomain);
                    return AdmitsAsReference(polygon, request, $"{name} {At(steps):R}", out _);
                }
                long undecided = 0, decided = 1L << 44;
                Assert.False(Decides(undecided), $"{name} {direction}: the certificate decided the boundary itself.");
                Assert.True(Decides(decided), $"{name} {direction}: the certificate decided no far value.");
                while (decided - undecided > 1)
                {
                    long middle = undecided + (decided - undecided) / 2;
                    if (Decides(middle)) decided = middle;
                    else undecided = middle;
                }
                string context = $"{name} {direction} at {At(decided):R}";
                (BigInteger gap, BigInteger margin) = Tightest(make(At(decided)), reach, edges);
                Assert.True(gap >= margin, $"{context}: decided inside the documented margin.");
                // Outward rounding may leave a value a few ulps past the exact margin undecided, never more.
                (gap, margin) = Tightest(make(At(undecided)), reach, edges);
                Assert.True(gap << 30 < margin * ((1 << 30) + 1), $"{context}: left a value well outside the margin.");
                Assert.True(gap << 30 > margin * ((1 << 30) - 1), $"{context}: undecided far inside the margin.");
            }
    }

    // The certificate's tightest exact clearance about the origin, as 2^14 times a vertex gap |V|^2 - R^2 against D^2,
    // or 2^14 times an edge discriminant a R^2 - c^2 against a D^2, on exact integers.
    static (BigInteger Gap, BigInteger Margin) Tightest(SupportPolygon polygon, double radius, bool edges)
    {
        ReadOnlySpan<FeaturePoint> vertices = polygon.Vertices, sides = polygon.Edges;
        BigInteger r = Fixed(radius), d = r;
        foreach (FeaturePoint vertex in vertices)
            d = BigInteger.Max(d, BigInteger.Max(BigInteger.Abs(Fixed(vertex.X.Value)),
                BigInteger.Abs(Fixed(vertex.Z.Value))));
        (BigInteger Gap, BigInteger Margin)? tightest = null;
        for (int i = 0; i < vertices.Length; i++)
        {
            BigInteger vx = Fixed(vertices[i].X.Value), vz = Fixed(vertices[i].Z.Value);
            BigInteger ex = Fixed(sides[i].X.Value), ez = Fixed(sides[i].Z.Value);
            (BigInteger Gap, BigInteger Margin) candidate;
            if (!edges) candidate = (BigInteger.Abs(vx * vx + vz * vz - r * r) << 14, d * d);
            else if (ex.IsZero && ez.IsZero) continue;
            else
            {
                BigInteger a = ex * ex + ez * ez, cross = vx * ez - vz * ex;
                candidate = (BigInteger.Abs(a * r * r - cross * cross) << 14, a * d * d);
            }
            if (tightest is not { } t || candidate.Gap * t.Margin < t.Gap * candidate.Margin) tightest = candidate;
        }
        return tightest!.Value;
    }

    // An exact double as an integer multiple of 2^-128. Every value here is zero or at least 2^-75 in magnitude.
    static BigInteger Fixed(double value)
    {
        double scaled = Math.ScaleB(value, 128);
        Assert.Equal(Math.Truncate(scaled), scaled);
        return new BigInteger(scaled);
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
