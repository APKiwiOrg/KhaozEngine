using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using static KhaozEngine.Tests.TileWorld.Physics.TileWorldPhysicsTestData;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>The colliders registered in a real Bepu world: rays straight down land where the floor sampler says the
/// ground is, in a rebased world too, and removal takes every static out exactly once.</summary>
public class TileWorldPhysicsQueryTests
{
    const float Millimetre = 1e-3f;

    // Absolute height the down rays start from, above every world here, and how far they reach.
    const float RayTop = 50f, RayReach = 200f;

    // Points in each tile. The first four sit on lattice features: the tile's corner, the middle of its south and
    // west edges, and its centre, which lies on both diagonals. Along a region's first row and column the corner and
    // edge points sit on the seam between two separately posed ground meshes, or on the world's outer boundary when
    // no region is loaded across it. The rest are strictly interior, so every triangle layout also gets hits away
    // from any edge.
    static readonly Vector2[] Offsets =
    {
        new(0f, 0f), new(0.5f, 0f), new(0f, 0.5f), new(0.5f, 0.5f),
        new(0.13f, 0.29f), new(0.71f, 0.18f), new(0.37f, 0.88f),
        new(0.91f, 0.63f), new(0.22f, 0.74f), new(0.64f, 0.41f),
    };

    // Casts straight down at every offset of every tile of the given regions, in the world's own coordinates, and
    // asserts each ray hits a ground collider at the sampler's height within a millimetre once the origin is added
    // back. Points on the loaded world's outer boundary are skipped (see OnOuterBoundary). Returns how many rays it
    // cast and how many points it skipped.
    static (int Cast, int Skipped) AssertDownRaysMatchTheSampler(IPhysicsWorld world, TileWorldColliders colliders,
        TileColliderRegistration registration, params RegionCoord[] regions)
    {
        Dictionary<StaticHandle, TileColliderKind> kinds = registration.Handles
            .Select((handle, i) => (handle, colliders.Colliders[i].Kind))
            .ToDictionary(p => p.handle, p => p.Kind);
        var loaded = new HashSet<RegionCoord>(regions);
        Vector3 origin = world.Origin;
        int rays = 0, skipped = 0;
        foreach (RegionCoord region in regions)
            for (int z = region.OriginZ; z < region.OriginZ + TileRegion.Size; z++)
                for (int x = region.OriginX; x < region.OriginX + TileRegion.Size; x++)
                    foreach (Vector2 offset in Offsets)
                    {
                        if (OnOuterBoundary(x + offset.X, z + offset.Y, loaded))
                        {
                            skipped++;
                            continue;
                        }
                        float absoluteX = TileWorldSpace.WorldX(x + offset.X, 1f);
                        float absoluteZ = TileWorldSpace.WorldZ(z + offset.Y, 1f);
                        Vector3 start = new Vector3(absoluteX, RayTop, absoluteZ) - origin;
                        bool hit = world.Raycast(start, -Vector3.UnitY, RayReach, out RayHit ray);
                        Assert.True(hit, $"the ray at tile ({x + offset.X}, {z + offset.Y}) fell through the ground");
                        Assert.NotNull(ray.Body);
                        Assert.Equal(TileColliderKind.Ground, kinds[ray.Body!.Value]);
                        float expected = colliders.Ground.HeightAt(absoluteX, absoluteZ);
                        Assert.True(MathF.Abs(ray.Point.Y + origin.Y - expected) <= Millimetre,
                            $"tile ({x + offset.X}, {z + offset.Y}): ray at {ray.Point.Y + origin.Y}, sampler at {expected}");
                        Assert.True(ray.Normal.Y > 0f, $"tile ({x + offset.X}, {z + offset.Y}): hit normal {ray.Normal}");
                        rays++;
                    }
        return (rays, skipped);
    }

    // Ruling B14: a ray exactly on an outer edge of the drawn ground may miss. Bepu's ray against a triangle treats
    // its edges as half-open, so of two meshes meeting at a seam one always owns the shared line, but on the loaded
    // world's boundary nothing lies across, and a ray exactly on that line can fall through. The same holds beside
    // an undrawn tile, which the worlds here do not have. Every offset sits in [0, 1) of its tile, so the sweep never
    // generates a point on the outer east or north line. The points it does generate on the outer west and south
    // lines are skipped here, not asserted to miss, so the test pins no backend convention. Undrawn tiles carry
    // blocked boxes and the movement floor is the analytic sampler, so no body stands there. A tile point is on the
    // boundary when any region touching it is not loaded.
    static bool OnOuterBoundary(float tileX, float tileZ, HashSet<RegionCoord> loaded)
    {
        int rx = (int)MathF.Floor(tileX / TileRegion.Size), rz = (int)MathF.Floor(tileZ / TileRegion.Size);
        bool onSeamX = tileX == rx * TileRegion.Size, onSeamZ = tileZ == rz * TileRegion.Size;
        for (int dx = onSeamX ? -1 : 0; dx <= 0; dx++)
            for (int dz = onSeamZ ? -1 : 0; dz <= 0; dz++)
                if (!loaded.Contains(new RegionCoord(rx + dx, rz + dz))) return true;
        return false;
    }

    [Fact]
    public void ADownwardRayHitsTheGroundEverywhereTheSamplerSaysItIs()
    {
        // Every triangle layout the triangulation writes, bumps over a slope, in four regions whose seams run along
        // x = 0 and z = 0 and cross at (0, 0), where four separately posed meshes meet.
        RegionCoord[] regions = { new(0, 0), new(-1, 0), new(0, -1), new(-1, -1) };
        TileWorldColliders colliders = TileWorldColliders.Build(RoughWorld(regions), Catalogs());
        using var world = new BepuPhysicsWorld();
        using TileColliderRegistration registration = colliders.AddTo(world);

        (int cast, int skipped) = AssertDownRaysMatchTheSampler(world, colliders, registration, regions);

        // Skipped: the outer west line x = -64 (two offsets on it in each of 128 tiles) and the outer south line
        // z = -64 (two in each of 128), which share the corner point.
        Assert.Equal(2 * 128 + 2 * 128 - 1, skipped);
        Assert.Equal(regions.Length * TileRegion.TileCount * Offsets.Length - skipped, cast);
    }

    // Not a lattice point and not zero on any axis, so a registration that skips the origin, or applies it twice,
    // lands the ground somewhere the rays do not find it at the sampler's height.
    static readonly Vector3 RebasedOrigin = new(40.5f, 3.25f, -24.75f);

    // The points one region's outer west and south lines skip, two offsets per tile on each, sharing the corner.
    const int SingleRegionBoundary = 2 * TileRegion.Size + 2 * TileRegion.Size - 1;

    [Fact]
    public void ARebasedWorldStillAgreesWithTheSampler()
    {
        RegionCoord region = new(0, 0);
        TileWorldColliders colliders = TileWorldColliders.Build(RoughWorld(region), Catalogs());

        // Registered into a world that was already rebased: AddTo subtracts the origin.
        using (var rebasedFirst = new BepuPhysicsWorld())
        {
            Assert.True(rebasedFirst.CanRebase);
            rebasedFirst.Rebase(RebasedOrigin);
            Assert.Equal(RebasedOrigin, rebasedFirst.Origin);
            using TileColliderRegistration registration = colliders.AddTo(rebasedFirst);
            Assert.Equal(TileRegion.TileCount * Offsets.Length - SingleRegionBoundary,
                         AssertDownRaysMatchTheSampler(rebasedFirst, colliders, registration, region).Cast);
        }

        // Registered first and rebased after: the backend moves the statics, and AddTo must not have baked anything
        // that the move leaves behind.
        using (var rebasedAfter = new BepuPhysicsWorld())
        {
            using TileColliderRegistration registration = colliders.AddTo(rebasedAfter);
            rebasedAfter.Rebase(RebasedOrigin);
            Assert.Equal(RebasedOrigin, rebasedAfter.Origin);
            Assert.Equal(TileRegion.TileCount * Offsets.Length - SingleRegionBoundary,
                         AssertDownRaysMatchTheSampler(rebasedAfter, colliders, registration, region).Cast);
        }
    }

    [Fact]
    public void RemoveLeavesNoStatics()
    {
        TileWorldColliders colliders = TileWorldColliders.Build(EveryKindWorld(), Catalogs());
        using var world = new RecordingWorld(new BepuPhysicsWorld());
        TileColliderRegistration registration = colliders.AddTo(world);
        Assert.Equal(colliders.Colliders.Count, registration.Handles.Count);
        Assert.Equal(registration.Handles.Count, registration.Handles.Distinct().Count());

        // Before: a ray down every tile centre hits one of this registration's statics.
        var handles = new HashSet<StaticHandle>(registration.Handles);
        var hitBodies = new HashSet<StaticHandle>();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
            {
                Assert.True(world.Raycast(new Vector3(x + 0.5f, RayTop, -(z + 0.5f)), -Vector3.UnitY, RayReach,
                    out RayHit hit), $"nothing under tile ({x}, {z}) before removal");
                Assert.Contains(hit.Body!.Value, handles);
                hitBodies.Add(hit.Body!.Value);
            }
        // More than the ground mesh was under the rays: the blocked box, the walls, the bench and the deck.
        Assert.True(hitBodies.Count > 1, $"only {hitBodies.Count} static hit");

        registration.Remove();

        Assert.Equal(registration.Handles.OrderBy(h => h.Value), world.Removed.OrderBy(h => h.Value));
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                Assert.False(world.Raycast(new Vector3(x + 0.5f, RayTop, -(z + 0.5f)), -Vector3.UnitY, RayReach,
                    out _), $"something is still under tile ({x}, {z}) after removal");

        // A second Remove and a Dispose after it are no-ops: nothing removed twice, nothing thrown.
        registration.Remove();
        registration.Dispose();
        Assert.Equal(registration.Handles.Count, world.Removed.Count);
    }

    [Fact]
    public void AFailedAddToRemovesWhatItAddedAndRethrows()
    {
        TileWorldColliders colliders = TileWorldColliders.Build(EveryKindWorld(), Catalogs());
        var refused = new InvalidOperationException("the world is full");
        using var world = new RecordingWorld(new BepuPhysicsWorld()) { FailAddAt = 5, AddFailure = refused };

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => colliders.AddTo(world));

        Assert.Same(refused, thrown);
        Assert.Equal(5, world.Added.Count);
        Assert.Equal(world.Added.OrderBy(h => h.Value), world.Removed.OrderBy(h => h.Value));
        Assert.False(world.Raycast(new Vector3(0.5f, RayTop, -0.5f), -Vector3.UnitY, RayReach, out _),
            "a static is left behind after the failed registration");
    }

    [Fact]
    public void OneFailedRemovalIsRethrownAsItWasAndRetriedAlone()
    {
        TileWorldColliders colliders = TileWorldColliders.Build(EveryKindWorld(), Catalogs());
        using var world = new RecordingWorld(new BepuPhysicsWorld());
        TileColliderRegistration registration = colliders.AddTo(world);
        StaticHandle stuck = registration.Handles[2];
        var refused = new InvalidOperationException("the static is stuck");
        world.FailRemovalOnce.Add(stuck, refused);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(registration.Remove);

        Assert.Same(refused, thrown);
        // Every other handle was still tried, and removed.
        Assert.Equal(registration.Handles.Where(h => h != stuck).OrderBy(h => h.Value),
                     world.Removed.OrderBy(h => h.Value));

        registration.Remove();
        registration.Dispose();

        // The retry removed only the one that failed, and nothing was removed twice.
        Assert.Equal(registration.Handles.OrderBy(h => h.Value), world.Removed.OrderBy(h => h.Value));
    }

    [Fact]
    public void SeveralFailedRemovalsThrowTogetherAfterEveryHandleIsTried()
    {
        TileWorldColliders colliders = TileWorldColliders.Build(EveryKindWorld(), Catalogs());
        using var world = new RecordingWorld(new BepuPhysicsWorld());
        TileColliderRegistration registration = colliders.AddTo(world);
        StaticHandle first = registration.Handles[1], last = registration.Handles[^1];
        var firstRefused = new InvalidOperationException("the first static is stuck");
        var lastRefused = new InvalidOperationException("the last static is stuck");
        world.FailRemovalOnce.Add(first, firstRefused);
        world.FailRemovalOnce.Add(last, lastRefused);

        AggregateException thrown = Assert.Throws<AggregateException>(registration.Remove);

        Assert.Equal(2, thrown.InnerExceptions.Count);
        Assert.Same(firstRefused, thrown.InnerExceptions[0]);
        Assert.Same(lastRefused, thrown.InnerExceptions[1]);
        Assert.Equal(registration.Handles.Count - 2, world.Removed.Count);

        registration.Remove();

        Assert.Equal(registration.Handles.OrderBy(h => h.Value), world.Removed.OrderBy(h => h.Value));
    }

    // A world that records every static added and removed through it, forwards everything to the real backend, and
    // can be told to refuse one add or to fail a removal once.
    sealed class RecordingWorld(IPhysicsWorld inner) : IPhysicsWorld
    {
        public List<StaticHandle> Added { get; } = new();

        // Only the removals the backend carried out.
        public List<StaticHandle> Removed { get; } = new();

        // The zero-based add that throws AddFailure instead of reaching the backend.
        public int? FailAddAt { get; init; }
        public Exception? AddFailure { get; init; }

        // Handles whose next removal throws the given exception, once each.
        public Dictionary<StaticHandle, Exception> FailRemovalOnce { get; } = new();

        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)
        {
            if (Added.Count == FailAddAt) throw AddFailure!;
            StaticHandle handle = inner.AddStatic(shape, pose, material);
            Added.Add(handle);
            return handle;
        }

        public void RemoveStatic(StaticHandle handle)
        {
            if (FailRemovalOnce.Remove(handle, out Exception? failure)) throw failure;
            inner.RemoveStatic(handle);
            Removed.Add(handle);
        }

        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body,
                                            PhysicsMaterial? material = null) =>
            inner.AddDynamic(shape, pose, body, material);
        public void RemoveDynamic(DynamicBodyHandle handle) => inner.RemoveDynamic(handle);
        public Pose GetDynamicPose(DynamicBodyHandle handle) => inner.GetDynamicPose(handle);
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) =>
            inner.GetDynamicVelocity(handle, out linear, out angular);
        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) =>
            inner.SetDynamicVelocity(handle, linear, angular);
        public bool IsAwake(DynamicBodyHandle handle) => inner.IsAwake(handle);
        public ConstraintHandle AddConstraint(in ConstraintDescription description) => inner.AddConstraint(description);
        public void RemoveConstraint(ConstraintHandle handle) => inner.RemoveConstraint(handle);
        public void SetConstraintTarget(ConstraintHandle handle, float target) =>
            inner.SetConstraintTarget(handle, target);
        public void Step(float dt) => inner.Step(dt);
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit,
                            QueryFilter filter = default) =>
            inner.Raycast(origin, direction, maxDistance, out hit, filter);
        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance,
                                 out SweepHit hit, QueryFilter filter = default) =>
            inner.SweepCapsule(capsule, pose, direction, maxDistance, out hit, filter);
        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) =>
            inner.ComputePenetration(capsule, pose, out mtv);
        public Vector3 Origin => inner.Origin;
        public bool CanRebase => inner.CanRebase;
        public void Rebase(Vector3 newOrigin) => inner.Rebase(newOrigin);
        public void Dispose() => inner.Dispose();
    }
}
