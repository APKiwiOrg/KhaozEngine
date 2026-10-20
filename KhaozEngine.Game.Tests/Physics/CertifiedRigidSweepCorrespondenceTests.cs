using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;
using static KhaozEngine.Tests.Physics.InstalledPoseOracle;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedRigidSweepCorrespondenceTests
{
    static readonly Vector3 Centre = new(0, 0.3535633981227875f, -0.5f);
    static readonly Vector3 Delta = new(0, 0, 0.125f);
    static readonly Vector3[] SlopedFace = [new(-2, 1022, -2), new(2, 1026, -2), new(-2, 1022, 2)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeshRigidRootCancellationCannotPublishSeparatedForwardModelsAsClear(bool cull)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var handle = world.AddStatic(new TriangleMeshShape(SlopedFace, [0, 1, 2]), Pose.At(new(0, -1024, 0)));
        InstalledSweepGeometry geometry = Capture(world, handle).Mesh!.Triangle(0);
        InstalledSweepGeometry native = geometry.SweepCompanion!;
        Assert.True(geometry.TryPose(geometry.ParentPose, out var originalMap));
        Assert.True(native.TryPose(native.ParentPose, out var nativeMap));
        Assert.True(originalMap.IsExactlyRigid && nativeMap.IsExactlyRigid);
        AssertSeparatedPlane(geometry, Centre, Delta);
        AssertSeparatedPlane(native, Centre, Delta);
        AssertOperationalContact(native, Centre, 0, 1, 2);
        QueryFilter filter = QueryFilter.StaticsOnly with { CullBackFaces = cull };
        Refused(Read(world, Centre, Delta, length: 0, filter: filter));
        Refused(Read(world, Centre, Delta, length: 0, filter: filter, useDimensions: true));
    }

    [Fact]
    public void HullRigidCentroidCancellationCannotPublishClear()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var handle = world.AddStatic(new ConvexHullShape([.. SlopedFace, new(0, 1020, 0)]), Pose.At(new(0, -1024, 0)));
        InstalledSweepGeometry geometry = Capture(world, handle);
        Assert.NotEqual(Vector3.Zero, geometry.LocalPose.Position);
        Assert.True(geometry.TryPose(geometry.ParentPose, out var map) && map.IsExactlyRigid);
        int[] face = TopFace(geometry);
        AssertSeparatedPlane(geometry, Centre, Delta, face[0], face[1], face[2]);
        AssertOperationalContact(geometry, Centre, face[0], face[1], face[2]);
        Refused(Read(world, Centre, Delta, length: 0));
        Refused(Read(world, Centre, Delta, length: 0, useDimensions: true));
    }

    [Fact]
    public void RotatedBoxRigidRootSubtractionCannotPublishClear()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var handle = world.AddStatic(new BoxShape(new(2, 2, 1024)), new Pose(new(1024, 0, 0), new(0.5f, 0.5f, 0.5f, 0.5f)));
        InstalledSweepGeometry geometry = Capture(world, handle);
        Assert.True(geometry.TryPose(geometry.ParentPose, out var map) && map.IsExactlyRigid);
        Vector3 centre = new(-0.25001f, 0, 0), delta = new(0, 0.125f, 0);
        // The cyclic map sends local Z to world X. The near face is exactly world X = 0.
        Assert.True(-(double)centre.X > 0.25);
        Vector3 local = OperationalCentre(geometry, centre);
        Assert.Equal(-1024.25f, local.Z);
        Assert.Equal(0.25f, -geometry.Half.Z - local.Z);
        Refused(Read(world, centre, delta, length: 0));
        Refused(Read(world, centre, delta, length: 0, useDimensions: true));
    }

    [Fact]
    public void HullCentroidAdditionAlsoLosesTheImmediateSeparatedNeighbor()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var handle = world.AddStatic(CertifiedHullSweepTests.Tetrahedron(), Pose.Identity);
        InstalledSweepGeometry geometry = Capture(world, handle);
        Vector3 centre = new(MathF.BitDecrement(-0.25f), 0.5f, 0.5f);
        Assert.Equal(new Vector3(0.5f), geometry.LocalPose.Position);
        Assert.True(-(double)centre.X > 0.25);
        Assert.Equal(-0.75f, OperationalCentre(geometry, centre).X);
        Refused(Read(world, centre, new(0, 0.125f, 0), length: 0));
    }

    [Theory]
    [InlineData("triangle")]
    [InlineData("node")]
    [InlineData("leaf")]
    [InlineData("metanode")]
    public void MeshPoseCacheCannotHideChangedGeometryOrBacklinks(string fault)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var handle = world.AddStatic(CertifiedMeshSweepTests.Quad(), Pose.Identity);
        Vector3 start = new(-0.5f, 2, -0.25f), delta = new(0, -4, 0);
        HitContains(Read(world, start, delta), 1.25, 1.25);
        Mutate(world, _ => { }, handle.Value);
        HitContains(Read(world, start, delta), 1.25, 1.25);
        var shape = Static(world, handle).Shape;
        Mutate(world, simulation =>
        {
            ref Mesh mesh = ref simulation.Shapes.GetShape<Mesh>(shape.Index);
            switch (fault)
            {
                case "triangle": mesh.Triangles[0].A.X += 0.125f; break;
                case "node": mesh.Tree.Nodes[0].A.Max.X = float.NaN; break;
                case "leaf": mesh.Tree.Leaves[0] = new(mesh.Tree.Leaves[0].NodeIndex, 1 - mesh.Tree.Leaves[0].ChildIndex); break;
                case "metanode": mesh.Tree.Metanodes[0].Parent = 0; break;
                default: throw new InvalidOperationException(fault);
            }
        }, handle.Value);
        Refused(Read(world, start, delta, exclusions: [handle]));
    }

    static InstalledSweepGeometry Capture(BepuPhysicsWorld world, KhaozEngine.Physics.StaticHandle handle) =>
        ((Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics"))[handle.Value]!;

    static int[] TopFace(InstalledSweepGeometry geometry)
    {
        var result = new List<int>();
        for (int i = 0; i < geometry.VertexCount; i++)
        {
            Vector3 point = geometry.Vertex(i) + geometry.LeafPose.Position;
            if (Math.Abs(point.Y - point.X) < 0.01f) result.Add(i);
        }
        Assert.Equal(3, result.Count);
        return result.ToArray();
    }

    static void AssertSeparatedPlane(InstalledSweepGeometry geometry, Vector3 centre, Vector3 delta, int a = 0, int b = 1, int c = 2)
    {
        V translation = V.From(geometry.LeafPose.Position);
        V origin = V.From(geometry.Vertex(a)) + translation;
        V normal = V.Cross(V.From(geometry.Vertex(b)) - V.From(geometry.Vertex(a)), V.From(geometry.Vertex(c)) - V.From(geometry.Vertex(a)));
        R distance = V.Dot(V.From(centre) - origin, normal);
        Assert.True(distance * distance > V.Dot(normal, normal) * R.From(0.25f) * R.From(0.25f));
        Assert.Equal(R.Zero, V.Dot(V.From(delta), normal));
    }

    static void AssertOperationalContact(InstalledSweepGeometry geometry, Vector3 centre, int a, int b, int c)
    {
        V point = V.From(OperationalCentre(geometry, centre)), origin = V.From(geometry.Vertex(a));
        V ab = V.From(geometry.Vertex(b)) - origin, ac = V.From(geometry.Vertex(c)) - origin;
        V normal = V.Cross(ab, ac);
        R n2 = V.Dot(normal, normal), distance = V.Dot(point - origin, normal);
        Assert.True(distance * distance <= n2 * R.From(0.25f) * R.From(0.25f));
        V projected = point - normal * (distance / n2), offset = projected - origin;
        R aa = V.Dot(ab, ab), bb = V.Dot(ac, ac), cross = V.Dot(ab, ac);
        R determinant = aa * bb - cross * cross;
        R u = (V.Dot(offset, ab) * bb - V.Dot(offset, ac) * cross) / determinant;
        R v = (V.Dot(offset, ac) * aa - V.Dot(offset, ab) * cross) / determinant;
        Assert.True(u > R.Zero && v > R.Zero && u + v < R.One);
    }

    static Vector3 OperationalCentre(InstalledSweepGeometry geometry, Vector3 centre)
    {
        RigidPose root = geometry.ParentPose;
        var q = new QuaternionWide { X = new(root.Orientation.X), Y = new(root.Orientation.Y), Z = new(root.Orientation.Z), W = new(root.Orientation.W) };
        Vector3Wide.Broadcast(root.Position - centre, out var rootOffset);
        Vector3Wide.Broadcast(geometry.LocalPose.Position, out var child);
        QuaternionWide.TransformWithoutOverlap(child, q, out var rotated);
        Vector3Wide.Add(rootOffset, rotated, out var offset);
        QuaternionWide.Conjugate(q, out var conjugate);
        QuaternionWide.TransformWithoutOverlap(offset, conjugate, out var local);
        return new(-local.X[0], -local.Y[0], -local.Z[0]);
    }
}
