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
    public void NonManifoldEdgeRefusesAmbiguous()
    {
        // Two triangles share the edge from p to q in opposite directions. A third on the same edge is non-manifold.
        Vector3 p = new(0, 0, -1), q = new(0, 0, 1);
        Vector3[] manifold = [p, q, new(1, 0, 0), q, p, new(-1, 0, 0)];
        Pose pose = Lowest(0, FootProbe.Radius, 0);
        using (var scene = new Scene())
        {
            AddMesh(scene, manifold);
            Assert.Equal(CapsuleFeatureStatus.Complete, scene.Query(scene.World, pose, probe: FootProbe).Result.Status);
        }
        using (var scene = new Scene())
        {
            AddMesh(scene, [.. manifold, q, p, new(0, -1, 0)]);
            Query query = scene.Query(scene.World, pose, probe: FootProbe);
            Assert.Equal(CapsuleFeatureStatus.Ambiguous, query.Result.Status);
            Assert.Equal(0, query.Result.Elements);
            Assert.Empty(query.Elements);
        }
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
                OracleVector target = SurfacePoint(random, face);
                // Outward from the chosen face, tilted at random, at a separation inside the band.
                OracleVector tilt = new OracleVector(Signed(random), Signed(random), Signed(random)) * 0.7;
                OracleVector outward = (face.Normal + tilt).Unit;
                if (OracleVector.Dot(outward, face.Normal) < 0.2) outward = face.Normal;
                OracleVector anchor = target + outward * (probe.Radius + Between(random, -5e-5, 5e-5));
                // The axis end nearer the face's plane takes the anchor, so the whole axis lies in front of the face.
                // A short probe under a down-facing face would otherwise reach behind its plane.
                Pose pose = face.Normal.Y >= 0 ? Lowest(anchor.X, anchor.Y, anchor.Z)
                    : Lowest(anchor.X, anchor.Y - probe.Length, anchor.Z);
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

    // An up-facing cells by cells grid of quads centred on the origin, with random heights within +-amplitude.
    static Vector3[] Heightfield(Random random, int cells, float spacing, float amplitude)
    {
        var heights = new float[cells + 1, cells + 1];
        for (int i = 0; i <= cells; i++)
            for (int j = 0; j <= cells; j++) heights[i, j] = amplitude * Signed(random);
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
