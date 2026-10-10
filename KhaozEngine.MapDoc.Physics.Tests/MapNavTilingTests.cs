using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
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

    [Fact]
    public void Partition_RefusesMoreTilesThanTheCap()
    {
        // Storage bounds -64 to 65,600 on both axes reach 1,026 tiles per axis.
        var world = NativeWorldFixtures.BuildCrateAt(65_536f, 0f, 65_536f);
        var error = Assert.Throws<MapDocumentException>(() => MapNavTiling.Partition(world, Grids, Options));
        Assert.Contains("1,052,676", error.Message);
        Assert.Contains("1,048,576", error.Message);
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
}
