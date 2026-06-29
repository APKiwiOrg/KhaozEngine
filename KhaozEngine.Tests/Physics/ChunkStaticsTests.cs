using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Headless tests for <see cref="ChunkStatics"/> (the render-free helper that streams
/// per-prop static bodies into an <see cref="IPhysicsWorld"/> as chunks load/unload).</summary>
public class ChunkStaticsTests
{
    // -------------------------------------------------------------------------
    // Minimal fake IPhysicsWorld: records AddStatic / RemoveStatic calls.
    // -------------------------------------------------------------------------
    sealed class FakePhysicsWorld : IPhysicsWorld
    {
        int _next = 1;
        public readonly List<(PhysicsShape Shape, Pose Pose)> Added = new();
        public readonly List<StaticHandle> Removed = new();

        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)
        {
            Added.Add((shape, pose));
            return new StaticHandle(_next++);
        }

        public void RemoveStatic(StaticHandle handle) => Removed.Add(handle);

        // unused query/step members
        public void Step(float dt) { }
        public bool Raycast(Vector3 o, Vector3 d, float max, out RayHit hit, QueryFilter f = default)
            => throw new NotSupportedException();
        public bool SweepCapsule(CapsuleShape c, Pose p, Vector3 d, float max, out SweepHit hit, QueryFilter f = default)
            => throw new NotSupportedException();
        public bool ComputePenetration(CapsuleShape c, Pose p, out Vector3 mtv)
            => throw new NotSupportedException();
        public void Dispose() { }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    static PropPlacement MakePlacement(string id, float x = 0f, float y = 2f, float z = 0f,
                                       float scale = 1f, float yaw = 0f)
        => new PropPlacement(id, x, y, z, scale, yaw, 0);

    static IReadOnlyDictionary<string, PhysicsShape> ShapesFor(params string[] ids)
    {
        var d = new Dictionary<string, PhysicsShape>();
        foreach (string id in ids)
            d[id] = new BoxShape(new Vector3(0.5f, 1f, 0.5f));
        return d;
    }

    // -------------------------------------------------------------------------
    // Tests: AddAll
    // -------------------------------------------------------------------------

    [Fact]
    public void AddAll_LoadsKStaticsForKMatchingPlacements()
    {
        var world = new FakePhysicsWorld();
        var placements = new[]
        {
            MakePlacement("rock_a", x: 1f, z: 2f),
            MakePlacement("rock_b", x: 3f, z: 4f),
            MakePlacement("rock_a", x: 5f, z: 6f),
        };
        var shapes = ShapesFor("rock_a", "rock_b");
        var handles = new List<StaticHandle>();

        ChunkStatics.AddAll(world, shapes, placements, handles);

        Assert.Equal(3, world.Added.Count);
        Assert.Equal(3, handles.Count);
    }

    [Fact]
    public void AddAll_PlacementWithoutShapeIsSkipped()
    {
        var world = new FakePhysicsWorld();
        var placements = new[]
        {
            MakePlacement("tree_a"),   // no shape in dictionary
            MakePlacement("rock_a"),   // has a shape
        };
        var shapes = ShapesFor("rock_a");
        var handles = new List<StaticHandle>();

        ChunkStatics.AddAll(world, shapes, placements, handles);

        Assert.Single(world.Added);
        Assert.Single(handles);
    }

    [Fact]
    public void AddAll_EmptyPlacementListAddsNothing()
    {
        var world = new FakePhysicsWorld();
        var shapes = ShapesFor("rock_a");
        var handles = new List<StaticHandle>();

        ChunkStatics.AddAll(world, shapes, Array.Empty<PropPlacement>(), handles);

        Assert.Empty(world.Added);
        Assert.Empty(handles);
    }

    [Fact]
    public void AddAll_EmptyShapeDictionaryAddsNothing()
    {
        var world = new FakePhysicsWorld();
        var placements = new[] { MakePlacement("rock_a"), MakePlacement("rock_b") };
        var shapes = new Dictionary<string, PhysicsShape>();
        var handles = new List<StaticHandle>();

        ChunkStatics.AddAll(world, shapes, placements, handles);

        Assert.Empty(world.Added);
        Assert.Empty(handles);
    }

    [Fact]
    public void AddAll_PoseUsesPlacementWorldPosition()
    {
        var world = new FakePhysicsWorld();
        var placements = new[] { MakePlacement("rock_a", x: 10f, y: 3.5f, z: 20f) };
        var shapes = ShapesFor("rock_a");
        var handles = new List<StaticHandle>();

        ChunkStatics.AddAll(world, shapes, placements, handles);

        var (_, pose) = world.Added[0];
        Assert.Equal(10f, pose.Position.X, 4);
        Assert.Equal(3.5f, pose.Position.Y, 4);
        Assert.Equal(20f, pose.Position.Z, 4);
    }

    [Fact]
    public void AddAll_PoseYawEncodesInOrientation()
    {
        var world = new FakePhysicsWorld();
        float yaw = MathF.PI / 2f;
        var placements = new[] { MakePlacement("rock_a", yaw: yaw) };
        var shapes = ShapesFor("rock_a");
        var handles = new List<StaticHandle>();

        ChunkStatics.AddAll(world, shapes, placements, handles);

        Quaternion expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        Quaternion actual = world.Added[0].Pose.Orientation;
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
        Assert.Equal(expected.W, actual.W, 4);
    }

    // -------------------------------------------------------------------------
    // Tests: RemoveAll
    // -------------------------------------------------------------------------

    [Fact]
    public void RemoveAll_RemovesExactlyTheRecordedHandles()
    {
        var world = new FakePhysicsWorld();
        var placements = new[]
        {
            MakePlacement("rock_a"),
            MakePlacement("rock_b"),
        };
        var shapes = ShapesFor("rock_a", "rock_b");
        var handles = new List<StaticHandle>();

        ChunkStatics.AddAll(world, shapes, placements, handles);
        var added = new List<StaticHandle>(handles); // snapshot before remove

        ChunkStatics.RemoveAll(world, handles);

        Assert.Equal(2, world.Removed.Count);
        Assert.Contains(added[0], world.Removed);
        Assert.Contains(added[1], world.Removed);
    }

    [Fact]
    public void RemoveAll_ClearsTheHandleList()
    {
        var world = new FakePhysicsWorld();
        var placements = new[] { MakePlacement("rock_a") };
        var shapes = ShapesFor("rock_a");
        var handles = new List<StaticHandle>();

        ChunkStatics.AddAll(world, shapes, placements, handles);
        ChunkStatics.RemoveAll(world, handles);

        Assert.Empty(handles);
    }

    [Fact]
    public void RemoveAll_EmptyHandleListIsNoOp()
    {
        var world = new FakePhysicsWorld();
        var handles = new List<StaticHandle>();

        ChunkStatics.RemoveAll(world, handles); // must not throw

        Assert.Empty(world.Removed);
    }

    // -------------------------------------------------------------------------
    // Tests: ScaleShape
    // -------------------------------------------------------------------------

    [Fact]
    public void ScaleShape_IdentityScaleReturnsSameInstance()
    {
        var box = new BoxShape(new Vector3(1f, 2f, 3f));
        PhysicsShape result = ChunkStatics.ScaleShape(box, 1f);
        Assert.Same(box, result);
    }

    [Fact]
    public void ScaleShape_BoxHalfExtentsScaled()
    {
        var box = new BoxShape(new Vector3(1f, 2f, 3f));
        var scaled = (BoxShape)ChunkStatics.ScaleShape(box, 2f);
        Assert.Equal(new Vector3(2f, 4f, 6f), scaled.HalfExtents);
    }

    [Fact]
    public void ScaleShape_SphereRadiusScaled()
    {
        var s = new SphereShape(1.5f);
        var scaled = (SphereShape)ChunkStatics.ScaleShape(s, 3f);
        Assert.Equal(4.5f, scaled.Radius, 4);
    }

    [Fact]
    public void ScaleShape_CapsuleDimsScaled()
    {
        var c = new CapsuleShape(0.4f, 1.0f);
        var scaled = (CapsuleShape)ChunkStatics.ScaleShape(c, 2f);
        Assert.Equal(0.8f, scaled.Radius, 4);
        Assert.Equal(2.0f, scaled.Length, 4);
    }

    [Fact]
    public void ScaleShape_ConvexHullPointsScaled()
    {
        var pts = new[] { new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
        var hull = new ConvexHullShape(pts);
        var scaled = (ConvexHullShape)ChunkStatics.ScaleShape(hull, 2f);
        Assert.Equal(new Vector3(2f, 0f, 0f), scaled.Points[0]);
        Assert.Equal(new Vector3(0f, 2f, 0f), scaled.Points[1]);
        // original unchanged
        Assert.Equal(new Vector3(1f, 0f, 0f), pts[0]);
    }

    [Fact]
    public void ScaleShape_TriangleMeshVertsScaled_IndicesPreserved()
    {
        var verts = new[] { new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f) };
        var indices = new[] { 0, 1, 2 };
        var mesh = new TriangleMeshShape(verts, indices);
        var scaled = (TriangleMeshShape)ChunkStatics.ScaleShape(mesh, 3f);
        Assert.Equal(new Vector3(3f, 0f, 0f), scaled.Vertices[0]);
        Assert.Equal(new[] { 0, 1, 2 }, scaled.Indices);
    }

    [Fact]
    public void AddAll_NonUnitScaleIsAppliedToShape()
    {
        var world = new FakePhysicsWorld();
        float scale = 2f;
        var placements = new[] { MakePlacement("rock_a", scale: scale) };
        var baseShape = new BoxShape(new Vector3(1f, 1f, 1f));
        var shapes = new Dictionary<string, PhysicsShape> { ["rock_a"] = baseShape };
        var handles = new List<StaticHandle>();

        ChunkStatics.AddAll(world, shapes, placements, handles);

        var (addedShape, _) = world.Added[0];
        var box = Assert.IsType<BoxShape>(addedShape);
        Assert.Equal(new Vector3(2f, 2f, 2f), box.HalfExtents);
    }
}
