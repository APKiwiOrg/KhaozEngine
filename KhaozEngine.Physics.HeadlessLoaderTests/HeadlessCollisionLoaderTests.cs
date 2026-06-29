using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Physics.HeadlessLoaderTests;

/// <summary>The headless authoritative-server path: load baked <c>.coll</c> shapes with ONLY the Physics seam
/// + Bepu backend (no Render3D), build a <see cref="BepuPhysicsWorld"/>, and run the same spatial queries the
/// client predicts against. The project reference set (see the csproj) is what proves "no Render3D"; these
/// tests prove the loaded shapes are queryable and byte-identical to the in-memory originals.</summary>
public class HeadlessCollisionLoaderTests : IDisposable
{
    // A unit cube hull centred on the origin (centroid = origin, so a static pose places it directly).
    static ConvexHullShape UnitCubeHull() => new(new[]
    {
        new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
        new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
        new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(0.5f, -0.5f,  0.5f),
        new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(-0.5f, 0.5f,  0.5f),
    });

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ke-headless-coll-" + Guid.NewGuid().ToString("N"));

    public HeadlessCollisionLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    // Write a .coll, disposing the file stream (PropCollisionFormat.Write leaves the caller's stream open).
    static void WriteColl(string path, PhysicsShape shape)
    {
        using FileStream fs = File.Create(path);
        PropCollisionFormat.Write(shape, fs);
    }

    // Run the standard query battery a server/client uses, so two shapes can be compared bit-for-bit.
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

    [Fact]
    public void LoadDirectory_LoadsBakedColl_AndBuildsAQueryableBepuWorld()
    {
        // Bake-equivalent: write shapes straight from the seam (no Render3D mesh path needed to PRODUCE a .coll).
        WriteColl(Path.Combine(_dir, "rock.coll"), UnitCubeHull());
        WriteColl(Path.Combine(_dir, "tree.coll"), new CylinderShape(0.4f, 3f));

        IReadOnlyDictionary<string, PhysicsShape> shapes = PropCollisionFormat.LoadDirectory(_dir);

        Assert.Equal(2, shapes.Count);
        Assert.IsType<ConvexHullShape>(shapes["rock"]);
        Assert.IsType<CylinderShape>(shapes["tree"]);

        // The whole point: a headless server adds a loaded shape to a BepuPhysicsWorld and queries it.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(shapes["rock"], Pose.At(new Vector3(0f, 0f, 5f)));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.3f, 0.6f);
        Assert.True(world.SweepCapsule(cap, Pose.At(Vector3.Zero), Vector3.UnitZ, 100f, out SweepHit sh),
            "sweep should contact the loaded rock hull");
        Assert.True(sh.Distance > 0f && sh.Distance < 5.5f, $"sweep distance {sh.Distance} should be before the hull");

        Assert.True(world.ComputePenetration(cap, Pose.At(new Vector3(0f, 0f, 5f)), out Vector3 mtv),
            "a capsule centred in the hull should report penetration");
        Assert.True(mtv.Length() > 0f, "penetration must yield a non-zero separating translation");
    }

    [Fact]
    public void Load_FromExplicitIdPathPairs_MapsIdsToShapes()
    {
        string p = Path.Combine(_dir, "boulder.coll");
        WriteColl(p, UnitCubeHull());

        IReadOnlyDictionary<string, PhysicsShape> shapes =
            PropCollisionFormat.Load(new[] { ("boulder_01", p) });

        Assert.Single(shapes);
        Assert.IsType<ConvexHullShape>(shapes["boulder_01"]);
    }

    public static IEnumerable<object[]> AllShapeKinds() => new[]
    {
        new object[] { "hull",     (PhysicsShape)UnitCubeHull() },
        new object[] { "cylinder", new CylinderShape(0.4f, 3f) },
        new object[] { "mesh",     new TriangleMeshShape(
            new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f) },
            new[] { 0, 1, 2, 0, 2, 3 }) },
    };

    [Theory]
    [MemberData(nameof(AllShapeKinds))]
    public void HeadlessLoadedShape_ProducesByteIdenticalQueries_ToInMemoryShape(string kind, PhysicsShape original)
    {
        // The determinism contract: a shape the client holds in memory and the same shape a server loads
        // headless from a .coll must drive identical query results, so client prediction reconciles exactly.
        string p = Path.Combine(_dir, kind + ".coll");
        WriteColl(p, original);
        PhysicsShape loaded = PropCollisionFormat.LoadDirectory(_dir)[kind];
        Assert.Equal(original.GetType(), loaded.GetType());

        var a = Query(original);
        var b = Query(loaded);

        Assert.Equal(a.rayHit, b.rayHit);
        Assert.Equal(a.rayDist, b.rayDist);       // exact, not approximate
        Assert.Equal(a.sweepHit, b.sweepHit);
        Assert.Equal(a.sweepDist, b.sweepDist);
        Assert.Equal(a.overlap, b.overlap);
        Assert.Equal(a.mtv, b.mtv);
    }
}
