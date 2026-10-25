// Every expectation below is derived from the installed geometry. The walkable plane is y = 0.7x + 0.7z, ending at
// x 0 in a sloped edge the footprint overhangs from the cliff side.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>#1454. A seat crossing a sloped overhung edge between two directly joined walkable members takes the
/// same walkable-grade rule as one member (CF-4, CF-5).</summary>
public sealed partial class JoinedEdgeContinuityTests(ITestOutputHelper output)
{
    static readonly MoveTuning Tuning = MoveTuning.Default with { StepHeight = 0.4f };
    const float FootRadius = 0.2f, Reach = 0.4f;
    static readonly float CosMaxSlope = MathF.Cos(MathF.PI / 4);

    // Each vertex height is computed once in binary32 from its exact XZ coordinates.
    static float PlaneY(float x, float z) => 0.7f * x + 0.7f * z;
    static Vector3 V(float x, float z) => new(x, PlaneY(x, z), z);
    static Vector3[] Up(Vector3 a, Vector3 b, Vector3 c) => FootSupportScene.Facing(a, b, c, Vector3.UnitY);
    static Vector3[] TriangleA() => Up(V(-1, 0), V(0, -1), V(0, 0));
    static Vector3[] TriangleB() => Up(V(-1, 0), V(0, 0), V(0, 1));

    // One polygon is a box's top face: a rectangle in the plane whose edge lies on x 0 and which covers x [-1, 0]
    // over z [-1, 1]. The box's steep cliff face beyond the edge is joined to it and contributes nothing.
    static readonly Vector3 BoxHalf = new(0.7f, 0.25f, 1.5f * new Vector2(0.7f, 1).Length());

    static Pose BoxPose()
    {
        Vector3 up = Vector3.Normalize(new(-0.7f, 1, -0.7f)), along = Vector3.Normalize(new(0, 0.7f, 1));
        Vector3 across = Vector3.Cross(up, along);
        Quaternion orientation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            across.X, across.Y, across.Z, 0, up.X, up.Y, up.Z, 0, along.X, along.Y, along.Z, 0, 0, 0, 0, 1));
        return new Pose(-BoxHalf.Y * up - BoxHalf.X * across, orientation);
    }

    internal static FootSupportScene Partition(string partition) => partition switch
    {
        "one-polygon" => new FootSupportScene(SceneVariant.Box).Add("a", new BoxShape(BoxHalf), BoxPose()),
        "one-mesh" => new FootSupportScene(SceneVariant.Mesh).Mesh("a", [.. TriangleA(), .. TriangleB()]),
        "split-statics" => new FootSupportScene(SceneVariant.Mesh).Mesh("a", TriangleA()).Mesh("b", TriangleB()),
        // B moved 0.001 along +Z, beyond the unchanged 0.0001 join band, in one mesh or as its own static.
        "gap-mesh" => new FootSupportScene(SceneVariant.Mesh).Mesh("a", [.. TriangleA(), .. GappedB()]),
        "gap-statics" => new FootSupportScene(SceneVariant.Mesh).Mesh("a", TriangleA()).Mesh("b", GappedB()),
        // Three coplanar pieces over z [-1, -0.05], [-0.05, 0.05] and [0.05, 1], two triangles each, in one mesh.
        // Elements 0 and 1 are piece A, 2 and 3 piece B and 4 and 5 piece C.
        "strip" => new FootSupportScene(SceneVariant.Mesh).Mesh("a", [
            .. Up(V(0, -1), V(0, -0.05f), V(-1, -0.05f)), .. Up(V(-1, -1), V(0, -1), V(-1, -0.05f)),
            .. Up(V(-1, -0.05f), V(0, -0.05f), V(0, 0.05f)), .. Up(V(-1, -0.05f), V(0, 0.05f), V(-1, 0.05f)),
            .. Up(V(0, 0.05f), V(0, 1), V(-1, 0.05f)), .. Up(V(-1, 0.05f), V(0, 1), V(-1, 1))]),
        _ => throw new ArgumentOutOfRangeException(nameof(partition)),
    };

    static Vector3[] GappedB() => Up(V(-1, 0.001f), V(0, 0.001f), V(0, 1));

    static string[] Statics(string partition) => partition is "split-statics" or "gap-statics" ? ["a", "b"] : ["a"];

    // The installed faces in binary64: the box from its float half extents and pose, a mesh from the backend.
    static List<OracleFace> Faces(FootSupportScene scene, string partition, string name) =>
        partition == "one-polygon"
            ? SupportNeighborhoodOracle.Box(scene[name], BoxHalf, scene.PoseOf(name))
            : scene.InstalledFaces(name);

    static FootSupportQuery Query(Vector2 axis, float feetY) =>
        new(axis, feetY, FootRadius, Reach, Reach, CosMaxSlope);

    static SupportSample Find(ContactQueryContext queries, Vector2 axis, float feetY) =>
        queries.FindSupport(Query(axis, feetY));

    // The highest installed walkable support in the closed disc: each walkable face's plane maximum over the face
    // and disc, capped by its plane at the axis, evaluated in binary64 from the installed vertices.
    internal readonly record struct Expected(double Height, StaticHandle Static, int ElementId);

    internal static Expected Oracle(FootSupportScene scene, string partition, Vector2 axis, double radius = FootRadius,
        double cosine = 0)
    {
        cosine = cosine == 0 ? CosMaxSlope : cosine;
        Expected best = new(double.NegativeInfinity, default, -1);
        foreach (string name in Statics(partition))
            foreach (OracleFace face in Faces(scene, partition, name))
            {
                OracleVector[] v = face.Vertices;
                OracleVector n = OracleVector.Cross(v[1] - v[0], v[2] - v[0]);
                double length = Math.Sqrt(OracleVector.Dot(n, n));
                if (!(Math.Abs(n.Y) / length >= cosine)) continue;
                double gx = -n.X / n.Y, gz = -n.Z / n.Y;
                double Plane(double x, double z) => v[0].Y + gx * (x - v[0].X) + gz * (z - v[0].Z);
                double? maximum = DiscMaximum(v, axis, radius, gx, gz, Plane);
                if (maximum is not double top) continue;
                double height = Math.Min(top, Plane(axis.X, axis.Y));
                if (height > best.Height) best = new(height, face.Static, face.ElementId);
            }
        Assert.True(double.IsFinite(best.Height));
        return best;
    }

    // A linear function's maximum over a convex polygon's XZ projection intersected with the closed disc lies at a
    // polygon vertex inside the disc, an edge's crossing of the circle, or the disc's own extreme point.
    static double? DiscMaximum(OracleVector[] polygon, Vector2 axis, double radius, double gx, double gz,
        Func<double, double, double> plane)
    {
        double cx = axis.X, cz = axis.Y, r2 = radius * radius;
        double? best = null;
        void Offer(double x, double z)
        {
            double h = plane(x, z);
            if (best is not double b || h > b) best = h;
        }
        int count = polygon.Length;
        double orientation = 0;
        for (int i = 0; i < count; i++)
        {
            OracleVector p = polygon[i], q = polygon[(i + 1) % count];
            orientation += p.X * q.Z - q.X * p.Z;
        }
        bool Inside(double x, double z)
        {
            for (int i = 0; i < count; i++)
            {
                OracleVector p = polygon[i], q = polygon[(i + 1) % count];
                double side = (q.X - p.X) * (z - p.Z) - (q.Z - p.Z) * (x - p.X);
                if (side * orientation < -1e-15) return false;
            }
            return true;
        }
        for (int i = 0; i < count; i++)
        {
            OracleVector p = polygon[i], q = polygon[(i + 1) % count];
            if ((p.X - cx) * (p.X - cx) + (p.Z - cz) * (p.Z - cz) <= r2) Offer(p.X, p.Z);
            double dx = q.X - p.X, dz = q.Z - p.Z, fx = p.X - cx, fz = p.Z - cz;
            double a = dx * dx + dz * dz, b = 2 * (fx * dx + fz * dz), c = fx * fx + fz * fz - r2;
            double discriminant = b * b - 4 * a * c;
            if (a == 0 || discriminant < 0) continue;
            foreach (double t in new[] { (-b - Math.Sqrt(discriminant)) / (2 * a), (-b + Math.Sqrt(discriminant)) / (2 * a) })
                if (t >= 0 && t <= 1) Offer(p.X + t * dx, p.Z + t * dz);
        }
        double g = Math.Sqrt(gx * gx + gz * gz);
        double ex = g > 0 ? cx + radius * gx / g : cx, ez = g > 0 ? cz + radius * gz / g : cz;
        if (Inside(ex, ez)) Offer(ex, ez);
        return best;
    }

    // The public raw query publishes the full released join matrix, independent of the support path's evidence.
    internal sealed class RawFootprint
    {
        internal readonly SupportFootprintMember[] Members = new SupportFootprintMember[SupportFootprintResult.MaximumElements];
        internal readonly ulong[] Joins = new ulong[SupportFootprintResult.MaximumElements *
            SupportFootprintResult.JoinWordsFor(SupportFootprintResult.MaximumElements)];
        internal SupportFootprintResult Result;

        internal static RawFootprint Query(IPhysicsWorld world, IPhysicsQueryLease lease, in FootSupportQuery query)
        {
            var raw = new RawFootprint();
            raw.Result = ((IPhysicsSupportFootprint)world).QuerySupportFootprint(lease,
                FootSupport.DescribeFootprint(query), raw.Members, raw.Joins, QueryFilter.StaticsOnly);
            return raw;
        }

        internal int IndexOf(StaticHandle owner, int element)
        {
            for (int i = 0; i < Result.Members; i++)
                if (Members[i].Static == owner && Members[i].ElementId == element) return i;
            return -1;
        }

        internal int IndexOf(in SupportSample sample) => IndexOf(sample.Static!.Value, sample.FeatureId);

        internal bool Joined(int i, int j) =>
            (Joins[i * Result.JoinWordsPerRow + j / 64] & (1UL << (j % 64))) != 0;
    }

    // Null when the raw footprint at query omits either member, else whether the released predicate joins them.
    internal static bool? RawJoin(FootSupportScene scene, IPhysicsQueryLease lease, in FootSupportQuery query,
        in SupportSample a, in SupportSample b)
    {
        RawFootprint raw = RawFootprint.Query(scene.World, lease, query);
        Assert.Equal(CapsuleFeatureStatus.Complete, raw.Result.Status);
        int i = raw.IndexOf(a), j = raw.IndexOf(b);
        if (i < 0 || j < 0) return null;
        Assert.Equal(raw.Joined(i, j), raw.Joined(j, i));
        return raw.Joined(i, j);
    }

    static bool DirectlyJoined(FootSupportScene scene, in FootSupportQuery query, in SupportSample a, in SupportSample b)
    {
        bool? joined = RawJoin(scene, scene.Lease, query, a, b);
        Assert.NotEqual(false, joined);
        return joined == true;
    }

    static SupportMemberKey Key(in SupportSample sample) => new(sample.Static!.Value, sample.FeatureId);

    // The sample's raw winner is the oracle's and its height encloses the installed support.
    static void AssertOracle(in SupportSample sample, in Expected expected)
    {
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(expected.Static, sample.Static);
        Assert.Equal(expected.ElementId, sample.FeatureId);
        Assert.True(Math.Abs((double)sample.Height - expected.Height) <= sample.HeightError,
            $"{sample.Height:R} against {expected.Height:R} within {sample.HeightError:R}");
    }

    /// <summary>One open read interval over a lease, as a dry tick opens it: a support memo whose arena holds the
    /// samples' continuity evidence until the interval closes.</summary>
    internal sealed class OpenInterval : IDisposable
    {
        internal OpenInterval(IPhysicsWorld world, IPhysicsQueryLease lease, MoveTuning? tuning = null)
        {
            Memo.Open(lease);
            Queries = new DryContactQueryContext(null, null, world, lease, tuning ?? Tuning, null, Memo);
        }

        internal OpenInterval(FootSupportScene scene) : this(scene.World, scene.Lease) { }

        internal SupportRequestMemo Memo { get; } = new();
        internal ContactQueryContext Queries { get; }

        public void Dispose() => Memo.Close();
    }

    // A seat from one oracle start to the target, with the start feet on the independent start height.
    (SupportSample Start, GroundSeatResult Seat, float StartFeetY) Cross(ContactQueryContext queries,
        FootSupportScene scene, string partition, Vector2 from, Vector2 to)
    {
        Expected origin = Oracle(scene, partition, from), expected = Oracle(scene, partition, to);
        float startFeetY = (float)origin.Height;
        SupportSample start = Find(queries, from, startFeetY);
        GroundSeatResult seat = GroundSeat.Resolve(queries, start, from, startFeetY, to, Vector2.Normalize(to - from),
            FootRadius, Tuning, CosMaxSlope);
        output.WriteLine($"{partition} {from} -> {to}: {start.Static}/{start.FeatureId} {start.Height:R} -> " +
            $"{seat.Support.Static}/{seat.Support.FeatureId} {seat.FeetY:R}, step part {seat.StepPart:R}, " +
            $"discrete step {seat.DiscreteStep:R}");
        AssertOracle(start, origin);
        Assert.Equal(SeatOutcome.Seated, seat.Outcome);
        AssertOracle(seat.Support, expected);
        return (start, seat, startFeetY);
    }

    public static TheoryData<string, bool> Crossings()
    {
        var data = new TheoryData<string, bool>();
        foreach (string partition in new[] { "one-polygon", "one-mesh", "split-statics" })
            foreach (bool reverse in new[] { false, true })
                data.Add(partition, reverse);
        return data;
    }

    // The disc reaches 0.02 past the edge onto the face, so its highest point is the edge at longitudinal
    // coordinate z + sqrt(r^2 - x^2). The move rises about 0.07 over 0.1, within the 45 degree walkable grade.
    [Theory]
    [MemberData(nameof(Crossings))]
    public void JoinedEdgeWithinWalkableGradeDoesNotCharge(string partition, bool reverse)
    {
        using FootSupportScene scene = Partition(partition);
        using var interval = new OpenInterval(scene);
        ContactQueryContext queries = interval.Queries;
        Vector2 from = new(0.18f, -0.125f), to = new(0.18f, -0.025f);
        if (reverse) (from, to) = (to, from);
        Expected origin = Oracle(scene, partition, from), expected = Oracle(scene, partition, to);
        double longitudinal = (double)to.Y + Math.Sqrt((double)FootRadius * FootRadius - (double)to.X * to.X);
        Assert.InRange(Math.Abs(expected.Height - 0.7f * longitudinal), 0, 1e-6);
        float startFeetY = (float)origin.Height;
        SupportSample start = Find(queries, from, startFeetY);
        Assert.Equal(SupportStatus.Walkable, start.Status);
        Assert.True(Math.Abs((double)start.Height - origin.Height) <= start.HeightError);

        GroundSeatResult seat = GroundSeat.Resolve(queries, start, from, startFeetY, to, Vector2.Normalize(to - from),
            FootRadius, Tuning, CosMaxSlope);
        output.WriteLine($"{partition} {(reverse ? "reverse" : "forward")}: {start.Static}/{start.FeatureId} " +
            $"{start.Height:R} -> {seat.Support.Static}/{seat.Support.FeatureId} {seat.FeetY:R}, " +
            $"step part {seat.StepPart:R}, discrete step {seat.DiscreteStep:R}");

        if (partition != "one-polygon")
        {
            // Different raw winners, both the oracle's, directly joined by the released predicate.
            Assert.Equal(origin.Static, start.Static);
            Assert.Equal(origin.ElementId, start.FeatureId);
            Assert.Equal(expected.Static, seat.Support.Static);
            Assert.Equal(expected.ElementId, seat.Support.FeatureId);
            Assert.NotEqual((start.Static, start.FeatureId), (seat.Support.Static, seat.Support.FeatureId));
            Assert.True(DirectlyJoined(scene, Query(from, startFeetY), start, seat.Support) |
                DirectlyJoined(scene, GroundSeat.Query(to, start, startFeetY, FootRadius, Tuning, CosMaxSlope), start,
                    seat.Support), "Neither endpoint footprint holds the pair");
        }
        Assert.Equal(SeatOutcome.Seated, seat.Outcome);
        Assert.Equal(SupportStatus.Walkable, seat.Support.Status);
        Assert.Equal(0f, seat.StepPart);
        Assert.Equal(0f, seat.DiscreteStep);
        Assert.True(Math.Abs((double)seat.FeetY - expected.Height) <= seat.Support.HeightError,
            $"{seat.FeetY:R} against {expected.Height:R} within {seat.Support.HeightError:R}");
    }

    // A 0.001 crack and a transitive A-B-C chain both leave the selected pair without a direct join, so the crossing
    // keeps the unjoined residual: at these overhung endpoints the level rise.
    [Theory]
    [InlineData("gap-mesh")]
    [InlineData("gap-statics")]
    [InlineData("strip")]
    public void JoinedEdgeNeedsTheDirectSelectedPair(string partition)
    {
        using FootSupportScene scene = Partition(partition);
        bool strip = partition == "strip";
        Vector2 from = strip ? new(0.18f, -0.2f) : new(0.18f, -0.125f), to = strip ? new(0.18f, 0) : new(0.18f, -0.025f);
        using var interval = new OpenInterval(scene);
        var (start, seat, startFeetY) = Cross(interval.Queries, scene, partition, from, to);
        FootSupportQuery target = GroundSeat.Query(to, start, startFeetY, FootRadius, Tuning, CosMaxSlope);
        RawFootprint raw = RawFootprint.Query(scene.World, scene.Lease, target);
        int a = raw.IndexOf(start), c = raw.IndexOf(seat.Support);
        Assert.True(a >= 0 && c >= 0 && a != c, $"members {a} and {c}");
        Assert.False(raw.Joined(a, c) || raw.Joined(c, a));
        Assert.NotEqual(true, RawJoin(scene, scene.Lease, Query(from, startFeetY), start, seat.Support));
        if (strip)
        {
            // Selected endpoints in pieces A and C, each directly joined to a piece B member, with A-C clear.
            Assert.Contains(start.FeatureId, new[] { 0, 1 });
            Assert.Contains(seat.Support.FeatureId, new[] { 4, 5 });
            bool ab = false, bc = false;
            foreach (int element in new[] { 2, 3 })
            {
                int b = raw.IndexOf(start.Static!.Value, element);
                Assert.True(b >= 0, $"piece B element {element} is not admitted");
                ab |= raw.Joined(a, b);
                bc |= raw.Joined(b, c);
            }
            Assert.True(ab && bc, $"A-B {ab}, B-C {bc}");
        }
        Assert.False(start.Joined.Contains(new(seat.Support.Static!.Value, seat.Support.FeatureId)));
        Assert.False(seat.Support.Joined.Contains(new(start.Static!.Value, start.FeatureId)));
        Assert.False(SupportContinuity.IsContinuous(start, from, seat.Support, to, CosMaxSlope));
        Assert.Equal(GroundPlacement.NotBelow((double)seat.Support.Height - start.Height), seat.StepPart);
        Assert.True(seat.StepPart > 0f);
        Assert.True(seat.DiscreteStep > 0f);
    }

    // Moving from B near the edge to A further along, B leaves the target's expanded admission disc. The preceding
    // footprint still holds the direct join, so either endpoint certifies the pair, without another query.
    [Theory]
    [InlineData("one-mesh", false)]
    [InlineData("one-mesh", true)]
    [InlineData("split-statics", false)]
    [InlineData("split-statics", true)]
    public void JoinedEdgeUsesPrecedingFootprintWhenTheTargetOmitsIt(string partition, bool reverse)
    {
        using FootSupportScene scene = Partition(partition);
        Vector2 near = new(0.18f, -0.075f), far = new(0.18f, -0.275f);
        Vector2 from = reverse ? far : near, to = reverse ? near : far;
        using var interval = new OpenInterval(scene);
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        SupportSample start;
        GroundSeatResult seat;
        float startFeetY;
        long finds;
        try
        {
            ContactQueryContext queries = interval.Queries;
            startFeetY = (float)Oracle(scene, partition, from).Height;
            start = Find(queries, from, startFeetY);
            long before = SupportQueryDiagnostics.Snapshot().Find;
            seat = GroundSeat.Resolve(queries, start, from, startFeetY, to, Vector2.Normalize(to - from), FootRadius,
                Tuning, CosMaxSlope);
            finds = SupportQueryDiagnostics.Snapshot().Find - before;
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
        output.WriteLine($"{partition} {from} -> {to}: {Key(start)} {start.Height:R} -> {Key(seat.Support)} " +
            $"{seat.FeetY:R}, step part {seat.StepPart:R}, discrete step {seat.DiscreteStep:R}");
        Assert.Equal(1, finds);
        AssertOracle(start, Oracle(scene, partition, from));
        AssertOracle(seat.Support, Oracle(scene, partition, to));
        SupportSample b = reverse ? seat.Support : start, a = reverse ? start : seat.Support;
        Assert.NotEqual(Key(a), Key(b));
        // B's nearest point to the far axis is the edge corner at the origin, outside the expanded admission disc.
        Assert.True(Math.Sqrt((double)far.X * far.X + (double)far.Y * far.Y) > FootRadius + SupportCertification.ContactBand);
        FootSupportQuery nearQuery = reverse ? GroundSeat.Query(near, start, startFeetY, FootRadius, Tuning, CosMaxSlope)
            : Query(near, startFeetY);
        FootSupportQuery farQuery = reverse ? Query(far, startFeetY)
            : GroundSeat.Query(far, start, startFeetY, FootRadius, Tuning, CosMaxSlope);
        Assert.Null(RawJoin(scene, scene.Lease, farQuery, a, b));
        Assert.Equal(true, RawJoin(scene, scene.Lease, nearQuery, a, b));
        Assert.True(b.Joined.Contains(Key(a)));
        Assert.False(a.Joined.Contains(Key(b)));
        Assert.True(SupportContinuity.IsContinuous(start, from, seat.Support, to, CosMaxSlope));
        Assert.Equal(SupportStatus.Walkable, seat.Support.Status);
        Assert.Equal(0f, seat.StepPart);
        Assert.Equal(0f, seat.DiscreteStep);
    }

    // The same-member grade arithmetic is unchanged, and a joined edge rising faster than walkable still charges
    // its signed excess.
    [Theory]
    [InlineData("one-mesh", false)]
    [InlineData("one-mesh", true)]
    [InlineData("split-statics", false)]
    [InlineData("split-statics", true)]
    public void JoinedEdgeGradeExcessAndDropsKeepTheClimbBudget(string partition, bool reverse)
    {
        using FootSupportScene scene = Partition(partition);
        SupportSample installed = Find(DryQueries.For(null, null, scene.World, scene.Lease, Tuning),
            new(0.18f, -0.125f), (float)Oracle(scene, partition, new(0.18f, -0.125f)).Height);
        SupportSample Flat(float height, float error) => new(SupportStatus.Walkable, height, error, Vector3.UnitY,
            installed.Static, installed.FeatureId, new(0, height, 0));
        Vector2 a0 = new(0.18f, -0.125f), a1 = new(0.18f, -0.125f + 0.125f);
        // A unit gate allows no grade, so the whole rise is excess, a drop is a negative step and nothing else.
        Assert.Equal(0.25f, GroundSeat.StepPart(Flat(0.25f, 0), Flat(0, 0), a0, 0, a1, 1f));
        Assert.Equal(0.25f, GroundSeat.DiscreteStep(Flat(0, 0), a0, Flat(0.25f, 0), a1, 1f));
        Assert.Equal(0f, GroundSeat.StepPart(Flat(0, 0), Flat(0.25f, 0), a1, 0.25f, a0, 1f));
        Assert.Equal(-0.25f, GroundSeat.DiscreteStep(Flat(0.25f, 0), a1, Flat(0, 0), a0, 1f));
        Assert.Equal(0f, GroundSeat.DiscreteStep(Flat(0, 0), a0, Flat(0.001f, 0), a1, 1f));
        Assert.Equal(MathF.BitIncrement(0.001f), GroundSeat.DiscreteStep(Flat(0, 0), a0,
            Flat(MathF.BitIncrement(0.001f), 0), a1, 1f));
        // Both certified errors are removed before any excess remains.
        Assert.Equal(0f, GroundSeat.StepPart(Flat(0.25f, 0.125f), Flat(0, 0.125f), a0, 0, a1, 1f));
        Assert.Equal(0f, GroundSeat.DiscreteStep(Flat(0, 0.125f), a0, Flat(0.25f, 0.125f), a1, 1f));
        float above = MathF.BitIncrement(0.25f);
        Assert.Equal((float)((double)above - 0.25), GroundSeat.StepPart(Flat(above, 0.125f), Flat(0, 0.125f), a0, 0, a1, 1f));
        Assert.Equal(0f, GroundSeat.DiscreteStep(Flat(0, 0.125f), a0, Flat(above, 0.125f), a1, 1f));

        // The disc closes on the edge from x 0.199, so the edge witness crosses from A to B and rises about 0.047 over
        // about 0.019, beyond the 45 degree grade.
        Vector2 from = new(0.199f, -0.03f), to = new(0.18f, -0.03f);
        if (reverse) (from, to) = (to, from);
        using var interval = new OpenInterval(scene);
        var (start, seat, startFeetY) = Cross(interval.Queries, scene, partition, from, to);
        Assert.NotEqual(Key(start), Key(seat.Support));
        Assert.True(DirectlyJoined(scene, Query(from, startFeetY), start, seat.Support) |
            DirectlyJoined(scene, GroundSeat.Query(to, start, startFeetY, FootRadius, Tuning, CosMaxSlope), start,
                seat.Support), "Neither endpoint footprint holds the pair");
        Assert.True(SupportContinuity.IsContinuous(start, from, seat.Support, to, CosMaxSlope));
        double delta = (double)seat.Support.Height - start.Height;
        double dx = (double)to.X - from.X, dz = (double)to.Y - from.Y, distance = Math.Sqrt(dx * dx + dz * dz);
        double c = CosMaxSlope, grade = Math.Sqrt(1 - c * c) / c * distance;
        double e = Math.Max(0, Math.Abs(delta) - grade - start.HeightError - seat.Support.HeightError);
        Assert.True(e > 0.02, $"excess {e:R}");
        Assert.Equal(delta > 0 ? GroundPlacement.NotBelow(e) : 0f, seat.StepPart);
        Assert.Equal(e > 0.001f ? (float)Math.CopySign(e, delta) : 0f, seat.DiscreteStep);
    }

    // A copied certificate: the source's interval metadata, its exact query bits and every direct raw key in order.
    static string Certificate(in SupportSample sample)
    {
        SupportFootprintResult result = sample.Source.Result;
        FootSupportQuery q = sample.Source.Query;
        var keys = new List<string>();
        for (int i = 0; i < sample.Joined.Count; i++) keys.Add(sample.Joined[i].ToString());
        return $"{result.Status} {result.Members} {result.Witnesses} {result.JoinWordsPerRow} " +
            $"{RuntimeHelpers.GetHashCode(result.QueryWorld)} {RuntimeHelpers.GetHashCode(result.SourceWorld)} " +
            $"{RuntimeHelpers.GetHashCode(result.Lease)} {result.Origin} {result.GeometryGeneration} " +
            $"{result.AxisVerdict} {result.FootVerdict} | {Bits(q.Axis.X)} {Bits(q.Axis.Y)} {Bits(q.FeetY)} " +
            $"{Bits(q.FootRadius)} {Bits(q.ReachUp)} {Bits(q.ReachDown)} {Bits(q.CosMaxSlope)} | " +
            $"{sample.Joined.Selected} {sample.Joined.Count}: {string.Join(";", keys)}";
    }

    static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    // A flat fan of up-facing triangles meeting at the origin with a rim of radius 1.
    internal static FootSupportScene Fan(int triangles)
    {
        Vector3 Rim(int i) => new(MathF.Cos(2 * MathF.PI * (i % triangles) / triangles), 0,
            MathF.Sin(2 * MathF.PI * (i % triangles) / triangles));
        var vertices = new Vector3[3 * triangles];
        for (int i = 0; i < triangles; i++) Up(Vector3.Zero, Rim(i), Rim(i + 1)).CopyTo(vertices, 3 * i);
        return new FootSupportScene(SceneVariant.Mesh).Mesh("fan", vertices);
    }

    // The selected row as certification publishes it: every walkable polygon the raw matrix joins to the winner, in
    // raw order, excluding the winner.
    static List<SupportMemberKey> DirectRow(RawFootprint raw, int selected, float cosine)
    {
        var row = new List<SupportMemberKey>();
        for (int j = 0; j < raw.Result.Members; j++)
        {
            SupportFootprintMember member = raw.Members[j];
            if (j != selected && raw.Joined(selected, j) && member.Kind == SupportElementKind.Polygon &&
                member.UpwardLower > 0 && member.UpwardLower >= cosine)
                row.Add(new(member.Static, member.ElementId));
        }
        return row;
    }

    static void AssertRow(in SupportSample sample, List<SupportMemberKey> row)
    {
        Assert.Equal(row.Count, sample.Joined.Count);
        for (int i = 0; i < row.Count; i++) Assert.Equal(row[i], sample.Joined[i]);
    }

    [Fact]
    public void JoinedEdgeEvidenceSurvivesScratchButNotItsInterval()
    {
        using FootSupportScene scene = Partition("one-mesh");
        BepuPhysicsWorld world = scene.World;
        Vector2 from = new(0.18f, -0.125f), to = new(0.18f, -0.025f);
        float feet = (float)Oracle(scene, "one-mesh", from).Height;
        FootSupportQuery startQuery = Query(from, feet);
        FootSupportQuery targetQuery = startQuery with { Axis = to };
        // A view is created and released outside any read interval.
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        var memo = new SupportRequestMemo();
        IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        memo.Open(lease);
        try { AcrossIntervals(scene, world, view, lease, memo, from, to, startQuery, targetQuery); }
        finally
        {
            memo.Close();
            lease.Dispose();
        }
    }

    void AcrossIntervals(FootSupportScene scene, BepuPhysicsWorld world, IPhysicsWorld view, IPhysicsQueryLease lease,
        SupportRequestMemo memo, Vector2 from, Vector2 to, FootSupportQuery startQuery, FootSupportQuery targetQuery)
    {
        SupportSample Read(IPhysicsWorld receiver, IPhysicsQueryLease interval, in FootSupportQuery query) =>
            FootSupport.Find(null, receiver, interval, query, null, memo);
        SupportSample start = Read(world, lease, startQuery);
        SupportSample target = Read(world, lease, targetQuery);
        string startCertificate = Certificate(start), targetCertificate = Certificate(target);
        output.WriteLine(startCertificate);
        output.WriteLine(targetCertificate);
        Assert.True(SupportContinuity.IsContinuous(start, from, target, to, CosMaxSlope));

        // Sixteen unrelated Finds reuse the thread's scratch and append to the arena. The rows the samples name do
        // not change.
        for (int k = 0; k < 16; k++)
            Read(world, lease, Query(new(-0.6f + 0.02f * k, 0.3f), PlaneY(-0.6f + 0.02f * k, 0.3f)));
        Assert.Equal(startCertificate, Certificate(start));
        Assert.Equal(targetCertificate, Certificate(target));
        Assert.Equal(0f, GroundSeat.StepPart(target, start, from, start.Height, to, CosMaxSlope));
        Assert.Equal(0f, GroundSeat.DiscreteStep(start, from, target, to, CosMaxSlope));

        // Each selected row is exactly the raw matrix's direct walkable neighbours, never a prefix or closure.
        RawFootprint raw = RawFootprint.Query(world, lease, targetQuery);
        AssertRow(target, DirectRow(raw, raw.IndexOf(target), CosMaxSlope));
        // At axis z -0.0873 B lies 0.20005 from the axis, inside the 0.1 mm ring only, so it is context-only and still
        // listed.
        FootSupportQuery ring = startQuery with { Axis = new(0.18f, -0.0873f) };
        SupportSample ringed = Read(world, lease, ring);
        RawFootprint context = RawFootprint.Query(world, lease, ring);
        int b = context.IndexOf(scene["a"], 1);
        Assert.True(b >= 0 && !context.Members[b].HasDiscMaximum, "B is not a context-only member");
        Assert.Equal(new SupportMemberKey(scene["a"], 0), Key(ringed));
        Assert.True(ringed.Joined.Contains(new(scene["a"], 1)));
        AssertRow(ringed, DirectRow(context, context.IndexOf(ringed), CosMaxSlope));

        // An exact repeat is served from the memo with the same handle. A fresh uncached read in the interval writes
        // its own row with the same complete content.
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        SupportSample served, fresh;
        long hits;
        try
        {
            served = Read(world, lease, targetQuery);
            hits = SupportQueryDiagnostics.Snapshot().FindMemoHits;
            using (SupportRequestMemo.Bypass()) fresh = Read(world, lease, targetQuery);
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
        Assert.Equal(1, hits);
        Assert.Equal(target, served);
        Assert.Same(target.Continuity.Arena, served.Continuity.Arena);
        Assert.Equal((target.Continuity.Epoch, target.Continuity.Slot), (served.Continuity.Epoch, served.Continuity.Slot));
        Assert.NotEqual(target.Continuity.Slot, fresh.Continuity.Slot);
        Assert.Equal(targetCertificate, Certificate(served));
        Assert.Equal(targetCertificate, Certificate(fresh));

        // Geometric equality and hashing ignore the evidence. The certificate does not.
        SupportSample bare = target with { Continuity = default };
        Assert.Equal(target, bare);
        Assert.Equal(target.GetHashCode(), bare.GetHashCode());
        Assert.NotEqual(targetCertificate, Certificate(bare));
        Assert.False(SupportContinuity.IsContinuous(bare, to, start, from, CosMaxSlope));

        // Another selected view over the same lease is another receiver, and another thread is not the interval's.
        SupportSample viewed = Read(view, lease, targetQuery);
        Assert.Equal(target, viewed);
        Assert.Throws<InvalidOperationException>(() => SupportContinuity.IsContinuous(start, from, viewed, to, CosMaxSlope));
        Exception? foreign = null;
        var thread = new Thread(() =>
        {
            try { SupportContinuity.IsContinuous(start, from, target, to, CosMaxSlope); }
            catch (Exception error) { foreign = error; }
        });
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(foreign);

        // A disposed lease fails its source authentication while the interval is still open, and a closed interval
        // fails the handle itself.
        long generation = lease.GeometryGeneration;
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => SupportContinuity.IsContinuous(start, from, target, to, CosMaxSlope));
        Assert.Throws<ObjectDisposedException>(() => GroundSeat.DiscreteStep(start, from, target, to, CosMaxSlope));
        memo.Close();
        Assert.Throws<InvalidOperationException>(() => SupportContinuity.IsContinuous(start, from, target, to, CosMaxSlope));
        Assert.Throws<InvalidOperationException>(() => start.Source);
        var entries = (Array)typeof(SupportRequestMemo).GetField("_entries",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memo)!;
        foreach (object entry in entries)
            Assert.True(((SupportSample)entry.GetType().GetProperty("Sample")!.GetValue(entry)!).Continuity.IsEmpty);
        Assert.Equal(0, memo.Arena.Count);

        // A same-generation replacement reopens the same arena and reuses its slots. The closed interval's handles name
        // those slots but not the new epoch, so they cannot read the new rows.
        using (IPhysicsQueryLease replacement = world.AcquireQueryReadLease())
        {
            Assert.Equal(generation, replacement.GeometryGeneration);
            memo.Open(replacement);
            SupportSample freshStart = Read(world, replacement, startQuery);
            SupportSample freshTarget = Read(world, replacement, targetQuery);
            Assert.Same(start.Continuity.Arena, freshStart.Continuity.Arena);
            Assert.Equal(start.Continuity.Slot, freshStart.Continuity.Slot);
            Assert.Equal(target.Continuity.Slot, freshTarget.Continuity.Slot);
            Assert.True(freshStart.Continuity.Epoch > start.Continuity.Epoch);
            Assert.Equal(start, freshStart);
            Assert.True(SupportContinuity.IsContinuous(freshStart, from, freshTarget, to, CosMaxSlope));
            Assert.Throws<InvalidOperationException>(() =>
                SupportContinuity.IsContinuous(start, from, freshTarget, to, CosMaxSlope));
            Assert.Throws<InvalidOperationException>(() =>
                SupportContinuity.IsContinuous(freshStart, from, target, to, CosMaxSlope));
            Assert.Throws<InvalidOperationException>(() => start.Joined);
            memo.Close();
        }

        // Reinstalling the static and a rebase after disposal change the interval, and the old evidence stays dead.
        StaticHandle original = scene["a"];
        world.RemoveStatic(original);
        StaticHandle reinstalled = world.AddStatic(new TriangleMeshShape([.. TriangleA(), .. TriangleB()],
            [0, 1, 2, 3, 4, 5]), Pose.Identity);
        output.WriteLine($"static {original} reinstalled as {reinstalled}");
        using (IPhysicsQueryLease rebuilt = world.AcquireQueryReadLease())
        {
            Assert.NotEqual(generation, rebuilt.GeometryGeneration);
            memo.Open(rebuilt);
            SupportSample again = Read(world, rebuilt, startQuery);
            Assert.Equal(reinstalled, again.Static);
            Assert.Throws<InvalidOperationException>(() =>
                SupportContinuity.IsContinuous(again, from, target, to, CosMaxSlope));
            memo.Close();
        }
        world.Rebase(new(1, 0, 0));
        using (IPhysicsQueryLease rebased = world.AcquireQueryReadLease())
        {
            memo.Open(rebased);
            Assert.Throws<InvalidOperationException>(() =>
                SupportContinuity.IsContinuous(start, from, target, to, CosMaxSlope));
            memo.Close();
        }
    }

    // 256 coplanar triangles meeting at the axis are the raw bound. The selected row holds all 255 others, and a
    // 257th member refuses the whole footprint atomically with no evidence.
    [Fact]
    public void JoinedEdgeRowHoldsEveryDirectNeighbourUpToTheRawBound()
    {
        FootSupportQuery query = new(Vector2.Zero, 0, FootRadius, Reach, Reach, CosMaxSlope);
        using (FootSupportScene full = Fan(SupportFootprintResult.MaximumElements))
        {
            using var interval = new OpenInterval(full);
            SupportSample sample = FootSupport.Find(null, full.World, full.Lease, query, null, interval.Memo);
            RawFootprint raw = RawFootprint.Query(full.World, full.Lease, query);
            Assert.Equal(SupportStatus.Walkable, sample.Status);
            Assert.Equal(SupportFootprintResult.MaximumElements, raw.Result.Members);
            int selected = raw.IndexOf(sample);
            List<SupportMemberKey> row = DirectRow(raw, selected, CosMaxSlope);
            Assert.Equal(SupportFootprintResult.MaximumElements - 1, row.Count);
            AssertRow(sample, row);
            for (int j = 0; j < raw.Result.Members; j++)
                Assert.Equal(j != selected, sample.Joined.Contains(new(raw.Members[j].Static, raw.Members[j].ElementId)));
        }
        using FootSupportScene over = Fan(SupportFootprintResult.MaximumElements + 1);
        using var refusing = new OpenInterval(over);
        SupportSample refused = FootSupport.Find(null, over.World, over.Lease, query, null, refusing.Memo);
        Assert.Equal(SupportStatus.Refused, refused.Status);
        Assert.True(refused.Continuity.IsEmpty);
        Assert.Equal(0, refusing.Memo.Arena.Count);
        RawFootprint capacity = RawFootprint.Query(over.World, over.Lease, query);
        Assert.Equal(CapsuleFeatureStatus.CapacityExceeded, capacity.Result.Status);
        Assert.Equal(SupportFootprintResult.MaximumElements + 1, capacity.Result.RequiredElements);
        Assert.All(capacity.Joins, word => Assert.Equal(0UL, word));
    }

    // Installed geometry gives no scene with an unresolved relevant join, so the collector publishes two admitted
    // walkable floors directly, one an unresolved twin, as SupportFootprintTests.LiveJoins does. Certification reads
    // that join, so the whole footprint refuses: no member, join word or evidence exists to excuse a crossing.
    [Fact]
    public void JoinedEdgeUnresolvedRelevantJoinRefusesTheFootprint()
    {
        SupportFootprintRequest request = SupportFootprintTests.Request();
        SupportPolygon west = JoinFace(1, [new(-1, 0, -1), new(-1, 0, 1), new(0, 0, 1), new(0, 0, -1)]);
        SupportPolygon east = JoinFace(2, [new(0, 0, -1), new(0, 0, 1), new(1, 0, 1), new(1, 0, -1)]);
        Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(west, request, out var westValue));
        Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(east, request, out var eastValue));
        Assert.True(westValue.UpwardLower >= CosMaxSlope && eastValue.UpwardLower >= CosMaxSlope);
        var unresolved = new SupportPolygon();
        unresolved.Begin(east.Static, east.ElementId, east.Vertices.Length);
        for (int i = 0; i < east.Vertices.Length; i++)
            unresolved.Set(i, i == 0 ? new(east.Vertices[i].X, default, east.Vertices[i].Z) : east.Vertices[i],
                east.Edges[i]);
        unresolved.Finish(east.Normal);
        Assert.Equal(GeometrySign.Unresolved,
            SupportNeighborhoodJoins.Joined(west, unresolved, request.ContactBand, new()));

        using var installed = new SupportFootprintScene();
        using IPhysicsQueryLease lease = installed.World.AcquireQueryReadLease();
        var collector = new SupportFootprintCollector();
        Assert.True(collector.Begin(request));
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        Type memberType = typeof(SupportFootprintCollector).GetNestedType("Member", BindingFlags.NonPublic)!;
        var stored = (Array)typeof(SupportFootprintCollector).GetField("_members", Private)!.GetValue(collector)!;
        stored.SetValue(Activator.CreateInstance(memberType, west, default(SupportCurvedLeaf), 0, westValue), 0);
        stored.SetValue(Activator.CreateInstance(memberType, unresolved, default(SupportCurvedLeaf), 0, eastValue), 1);
        typeof(SupportFootprintCollector).GetField("_count", Private)!.SetValue(collector, 2);
        var raw = new RawFootprint();
        raw.Result = collector.Publish(installed.World, lease, raw.Members, raw.Joins, true);
        Assert.Equal(CapsuleFeatureStatus.Unsupported, raw.Result.Status);
        Assert.Equal(0, raw.Result.Members);
        Assert.All(raw.Members, member => Assert.Equal(default, member));
        Assert.All(raw.Joins, word => Assert.Equal(0UL, word));
        Assert.Equal(-1, SupportCertification.CertifyFootprint((IPhysicsSupportFootprint)installed.World, lease,
            raw.Result, raw.Members, raw.Joins, request.Axis, request.CosMaxSlope,
            new FootprintContribution[2 * SupportFootprintResult.MaximumElements]));
        FootSupportQuery query = Query(request.Axis, 0);
        var arena = new SupportContinuityArena();
        arena.Open();
        Assert.Throws<ArgumentException>(() =>
            SupportContinuity.Capture(arena, raw.Result, query, raw.Members, raw.Joins, 0));
        Assert.Equal(0, arena.Count);
    }

    static SupportPolygon JoinFace(int element, Vector3[] vertices)
    {
        var polygon = new SupportPolygon();
        polygon.Begin(new StaticHandle(42), element, vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
        {
            FeaturePoint a = FeaturePoint.Exact(vertices[i]);
            polygon.Set(i, a, FeaturePoint.Subtract(FeaturePoint.Exact(vertices[(i + 1) % vertices.Length]), a));
        }
        polygon.Finish(FeaturePoint.Cross(polygon.Edges[0], FeaturePoint.Subtract(polygon.Vertices[2],
            polygon.Vertices[0])));
        return polygon;
    }
}
