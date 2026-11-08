using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
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
        var origin = Assert.Throws<MapDocumentException>(() => new MapWorldGrids(64f, 2, 4, new Vector2(10, 0)).Validate(world));
        Assert.Equal(MapDocumentRefusal.PhysicsGridAlignmentInvalid, origin.Reason);
        Assert.Equal("Origin.X", origin.FieldPath);
        Assert.Equal(10, origin.AxisOrigin);
        var size = Assert.Throws<MapDocumentException>(() => new MapWorldGrids(32f, 2, 4, Vector2.Zero).Validate(world));
        Assert.Equal(MapDocumentRefusal.PhysicsGridAlignmentInvalid, size.Reason);
        Assert.Equal("StorageTileSize", size.FieldPath);
        Assert.Equal(32, size.ActualNumber);
        Assert.Equal(64, size.ExpectedNumber);
        Grids.Validate(world);
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
        Assert.Equal(MapDocumentRefusal.PhysicsGridAlignmentInvalid, navError.Reason);
        Assert.Equal("navigation tiles", navError.GridKind);
        Assert.Equal("X", navError.Axis);
        Assert.Equal(-1_099_511_627_776d, navError.AxisOrigin);
        Assert.True(navError.FirstIndex > int.MaxValue);
        Assert.NotEmpty(MapResidencyOwnership.Build(world, farNav with { Origin = Vector2.Zero }));
        // Navigation tiles of 2^12 storage tiles still index, so only the server cells leave the range.
        var farCells = new MapWorldGrids(64f, 1 << 12, 1, new Vector2(-1_099_511_627_776f, 0f));
        var cellError = Assert.Throws<MapDocumentException>(() => MapResidencyOwnership.Build(world, farCells));
        Assert.Equal(MapDocumentRefusal.PhysicsGridAlignmentInvalid, cellError.Reason);
        Assert.Equal("server cells", cellError.GridKind);
        Assert.True(cellError.LastIndex > int.MaxValue);
        Assert.NotEmpty(MapResidencyOwnership.Build(world, farCells with { Origin = Vector2.Zero }));
    }

    [Fact]
    public void NavTileOf_RefusesAFarTileNamingTheTileRange()
    {
        // With the origin 2^40 m west, storage tile 0 lies 2^34 navigation tiles east of it.
        var far = new MapWorldGrids(64f, 1, 4, new Vector2(-1_099_511_627_776f, 0f));
        var index = Assert.Throws<MapDocumentException>(() => far.NavTileOf(new MapTileCoord(0, 0)));
        Assert.Equal(MapDocumentRefusal.PhysicsGridIndexOutOfRange, index.Reason);
        Assert.Equal(0, index.TileIndex);
        Assert.Equal(1, index.StorageTiles);
        Assert.Equal(-1_099_511_627_776d, index.AxisOrigin);
        Assert.Equal(1L << 34, index.ActualInteger);
        Assert.Equal(new MapNavTileCoord(0, 0), (far with { Origin = Vector2.Zero }).NavTileOf(new(0, 0)));
        // An origin past 2^62 storage tiles leaves no long tile offset.
        var beyondLong = new MapWorldGrids(64f, 1, 4, new Vector2(1e30f, 0f));
        var origin = Assert.Throws<MapDocumentException>(() => beyondLong.NavTileOf(new MapTileCoord(0, 0)));
        Assert.Equal(MapDocumentRefusal.PhysicsGridIndexOutOfRange, origin.Reason);
        Assert.Equal("OriginTile", origin.FieldPath);
        Assert.Equal((double)1e30f, origin.AxisOrigin);
        Assert.Null(origin.ActualInteger);
        Assert.Equal(new MapNavTileCoord(0, 0), (beyondLong with { Origin = Vector2.Zero }).NavTileOf(new(0, 0)));
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
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("large-building", error.OwnerId);
        Assert.Equal("MaxStorageTiles", error.Budget);
        Assert.Equal(1_065_024, error.ActualCount);
        Assert.Equal(1_048_576, error.Limit);
        // 50 * 163.75 = 8187.5 exactly, inside the outer seams at +/-8192 even after registration allowance.
        // Both axes cover exactly 1024 tiles, so the real membership list reaches the inclusive 2^20 cap.
        NativeFixture control = NativeWorldFixtures.Placed("large-building", scale: 163.75f);
        control.Document.TileSize = 16f;
        var admitted = MapWorldBuilder.Build(control.Document, control.Assets, NativeWorldFixtures.Options());
        Assert.Equal(1_048_576, Assert.Single(MapResidencyOwnership.Build(admitted, new(16f, 4, 16, Vector2.Zero)),
            entry => entry.OwnerId == "large-building").Membership.Count);
    }

    [Theory]
    [InlineData(double.NaN, MapDocumentRefusal.ValueNonfinite)]
    [InlineData(1_000_001d, MapDocumentRefusal.ValueOutsideRange)]
    public void EditBounds_IdentifyTheFailedCoordinateAndAdmitTheWorldLimit(double value, MapDocumentRefusal reason)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapResidencyOwnership.RequireWithinWorld(new(0, 0, 0, 1, value, 1)));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("MaxY", error.FieldPath);
        Assert.Equal(value, error.ActualNumber);
        Assert.Equal(-1_000_000, error.MinimumNumber);
        Assert.Equal(1_000_000, error.MaximumNumber);
        MapResidencyOwnership.RequireWithinWorld(new(0, 0, 0, 1, 1_000_000, 1));
    }

    [Theory]
    [InlineData(double.NaN, 1d, MapDocumentRefusal.ValueNonfinite)]
    [InlineData(0d, double.PositiveInfinity, MapDocumentRefusal.ValueNonfinite)]
    [InlineData(2d, 1d, MapDocumentRefusal.BoundsInvalid)]
    public void StorageBounds_IdentifyNonfiniteOrInvertedEndpoints(double min, double max, MapDocumentRefusal reason)
    {
        var error = Assert.Throws<MapNavigationArgumentException>(() =>
            MapResidencyOwnership.RequireFiniteStorageBoundsForTests(new(min, 0, 0, max, 0, 0), 64));
        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Null(error.ParamName);
        Assert.Equal(reason, error.Reason);
        Assert.Equal("X", error.FieldPath);
        Assert.Equal(min, error.MinimumNumber);
        Assert.Equal(max, error.MaximumNumber);
        Assert.Equal(new MapTileRect(new(0, 0), new(0, 0)),
            MapResidencyOwnership.RequireFiniteStorageBoundsForTests(new(0, 0, 0, 0, 0, 0), 64));
    }

    [Fact]
    public void WaterTileUnion_ReportsItsOwnerAndAdmitsTheCapAndOneBelow()
    {
        var original = Assert.Single(NativeWorldFixtures.BuildBridgeOverRiver().Water);
        // Two disjoint triangle bounds claim 1023 * 1024 + 1023 tiles, exactly 2^20 - 1.
        // The two single-tile bounds then reach the cap and exceed it by one without any rectangle exceeding it.
        var triangles = new List<MapExactTriangle>
        {
            Triangle(0, 0, 1023, 1024), Triangle(1023, 0, 1024, 1023),
            Triangle(1023, 1023, 1024, 1024), Triangle(1024, 1023, 1025, 1024),
        };
        var grids = new MapWorldGrids(16, 4, 16, Vector2.Zero);
        MapCompiledWaterBody Body() => original with
        {
            BodyId = "reservoir", SurfaceY = 0, DeepestBedY = -1, Bounds = new(0, -1, 0, 16400, 0, 16384),
            Regions = [new("reservoir-space", MapWaterRegionKind.FreeSurface, triangles.ToArray())],
        };
        var error = Assert.Throws<MapDocumentException>(() => MapResidencyOwnership.WaterTiles(Body(), grids));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("water:reservoir", error.OwnerId);
        Assert.Equal("MaxStorageTiles", error.Budget);
        Assert.Equal(1_048_577, error.ActualCount);
        Assert.Equal(1_048_576, error.Limit);
        triangles.RemoveAt(3);
        Assert.Equal(1_048_576, MapResidencyOwnership.WaterTiles(Body(), grids).Length);
        triangles.RemoveAt(2);
        Assert.Equal(1_048_575, MapResidencyOwnership.WaterTiles(Body(), grids).Length);

        static MapExactPoint Point(long x, long z) => new(new(x * 16, 1), new(0, 1), new(z * 16, 1));
        static MapExactTriangle Triangle(long x0, long z0, long x1, long z1) => new(Point(x0, z0), Point(x1, z0), Point(x1, z1));
    }
}
