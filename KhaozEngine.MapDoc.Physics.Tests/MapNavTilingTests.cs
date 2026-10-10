using System.Linq;
using System.Numerics;
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
    }

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
