using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using static KhaozEngine.Tests.Physics.SupportNeighborhoodTests;

namespace KhaozEngine.Tests.Physics;

// Mesh triangles in the support neighborhood. Every probe pose is computed from the installed geometry in binary64.
// Expectations come from that geometry or from the binary64 oracle, never from a query result.
public class SupportNeighborhoodMeshTests
{
    // SupportCertification.ContactBand, which this project cannot reference.
    const float Band = 0.0001f;
    // FootSupport's axis probe and the foot probe of the default tuning. Both are 0.01 long, as the shared helpers
    // assume.
    static readonly CapsuleShape AxisProbe = new(0.01f, 0.01f);
    static readonly CapsuleShape FootProbe = new(0.2f, 0.01f);

    [Fact]
    public void ValleyLinePublishesBothFaces()
    {
        // The symmetric 10 degree V of FootSupportBoundaryTests: its valley line along x 0 at height 0, each side
        // 2 m along its slope. Quad emits [a, b, c] then [b, d, c], so the triangles holding the valley line are
        // the low X side's second (1) and the high X side's first (2).
        float angle = FootSupportScenes.Radians(10);
        float eaveX = 2 * MathF.Cos(angle), eaveY = 2 * MathF.Sin(angle);
        using var scene = new Scene();
        StaticHandle valley = AddMesh(scene, [
            .. FootSupportScene.Quad(new(-eaveX, eaveY, -2), new(0, 0, -2), new(-eaveX, eaveY, 2), new(0, 0, 2)),
            .. FootSupportScene.Quad(new(0, 0, -2), new(eaveX, eaveY, -2), new(0, 0, 2), new(eaveX, eaveY, 2)),
        ]);
        // The axis probe rests in the V, one radius from both installed planes.
        double radius = AxisProbe.Radius;
        Pose pose = Lowest(0, radius * Math.Sqrt((double)eaveX * eaveX + (double)eaveY * eaveY) / eaveX, 0);
        Query query = scene.Query(scene.World, pose, probe: AxisProbe);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        Assert.Equal(new[] { 1, 2 }, query.Elements.Select(e => e.ElementId));
        Assert.All(query.Elements, e => Assert.Equal(valley, e.Static));
        Assert.Empty(query.Joins);
        AssertOracle(scene, valley, query, pose, AxisProbe);
    }

    [Theory]
    [InlineData(0.01f)]
    [InlineData(0.2f)]
    public void MeshNosingPoseIncludesTheTread(float radius)
    {
        // The #1342 mesh pose: treads 0.35, risers 0.25, the substep axis at x 1.4000002 just past the fifth nosing
        // at x 0.35 * 4. The probe rests on that tread at height 0.25 * 5.
        using FootSupportScene stairs = FootSupportScenes.Stairs(SceneVariant.Mesh, 0.35f, 0.25f, 6);
        const float axis = 1.4000002f;
        float nosing = 0.35f * 4, tread = 0.25f * 5;
        Assert.InRange((double)axis - nosing, 1e-7, 3e-7);
        var probe = new CapsuleShape(radius, 0.01f);
        Pose pose = Lowest(axis, tread + (double)radius, 0);
        Query query = Query.Run(stairs.World, stairs.Lease, pose, probe: probe);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        StaticHandle mesh = stairs["stairs"];
        List<OracleFace> faces = SupportNeighborhoodOracle.InstalledMesh(stairs.World, mesh);
        (OracleVector lower, OracleVector upper) = Segment(pose);
        SupportNeighborhoodOracle.AssertMatches(query.Elements, faces, lower, upper, radius, Band);
        // The fifth tread's triangles: level, at the tread height, starting at the nosing.
        OracleFace[] treads = [.. faces.Where(f => f.Vertices.All(v => v.Y == tread) &&
            f.Vertices.Min(v => v.X) == nosing)];
        Assert.Equal(2, treads.Length);
        Assert.Contains(treads, f => SupportNeighborhoodOracle.IndexOf(query.Elements, f) >= 0);
    }

    [Fact]
    public void PyramidApexJoinsEveryFace()
    {
        // Four faces rising 0.5 over 1 to an apex. Every face meets the apex and lies below the other planes.
        using var scene = new Scene();
        StaticHandle pyramid = AddMesh(scene, Fan(new(0, 0.5f, 0), [new(1, 0, 0), new(0, 0, 1), new(-1, 0, 0),
            new(0, 0, -1)]));
        // Straight above the apex, each face's nearest point is the apex itself.
        Pose pose = Lowest(0, 0.5 + FootProbe.Radius, 0);
        Query query = scene.Query(scene.World, pose, probe: FootProbe);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        Assert.Equal(new[] { 0, 1, 2, 3 }, query.Elements.Select(e => e.ElementId));
        Assert.Equal(new Join[] { new(0, 1), new(0, 2), new(0, 3), new(1, 2), new(1, 3), new(2, 3) }, query.Joins);
        AssertOracle(scene, pyramid, query, pose, FootProbe);
    }

    [Fact]
    public void SaddleJoinsOnlyConvexNeighbours()
    {
        // Four faces about a saddle vertex at the origin. The ring rises to +h on X and falls to -h on Z, so the
        // edges toward +-X are ridges and the edges toward +-Z are valleys. Face i spans ring i to ring i + 1: faces 3
        // and 0 share the +X ridge, faces 1 and 2 the -X ridge. Opposite faces cross each other's planes.
        const float h = 0.25f;
        using var scene = new Scene();
        StaticHandle saddle = AddMesh(scene, Fan(Vector3.Zero, [new(1, h, 0), new(0, -h, 1), new(-1, h, 0),
            new(0, -h, -1)]));
        // The probe rests on both ridges, one radius from each ridge line.
        Pose pose = Lowest(0, FootProbe.Radius * Math.Sqrt(1 + (double)h * h), 0);
        Query query = scene.Query(scene.World, pose, probe: FootProbe);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        Assert.Equal(new[] { 0, 1, 2, 3 }, query.Elements.Select(e => e.ElementId));
        Assert.Equal(new Join[] { new(0, 3), new(1, 2) }, query.Joins);
        AssertOracle(scene, saddle, query, pose, FootProbe);
    }

    [Fact]
    public void BackFaceIsNotAMember()
    {
        // One triangle at y 0.1 under the probe, wound down, then the same triangle wound up as the control.
        Vector3 a = new(-1, 0.1f, -1), b = new(1, 0.1f, -1), c = new(-1, 0.1f, 1);
        Pose pose = Lowest(-0.5, 0.1 + FootProbe.Radius, -0.5);
        using (var scene = new Scene())
        {
            AddMesh(scene, [a, c, b]);
            Query down = scene.Query(scene.World, pose, probe: FootProbe);
            Assert.Equal(CapsuleFeatureStatus.Complete, down.Result.Status);
            Assert.Empty(down.Elements);
        }
        using (var scene = new Scene())
        {
            StaticHandle up = AddMesh(scene, [a, b, c]);
            Query query = scene.Query(scene.World, pose, probe: FootProbe);
            Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
            SupportElement member = Assert.Single(query.Elements);
            Assert.Equal(up, member.Static);
            Assert.Equal(0, member.ElementId);
        }
    }

    [Fact]
    public void NonManifoldEdgeCertifiesEachTriangle()
    {
        // Three triangles on the edge from p to q along Z: level toward +X (0), level toward -X (1), and a fin falling
        // 45 degrees toward -X under triangle 1 whose front faces up and toward -X (2). The probe rests on the edge,
        // which is the nearest point of each, so all three are members. 0 and 1 are coplanar. The fin lies below 0's
        // plane and 0 below the fin's. Triangle 1's far vertex lies above the fin's plane, so 1 and 2 do not join.
        Vector3 p = new(0, 0, -1), q = new(0, 0, 1);
        using var scene = new Scene();
        StaticHandle mesh = AddMesh(scene, [p, new(1, 0, 0), q, q, new(-1, 0, 0), p, q, new(-1, -1, 0), p]);
        Pose pose = Lowest(0, FootProbe.Radius, 0);
        Query query = scene.Query(scene.World, pose, probe: FootProbe);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        Assert.Equal(new[] { 0, 1, 2 }, query.Elements.Select(e => e.ElementId));
        Assert.Equal(new Join[] { new(0, 1), new(0, 2) }, query.Joins);
        AssertOracle(scene, mesh, query, pose, FootProbe);
    }

    [Fact]
    public void DuplicateTriangleIsTwoMembers()
    {
        // The same up-facing triangle twice under the probe. Each copy is a member, and the coplanar copies join.
        Vector3 a = new(-1, 0, -1), b = new(1, 0, -1), c = new(-1, 0, 1);
        using var scene = new Scene();
        StaticHandle mesh = AddMesh(scene, [a, b, c, a, b, c]);
        Pose pose = Lowest(-0.5, FootProbe.Radius, -0.5);
        Query query = scene.Query(scene.World, pose, probe: FootProbe);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        Assert.Equal(new[] { 0, 1 }, query.Elements.Select(e => e.ElementId));
        Assert.Equal(new Join[] { new(0, 1) }, query.Joins);
        AssertOracle(scene, mesh, query, pose, FootProbe);
    }

    [Fact]
    public void LargeMeshQueryReadsOnlyNearbyTriangles()
    {
        // A 100 by 100 cell grid, 20,000 triangles over 20 m, plus one triangle 100 m out. That triangle lies past the
        // 64 m local coordinate bound, so reading it would refuse the query. Read only near the probe, the mesh
        // certifies, and its members match the oracle over every triangle.
        var random = new Random(4438);
        var vertices = new List<Vector3>(Heightfield(random, 100, 0.2f, 0.05f, flat: false));
        Assert.Equal(60_000, vertices.Count);
        vertices.AddRange([new(100, 0, 0), new(101, 0, 0), new(100, 0, 1)]);
        using var scene = new Scene();
        StaticHandle mesh = AddMesh(scene, [.. vertices]);
        List<OracleFace> faces = SupportNeighborhoodOracle.InstalledMesh(scene.World, mesh);
        for (int sample = 0; sample < 6; sample++)
        {
            CapsuleShape probe = sample % 2 == 0 ? AxisProbe : FootProbe;
            OracleFace face = faces[random.Next(20_000)];
            Pose pose = Against(random, face, probe);
            Query query = scene.Query(scene.World, pose, probe: probe);
            Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
            (OracleVector lower, OracleVector upper) = Segment(pose);
            SupportNeighborhoodOracle.AssertMatches(query.Elements, faces, lower, upper, probe.Radius, Band);
            Assert.True(SupportNeighborhoodOracle.IndexOf(query.Elements, face) >= 0,
                $"Sample {sample}: the face the probe was placed against is a member.");
        }
    }

    [Fact]
    public void DegenerateTriangleIsNotAMember()
    {
        // A level floor triangle (0) and a zero-area triangle (1) whose three collinear points lie on the floor right
        // under the probe. The probe rests on the floor, so the zero-area triangle is in reach but has no surface.
        using var scene = new Scene();
        StaticHandle mesh = AddMesh(scene, [new(-1, 0, -1), new(1, 0, -1), new(-1, 0, 1),
            new(-0.5f, 0, -0.8f), new(-0.5f, 0, -0.2f), new(-0.5f, 0, -0.5f)]);
        Query query = scene.Query(scene.World, Lowest(-0.5, FootProbe.Radius, -0.5), probe: FootProbe);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        SupportElement floor = Assert.Single(query.Elements);
        Assert.Equal(mesh, floor.Static);
        Assert.Equal(0, floor.ElementId);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1e-6f)]
    public void FarSliverDoesNotRefuse(float width)
    {
        // A level floor triangle (0) under the probe, and a triangle (1) along the line x + z = 0.3 sqrt 2 at the
        // height of the probe's sphere centre, 0.3 from the axis. Its bounds overlap the probe's, so it is read. Width
        // 0 makes it collinear, a positive width a sliver toward the axis. Either way it is 0.1 beyond the 0.2 probe.
        float span = (float)(0.3 * Math.Sqrt(2));
        float y = FootProbe.Radius;
        using var scene = new Scene();
        StaticHandle mesh = AddMesh(scene, [new(-1, 0, -1), new(1, 0, -1), new(-1, 0, 1),
            new(span, y, 0), new(0, y, span), new(span / 2 - width, y, span / 2 - width)]);
        Query query = scene.Query(scene.World, Lowest(0, FootProbe.Radius, 0), probe: FootProbe);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        SupportElement floor = Assert.Single(query.Elements);
        Assert.Equal(mesh, floor.Static);
        Assert.Equal(0, floor.ElementId);
    }

    [Fact]
    public void UnboundedSliverNextToTheProbeIsSkipped()
    {
        // A level floor triangle (0) and a sliver (1) on its long edge from b to c, which passes through the origin.
        // The sliver's third vertex is 1e-9 off the edge's midpoint, so its exact area is 2e-9 and nonzero. The mesh
        // is yawed 30 degrees: the installed operator's certified coefficients are then intervals about 1e-7 wide,
        // far wider than the sliver's normal, so the normal cannot be bounded away from zero. The probe rests on the
        // floor straight above the origin, touching both triangles there. Yaw keeps the floor level at y 0.
        const float offset = 1e-9f;
        Vector3 a = new(-1, 0, -1), b = new(1, 0, -1), c = new(-1, 0, 1);
        using var scene = new Scene();
        StaticHandle mesh = AddMesh(scene, [a, b, c, c, b, new(offset, 0, offset)],
            new Pose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, FootSupportScenes.Radians(30))));
        Query query = scene.Query(scene.World, Lowest(0, FootProbe.Radius, 0), probe: FootProbe);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        SupportElement floor = Assert.Single(query.Elements);
        Assert.Equal(mesh, floor.Static);
        Assert.Equal(0, floor.ElementId);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteMeshVertexIsRejectedAtInstall(float value)
    {
        // A non-finite vertex would poison the mesh tree's bounds and could prune valid triangles from queries.
        using var scene = new Scene();
        Vector3[] vertices = [new(-1, 0, -1), new(1, 0, -1), new(-1, 0, 1), new(1, 0, 1), new(value, 0, 1), new(1, 0, -1)];
        Assert.Throws<ArgumentException>(() => AddMesh(scene, vertices));
        // Nothing was installed: a finite floor installs and certifies alone.
        StaticHandle floor = AddMesh(scene, [new(-1, 0, -1), new(1, 0, -1), new(-1, 0, 1)]);
        Query query = scene.Query(scene.World, Lowest(-0.5, FootProbe.Radius, -0.5), probe: FootProbe);
        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        Assert.Equal(floor, Assert.Single(query.Elements).Static);
    }

    [Fact]
    public void DenseFanCertifiesWithinCapacity()
    {
        // 96 level triangles around one vertex. The probe rests on the vertex, which every triangle holds, so every
        // triangle is a member and every coplanar pair meets there and joins. The shared query spans hold the
        // maximum element count and its full matrix, well past 96.
        const int count = 96;
        var ring = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            double angle = 2 * Math.PI * i / count;
            ring[i] = new Vector3((float)Math.Cos(angle), 0, (float)Math.Sin(angle));
        }
        using var scene = new Scene();
        StaticHandle fan = AddMesh(scene, Fan(Vector3.Zero, ring));
        Pose pose = Lowest(0, FootProbe.Radius, 0);
        Query query = scene.Query(scene.World, pose, probe: FootProbe);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        Assert.Equal(count, query.Result.Elements);
        Assert.Equal(Enumerable.Range(0, count), query.Elements.Select(e => e.ElementId));
        Assert.Equal(count * (count - 1) / 2, query.Joins.Length);
        AssertOracle(scene, fan, query, pose, FootProbe);
    }

    [Fact]
    public void MeshMembershipMatchesBruteForceOracle()
    {
        var random = new Random(2438);
        int poses = 0;
        for (int field = 0; field < 50; field++)
        {
            using var scene = new Scene();
            Vector3[] vertices = Heightfield(random, 4, Between(random, 0.2, 0.5), Between(random, 0.05, 0.3));
            // Even fields lie level as terrain does. Odd fields take any orientation, so some faces look down.
            Quaternion rotation = field % 2 == 0 ? Quaternion.Identity : Quaternion.Normalize(new Quaternion(
                Signed(random), Signed(random), Signed(random), Signed(random)));
            var position = new Vector3(Signed(random), Signed(random), Signed(random));
            StaticHandle mesh = AddMesh(scene, vertices, new Pose(position, rotation));
            List<OracleFace> faces = SupportNeighborhoodOracle.InstalledMesh(scene.World, mesh);
            for (int sample = 0; sample < 10; sample++, poses++)
            {
                CapsuleShape probe = sample % 2 == 0 ? AxisProbe : FootProbe;
                OracleFace face = faces[random.Next(faces.Count)];
                Pose pose = Against(random, face, probe);
                Query query = scene.Query(scene.World, pose, probe: probe);
                Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
                (OracleVector lower, OracleVector upper) = Segment(pose);
                SupportNeighborhoodOracle.AssertMatches(query.Elements, faces, lower, upper, probe.Radius, Band);
                Assert.True(SupportNeighborhoodOracle.IndexOf(query.Elements, face) >= 0,
                    $"Pose {poses}: the face the probe was placed against is a member.");
            }
        }
        Assert.Equal(500, poses);
    }

    static StaticHandle AddMesh(Scene scene, Vector3[] vertices, Pose? pose = null) =>
        scene.World.AddStatic(new TriangleMeshShape(vertices, [.. Enumerable.Range(0, vertices.Length)]),
            pose ?? Pose.Identity);

    // Triangle i is (centre, ring i, ring i + 1). With the ring turning from +X toward +Z, Cross(C - A, B - A)
    // points up.
    static Vector3[] Fan(Vector3 centre, Vector3[] ring)
    {
        var vertices = new List<Vector3>();
        for (int i = 0; i < ring.Length; i++) vertices.AddRange([centre, ring[i], ring[(i + 1) % ring.Length]]);
        return [.. vertices];
    }

    // The probe placed against a random point of the face: outward, tilted at random, at a separation inside the
    // band. The axis end nearer the face's plane takes that point, so the whole axis lies in front of the face. A
    // short probe under a down-facing face would otherwise reach behind its plane.
    static Pose Against(Random random, OracleFace face, CapsuleShape probe)
    {
        OracleVector target = SurfacePoint(random, face);
        OracleVector tilt = new OracleVector(Signed(random), Signed(random), Signed(random)) * 0.7;
        OracleVector outward = (face.Normal + tilt).Unit;
        if (OracleVector.Dot(outward, face.Normal) < 0.2) outward = face.Normal;
        OracleVector anchor = target + outward * (probe.Radius + Between(random, -5e-5, 5e-5));
        return face.Normal.Y >= 0 ? Lowest(anchor.X, anchor.Y, anchor.Z)
            : Lowest(anchor.X, anchor.Y - probe.Length, anchor.Z);
    }

    // An up-facing cells by cells grid of quads centred on the origin, with random heights within +-amplitude. With
    // flat set, a random 2 by 2 cell block is level, so its eight triangles are exactly coplanar neighbours.
    static Vector3[] Heightfield(Random random, int cells, float spacing, float amplitude, bool flat = true)
    {
        var heights = new float[cells + 1, cells + 1];
        for (int i = 0; i <= cells; i++)
            for (int j = 0; j <= cells; j++) heights[i, j] = amplitude * Signed(random);
        if (flat)
        {
            int i0 = random.Next(cells - 1), j0 = random.Next(cells - 1);
            for (int i = i0; i <= i0 + 2; i++)
                for (int j = j0; j <= j0 + 2; j++) heights[i, j] = heights[i0, j0];
        }
        Vector3 Corner(int i, int j) => new((i - cells / 2f) * spacing, heights[i, j], (j - cells / 2f) * spacing);
        var vertices = new List<Vector3>();
        for (int i = 0; i < cells; i++)
            for (int j = 0; j < cells; j++)
                vertices.AddRange(FootSupportScene.Quad(Corner(i, j), Corner(i + 1, j), Corner(i, j + 1),
                    Corner(i + 1, j + 1)));
        return [.. vertices];
    }

    static void AssertOracle(Scene scene, StaticHandle mesh, Query query, Pose pose, CapsuleShape probe)
    {
        (OracleVector lower, OracleVector upper) = Segment(pose);
        int required = SupportNeighborhoodOracle.AssertMatches(query.Elements,
            SupportNeighborhoodOracle.InstalledMesh(scene.World, mesh), lower, upper, probe.Radius, Band);
        Assert.Equal(query.Elements.Length, required);
    }
}
