using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Regression for KhaozEngine #1348.</summary>
public class MapTerrainPlacementResidencyTests
{
    static readonly MapWorldGrids Grids = new(64f, 1, 4, Vector2.Zero);
    static readonly MapNavTileOptions Options = new(2f, "profile", "controller");

    [Theory]
    [InlineData("large-building", 1.5f)]
    [InlineData("examine-sign", 170f)]
    public void TerrainEditInvalidatesTilesReachedByMovedColliderOrEnvelope(string assetId, float scale)
    {
        var fixture = NativeWorldFixtures.TerrainResidencyPlacement(assetId, 4f, scale, height: 0);
        MapBuiltWorld before = NativeWorldFixtures.Build(fixture);
        var edit = new MapSetCornerHeights(new("edited-floor", 0, 0), 4, 4, 2, 2, [100, 100, 100, 100]);
        var prepared = MapDocumentTerrainEdits.Prepare(fixture.Document, fixture.Assets, edit);
        MapBuiltWorld after = MapWorldBuilder.Build(prepared.After, fixture.Assets, NativeWorldFixtures.Options());
        Assert.Equal(0f, Placement(before).WorldPose.Position.Y);
        Assert.Equal(1f, Placement(after).WorldPose.Position.Y);
        Assert.True(Placement(after).Envelope.Bounds.MaxX > 64f);
        var effects = prepared.Effects with { };
        Assert.True(effects.NewBounds!.Value.MaxX < 64f);

        var neighbor = new MapTileCoord(1, 0);
        var navNeighbor = new MapNavTileCoord(1, 0);
        foreach (var affected in new[]
        {
            MapResidencyOwnership.Affected(after, Grids, effects),
            MapResidencyOwnership.Affected(before, after, Grids, effects),
        })
        {
            Assert.Contains("moving", affected.Owners);
            Assert.Contains(neighbor, affected.StorageTiles);
            Assert.Contains(navNeighbor, affected.NavTiles);
            Assert.DoesNotContain(new MapTileCoord(2, 0), affected.StorageTiles);
        }
        Assert.Contains(navNeighbor, MapNavTiling.AffectedTiles(after, Grids, Options, effects));
        Assert.Contains(navNeighbor, MapNavTiling.AffectedTiles(before, after, Grids, Options, effects));
        Assert.DoesNotContain(new MapNavTileCoord(2, 0), MapNavTiling.AffectedTiles(after, Grids, Options, effects));

        using var description = JsonDocument.Parse(effects.Describe());
        var moved = Assert.Single(description.RootElement.GetProperty("reseatedPlacements").EnumerateArray());
        Assert.Equal("moving", moved[0].GetString());
        Assert.Equal(0d, moved[1][1].GetDouble());
        Assert.Equal(1d, moved[2][1].GetDouble());
    }

    [Fact]
    public void TerrainEditInvalidatesCaptureWithinTheMovedColliderSeamMargin()
    {
        var fixture = NativeWorldFixtures.TerrainResidencyPlacement("large-building", 4f, 1.18f, height: 0);
        MapBuiltWorld before = NativeWorldFixtures.Build(fixture);
        var edit = new MapSetCornerHeights(new("edited-floor", 0, 0), 4, 4, 2, 2, [100, 100, 100, 100]);
        var prepared = MapDocumentTerrainEdits.Prepare(fixture.Document, fixture.Assets, edit);
        MapBuiltWorld after = MapWorldBuilder.Build(prepared.After, fixture.Assets, NativeWorldFixtures.Options());
        var east = new MapNavTileCoord(1, 0);

        Assert.DoesNotContain(new MapTileCoord(1, 0), MapResidencyOwnership.Affected(after, Grids, prepared.Effects).StorageTiles);
        Assert.NotEqual(Identity(before), Identity(after));
        Assert.Contains(east, MapNavTiling.AffectedTiles(after, Grids, Options, prepared.Effects));

        string Identity(MapBuiltWorld world) =>
            MapNavTiling.Partition(world, Grids, Options).Single(tile => tile.Coord == east).CaptureIdentity;
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void TerrainEditKeepsBothRegistrationExtentsWhenOnlyOneReachesTheNeighbor(int oldHeight, int newHeight)
    {
        var fixture = NativeWorldFixtures.TerrainResidencyPlacement("seam-box", 0f, 1f, oldHeight);
        MapBuiltWorld before = NativeWorldFixtures.Build(fixture);
        var edit = new MapSetCornerHeights(new("edited-floor", 0, 0), 0, 4, 2, 2,
            [newHeight, newHeight, newHeight, newHeight]);
        var prepared = MapDocumentTerrainEdits.Prepare(fixture.Document, fixture.Assets, edit);
        MapBuiltWorld after = MapWorldBuilder.Build(prepared.After, fixture.Assets, NativeWorldFixtures.Options());
        Assert.Equal(oldHeight != 0, Placement(before).ColliderBounds!.Value.MaxX > 64d);
        Assert.Equal(newHeight != 0, Placement(after).ColliderBounds!.Value.MaxX > 64d);

        var tile = new MapTileCoord(1, 0);
        Assert.Contains(tile, MapResidencyOwnership.Affected(after, Grids, prepared.Effects).StorageTiles);
        Assert.Contains(tile, MapResidencyOwnership.Affected(before, after, Grids, prepared.Effects).StorageTiles);
    }

    [Fact]
    public void SurfaceFrameEditInvalidatesAReseatedPlacementWithoutChangingItsPatchPayload()
    {
        var fixture = NativeWorldFixtures.TerrainResidencyPlacement("seam-box", 0f, 1f, height: 1);
        MapBuiltWorld before = NativeWorldFixtures.Build(fixture);
        var surface = fixture.Document.Surfaces.Refs.Single(s => s.Id == "edited-floor");
        var edit = new MapReplaceTopology([], [], [surface with { Frame = surface.Frame with { HeightUnitMetres = new(1, 1) } }], []);
        var prepared = MapDocumentTerrainEdits.Prepare(fixture.Document, fixture.Assets, edit);
        MapBuiltWorld after = MapWorldBuilder.Build(prepared.After, fixture.Assets, NativeWorldFixtures.Options());
        Assert.Empty(prepared.WriteSet.Patches);
        Assert.Equal(0.01f, Placement(before).WorldPose.Position.Y);
        Assert.Equal(1f, Placement(after).WorldPose.Position.Y);
        Assert.Contains(new MapTileCoord(1, 0), MapResidencyOwnership.Affected(after, Grids, prepared.Effects).StorageTiles);
    }

    static MapPlacementGeometry Placement(MapBuiltWorld world) => world.Placements.Single(p => p.PlacementId == "moving");
}

internal static partial class NativeWorldFixtures
{
    internal static NativeFixture TerrainResidencyPlacement(string assetId, float x, float scale, int height)
    {
        var surfaces = new MapSurfaceSet();
        AddSurface(surfaces, "edited-floor", CentimetreHeights, 0, 0, 8, 12, (_, _) => height);
        AddSurface(surfaces, "separate-floor", CentimetreHeights, 10, 0, 16, 12, (_, _) => 0);
        var moving = OnFloor("moving", assetId, x, 4f, scale: scale);
        moving.Y = null;
        moving.SupportBinding = new(MapSupportBindingKind.Surface, "edited-floor", null, null, null, null);
        var unchanged = OnFloor("unchanged", "large-building", 12f, 4f, scale: 4f);
        unchanged.Y = null;
        unchanged.SupportBinding = new(MapSupportBindingKind.Surface, "separate-floor", null, null, null, null);
        var fixedY = OnFloor("fixed", "large-building", 6f, 4f, scale: 4f);
        return Resolve(surfaces, moving, unchanged, fixedY);
    }
}
