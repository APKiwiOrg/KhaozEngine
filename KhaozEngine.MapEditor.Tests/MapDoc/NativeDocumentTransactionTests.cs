using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeDocumentTransactionTests
{
    [Fact]
    public void PlacementCommands_KeepReleasedBehaviourThroughTheGeneralSeam()
    {
        using var f = new NativePlacementHistoryFixture();
        f.Editor.Execute(new AddPlacementCommand(f.NewProp("seam-check")));
        // The editor binds no placement bounds, so the add cannot be sized and invalidates everything.
        const MapNativeInvalidation unbounded = MapNativeInvalidation.Placements | MapNativeInvalidation.Physics |
            MapNativeInvalidation.Nav | MapNativeInvalidation.Residency | MapNativeInvalidation.Unbounded;
        Assert.Equal((11L, unbounded), (f.Document.Placements.Single(p => p.Id == "seam-check").NumericId, f.Editor.LastNativeEffects!.Invalidates));
        Assert.True(f.Editor.Undo());
        Assert.DoesNotContain(f.Document.Placements, p => p.Id == "seam-check");
        Assert.True(f.Editor.Redo());
        Assert.Equal((11L, 11L), (f.Document.Placements.Single(p => p.Id == "seam-check").NumericId, f.Document.NumericIdHighWaterMark));
    }
    [Fact]
    public void WriteSet_PublishesOnlyItsMembers()
    {
        EditorDocument e = TransactionFixtures.NativeWithSurfaces();
        var placements = e.Doc.Placements;
        MapSurfacePatch ridge = e.Doc.Surfaces.Patches[new("ridge", 0, 0)];
        e.Execute(new TestPatchHeightCommand(new("ground", 0, 0), 0, 1100));
        Assert.Same(placements, e.Doc.Placements);
        Assert.Same(ridge, e.Doc.Surfaces.Patches[new("ridge", 0, 0)]);
        Assert.Equal(1100, e.Doc.Surfaces.Patches[new("ground", 0, 0)].Heights[0]);
        Assert.Equal(new[] { new MapPatchKey("ground", 0, 0) }, e.LastNativeEffects!.Patches);
    }
    [Fact]
    public void RejectedCommand_LeavesDocumentHistoryDirtyIdentityAndEffectsUnchanged()
    {
        EditorDocument e = TransactionFixtures.NativeWithSurfaces();
        e.Execute(new TestPatchHeightCommand(new("ground", 0, 0), 0, 1100));
        string before = TransactionFixtures.State(e);
        for (int i = 0; i < 2; i++)
        {
            // corner index 4 is cell X 64, the owner corner ground(1,0) depends on
            Assert.Contains("owner", Assert.Throws<MapDocumentException>(() => e.Execute(new TestPatchHeightCommand(new("ground", 0, 0), 4, 1234))).Message);
            Assert.Equal(before, TransactionFixtures.State(e));
        }
    }
    [Fact]
    public void PartialDocument_NativeTerrainCommandRefuses() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
        MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 300);
        Assert.False(window.Tiles!.HasUnloadedTiles);                                  // surfaces-only window
        var e = new EditorDocument(window);
        Assert.Contains("window", Assert.Throws<MapDocumentException>(() => e.Execute(new TestPatchHeightCommand(new("far", 300, 0), 0, 2100))).Message);
    });
}
