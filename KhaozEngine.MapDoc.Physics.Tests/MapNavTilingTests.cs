using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Navigation tile identity: capture identities that move only with their own geometry, deterministic seams and
/// cross-tile links, and the tiles an edit invalidates.</summary>
public class MapNavTilingTests
{
    static readonly MapWorldGrids Grids = new(64f, 1, 4, Vector2.Zero);
    static readonly MapNavTileOptions Options = new(2f, "profile-a", "legacy-stepper");

    [Fact]
    public void AffectedTileRebake_PreservesUnaffectedDigests()
    {
        var before = MapNavTiling.Partition(NativeWorldFixtures.BuildLegacyStrip(), Grids, Options);
        var movedWorld = NativeWorldFixtures.BuildLegacyStripWithMovedCrate();
        var after = MapNavTiling.Partition(movedWorld, Grids, Options);
        var affected = MapNavTiling.AffectedTiles(movedWorld, Grids, Options, NativeWorldFixtures.MovedCrateEffects());
        Assert.NotEmpty(affected);
        foreach (var tile in before)
            if (affected.Contains(tile.Coord)) Assert.NotEqual(tile.CaptureIdentity, after.Single(t => t.Coord == tile.Coord).CaptureIdentity);
            else Assert.Equal(tile.CaptureIdentity, after.Single(t => t.Coord == tile.Coord).CaptureIdentity);
        AssertInPartition(affected, after);
    }

    [Fact]
    public void LegacySculptEdit_ChangesOnlyItsTiles()
    {
        var before = MapNavTiling.Partition(NativeWorldFixtures.BuildLegacyStrip(), Grids, Options);
        var after = MapNavTiling.Partition(NativeWorldFixtures.BuildLegacyStripWithSculptInTileZero(), Grids, Options);
        Assert.NotEqual(before.Single(t => t.Coord == new MapNavTileCoord(0, 0)).CaptureIdentity, after.Single(t => t.Coord == new MapNavTileCoord(0, 0)).CaptureIdentity);
        Assert.Equal(before.Single(t => t.Coord == new MapNavTileCoord(2, 0)).CaptureIdentity, after.Single(t => t.Coord == new MapNavTileCoord(2, 0)).CaptureIdentity);
        var affected = MapNavTiling.AffectedTiles(NativeWorldFixtures.BuildLegacyStripWithSculptInTileZero(), Grids, Options, NativeWorldFixtures.SculptInTileZeroEffects());
        Assert.Contains(new MapNavTileCoord(0, 0), affected);
        Assert.DoesNotContain(new MapNavTileCoord(2, 0), affected);
        AssertInPartition(affected, after);
    }

    [Fact]
    public void RemovedSculptTile_InvalidatesEveryTileItsFootprintReached()
    {
        var east = new MapNavTileCoord(1, 0);
        var before = MapNavTiling.Partition(NativeWorldFixtures.BuildLegacyStripWithSeamSculpt(), Grids, Options);
        var afterWorld = NativeWorldFixtures.BuildLegacyStrip();
        var after = MapNavTiling.Partition(afterWorld, Grids, Options);
        var affected = MapNavTiling.AffectedTiles(afterWorld, Grids, Options, NativeWorldFixtures.RemovedSeamSculptEffects());
        Assert.NotEqual(Identity(before, east), Identity(after, east));
        Assert.Contains(east, affected);
        AssertInPartition(affected, after);
    }

    [Fact]
    public void EditWithinTheSeamMargin_InvalidatesTheNeighbour()
    {
        var east = new MapNavTileCoord(1, 0);
        var before = MapNavTiling.Partition(NativeWorldFixtures.BuildLegacyStrip(), Grids, Options);
        var movedWorld = NativeWorldFixtures.BuildLegacyStripWithCrateAtSeam();
        var after = MapNavTiling.Partition(movedWorld, Grids, Options);
        var affected = MapNavTiling.AffectedTiles(movedWorld, Grids, Options, NativeWorldFixtures.CrateToSeamEffects());
        Assert.NotEqual(Identity(before, east), Identity(after, east));
        Assert.Contains(east, affected);
        Assert.DoesNotContain(new MapNavTileCoord(2, 0), affected);
        AssertInPartition(affected, after);
    }

    [Theory]
    [InlineData("height")]
    [InlineData("cell")]
    [InlineData("row")]
    public void SurfaceFrameEditInvalidatesOldAndNewTerrainCoverageWithoutWritingPatchPayload(string field)
    {
        // Regression #1903: terrain chunks have no placement effects to recover a missed surface edit.
        var fixture = NativeWorldFixtures.TerrainOnlyAtNavSeam();
        MapBuiltWorld before = NativeWorldFixtures.Build(fixture);
        MapSurfaceRef surface = Assert.Single(fixture.Document.Surfaces.Refs);
        MapLatticeFrame frame = field switch
        {
            "height" => surface.Frame with { HeightUnitMetres = new(1, 50) },
            "cell" => surface.Frame with { CellUnitMetres = new(2, 1) },
            _ => surface.Frame with { RowDirection = MapRowDirection.NegativeZ },
        };
        var edit = new MapReplaceTopology([], [], [surface with { Frame = frame }], []);
        var prepared = MapDocumentTerrainEdits.Prepare(fixture.Document, fixture.Assets, edit);
        MapBuiltWorld after = MapWorldBuilder.Build(prepared.After, fixture.Assets, NativeWorldFixtures.Options());
        Assert.Empty(after.Placements);
        AssertSurfaceOnlyWrite(prepared, surface.Id);

        var beforeTiles = MapNavTiling.Partition(before, Grids, Options);
        var afterTiles = MapNavTiling.Partition(after, Grids, Options);
        var changed = beforeTiles.Where(tile => tile.CaptureIdentity != Identity(afterTiles, tile.Coord))
            .Select(tile => tile.Coord).ToArray();
        Assert.Contains(new MapNavTileCoord(1, 0), changed); // Old footprint's capture-only neighbour.
        if (field == "cell") Assert.Contains(new MapNavTileCoord(2, 0), changed); // New footprint's capture-only neighbour.
        Assert.Equal(changed, MapNavTiling.AffectedTiles(before, after, Grids, Options, prepared.Effects));

        var oldChunk = Assert.Single(MapResidencyOwnership.Build(before, Grids));
        var newChunk = Assert.Single(MapResidencyOwnership.Build(after, Grids));
        var affected = MapResidencyOwnership.Affected(before, after, Grids, prepared.Effects);
        Assert.Contains(oldChunk.OwnerId, affected.Owners);
        Assert.Contains(newChunk.OwnerId, affected.Owners);
        Assert.Equal(oldChunk.Membership.Union(newChunk.Membership).OrderBy(t => t.Z).ThenBy(t => t.X), affected.StorageTiles);
        Assert.Equal(Assert.Single(fixture.Document.Surfaces.Patches).Key, Assert.Single(prepared.Effects.Patches));
        Assert.Equal(new MapBox3(62, 1, 10, 63, 1, 11), prepared.Effects.OldBounds);
        Assert.Equal(newChunk.WorldBounds, prepared.Effects.NewBounds);
    }

    [Fact]
    public void SurfaceFrameEditInvalidatesWallUsingAChainAnchoredOnAnotherSurface()
    {
        // Regression #1903: the edited floor's footprint does not reach the wall or its seam captures.
        var fixture = NativeWorldFixtures.TerrainWithWallReadingAnotherFrame();
        MapBuiltWorld before = NativeWorldFixtures.Build(fixture);
        MapSurfaceRef floor = fixture.Document.Surfaces.Refs.Single(s => s.Id == "edited-floor");
        var edit = new MapReplaceTopology([], [], [floor with { Frame = floor.Frame with { HeightUnitMetres = new(1, 50) } }], []);
        var prepared = MapDocumentTerrainEdits.Prepare(fixture.Document, fixture.Assets, edit);
        MapBuiltWorld after = MapWorldBuilder.Build(prepared.After, fixture.Assets, NativeWorldFixtures.Options());
        AssertSurfaceOnlyWrite(prepared, floor.Id);

        var oldWall = Assert.Single(before.Terrain.Chunks, c => c.TriangleRoles.Contains(MapFaceRole.Wall));
        var newWall = Assert.Single(after.Terrain.Chunks, c => c.TriangleRoles.Contains(MapFaceRole.Wall));
        Assert.NotEqual(oldWall.Digest, newWall.Digest);
        var seam = new MapNavTileCoord(2, 0);
        Assert.NotEqual(Identity(MapNavTiling.Partition(before, Grids, Options), seam),
            Identity(MapNavTiling.Partition(after, Grids, Options), seam));
        Assert.Contains(seam, MapNavTiling.AffectedTiles(before, after, Grids, Options, prepared.Effects));
        var affected = MapResidencyOwnership.Affected(before, after, Grids, prepared.Effects);
        Assert.Contains(oldWall.ChunkId, affected.Owners);
        Assert.Contains(newWall.ChunkId, affected.Owners);
        Assert.Contains(new MapTileCoord(1, 0), affected.StorageTiles);
        Assert.DoesNotContain(new MapTileCoord(2, 0), affected.StorageTiles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PresencePolicyEditInvalidatesLegacyFallbackTerrainWithoutWritingPatchPayload(bool toLegacy)
    {
        // Regression #1903: the imported frame can stay fixed as the legacy fallback policy removes or adds a chunk.
        var fixture = NativeWorldFixtures.TerrainOnlyAtNavSeam(legacyFallback: true, startLegacy: !toLegacy);
        MapBuiltWorld before = NativeWorldFixtures.Build(fixture);
        MapSurfaceRef surface = Assert.Single(fixture.Document.Surfaces.Refs);
        var policy = toLegacy ? MapPresencePolicy.LegacyTileWorld : MapPresencePolicy.Native;
        var edit = new MapReplaceTopology([], [], [surface with { PresencePolicy = policy }], []);
        var prepared = MapDocumentTerrainEdits.Prepare(fixture.Document, fixture.Assets, edit);
        MapBuiltWorld after = MapWorldBuilder.Build(prepared.After, fixture.Assets, NativeWorldFixtures.Options());
        AssertSurfaceOnlyWrite(prepared, surface.Id);
        var chunk = Assert.Single(MapResidencyOwnership.Build(toLegacy ? before : after, Grids));
        Assert.Empty(MapResidencyOwnership.Build(toLegacy ? after : before, Grids));

        var seam = new MapNavTileCoord(1, 0);
        Assert.NotEqual(Identity(MapNavTiling.Partition(before, Grids, Options), seam),
            Identity(MapNavTiling.Partition(after, Grids, Options), seam));
        Assert.Contains(seam, MapNavTiling.AffectedTiles(after, Grids, Options, prepared.Effects));
        Assert.Equal(chunk.Membership, MapResidencyOwnership.Affected(after, Grids, prepared.Effects).StorageTiles);
        Assert.Equal(Assert.Single(fixture.Document.Surfaces.Patches).Key, Assert.Single(prepared.Effects.Patches));
    }

    [Fact]
    public void PaintPriorityEditWritesTheSurfaceWithoutInvalidatingTerrainPhysicsOrCapture()
    {
        // Regression #1903 control: a changed surface digest alone does not imply moved geometry.
        var fixture = NativeWorldFixtures.TerrainOnlyAtNavSeam(withPaint: true);
        MapBuiltWorld before = NativeWorldFixtures.Build(fixture);
        MapSurfaceRef paint = fixture.Document.Surfaces.Refs.Single(s => s.Role == MapSurfaceRole.PaintOverride);
        var edit = new MapReplaceTopology([], [], [paint with { PaintPriority = 2 }], []);
        var prepared = MapDocumentTerrainEdits.Prepare(fixture.Document, fixture.Assets, edit);
        MapBuiltWorld after = MapWorldBuilder.Build(prepared.After, fixture.Assets, NativeWorldFixtures.Options());
        AssertSurfaceOnlyWrite(prepared, paint.Id);
        Assert.Empty(prepared.Effects.Patches);
        Assert.Null(prepared.Effects.OldBounds);
        Assert.Null(prepared.Effects.NewBounds);
        Assert.Empty(MapResidencyOwnership.Affected(before, after, Grids, prepared.Effects).Owners);
        Assert.Empty(MapNavTiling.AffectedTiles(before, after, Grids, Options, prepared.Effects));
        Assert.Equal(MapNavTiling.Partition(before, Grids, Options).Select(t => t.CaptureIdentity),
            MapNavTiling.Partition(after, Grids, Options).Select(t => t.CaptureIdentity));
    }

    static void AssertSurfaceOnlyWrite(MapPrefabEditResult prepared, string surfaceId)
    {
        Assert.Empty(prepared.WriteSet.Patches);
        Assert.Empty(prepared.WriteSet.RecordIds);
        Assert.False(prepared.WriteSet.Placements);
        Assert.Equal(surfaceId, Assert.Single(prepared.WriteSet.SurfaceIds));
        var change = Assert.Single(prepared.Effects.DigestChanges);
        Assert.Equal($"surface/{surfaceId}", change.Key);
        Assert.NotEqual(change.Before, change.After);
    }

    [Fact]
    public void Partition_RefusesMoreTilesThanTheCap()
    {
        // Storage bounds -64 to 65,600 on both axes reach 1,026 tiles per axis.
        var world = NativeWorldFixtures.BuildCrateAt(65_536f, 0f, 65_536f);
        var error = Assert.Throws<MapDocumentException>(() => MapNavTiling.Partition(world, Grids, Options));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("navigation partition tiles", error.Budget);
        Assert.Equal(1_052_676, error.ActualCount);
        Assert.Equal(MapNavTiling.MaxPartitionTiles, error.Limit);
        Assert.Equal(MapNavTiling.MaxPartitionTiles, EmptyPartitionCount(MapNavTiling.MaxPartitionTiles));
        Assert.Equal(MapNavTiling.MaxPartitionTiles - 1, EmptyPartitionCount(MapNavTiling.MaxPartitionTiles - 1));
    }

    [Fact]
    public void CaptureIdentityAndLinkDigest_MatchTheirCanonicalForms()
    {
        // Derived outside the engine from the documented canonical JSON (and, for the link, the surface patch codec's
        // JSON form of a one-cell record envelope), never from a run.
        var empty = MapNavTiling.Partition(NativeWorldFixtures.BuildSeamAligned(), Grids, Options)
            .Single(t => t.Coord == new MapNavTileCoord(-2, -2));
        Assert.Equal("435209ca491da91cdc6f7d27d0a0b938f25ce4cfbcc248a9306aac54abbe3e56", empty.GeometryDigest);
        Assert.Equal("2ee6b931489d0936d7bc863aa6d4d3fef173df4a2f6fa1a857c700021468a082", empty.CaptureIdentity);
        var link = Assert.Single(MapNavTiling.Links(NativeWorldFixtures.BuildStackedCaveAcrossTiles(), Grids));
        Assert.Equal(new MapNavTileCoord(0, 0), link.From);
        Assert.Equal(new MapNavTileCoord(1, 0), link.To);
        Assert.Equal("b2de56231403a3b1ab60846704c03a659c790636bb028925a49448e4e78aaae2", link.Digest);
    }

    static string Identity(IReadOnlyList<MapNavTile> tiles, MapNavTileCoord coord) => tiles.Single(t => t.Coord == coord).CaptureIdentity;

    static void AssertInPartition(IReadOnlyList<MapNavTileCoord> affected, IReadOnlyList<MapNavTile> partition) =>
        Assert.All(affected, coord => Assert.Contains(partition, t => t.Coord == coord));

    [Fact]
    public void TiledNav_SeamsAndLinksAreDeterministic()
    {
        var world = NativeWorldFixtures.BuildLegacyStrip();
        var tiles = MapNavTiling.Partition(world, Grids, Options);
        Assert.Equal(MapNavTiling.Seams(tiles, world, Options), MapNavTiling.Seams(MapNavTiling.Partition(world, Grids, Options), world, Options));
        Assert.True(NativeWorldFixtures.SeamColumnsAgree(world, tiles[0], tiles[1]));
        var links = MapNavTiling.Links(NativeWorldFixtures.BuildStackedCaveAcrossTiles(), Grids);
        Assert.Contains(links, l => l.RecordId == NativeWorldFixtures.ShaftLinkRecordId && l.From != l.To);
        Assert.Equal(links, MapNavTiling.Links(NativeWorldFixtures.BuildStackedCaveAcrossTiles(), Grids));
    }

    [Fact]
    public void Partition_CoversTheWorldExactlyOnce_AndIdentityFollowsProfileAndController()
    {
        var world = NativeWorldFixtures.BuildLegacyStrip();
        var tiles = MapNavTiling.Partition(world, Grids, Options);
        Assert.Equal(tiles.Count, tiles.Select(t => t.Coord).Distinct().Count());
        Assert.True(NativeWorldFixtures.BoundsUnionEquals(tiles.Select(t => t.Bounds), world.Bounds, Grids));
        Assert.NotEqual(tiles[0].CaptureIdentity, MapNavTiling.Partition(world, Grids, Options with { ProfileIdentity = "profile-b" })[0].CaptureIdentity);
        Assert.NotEqual(tiles[0].CaptureIdentity, MapNavTiling.Partition(world, Grids, Options with { ControllerIdentity = "contact-controller" })[0].CaptureIdentity);
    }

    [Fact]
    public void TileIndexBeyondTheIntRange_RefusesAsADocumentError()
    {
        // With the origin 2^40 m west, the strip's tiles lie about 2^34 tiles east of it.
        var far = new MapWorldGrids(64f, 1, 4, new Vector2(-1_099_511_627_776f, 0f));
        var error = Assert.Throws<MapDocumentException>(() =>
            MapNavTiling.Partition(NativeWorldFixtures.BuildLegacyStrip(), far, Options));
        // Validate refuses the grids before any capture is placed.
        Assert.Equal(MapDocumentRefusal.PhysicsGridAlignmentInvalid, error.Reason);
        Assert.Equal("navigation tiles", error.GridKind);
        Assert.Equal("X", error.Axis);
        Assert.Equal(-1_099_511_627_776d, error.AxisOrigin);
        Assert.True(error.FirstIndex > int.MaxValue);
        Assert.True(error.LastIndex > int.MaxValue);
        Assert.NotEmpty(MapNavTiling.Partition(NativeWorldFixtures.BuildLegacyStrip(), far with { Origin = Vector2.Zero }, Options));
    }

    static int EmptyPartitionCount(int count)
    {
        var fixture = NativeWorldFixtures.Crate();
        fixture.Document.Placements.Clear();
        fixture.Document.Surfaces = new();
        fixture.Document.TileSize = 1;
        fixture.Document.TerrainOverrides = new(1f / 32);
        fixture.Document.Bounds = new() { MinX = -524288, MinZ = 0, MaxX = -524288 + count, MaxZ = 1 };
        fixture.Document.PlayableBounds = fixture.Document.Bounds;
        var world = NativeWorldFixtures.Build(fixture);
        return MapNavTiling.Partition(world, new(1, 1, 1, Vector2.Zero), Options with { SeamMarginMetres = 0 }).Count;
    }

    [Fact]
    public void SeamMarginBeyondTheIntRange_RefusesNamingTheTileRange()
    {
        // With the origin 2^36 m west, the strip's tiles index near 2^30 and pass Validate, but a 10^12 m margin
        // carries the capture past the int range.
        var nearEdge = new MapWorldGrids(64f, 1, 4, new Vector2(-68_719_476_736f, 0f));
        var error = Assert.Throws<MapDocumentException>(() => MapNavTiling.Partition(
            NativeWorldFixtures.BuildLegacyStrip(), nearEdge, Options with { SeamMarginMetres = 1e12f }));
        Assert.Equal(MapDocumentRefusal.PhysicsNavIndexOutOfRange, error.Reason);
        Assert.Equal("X", error.Axis);
        Assert.Equal(-68_719_476_736d, error.AxisOrigin);
        Assert.Equal(int.MinValue, error.MinimumInteger);
        Assert.Equal(int.MaxValue, error.MaximumInteger);
        Assert.NotEmpty(MapNavTiling.Partition(NativeWorldFixtures.BuildLegacyStrip(), nearEdge, Options with { SeamMarginMetres = 0 }));
    }

    [Fact]
    public void UnboundedEdit_StillRefusesAnEditBoxBeyondTheWorld()
    {
        var effects = NativeWorldFixtures.MovedCrateEffects() with
        {
            NewBounds = new MapBox3(2_000_000, 0, 0, 2_000_001, 1, 1),
            Invalidates = MapNativeInvalidation.Nav | MapNativeInvalidation.Unbounded,
        };
        var error = Assert.Throws<MapDocumentException>(() =>
            MapNavTiling.AffectedTiles(NativeWorldFixtures.BuildLegacyStrip(), Grids, Options, effects));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
        Assert.Equal("MinX", error.FieldPath);
        Assert.Equal(2_000_000, error.ActualNumber);
        Assert.Equal(-1_000_000, error.MinimumNumber);
        Assert.Equal(1_000_000, error.MaximumNumber);
        Assert.NotEmpty(MapNavTiling.AffectedTiles(NativeWorldFixtures.BuildLegacyStrip(), Grids, Options,
            effects with { NewBounds = new(999_999, 0, 0, 1_000_000, 1, 1) }));
    }
}
