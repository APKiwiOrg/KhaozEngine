using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public class MapResidencyOwnershipTests
{
    static readonly MapWorldGrids Grids = new(64f, 2, 4, Vector2.Zero);

    [Fact]
    public void LargePlacement_IntersectsEveryTileAndBuildsOnce()
    {
        var world = NativeWorldFixtures.BuildLargeBuilding();
        var entry = Assert.Single(MapResidencyOwnership.Build(world, Grids), e => e.OwnerId == "large-building");
        Assert.Contains(new MapTileCoord(-1, -1), entry.Membership);
        Assert.Contains(new MapTileCoord(0, 0), entry.Membership);
        // Keyed by its origin at (0, 0), as MapSpatialIndex stores it, not by its bounds' minimum corner (-50, -50).
        Assert.Equal(new MapTileCoord(0, 0), entry.StorageOwner);
        Assert.Equal(entry.Membership.Count, entry.Membership.Distinct().Count());
        Assert.Single(world.Statics, s => s.OwnerId == "large-building");
    }

    [Fact]
    public void ExactSeams_DoNotOvercount_AndWindowsUnionToTheWhole()
    {
        var world = NativeWorldFixtures.BuildSeamAligned();
        var entries = MapResidencyOwnership.Build(world, Grids);
        Assert.Single(Assert.Single(entries, e => e.OwnerId == "seam-box").Membership);
        var union = NativeWorldFixtures.TilingWindows().SelectMany(w => MapResidencyOwnership.InWindow(entries, w)).Distinct().OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(entries.Select(e => e.OwnerId).OrderBy(s => s, StringComparer.Ordinal), union);
    }

    [Fact]
    public void Grids_RefuseMisalignment_AndMatchShardingCells()
    {
        var world = NativeWorldFixtures.BuildSeamAligned();
        Assert.Contains("grid alignment", Assert.Throws<MapDocumentException>(() => new MapWorldGrids(64f, 2, 4, new Vector2(10, 0)).Validate(world)).Message);
        Assert.Contains("grid alignment", Assert.Throws<MapDocumentException>(() => new MapWorldGrids(32f, 2, 4, Vector2.Zero).Validate(world)).Message);
        var cellGrid = new KhaozEngine.Sharding.CellGrid(Grids.ServerCellSize, Grids.Origin);
        foreach (var t in new[] { new MapTileCoord(-5, 3), new MapTileCoord(7, -9) })
        {
            var centre = MapTileGrid.CenterOf(t, Grids.StorageTileSize);
            var c = cellGrid.CoordFor(centre.X, centre.Y);
            Assert.Equal(new MapServerCellCoord(c.X, c.Y), Grids.ServerCellOf(t));
        }
    }

    [Fact]
    public void Grids_RefuseAnOriginThatPutsTheWorldOutsideTheIntRange()
    {
        var world = NativeWorldFixtures.BuildSeamAligned();
        // With the origin 2^40 m west, the world lies about 2^34 storage tiles east of it.
        var farNav = new MapWorldGrids(64f, 1, 1 << 12, new Vector2(-1_099_511_627_776f, 0f));
        var navError = Assert.Throws<MapDocumentException>(() => MapResidencyOwnership.Build(world, farNav));
        Assert.Contains("grid alignment", navError.Message);
        Assert.Contains("navigation tiles", navError.Message);
        // Navigation tiles of 2^12 storage tiles still index, so only the server cells leave the range.
        var farCells = new MapWorldGrids(64f, 1 << 12, 1, new Vector2(-1_099_511_627_776f, 0f));
        var cellError = Assert.Throws<MapDocumentException>(() => MapResidencyOwnership.Build(world, farCells));
        Assert.Contains("grid alignment", cellError.Message);
        Assert.Contains("server cells", cellError.Message);
    }

    [Fact]
    public void NavTileOf_RefusesAFarTileNamingTheTileRange()
    {
        // With the origin 2^40 m west, storage tile 0 lies 2^34 navigation tiles east of it.
        var far = new MapWorldGrids(64f, 1, 4, new Vector2(-1_099_511_627_776f, 0f));
        Assert.Contains("tile range", Assert.Throws<MapDocumentException>(() => far.NavTileOf(new MapTileCoord(0, 0))).Message);
        // An origin past 2^62 storage tiles leaves no long tile offset.
        var beyondLong = new MapWorldGrids(64f, 1, 4, new Vector2(1e30f, 0f));
        Assert.Contains("tile range", Assert.Throws<MapDocumentException>(() => beyondLong.NavTileOf(new MapTileCoord(0, 0))).Message);
    }

    [Fact]
    public void EditEffects_MapToAffectedOwnersAndTiles()
    {
        var world = NativeWorldFixtures.BuildStackedCave();
        var affected = MapResidencyOwnership.Affected(world, Grids, NativeWorldFixtures.CeilingEditEffects());
        Assert.Contains(NativeWorldFixtures.CeilingChunkId, affected.Owners);
        Assert.Equal(new[] { new MapNavTileCoord(0, 0) }, affected.NavTiles);
    }

    [Fact]
    public void ListedPatch_OwnsItsChunks_WithoutBounds()
    {
        var world = NativeWorldFixtures.BuildStackedCave();
        var effects = NativeWorldFixtures.CeilingEditEffects() with { OldBounds = null, NewBounds = null };
        var affected = MapResidencyOwnership.Affected(world, Grids, effects);
        Assert.Equal(new[] { NativeWorldFixtures.CeilingChunkId }, affected.Owners);
        Assert.Equal(new[] { new MapTileCoord(0, 0) }, affected.StorageTiles);
        Assert.Equal(new[] { new MapNavTileCoord(0, 0) }, affected.NavTiles);
    }

    [Fact]
    public void ListedPatch_OwnsItsStripChunks_AndNoStripSharingAnIdPrefix()
    {
        var world = NativeWorldFixtures.BuildWallPrefixYards();
        Assert.Contains(world.Statics, s => s.OwnerId == "wall-10/0/Front");
        var effects = new MapNativeEditEffects(null, null, new[] { NativeWorldFixtures.WallOneYard }, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<MapDigestChange>(), MapNativeInvalidation.Physics);
        var affected = MapResidencyOwnership.Affected(world, Grids, effects);
        Assert.Contains("wall-1/0/Front", affected.Owners);
        Assert.DoesNotContain(affected.Owners, o => o.StartsWith("wall-10/", StringComparison.Ordinal));
    }

    [Fact]
    public void NonSpatialInvalidation_AffectsNothing()
    {
        var world = NativeWorldFixtures.BuildStackedCave();
        var effects = NativeWorldFixtures.CeilingEditEffects() with { Invalidates = MapNativeInvalidation.Material };
        var affected = MapResidencyOwnership.Affected(world, Grids, effects);
        Assert.Empty(affected.Owners);
        Assert.Empty(affected.StorageTiles);
        Assert.Empty(affected.NavTiles);
    }

    [Fact]
    public void StaticReachingMoreStorageTilesThanTheCap_RefusesBeforeListingThem()
    {
        // At 165 times its size the building spans x and z -8,250 to 8,250, which is 1,032 by 1,032 storage tiles of
        // 16 m, the narrowest tile a document accepts.
        NativeFixture f = NativeWorldFixtures.Placed("large-building", scale: 165f);
        f.Document.TileSize = 16f;
        var world = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        var error = Assert.Throws<MapDocumentException>(() =>
            MapResidencyOwnership.Build(world, new MapWorldGrids(16f, 4, 16, Vector2.Zero)));
        Assert.Contains("'large-building'", error.Message);
        Assert.Contains("1,065,024", error.Message);
        Assert.Contains("1,048,576", error.Message);
    }
}
