using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
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
}
