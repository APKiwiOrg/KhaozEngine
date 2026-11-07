using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class TerrainTransactionTests
{
    [Fact]
    public void TerrainTransaction_RestoresSeamSpaceAndIdentity()
    {
        using TerrainTransactionFixture gui = TerrainTransactionFixture.Create(CaveFixtures.ClosedMouth());
        using TerrainTransactionFixture service = TerrainTransactionFixture.Create(CaveFixtures.ClosedMouth());
        (string text0, string token0) = (gui.Text(), gui.Token());
        gui.Editor.Execute(new TerrainEditCommand(CaveFixtures.OpenMouthEdit()));
        MapTerrainMutationResult viaService = service.Service.TerrainApply(CaveFixtures.OpenMouthEdit());
        Assert.Equal(gui.Text(), service.Text());
        Assert.Equal(gui.Editor.LastNativeEffects!.Describe(), viaService.Effects.Describe());
        Assert.Equal(MapDocumentFile.SaveText(CaveFixtures.RampChamberShaft()), gui.Text());
        Assert.NotEqual(token0, gui.Token());
        Assert.True(gui.Editor.Undo());
        Assert.Equal((text0, token0), (gui.Text(), gui.Token()));
        Assert.True(gui.Editor.Redo());
        Assert.Equal(service.Text(), gui.Text());
        string state = gui.State();
        for (int i = 0; i < 2; i++)
        {
            var error = Assert.Throws<MapDocumentException>(() => gui.Editor.Execute(new TerrainEditCommand(CaveFixtures.CeilingCrossingEdit())));
            Assert.Equal(MapDocumentRefusal.SpaceBoundaryGap, error.Reason);
            Assert.NotNull(error.RecordId);
            Assert.NotNull(error.SpaceId);
            Assert.NotNull(error.SlotCell);
            Assert.Equal(state, gui.State());
        }
        string ceilingBefore = gui.Text();
        var repairedCeiling = CaveFixtures.CeilingCrossingEdit();
        // The mouth floor is 800 height units. 801 is its first positive integer ceiling interval.
        for (int x = 0; x <= repairedCeiling.UpsertPatches[0].Width; x++) repairedCeiling.UpsertPatches[0].Heights[x] = 801;
        gui.Editor.Execute(new TerrainEditCommand(repairedCeiling));
        Assert.NotEqual(ceilingBefore, gui.Text());
        state = gui.State();
        var halo = Assert.Throws<MapDocumentException>(() => gui.Editor.Execute(new TerrainEditCommand(
            new MapSmoothCorners(new("outer", 0, 0), 0, 0, 1, 1, 1))));
        Assert.Equal(MapDocumentRefusal.TerrainSmoothingHaloUnavailable, halo.Reason);
        Assert.Equal(new MapPatchKey("outer", 0, 0), halo.Patch);
        Assert.Equal(MapLatticeAddress.Corner(-1, -1), halo.Address);
        Assert.Equal(state, gui.State());
        gui.Editor.Execute(new TerrainEditCommand(new MapSmoothCorners(new("outer", 0, 0), 1, 1, 1, 1, 1)));
    }
    [Fact]
    public void FinePatchConversion_UndoRedoRestoresExactPatchesAndOwners()
    {
        using TerrainTransactionFixture f = TerrainTransactionFixture.Create(MixedResolutionFixtures.Document(ConversionFixtures.Slopes()));
        string text0 = f.Text();
        f.Editor.Execute(new TerrainEditCommand(new MapConvertFinePatch(ConversionFixtures.Request(3, false))));
        string converted = f.Text();
        Assert.Contains("coarse-fine-1", converted);
        Assert.True(f.Editor.Undo());
        Assert.Equal(text0, f.Text());
        Assert.True(f.Editor.Redo());
        Assert.Equal(converted, f.Text());
    }
}
