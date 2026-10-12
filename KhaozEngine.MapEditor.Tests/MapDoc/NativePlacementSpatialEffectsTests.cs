using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativePlacementSpatialEffectsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedCompoundMove_AffectsTheInstalledFaceStorageTileAndNavCapture(bool undo)
    {
        var f = NativePlacementEffectsFixture.NestedCompound();
        MapBuiltWorld before = f.BuildNestedCompound();
        f.Editor.Execute(new MovePlacementCommand("nested-compound", 63.5f, 0f, 0f));
        MapBuiltWorld moved = f.BuildNestedCompound();
        // The shared collider's installed right face is proven in NestedCompoundBounds_ContainInstalledLeafAndCrossedTile.
        Assert.True(moved.Placements.Single().ColliderBounds!.Value.MaxX >= 64.015625);
        MapBuiltWorld after = moved;
        if (undo)
        {
            before = moved;
            Assert.True(f.Editor.Undo());
            after = f.BuildNestedCompound();
        }
        MapNativeEditEffects effects = f.Editor.LastNativeEffects!;
        Assert.Equal(MapNativeInvalidation.Placements | MapNativeInvalidation.Physics | MapNativeInvalidation.Nav |
            MapNativeInvalidation.Residency, effects.Invalidates);
        Assert.Equal(undo ? 63.953125 : 62.953125, effects.OldBounds!.Value.MaxX);
        Assert.Equal(undo ? 62.953125 : 63.953125, effects.NewBounds!.Value.MaxX);
        Assert.Contains(MapResidencyOwnership.Build(moved, NativePlacementEffectsFixture.Grids).Single().Membership, t => t.X == 1);
        MapAffectedSet affected = MapResidencyOwnership.Affected(before, after, NativePlacementEffectsFixture.Grids, effects);
        Assert.Contains(affected.StorageTiles, t => t.X == 1);
        Assert.Contains(new MapNavTileCoord(1, 0), MapNavTiling.AffectedTiles(before, after,
            NativePlacementEffectsFixture.Grids, NativePlacementEffectsFixture.NavOptions, effects));
    }

    [Fact]
    public void NestedCompoundMove_DescriptionCarriesTheSpatialExtentBesideTheTightReport()
    {
        var f = NativePlacementEffectsFixture.NestedCompound();
        f.Editor.Execute(new MovePlacementCommand("nested-compound", 63.5f, 0f, 0f));
        using var description = JsonDocument.Parse(f.Editor.LastNativeEffects!.Describe());
        Assert.Equal(63.953125, description.RootElement.GetProperty("newBounds")[3].GetDouble());
        Assert.True(description.RootElement.TryGetProperty("newSpatialBounds", out var spatial));
        Assert.True(spatial[3].GetDouble() >= 64.015625);
    }

    [Fact]
    public void ColliderHeightEdit_DescriptionKeepsTheReleasedHeightAndConservativeExtent()
    {
        using var f = new NativeCollisionToolFixture();
        var effects = f.Service.SetHeights("lone-box", 0f, 0.5f, dryRun: true).Effects;
        using var description = JsonDocument.Parse(effects.Describe());
        JsonElement reported = description.RootElement.GetProperty("oldBounds");
        Assert.Equal(1.5, reported[4].GetDouble() - reported[1].GetDouble(), 4);
        Assert.True(description.RootElement.TryGetProperty("oldSpatialBounds", out var spatial));
        Assert.True(spatial[1].GetDouble() < reported[1].GetDouble());
        Assert.True(spatial[4].GetDouble() > reported[4].GetDouble());
    }
}
