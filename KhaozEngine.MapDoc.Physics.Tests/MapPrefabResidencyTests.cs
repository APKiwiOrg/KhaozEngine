using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public class MapPrefabResidencyTests
{
    static readonly MapWorldGrids Grids = new(64, 1, 4, Vector2.Zero);
    static readonly MapNavTileOptions Nav = new(1, "prefab", "physical-clearance-only");

    [Fact]
    public void RotatedBounds_NameEveryCrossedTileOnce()
    {
        var f = PrefabPhysicsFixtures.TightBounds(PrefabFixtures.DryTransformed(0.371f, 1.2f, new(64, 5, -64)));
        var world = PrefabPhysicsFixtures.Build(f);
        var support = MapPrefabSupportBounds.Bounds(world.Document.EffectiveResources!);
        Assert.True(support.MinZ < -64 && support.MaxX > 64);
        var entries = MapResidencyOwnership.Build(world, Grids);
        Assert.Equal(entries.Count, entries.Select(e => e.OwnerId).Distinct().Count());
        var chunk = entries.Single(e => world.Terrain.Chunks.Any(c => c.ChunkId == e.OwnerId && c.TriangleOwners.Any(f => f.OwnerId == "a/floor")));
        Assert.Contains(new MapTileCoord(0, -2), chunk.Membership);
        Assert.Contains(new MapTileCoord(1, -2), chunk.Membership);
        Assert.Equal(chunk.Membership.Count, chunk.Membership.Distinct().Count());
        var tiles = MapNavTiling.Partition(world, Grids, Nav);
        Assert.Equal(tiles.Count, tiles.Select(t => t.Coord).Distinct().Count());
        Assert.All(entries, e => Assert.All(e.NavTiles, t => Assert.Contains(tiles, tile => tile.Coord == t)));
    }

    [Fact]
    public void RemovedInstance_InvalidatesOldTiles()
    {
        var f = PrefabPhysicsFixtures.TightBounds(PrefabFixtures.DryTransformed(0.371f, 1.2f, new(64, 5, -64)));
        var before = PrefabPhysicsFixtures.Build(f);
        f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with { Removed = true } }, []);
        var after = PrefabPhysicsFixtures.Build(f);
        var effects = new MapNativeEditEffects(before.Bounds, after.Bounds, [], [], [], [],
            MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency);
        var affected = MapResidencyOwnership.Affected(before, after, Grids, effects);
        Assert.All(before.Statics.Where(s => !after.Statics.Any(n => n.OwnerId == s.OwnerId)), s => Assert.Contains(s.OwnerId, affected.Owners));
        var oldTiles = MapNavTiling.Partition(before, Grids, Nav);
        var newTiles = MapNavTiling.Partition(after, Grids, Nav).ToDictionary(t => t.Coord);
        var changed = oldTiles.Where(t => !newTiles.TryGetValue(t.Coord, out var n) || n.GeometryDigest != t.GeometryDigest).Select(t => t.Coord).ToArray();
        Assert.NotEmpty(changed);
        var invalidated = MapNavTiling.AffectedTiles(before, after, Grids, Nav, effects);
        Assert.All(changed, t => Assert.Contains(t, invalidated));
    }

    [Fact]
    public void TerrainSupportReseat_HasAReachableNeighborCounterexample()
    {
        var (before, after, patchEffects) = PrefabPhysicsFixtures.SupportReseat();
        Assert.Equal(0, before.Placements.Single().WorldPose.Position.Y);
        Assert.Equal(1, after.Placements.Single().WorldPose.Position.Y);
        Assert.Equal(150, before.Placements.Single().ColliderBounds!.Value.MaxX - before.Placements.Single().ColliderBounds!.Value.MinX);
        var oldTiles = MapNavTiling.Partition(before, Grids, Nav).ToDictionary(t => t.Coord);
        var changed = MapNavTiling.Partition(after, Grids, Nav).Where(t => oldTiles[t.Coord].GeometryDigest != t.GeometryDigest).Select(t => t.Coord).ToArray();
        var narrow = MapNavTiling.AffectedTiles(after, Grids, Nav, patchEffects);
        Assert.Contains(new MapNavTileCoord(2, 0), changed);
        Assert.DoesNotContain(new MapNavTileCoord(2, 0), narrow);
        // Task 10 emits this widening from preparation. Here the consumer proves that it covers the real change.
        var wide = patchEffects with
        {
            OldBounds = patchEffects.OldBounds!.Value.Union(before.Placements.Single().ColliderBounds!.Value),
            NewBounds = patchEffects.NewBounds!.Value.Union(after.Placements.Single().ColliderBounds!.Value)
        };
        Assert.All(changed, t => Assert.Contains(t, MapNavTiling.AffectedTiles(before, after, Grids, Nav, wide)));
    }

    [Fact]
    public void FarColliderBounds_ContainPreciseRegistrationGeometry()
    {
        var f = NativeWorldFixtures.Placed("centimetre-crate");
        f.Document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Placement,
            "centimetre-crate", new(30016.0001, 5, -30016.0001, 0, 1), new string('a', 64)) };
        var world = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        var collider = world.Placements.Single().ColliderBounds!.Value;
        Assert.True(collider.MaxX >= 30016.50009);
        Assert.True(collider.MinZ <= -30016.50009);
        var reloaded = MapWorldBuilder.Build(MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)), f.Assets, NativeWorldFixtures.Options());
        Assert.Equal(collider, reloaded.Placements.Single().ColliderBounds);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(-1, -1)]
    public void QuantumSupportExtent_NamesTheTileBeyondRoundedSubmission(int sign, int crossedX)
    {
        const double quantum = 1d / 1048576;
        var f = PrefabFixtures.Create("roomless");
        var patch = PrefabFixtures.Patch("seam-floor", 16, 16, 0);
        var surface = PrefabFixtures.Surface("seam-floor", KhaozEngine.MapDoc.Surfaces.MapSurfaceRole.SupportFloor);
        f.Document.Surfaces.Patches.Add(patch.Key, patch);
        f.Document.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = KhaozEngine.MapDoc.Surfaces.MapSurfaceSemantics.SurfaceDigest(surface,
                new[] { new System.Collections.Generic.KeyValuePair<KhaozEngine.MapDoc.Surfaces.MapPatchKey, string>(patch.Key,
                    KhaozEngine.MapDoc.Surfaces.MapSurfaceSemantics.PatchDigest(patch)) })
        });
        f.Document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Surface,
            "seam-floor", new(sign * quantum, 0, 0, 0, 4), new string('a', 64)) };
        f.Document.TileSize = 64;
        f.Document.Bounds = new() { MinX = 0, MinZ = -64, MaxX = 64, MaxZ = 0 };
        f.Document.PlayableBounds = f.Document.Bounds;
        var world = PrefabPhysicsFixtures.Build(f);
        var chunk = Assert.Single(world.Terrain.Chunks);
        Assert.Equal(512, chunk.TriangleOwners.Count);
        Assert.Equal(32, chunk.Anchor.X);
        var descriptor = Assert.Single(world.Statics);
        double exactMin = sign * quantum, exactMax = 64 + sign * quantum;
        Assert.True(descriptor.Bounds.MinX <= exactMin, $"static minimum {descriptor.Bounds.MinX:R}, exact {exactMin:R}");
        Assert.True(descriptor.Bounds.MaxX >= exactMax, $"static maximum {descriptor.Bounds.MaxX:R}, exact {exactMax:R}");
        Assert.True(world.Bounds.MinX <= exactMin);
        Assert.True(world.Bounds.MaxX >= exactMax);
        var membership = Assert.Single(MapResidencyOwnership.Build(world, Grids)).Membership;
        Assert.Contains(membership, t => t.X == crossedX);
        Assert.Contains(MapNavTiling.Partition(world, Grids, Nav), t => t.Coord.X == crossedX);
        var reload = PrefabPhysicsFixtures.Build(new(MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)), f.Assets));
        Assert.Equal(world.Bounds, reload.Bounds);
    }

    [Fact]
    public void RationalSupportBounds_RoundOutwardInsteadOfToNearestDouble()
    {
        var f = PrefabFixtures.Create("roomless");
        var patch = PrefabFixtures.Patch("rational-floor", 1, 1, 0);
        f.Document.Surfaces.Patches.Add(patch.Key, patch);
        var surface = PrefabFixtures.Surface("rational-floor", KhaozEngine.MapDoc.Surfaces.MapSurfaceRole.SupportFloor);
        f.Document.Surfaces.Refs.Add(surface with { Frame = surface.Frame with { CellUnitMetres = new(1, 3) } });
        PrefabFixtures.SealWorldParent(f.Document);
        var box = MapPrefabSupportBounds.Bounds(PrefabFixtures.Effective(f));
        var third = KhaozEngine.MapDoc.Water.MapWideRational.From(new KhaozEngine.MapDoc.Surfaces.MapExactValue(1, 3));
        Assert.True(KhaozEngine.MapDoc.Water.MapWideRational.FromDouble(box.MaxX).CompareTo(third) >= 0);
        Assert.True(KhaozEngine.MapDoc.Water.MapWideRational.FromDouble(box.MinZ).CompareTo(third.Negate()) <= 0);
    }

    [Fact]
    public void WideNumeratorSupportBounds_ContainTheExactRationalAfterConversion()
    {
        var exact = new KhaozEngine.MapDoc.Surfaces.MapExactValue(2305843009213694079, 4611686018427388417);
        var bounds = MapPrefabSupportBounds.Of(new[] { new KhaozEngine.MapDoc.Surfaces.MapExactPoint(exact, default, exact.Negate()) });
        var rational = KhaozEngine.MapDoc.Water.MapWideRational.From(exact);
        Assert.True(KhaozEngine.MapDoc.Water.MapWideRational.FromDouble(bounds.MaxX).CompareTo(rational) >= 0);
        Assert.True(KhaozEngine.MapDoc.Water.MapWideRational.FromDouble(bounds.MinZ).CompareTo(rational.Negate()) <= 0);
    }

    [Theory]
    [InlineData(30336, 0, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(30016, 5, -30016)]
    [InlineData(29952, 0, -29952)]
    [InlineData(1000000, 1000000, 1000000)]
    [InlineData(-1000000, -1000000, -1000000)]
    public void ColliderBounds_ContainInstalledFacesAtEveryPermittedOrigin(float x, float y, float z)
    {
        const double px = 30016.00002, py = 5.00002, pz = -30016.00002;
        var f = NativeWorldFixtures.Placed("centimetre-crate");
        f.Document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Placement,
            "centimetre-crate", new(px, py, pz, 0, 1), new string('a', 64)) };
        var world = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        var box = world.Placements.Single().ColliderBounds!.Value;
        var origin = new Vector3(x, y, z);
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(origin);
        using var registration = MapPhysicsRegistration.Register(world, physics);
        // Independent calculation of the API's documented frame-local origin, followed by a real backend ray.
        double ax = Math.Round(x / 128d) * 128, az = Math.Round(z / 128d) * 128;
        var local = new Vector3((float)(px - ax) - (float)(x - ax), (float)py - y,
            (float)(pz - az) - (float)(z - az));
        var centre = local + new Vector3(0, 0.5f, 0);
        foreach (var direction in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
        {
            Assert.True(physics.Raycast(centre + direction, -direction, 2, out var hit));
            Assert.True(registration.TryOwner(hit.Body!.Value, out var owner));
            Assert.Equal("centimetre-crate", owner.OwnerId);
            double wx = (double)x + hit.Point.X, wy = (double)y + hit.Point.Y, wz = (double)z + hit.Point.Z;
            Assert.True(wx >= box.MinX && wx <= box.MaxX, $"origin {origin}, installed X {wx:R}, bounds [{box.MinX:R}, {box.MaxX:R}]");
            Assert.True(wy >= box.MinY && wy <= box.MaxY, $"origin {origin}, installed Y {wy:R}, bounds [{box.MinY:R}, {box.MaxY:R}]");
            Assert.True(wz >= box.MinZ && wz <= box.MaxZ, $"origin {origin}, installed Z {wz:R}, bounds [{box.MinZ:R}, {box.MaxZ:R}]");
            if (x == 30336 && direction == Vector3.UnitX) Assert.Equal(30016.500030517578, wx);
        }
        var descriptor = world.Statics.Single(s => s.OwnerId == "centimetre-crate");
        Assert.Equal(box, descriptor.Bounds);
    }

    [Fact]
    public void ColliderBounds_ContainChildCompositionWhenEndpointsAreGridAligned()
    {
        var shape = new CompoundShape(new[] { new CompoundChild(new BoxShape(new(0.03125f, 0.5f, 0.5f)),
            Pose.At(new(0.03125f, 0.5f, 0))) });
        var transform = new MapEffectiveTransform(0, 0, 0, 0, 1);
        var bounds = MapRegistrationBounds.Of(shape, transform);
        var origin = new Vector3(1000000, 0, 0);
        var frame = WorldFrame.Nearest(origin);
        var submitted = MapBackendPoseAdapter.Pose(transform, frame).Position - (origin - frame.Anchor);
        // The backend adds the child's float offset to the submitted float parent position before its extents.
        double installedCentre = (double)(submitted.X + 0.03125f) + origin.X;
        Assert.Equal(0, installedCentre);
        Assert.True(bounds.MinX <= installedCentre - 0.03125);
        Assert.True(bounds.MaxX >= installedCentre + 0.03125);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(16, 0)]
    [InlineData(4, 30336)]
    [InlineData(16, 1000000)]
    [InlineData(16, -1000000)]
    public void NestedCompoundBounds_ContainInstalledLeafAndCrossedTile(int levels, float originX)
    {
        var world = NativeWorldFixtures.BuildNestedCompound(levels);
        var bounds = world.Placements.Single().ColliderBounds!.Value;
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(new(originX, 0, 0));
        using var registration = MapPhysicsRegistration.Register(world, physics);
        Assert.True(physics.Raycast(new(65 - originX, 0, 0), -Vector3.UnitX, 2, out var hit));
        Assert.True(registration.TryOwner(hit.Body!.Value, out var owner));
        Assert.Equal("nested-compound", owner.OwnerId);
        double installedFace = (double)originX + hit.Point.X;
        if (originX == 0) Assert.Equal(64.015625, installedFace);
        Assert.True(bounds.MaxX >= installedFace, $"origin {originX}, {levels} levels, installed {installedFace:R}, maximum {bounds.MaxX:R}");
        // Independent geometry expectation. At every origin, rounded flattening leaves a zero-centred child.
        Assert.True(bounds.MaxX >= 64.015625);
        Assert.True(world.Bounds.MaxX >= 64.015625);
        Assert.Contains(MapResidencyOwnership.Build(world, Grids).Single().Membership, t => t.X == 1);
        Assert.Contains(MapNavTiling.Partition(world, Grids, Nav), t => t.Coord.X == 1);
    }

    [Theory]
    [InlineData(false, true, 0, 0, 0)]
    [InlineData(true, true, 0, 0, 0)]
    [InlineData(true, true, 926800, 0, 64)]
    [InlineData(true, true, -1000000, 1000000, -1000000)]
    [InlineData(false, false, 0, 0, 0)]
    public void FinalRotationCancellation_ContainsInstalledLeafAcrossZ64(bool orientedChild, bool preciseYaw, float x, float y, float z)
    {
        var world = NativeWorldFixtures.BuildRotationCancellation(orientedChild, preciseYaw);
        var origin = new Vector3(x, y, z);
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(origin);
        using var registration = MapPhysicsRegistration.Register(world, physics);
        var installed = NativeWorldFixtures.InstalledBox(physics);
        Assert.True(registration.TryOwner(registration.Handles.Single(), out var owner));
        Assert.Equal("rotation-cancellation", owner.OwnerId);
        Assert.Equal(926819.375, (double)x + installed.Centre.X);
        double expectedZ = preciseYaw ? 64 : 63.84375;
        Assert.Equal(expectedZ, (double)z + installed.Centre.Z);
        Assert.Equal(expectedZ, (double)z + installed.WideCentre.Z);
        var bounds = world.Placements.Single().ColliderBounds!.Value;
        Assert.True(bounds.MinZ <= 64 && bounds.MaxZ > 64, $"installed leaf centre Z 64, bounds [{bounds.MinZ:R}, {bounds.MaxZ:R}]");
        Assert.True(bounds.MinZ <= (double)z + installed.Centre.Z && bounds.MaxZ >= (double)z + installed.Centre.Z);
        Assert.True(bounds.MinX <= 926819.375 && bounds.MaxX >= 926819.375);
        Assert.True(bounds.MinX <= (double)x + installed.Min.X && bounds.MaxX >= (double)x + installed.Max.X);
        Assert.True(bounds.MinY <= (double)y + installed.Min.Y && bounds.MaxY >= (double)y + installed.Max.Y);
        Assert.True(bounds.MinZ <= (double)z + installed.Min.Z && bounds.MaxZ >= (double)z + installed.Max.Z);
        Assert.True(world.Bounds.MaxZ > 64);
        Assert.Contains(MapResidencyOwnership.Build(world, Grids).Single().Membership, t => t.Z == 1);
    }
}
