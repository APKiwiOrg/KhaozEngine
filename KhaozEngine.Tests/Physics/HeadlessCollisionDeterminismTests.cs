using System.IO;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Determinism contract across the package boundary: a shape the client BAKES from a mesh
/// (<see cref="PropCollisionBake"/>, in KhaozEngine.Render3D) and the same shape a headless server LOADS from
/// the written <c>.coll</c> (<see cref="PropCollisionFormat"/>, in the GPU-free KhaozEngine.Physics) must drive
/// byte-identical <see cref="BepuPhysicsWorld"/> queries, so client prediction reconciles exactly against
/// server authority. Mirrors <c>BepuDeterminismGateTests</c>' run-and-compare style.</summary>
public class HeadlessCollisionDeterminismTests
{
    static (bool rayHit, float rayDist, bool sweepHit, float sweepDist, bool overlap, Vector3 mtv) Query(PhysicsShape shape)
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(shape, Pose.At(new Vector3(0f, 0f, 5f)));
        world.Step(1f / 60f);
        bool rayHit = world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out RayHit rh);
        var cap = new CapsuleShape(0.3f, 0.6f);
        bool sweepHit = world.SweepCapsule(cap, Pose.At(Vector3.Zero), Vector3.UnitZ, 100f, out SweepHit sh);
        bool overlap = world.ComputePenetration(cap, Pose.At(new Vector3(0f, 0f, 5f)), out Vector3 mtv);
        return (rayHit, rh.Distance, sweepHit, sh.Distance, overlap, mtv);
    }

    // Round-trip a baked shape through the render-free Physics format (the server's load path).
    static PhysicsShape RoundTripHeadless(PhysicsShape baked)
    {
        using var ms = new MemoryStream();
        PropCollisionFormat.Write(baked, ms);
        ms.Position = 0;
        return PropCollisionFormat.Read(ms);
    }

    [Theory]
    [InlineData("rock")]      // PropCollisionBake.Bake -> ConvexHullShape
    [InlineData("building")]  // PropCollisionBake.Bake -> TriangleMeshShape
    public void ClientBakedShape_AndHeadlessLoadedShape_QueryIdentically(string kind)
    {
        GltfMesh mesh = kind == "rock" ? TestMeshes.UnitIcosphere() : TestMeshes.BoxRoomWithDoorway();
        PhysicsShape baked = PropCollisionBake.Bake(mesh);   // client path (Render3D)
        PhysicsShape loaded = RoundTripHeadless(baked);      // server path (Physics)
        Assert.Equal(baked.GetType(), loaded.GetType());

        var a = Query(baked);
        var b = Query(loaded);
        Assert.Equal(a.rayHit, b.rayHit);
        Assert.Equal(a.rayDist, b.rayDist);
        Assert.Equal(a.sweepHit, b.sweepHit);
        Assert.Equal(a.sweepDist, b.sweepDist);
        Assert.Equal(a.overlap, b.overlap);
        Assert.Equal(a.mtv, b.mtv);
    }
}
