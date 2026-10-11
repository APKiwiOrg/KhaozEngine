using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public class MapWaterWorldFixTests
{
    static readonly MapWorldGrids Grids = new(64f, 2, 4, Vector2.Zero);
    static readonly MapNavTileOptions Nav = new(0.5f, "test-profile", "test-controller");

    [Fact]
    public void PlacementAndBodyWithSameId_HaveDistinctResidencyOwners()
    {
        WaterWorldFixture fixture = NativeWorldFixtures.BridgeOverRiver();
        fixture.Document.Placements[0].Id = "river";
        MapBuiltWorld world = NativeWorldFixtures.Build(NativeWorldFixtures.Reresolve(fixture));
        var entries = MapResidencyOwnership.Build(world, Grids).Concat(MapResidencyOwnership.BuildWater(world, Grids));
        Assert.Equal(new[] { "river", "water:river" }, entries.Where(e => e.OwnerId is "river" or "water:river")
            .Select(e => e.OwnerId).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "water:river" }, MapResidencyOwnership.Affected(world, Grids, MediumEdit("river")).Owners);
    }

    [Fact]
    public void Builder_RefusesStaticOwnerCollidingWithWaterNamespace()
    {
        WaterWorldFixture fixture = NativeWorldFixtures.BridgeOverRiver();
        fixture.Document.Placements[0].Id = "water:river";
        fixture = NativeWorldFixtures.Reresolve(fixture);
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.Build(fixture));
        Assert.StartsWith("water: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("static owner 'water:river'", error.Message, StringComparison.Ordinal);
        Assert.Contains("body 'river'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MapNativeInvalidation.Physics)]
    [InlineData(MapNativeInvalidation.Residency)]
    [InlineData(MapNativeInvalidation.Nav)]
    public void BodyNamesWithoutMedium_DoNotInvalidateWaterOwners(MapNativeInvalidation invalidates)
    {
        var effects = MediumEdit("river") with { Invalidates = invalidates };
        MapAffectedSet affected = MapResidencyOwnership.Affected(NativeWorldFixtures.BuildLRiver(), Grids, effects);
        Assert.Empty(affected.Owners);
        Assert.Empty(affected.StorageTiles);
        Assert.Empty(affected.NavTiles);
    }

    [Fact]
    public void MediumOnlyWithoutBounds_InvalidatesExactlyNamedBodyCaptureTiles()
    {
        MapBuiltWorld world = NativeWorldFixtures.Build(NativeWorldFixtures.LRiver(withPond: true));
        Assert.Equal(new[] { new MapNavTileCoord(0, -1), new MapNavTileCoord(0, 0) },
            MapNavTiling.AffectedTiles(world, Grids, Nav, MediumEdit("river")));
        Assert.Equal(new[] { new MapNavTileCoord(1, -1) },
            MapNavTiling.AffectedTiles(world, Grids, Nav, MediumEdit("pond")));
    }

    [Fact]
    public void UnboundedMediumWithoutNav_InvalidatesEveryPartitionTileAndOwner()
    {
        MapBuiltWorld world = NativeWorldFixtures.Build(NativeWorldFixtures.LRiver(withPond: true));
        var effects = MediumEdit("river") with { Invalidates = MapNativeInvalidation.Unbounded | MapNativeInvalidation.Medium };
        Assert.Equal(MapNavTiling.Partition(world, Grids, Nav).Select(t => t.Coord),
            MapNavTiling.AffectedTiles(world, Grids, Nav, effects));
        Assert.Equal(world.Statics.Select(s => s.OwnerId).Concat(new[] { "water:pond", "water:river" }).Order(StringComparer.Ordinal),
            MapResidencyOwnership.Affected(world, Grids, effects).Owners);
    }

    [Fact]
    public void DiagonalWaterWhoseBoundsEnterADryCapture_DoesNotChangeItsIdentity()
    {
        WaterWorldFixture fixture = NativeWorldFixtures.LRiver();
        MapWaterBodyDoc body = fixture.Document.Water!.Bodies[0];
        fixture.Document.Water.Bodies[0] = body with
        {
            Domain = body.Domain with
            {
                Rects = Array.Empty<MapWaterRect>(),
                Rings = new[] { new MapWaterRing(new[]
                {
                    MapLatticeAddress.Create(508, -4, 4), MapLatticeAddress.Create(512, -4, 4),
                    MapLatticeAddress.Create(508, -1, 4),
                }) },
            },
        };
        MapBuiltWorld wetWorld = NativeWorldFixtures.Build(NativeWorldFixtures.Reresolve(fixture));
        var wet = MapNavTiling.Partition(wetWorld, Grids, Nav).ToDictionary(t => t.Coord);
        var dry = MapNavTiling.Partition(NativeWorldFixtures.BuildDryTwinOfLRiver(), Grids, Nav).ToDictionary(t => t.Coord);
        var dryCoord = new MapNavTileCoord(1, 0);
        Assert.NotEqual(dry[new(1, -1)].CaptureIdentity, wet[new(1, -1)].CaptureIdentity);
        Assert.Equal("", wet[dryCoord].MediumDigest);
        Assert.Equal(dry[dryCoord].CaptureIdentity, wet[dryCoord].CaptureIdentity);
        Assert.DoesNotContain(dryCoord, MapNavTiling.AffectedTiles(wetWorld, Grids, Nav, MediumEdit("river")));
    }

    [Fact]
    public void WaterTouchingCaptureEdge_DoesNotChangeTheDryTileIdentity()
    {
        WaterWorldFixture fixture = NativeWorldFixtures.LRiver();
        MapWaterBodyDoc body = fixture.Document.Water!.Bodies[0];
        fixture.Document.Water.Bodies[0] = body with { Domain = body.Domain with { Rects = new[] { new MapWaterRect(120, -16, 128, -8) } } };
        MapBuiltWorld world = NativeWorldFixtures.Build(NativeWorldFixtures.Reresolve(fixture));
        var nav = Nav with { SeamMarginMetres = 0f };
        var wet = MapNavTiling.Partition(world, Grids, nav).ToDictionary(t => t.Coord);
        var dry = MapNavTiling.Partition(NativeWorldFixtures.BuildDryTwinOfLRiver(), Grids, nav).ToDictionary(t => t.Coord);
        var dryCoord = new MapNavTileCoord(1, -1);
        Assert.NotEqual(dry[new(0, -1)].CaptureIdentity, wet[new(0, -1)].CaptureIdentity);
        Assert.Equal("", wet[dryCoord].MediumDigest);
        Assert.Equal(dry[dryCoord].CaptureIdentity, wet[dryCoord].CaptureIdentity);
        Assert.Equal(new[] { new MapNavTileCoord(0, -1) }, MapNavTiling.AffectedTiles(world, Grids, nav, MediumEdit("river")));
    }

    [Fact]
    public void Builder_RefusesBodyThatCompilesIncomplete()
    {
        WaterWorldFixture fixture = NativeWorldFixtures.BridgeOverRiver();
        fixture.Document.Surfaces.Patches[new MapPatchKey("floor", 0, 0)].SetPresent(0, 0, false);
        fixture = NativeWorldFixtures.Reresolve(fixture);
        Assert.Equal(MapWaterCompileStatus.Incomplete, Assert.Single(MapWaterCompiler.Compile(fixture.View, fixture.Document.Water!)).Status);
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.Build(fixture));
        Assert.StartsWith("water: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResidencyBuilder_RefusesCompleteBodyWithRegionsButNoBounds()
    {
        MapBuiltWorld world = NativeWorldFixtures.BuildBridgeOverRiver();
        MapCompiledWaterBody body = Assert.Single(world.Water) with { Bounds = null };
        Assert.NotEmpty(body.Regions);
        var malformed = new MapBuiltWorld(world.Document, world.Surfaces, world.Placements, world.Terrain, world.Statics,
            world.Diagnostics, world.Bounds, world.TileSize, world.BuildHash, world.IsNative, world.LegacySupportHeight,
            world.LegacySculptTiles, world.LegacySculptCellSize, world.LegacyTerrainBlockDigest, world.LegacyTerrainIdentity,
            new[] { body }, world.WaterMedia, world.WaterDigest, world.LegacyWaterLevel);
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapResidencyOwnership.BuildWater(malformed, Grids));
        Assert.Equal("water: body 'river' has regions but no bounds", error.Message);
    }

    static MapNativeEditEffects MediumEdit(string id) => new(null, null, Array.Empty<MapPatchKey>(), Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<MapDigestChange>(), MapNativeInvalidation.Medium)
    { WaterBodyIds = new[] { id } };
}
