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

    // Points inside each tile, none on a tile edge or a diagonal, so every triangle layout gets interior hits.
    static readonly Vector2[] Offsets =
    {
        new(0.5f, 0.5f), new(0.13f, 0.29f), new(0.71f, 0.18f), new(0.37f, 0.88f),
        new(0.91f, 0.63f), new(0.22f, 0.74f), new(0.64f, 0.41f),
    };

    // Casts straight down at every offset of every tile of the given regions, in the world's own coordinates, and
    // asserts each ray hits a ground collider at the sampler's height within a millimetre once the origin is added
    // back. Returns how many rays it cast.
    static int AssertDownRaysMatchTheSampler(IPhysicsWorld world, TileWorldColliders colliders,
                                             TileColliderRegistration registration, params RegionCoord[] regions)
    {
        Dictionary<StaticHandle, TileColliderKind> kinds = registration.Handles
            .Select((handle, i) => (handle, colliders.Colliders[i].Kind))
            .ToDictionary(p => p.handle, p => p.Kind);
        Vector3 origin = world.Origin;
        int rays = 0;
        foreach (RegionCoord region in regions)
            for (int z = region.OriginZ; z < region.OriginZ + TileRegion.Size; z++)
                for (int x = region.OriginX; x < region.OriginX + TileRegion.Size; x++)
                    foreach (Vector2 offset in Offsets)
                    {
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
        return rays;
    }

    [Fact]
    public void ADownwardRayHitsTheGroundEverywhereTheSamplerSaysItIs()
    {
        // Every triangle layout the triangulation writes, bumps over a slope, in a positive and a negative region.
        RegionCoord[] regions = { new(0, 0), new(-1, -1) };
        TileWorldColliders colliders = TileWorldColliders.Build(RoughWorld(regions), Catalogs());
        using var world = new BepuPhysicsWorld();
        using TileColliderRegistration registration = colliders.AddTo(world);

        int rays = AssertDownRaysMatchTheSampler(world, colliders, registration, regions);

        Assert.Equal(regions.Length * TileRegion.TileCount * Offsets.Length, rays);
    }

    // Not a lattice point and not zero on any axis, so a registration that skips the origin, or applies it twice,
    // lands the ground somewhere the rays do not find it at the sampler's height.
    static readonly Vector3 RebasedOrigin = new(40.5f, 3.25f, -24.75f);

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
            AssertDownRaysMatchTheSampler(rebasedFirst, colliders, registration, region);
        }

        // Registered first and rebased after: the backend moves the statics, and AddTo must not have baked anything
        // that the move leaves behind.
        using (var rebasedAfter = new BepuPhysicsWorld())
        {
            using TileColliderRegistration registration = colliders.AddTo(rebasedAfter);
            rebasedAfter.Rebase(RebasedOrigin);
            Assert.Equal(RebasedOrigin, rebasedAfter.Origin);
            AssertDownRaysMatchTheSampler(rebasedAfter, colliders, registration, region);
        }
    }

    [Fact]
    public void RemoveLeavesNoStatics()
    {
        TileWorldColliders colliders = TileWorldColliders.Build(EveryKindWorld(), Catalogs());
        using var world = new RemovalCountingWorld(new BepuPhysicsWorld());
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

    // A world that records every static removed through it and forwards everything to the real backend.
    sealed class RemovalCountingWorld(IPhysicsWorld inner) : IPhysicsWorld
    {
        public List<StaticHandle> Removed { get; } = new();

        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) =>
            inner.AddStatic(shape, pose, material);

        public void RemoveStatic(StaticHandle handle)
        {
            Removed.Add(handle);
            inner.RemoveStatic(handle);
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
