using System;
using System.Collections.Generic;
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
    // Bepu installs the flat hull at this measured centre, so its world vertices are represented sums that binary32
    // cannot hold. The second offset also lifts the plane off binary32 and moves z.
    static readonly Vector3 HullCentre = new(2.9802326e-8f, -1, 0);
    static readonly Vector3 LiftedCentre = new(-1.3271044e-9f, 9.313226e-10f, 3.7252903e-9f);
    const int SampleRows = 32;

    [Fact]
    public void FlatHullTopTakesThePlanarFastPath()
    {
        using var counters = new WorldFormCounters();
        (int Contacts, int Heights) box = PlanarPhases("flat box", reference: false);
        Assert.True(box.Contacts > 0 && box.Heights > 0, $"flat box planar phases {box}.");
        Assert.Equal(box, PlanarPhases("flat box", reference: true));
        // The hull's top face is the box's, at an offset centre. It now certifies as the box's does, and the
        // reference still measures it with the kernel.
        Assert.Equal(box, PlanarPhases("flat hull", reference: false));
        Assert.Equal((0, 0), PlanarPhases("flat hull", reference: true));
    }

    [Fact]
    public void ExactPlanarCertificateKeepsKernelVerdicts()
    {
        var kernel = new SupportSegmentPolygon();
        int inside = 0, boundary = 0, outside = 0;
        foreach ((Vector3[] source, Vector3 centre, bool lifted) in new[]
        {
            (new Vector3[] { new(-4, 1, -2), new(4, 1, -2), new(4, 1, 2), new(-4, 1, 2) }, HullCentre, false),
            (new Vector3[] { new(-0.75f, 0.5f, -0.5f), new(0.625f, 0.5f, -0.25f), new(-0.125f, 0.5f, 0.875f) },
                LiftedCentre, true),
        })
        {
            SupportPolygon polygon = Installed(source, centre);
            Assert.Contains(polygon.Vertices.ToArray(), v => !Binary32(v.X.Value) || !Binary32(v.Y.Value) ||
                !Binary32(v.Z.Value));
            Assert.True(polygon.HasJoinIncidenceCertificate);
            using (SupportReferenceEvaluation.Begin())
                Assert.False(Installed(source, centre).HasJoinIncidenceCertificate);
            double height = polygon.Vertices[0].Y.Value;
            int edges = polygon.Vertices.Length;
            int[] beyond = new int[edges], within = new int[edges], split = new int[edges];
            foreach ((int edge, float x, float z) in BoundarySamples(polygon))
            {
                // Each edge's own side, and whether its binary32-cast edge would put the sample on the other side.
                FeaturePoint a = polygon.Vertices[edge], b = polygon.Vertices[(edge + 1) % edges];
                GeometrySign own = LatticeOrient(a.X.Value, a.Z.Value, b.X.Value, b.Z.Value, x, z);
                GeometrySign cast = LatticeOrient((float)a.X.Value, (float)a.Z.Value, (float)b.X.Value,
                    (float)b.Z.Value, x, z);
                beyond[edge] += own == GeometrySign.Positive ? 1 : 0;
                within[edge] += own == GeometrySign.Negative ? 1 : 0;
                split[edge] += (own == GeometrySign.Positive) != (cast == GeometrySign.Positive) ? 1 : 0;

                GeometrySign side = OracleInside(polygon, x, z);
                inside += side == GeometrySign.Positive ? 1 : 0;
                boundary += side == GeometrySign.Zero ? 1 : 0;
                outside += side == GeometrySign.Negative ? 1 : 0;
                bool closed = side != GeometrySign.Negative;

                // The axis lies the full probe radius above the plane, inside the inclusive contact band.
                FeaturePoint start = Represented(x, height + 0.25, z);
                FeaturePoint direction = FeaturePoint.Exact(new(0, 0.01f, 0));
                bool contact = SupportPlanarProposal.Contact(polygon, start, direction, 0.25f, 0.0001f);
                Assert.True(closed == contact, $"Contact at ({x:R}, {z:R}) is {contact}, the closed polygon {side}.");
                if (contact) Assert.True(ExhaustiveContact(kernel, polygon, start, direction, 0.25f));

                var proposal = new SupportFootprintProposal(new CapsuleShape(0.25f, 0.01f),
                    new Pose(new(x, 1, z), Quaternion.Identity), 2);
                bool lowest = SupportPlanarProposal.Lowest(polygon, proposal, true, out GeometryInterval fast);
                Assert.True(closed == lowest, $"Lowest at ({x:R}, {z:R}) is {lowest}, the closed polygon {side}.");
                if (!lowest) continue;
                Assert.Equal(GeometrySign.Positive, SupportFootprintProposalGeometry.LowestExhaustive(polygon, proposal,
                    out GeometryInterval exhaustive, sourceHeight: true));
                Assert.Equal(exhaustive, fast);
            }
            for (int edge = 0; edge < edges; edge++)
            {
                string context = $"{(lifted ? "lifted triangle" : "hull top")} edge {edge}: {within[edge]} within, " +
                    $"{beyond[edge]} beyond, {split[edge]} between it and its binary32 cast.";
                Assert.True(within[edge] > 0 && beyond[edge] > 0, context);
                // Every lifted edge moves under the cast, so samples must fall between the two edges.
                if (lifted) Assert.True(split[edge] > 0, context);
            }
        }
        Assert.True(inside > 0 && boundary > 0 && outside > 0, $"{inside} inside, {boundary} on, {outside} outside.");

        // Raising one vertex by the smallest represented step tilts the plane, and the certificate stops applying.
        Vector3[] triangle = [new(-0.75f, 0.5f, -0.5f), new(0.625f, 0.5f, -0.25f), new(-0.125f, 0.5f, 0.875f)];
        double level = Installed(triangle, LiftedCentre).Vertices[0].Y.Value;
        double step = Math.BitIncrement(level) - level;
        foreach (double tilt in new[] { -step, 0, step, 2 * step })
        {
            SupportPolygon tilted = World(triangle.Select((v, i) => (v.X + (double)LiftedCentre.X,
                level + (i == 2 ? tilt : 0), v.Z + (double)LiftedCentre.Z)).ToArray());
            Assert.Equal(tilt == 0, tilted.HasJoinIncidenceCertificate);
            FeaturePoint start = Represented(0, level + 0.25, 0);
            FeaturePoint direction = FeaturePoint.Exact(new(0, 0.01f, 0));
            Assert.Equal(tilt == 0, SupportPlanarProposal.Contact(tilted, start, direction, 0.25f, 0.0001f));
            Assert.True(ExhaustiveContact(kernel, tilted, start, direction, 0.25f));
        }
    }

    [Fact]
    public void ExactPlanarJoinsKeepKernelVerdicts()
    {
        var released = new SupportSegmentPolygon();
        var optimized = new SupportSegmentPolygon();
        double x = 0.25 + Math.ScaleB(1, -40), z = -0.5 + Math.ScaleB(1, -45), y = 0.5 + Math.ScaleB(1, -30);
        double step = Math.BitIncrement(y) - y;
        (double, double, double)[] first = [(x, y, z), (x + 1, y, z), (x, y, z + 1)];
        foreach ((double gap, double tilt, bool shared) in new[]
        {
            (0d, 0d, true), (0d, step, true), (0d, -step, true), (0.00005, 0d, false), (0.0001, 0d, false),
        })
        {
            // The neighbour shares the first face's hypotenuse, or sits past it by the gap.
            (double, double, double)[] second = [(x + 1 + gap, y, z + gap), (x + 1 + gap, y + tilt, z + 1 + gap),
                (x + gap, y, z + 1 + gap)];
            SupportPolygon a = World(first), b = World(second);
            Assert.True(a.HasJoinIncidenceCertificate);
            Assert.Equal(tilt == 0, b.HasJoinIncidenceCertificate);
            Assert.Equal(shared && tilt == 0, SupportJoinIncidence.CommonVertex(a, b));
            GeometrySign expected = ReleasedJoin(a, b, SupportJoinIncidence.ContactBand, released);
            Assert.NotEqual(GeometrySign.Unresolved, expected);
            Assert.Equal(expected, SupportNeighborhoodJoins.Joined(a, b, SupportJoinIncidence.ContactBand, optimized));
            if (shared) Assert.Equal(GeometrySign.Positive, expected);
        }
    }

    [Fact]
    public void ExactPlanarCertificatesKeepEveryCostRowEquivalent()
    {
        using var counters = new WorldFormCounters();
        var gained = new List<string>();
        foreach (SupportCostRow row in CostRows())
        {
            using var scene = new WorldFormScene(row);
            scene.Checked($"{row.Name} cold", out _);
            scene.Checked($"{row.Name} warm", out _);
            scene.CheckedFootprint($"{row.Name} raw members");
            if (row.Name.StartsWith("flat ", StringComparison.Ordinal) ||
                row.Name.StartsWith("stairs ", StringComparison.Ordinal))
                scene.CheckedSubstep($"{row.Name} substep");
            if (row.Name is "flat box" or "flat hull" or "stairs box" or "stairs hull" &&
                GainsExactCertificate(scene))
                gained.Add(row.Name);
        }
        // Hulls sit at Bepu's computed centres. Stairs box treads sit at centres whose sums with their half
        // extents leave binary32, while the flat box is centred on represented binary32 corners.
        Assert.Equal(new[] { "stairs box", "flat hull", "stairs hull" }, gained);
    }

    [Fact]
    public void DoubleOrientationMatchesTheDyadicPath()
    {
        var random = new Random(1334);
        for (int i = 0; i < 512; i++)
        {
            double[] p = [.. Enumerable.Range(0, 6)
                .Select(_ => Math.ScaleB(random.Next(-1 << 20, 1 << 20), -random.Next(0, 60)))];
            // A collinear third point whenever the doubles hold it exactly.
            double t = random.Next(-2, 3);
            foreach ((double cx, double cy) in new[]
                { (p[4], p[5]), (p[0] + (p[2] - p[0]) * t, p[1] + (p[3] - p[1]) * t) })
            {
                GeometrySign expected = BoundedGeometryArithmetic.Orient2DDyadic(p[0], p[1], p[2], p[3], cx, cy);
                Assert.Equal(expected, BoundedGeometryArithmetic.Orient2D(p[0], p[1], p[2], p[3], cx, cy));
                Assert.Equal(expected, LatticeOrient(p[0], p[1], p[2], p[3], cx, cy));
            }
        }
        // Outside the expansion domain the dyadic path decides.
        Assert.Equal(GeometrySign.Positive, BoundedGeometryArithmetic.Orient2D(1e-250, 0, 1, 0, 0, 1));
        Assert.Equal(GeometrySign.Unresolved, BoundedGeometryArithmetic.Orient2D(double.NaN, 0, 1, 0, 0, 1));
    }

    [Fact]
    public void DoubleOrientationDecidesAtTheExpansionDomainEdges()
    {
        double low = Math.ScaleB(1, -200), high = Math.ScaleB(1, 200), normal = Math.ScaleB(1, -1022);
        // At, just inside and just past each end of the expansion domain, and subnormal.
        foreach (double m in new[] { Math.BitDecrement(low), low, Math.BitIncrement(low), Math.BitDecrement(high),
            high, Math.BitIncrement(high), double.Epsilon, Math.BitDecrement(normal), normal })
        {
            Assert.Equal(GeometrySign.Positive, BothOrientations(0, 0, m, 0, 0, m));
            Assert.Equal(GeometrySign.Negative, BothOrientations(0, 0, 0, m, m, 0));
            Assert.Equal(GeometrySign.Zero, BothOrientations(-m, -m, 0, 0, m, m));
        }
        // The area is the largest operand times one step of the smallest, so the sign rests on that step.
        double top = Math.BitDecrement(high);
        Assert.Equal(GeometrySign.Positive, BothOrientations(0, 0, top, low, top, Math.BitIncrement(low)));
        Assert.Equal(GeometrySign.Negative, BothOrientations(0, 0, top, low, top, Math.BitDecrement(low)));
        Assert.Equal(GeometrySign.Positive, BothOrientations(0, 0, high, Math.BitDecrement(low), high, low));
        // Products 2^-2148 and about 2^2046 lie more than 4096 bits apart, so the dyadic cap refuses.
        Assert.Equal(GeometrySign.Unresolved, BothOrientations(0, 0, double.Epsilon, 1e308, 1e308, double.Epsilon));
    }

    // The entry overload and the dyadic path agree, and the agreed sign is returned.
    static GeometrySign BothOrientations(double ax, double ay, double bx, double by, double cx, double cy)
    {
        GeometrySign sign = BoundedGeometryArithmetic.Orient2D(ax, ay, bx, by, cx, cy);
        Assert.Equal(BoundedGeometryArithmetic.Orient2DDyadic(ax, ay, bx, by, cx, cy), sign);
        return sign;
    }

    static (int Contacts, int Heights) PlanarPhases(string name, bool reference)
    {
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == name));
        SupportSample cold = scene.Checked($"{name} cold", out _);
        int contacts = 0, heights = 0;
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (entering && phase == ContactAllocationPhase.SupportPlanarContact) contacts++;
            if (entering && phase == ContactAllocationPhase.SupportPlanarHeight) heights++;
        });
        SupportSample warm;
        try
        {
            if (reference)
                using (SupportReferenceEvaluation.Begin()) warm = scene.Find();
            else warm = scene.Find();
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
        Assert.Equal(cold.ToString(), warm.ToString());
        return (contacts, heights);
    }

    // Whether a published face of the scene certifies only on its represented, non-binary32 coordinates.
    static bool GainsExactCertificate(WorldFormScene scene)
    {
        foreach (StaticHandle owner in scene.Owners())
        {
            // A static outside every query keeps no published form.
            if (scene.Form(owner) is not { } form) continue;
            foreach (InstalledSupportWorldLeaf leaf in form.Leaves)
                foreach (InstalledSupportWorldFace face in leaf.Faces)
                {
                    if (!face.HasJoinIncidenceCertificate) continue;
                    var polygon = new SupportPolygon();
                    polygon.Bind(owner, face);
                    using (SupportReferenceEvaluation.Begin())
                        if (!SupportJoinIncidence.Certify(polygon)) return true;
                }
        }
        return false;
    }

    // A source face installed at an offset centre, as a hull child: world vertices are the exact represented sums.
    static SupportPolygon Installed(Vector3[] source, Vector3 centre)
    {
        Vector3 normal = Vector3.Cross(source[1] - source[0], source[2] - source[0]);
        if (normal.Y < 0) source = [.. source.Reverse()];
        Assert.True(InstalledPoseOperator.TryCreate(new BepuPhysics.RigidPose(centre, Quaternion.Identity),
            out var map));
        var polygon = new SupportPolygon();
        polygon.Begin(new StaticHandle(42), 7, source.Length);
        polygon.BeginSource(map);
        FeaturePoint[] world =
            [.. source.Select(v => FeaturePoint.Add(FeaturePoint.Exact(v), FeaturePoint.Exact(centre)))];
        Assert.All(world, v => Assert.True(v.IsExact));
        for (int i = 0; i < world.Length; i++)
        {
            polygon.SetSource(i, source[i]);
            polygon.Set(i, world[i], FeaturePoint.Subtract(world[(i + 1) % world.Length], world[i]));
        }
        return Finished(polygon);
    }

    // A face of represented world vertices with no source, wound upward.
    static SupportPolygon World((double X, double Y, double Z)[] vertices)
    {
        FeaturePoint[] world = [.. vertices.Select(v => Represented(v.X, v.Y, v.Z))];
        if (FeaturePoint.Cross(FeaturePoint.Subtract(world[1], world[0]), FeaturePoint.Subtract(world[2], world[0]))
            .Y.Bounds.Upper < 0) Array.Reverse(world);
        var polygon = new SupportPolygon();
        polygon.Begin(new StaticHandle(42), 7, world.Length);
        for (int i = 0; i < world.Length; i++)
            polygon.Set(i, world[i], FeaturePoint.Subtract(world[(i + 1) % world.Length], world[i]));
        return Finished(polygon);
    }

    // The normal from the edges, as footprint capture finishes a face.
    static SupportPolygon Finished(SupportPolygon polygon)
    {
        polygon.Finish(FeaturePoint.Cross(polygon.Edges[0],
            FeaturePoint.Subtract(polygon.Vertices[2], polygon.Vertices[0]), exactZeroProducts: true));
        return polygon;
    }

    static FeaturePoint Represented(double x, double y, double z) =>
        new(FeatureNumber.Exact(x), FeatureNumber.Exact(y), FeatureNumber.Exact(z));

    // Represented binary32 axes on each side of every edge, where the edge crosses represented rows or columns
    // spread along it. Different rows put the edge at different offsets within the binary32 spacing.
    static IEnumerable<(int Edge, float X, float Z)> BoundarySamples(SupportPolygon polygon)
    {
        for (int i = 0; i < polygon.Vertices.Length; i++)
        {
            FeaturePoint a = polygon.Vertices[i], b = polygon.Vertices[(i + 1) % polygon.Vertices.Length];
            double dx = b.X.Value - a.X.Value, dz = b.Z.Value - a.Z.Value;
            for (int row = 0; row < SampleRows; row++)
            {
                double fraction = (row + 0.5) / SampleRows;
                bool column = Math.Abs(dz) >= Math.Abs(dx);
                float fixedValue = column ? (float)(a.Z.Value + fraction * dz) : (float)(a.X.Value + fraction * dx);
                double crossing = column ? a.X.Value + (fixedValue - a.Z.Value) * dx / dz
                    : a.Z.Value + (fixedValue - a.X.Value) * dz / dx;
                float near = (float)crossing;
                for (int k = 0; k < 2; k++) near = MathF.BitDecrement(near);
                for (int k = 0; k < 5; k++, near = MathF.BitIncrement(near))
                    yield return column ? (i, near, fixedValue) : (i, fixedValue, near);
            }
        }
    }

    // The closed polygon's side of a point by integer arithmetic on the 2^-80 lattice, independent of the expansion.
    static GeometrySign OracleInside(SupportPolygon polygon, double x, double z)
    {
        bool boundary = false;
        for (int i = 0; i < polygon.Vertices.Length; i++)
        {
            FeaturePoint a = polygon.Vertices[i], b = polygon.Vertices[(i + 1) % polygon.Vertices.Length];
            GeometrySign side = LatticeOrient(a.X.Value, a.Z.Value, b.X.Value, b.Z.Value, x, z);
            if (side == GeometrySign.Positive) return GeometrySign.Negative;
            boundary |= side == GeometrySign.Zero;
        }
        return boundary ? GeometrySign.Zero : GeometrySign.Positive;
    }

    static GeometrySign LatticeOrient(double ax, double ay, double bx, double by, double cx, double cy)
    {
        static BigInteger Scaled(double value)
        {
            double scaled = Math.ScaleB(value, 80);
            Assert.Equal(Math.Truncate(scaled), scaled);
            return new BigInteger(scaled);
        }
        BigInteger area = (Scaled(bx) - Scaled(ax)) * (Scaled(cy) - Scaled(ay)) -
            (Scaled(by) - Scaled(ay)) * (Scaled(cx) - Scaled(ax));
        return area.Sign switch { > 0 => GeometrySign.Positive, < 0 => GeometrySign.Negative, _ => GeometrySign.Zero };
    }

    static bool Binary32(double value) => (double)(float)value == value;
}
