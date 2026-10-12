using KhaozEngine.Locomotion;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.MapDoc;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Prefabs;
using System.Linq;
using System.Numerics;
using System;
using System.Collections.Generic;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public class MapPrefabWaterWorldTests
{
    static readonly MapWorldGrids Grids = new(64, 1, 4, Vector2.Zero);
    static readonly MapNavTileOptions Nav = new(1, "prefab", "physical-clearance-only");

    [Theory]
    [InlineData(0.8f)]
    [InlineData(1.2f)]
    public void FreeDoorway_ProvesPhysicalClearanceOnly(float scale)
    {
        var world = PrefabPhysicsFixtures.Build(PrefabFixtures.Transformed(0.371f, scale, new(10.25f, 5, -3.125f)));
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, physics);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var relation = new MapPhysicalRelations(registration);
        Assert.Equal(MapPhysicalCertainty.Clear, relation.Clearance(lease, PrefabPhysicsFixtures.DoorPath(world, false), MoveTuning.Default).Certainty);
        Assert.Equal(MapPhysicalCertainty.Blocked, relation.Clearance(lease, PrefabPhysicsFixtures.DoorPath(world, true), MoveTuning.Default).Certainty);
        Assert.Equal(1f, MapInteractionPolicy.MinimumVerticalReachHeightMetres);
        Assert.Equal(3, Assert.IsType<CompoundShape>(world.Placements.Single(p => p.AssetId == "doorway").Collider).Children.Length);
    }

    [Theory]
    [InlineData(0.8f)]
    [InlineData(1.2f)]
    public void ScaledLowItem_StillHasOneMetreReachMinimum(float scale)
    {
        var world = PrefabPhysicsFixtures.Build(PrefabFixtures.Transformed(0.371f, scale, new(10.25f, 5, -3.125f)));
        var item = world.Placements.Single(p => p.AssetId == "item");
        Assert.Null(item.Collider);
        Assert.Equal(1, item.Envelope.Bounds.MaxY - item.Envelope.Bounds.MinY, 6);
        Assert.Equal(1 - 0.2 * scale, item.Envelope.RaiseMetres, 6);
    }

    [Fact]
    public void TransformedCavePrefabOrLayer_PreservesCollisionNavAndMembership()
    {
        var f = PrefabPhysicsFixtures.TightBounds(PrefabFixtures.Transformed(0.371f, 1.2f, new(30000.25f, -450, -29999.75f)));
        var world = PrefabPhysicsFixtures.Build(f);
        var reloaded = PrefabPhysicsFixtures.Build(new(MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)), f.Assets));
        var grids = new MapWorldGrids(64, 1, 4, Vector2.Zero);
        var options = new MapNavTileOptions(1, "prefab", "physical-clearance-only");
        Assert.Equal(MapNavTiling.Partition(world, grids, options).Select(t => (t.Coord, t.GeometryDigest)),
            MapNavTiling.Partition(reloaded, grids, options).Select(t => (t.Coord, t.GeometryDigest)));
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(new(30000, -450, -30000));
        using var registration = MapPhysicsRegistration.Register(reloaded, physics);
        var pose = reloaded.Document.EffectiveResources!.Transforms["Surface:a/floor"];
        var point = PrefabPhysicsFixtures.At(pose, 3, 1.5, -3);
        Vector3 query = point.Frame.Anchor + point.Local - physics.Origin;
        Assert.True(physics.Raycast(query, -Vector3.UnitY, 3, out var floor));
        Assert.True(physics.Raycast(query, Vector3.UnitY, 3, out var ceiling));
        Assert.Equal("a/floor", FloorOwner(floor));
        Assert.Equal("a/ceiling", FloorOwner(ceiling));
        Assert.Contains(reloaded.Terrain.Chunks.SelectMany(c => c.TriangleRoles), r => r == MapFaceRole.SupportFloor);
        Assert.Contains(reloaded.Terrain.Chunks.SelectMany(c => c.TriangleRoles), r => r == MapFaceRole.Ceiling);
        var member = new MapSpaceMembership(reloaded.Surfaces).Query(point);
        Assert.Equal("a/room", member.SpaceId);
        Assert.Contains("exterior", member.ParentSpaceIds);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        Assert.Equal(MapPhysicalCertainty.Clear, new MapPhysicalRelations(registration).Clearance(lease,
            PrefabPhysicsFixtures.DoorPath(reloaded, false), MoveTuning.Default).Certainty);
        Assert.Equal(MapPhysicalCertainty.Blocked, new MapPhysicalRelations(registration).Clearance(lease,
            PrefabPhysicsFixtures.DoorPath(reloaded, true), MoveTuning.Default).Certainty);

        string FloorOwner(RayHit hit)
        {
            Assert.True(registration.TryOwner(hit.Body!.Value, out var owner));
            var chunk = reloaded.Terrain.Chunks.Single(c => c.ChunkId == owner.OwnerId);
            return chunk.TriangleOwners[0].OwnerId;
        }
    }

    [Fact]
    public void RotatedBounds_NameEveryCrossedTileOnce()
    {
        var f = PrefabPhysicsFixtures.TightBounds(PrefabFixtures.Transformed(0.371f, 1.2f, new(64, 5, -64)));
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
        var f = PrefabPhysicsFixtures.TightBounds(PrefabFixtures.Transformed(0.371f, 1.2f, new(64, 5, -64)));
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
    public void LocalWaterNeverBecomesAStatic()
    {
        var world = PrefabPhysicsFixtures.Build(PrefabPhysicsFixtures.TightBounds(PrefabFixtures.Transformed(0.371f, 1.2f, new(10.25f, 5, -3.125f))));
        var body = Assert.Single(world.Water);
        Assert.Equal(MapWaterCompileStatus.Complete, body.Status);
        Assert.DoesNotContain(world.Statics, s => s.OwnerId == "water:" + body.BodyId);
        var entry = Assert.Single(MapResidencyOwnership.BuildWater(world, Grids));
        Assert.Equal("water:a/pool-water", entry.OwnerId);
        Assert.NotEmpty(entry.Membership);
        Assert.NotEmpty(body.FragmentBounds);
    }

    [Fact]
    public void MoveChangesOnlyIntersectedMediumTiles()
    {
        var before = PrefabPhysicsFixtures.Build(PrefabPhysicsFixtures.TightBounds(PrefabFixtures.Transformed(0.371f, 1.2f, new(64, 5, -64))));
        var after = PrefabPhysicsFixtures.Build(PrefabPhysicsFixtures.TightBounds(PrefabFixtures.Transformed(0.371f, 1.2f, new(65, 5, -64))));
        var old = MapNavTiling.Partition(before, Grids, Nav).ToDictionary(t => t.Coord);
        var current = MapNavTiling.Partition(after, Grids, Nav).ToDictionary(t => t.Coord);
        var coords = old.Keys.Concat(current.Keys).Distinct().ToArray();
        var changed = coords.Where(c => (old.GetValueOrDefault(c)?.MediumDigest ?? "") != (current.GetValueOrDefault(c)?.MediumDigest ?? "")).ToArray();
        Assert.NotEmpty(changed);
        Assert.Contains(coords, c => (old.GetValueOrDefault(c)?.MediumDigest ?? "") == "" && (current.GetValueOrDefault(c)?.MediumDigest ?? "") == "");
        Assert.All(changed, c => Assert.True((old.GetValueOrDefault(c)?.MediumDigest ?? "") != "" || (current.GetValueOrDefault(c)?.MediumDigest ?? "") != ""));
    }

    [Fact]
    public void MarkerOnlyAssetEdit_KeepsWetTileMediumIdentity()
    {
        var f = PrefabPhysicsFixtures.TightBounds(PrefabFixtures.Transformed(0.371f, 1.2f, new(64, 5, -64)));
        var before = PrefabPhysicsFixtures.Build(f);
        var after = PrefabPhysicsFixtures.Build(PrefabFixtures.MarkerOnlyWaterEdit(f));
        Assert.Equal(Assert.Single(before.Water).Digest, Assert.Single(after.Water).Digest);
        Assert.Equal(MapNavTiling.Partition(before, Grids, Nav).Select(t => (t.Coord, t.MediumDigest)),
            MapNavTiling.Partition(after, Grids, Nav).Select(t => (t.Coord, t.MediumDigest)));
    }
}
