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
            var error = Assert.Throws<MapDocumentException>(() => e.Execute(new TestPatchHeightCommand(new("ground", 0, 0), 4, 1234)));
            Assert.Equal(MapDocumentRefusal.GeometryVertexHeightMismatch, error.Reason);
            Assert.Equal(new MapPatchKey("ground", 1, 0), error.Patch);
            Assert.Equal(new MapPatchKey("ground", 0, 0), error.ExpectedPatch);
            Assert.Equal(MapLatticeAddress.Corner(64, 0), error.Address);
            Assert.Equal(0, error.Index);
            Assert.Equal(before, TransactionFixtures.State(e));
        }
        e.Execute(new TestPatchHeightCommand(new("ground", 0, 0), 4, 1000));
    }
    [Fact]
    public void StagedAllocation_DefersAcceptanceAndCanRetry()
    {
        using var f = new NativePlacementHistoryFixture();
        var command = new AllocateNativePlacementCommand(f.NewProp("staged"), true);
        var staged = NativeDocumentTransaction.Stage(f.Document, command, false, null, localOnly: true);
        Assert.Null(command.AcceptedId);
        Assert.DoesNotContain(f.Document.Placements, p => p.Id == "staged");
        Assert.Equal(11L, staged.Candidate.Placements.Single(p => p.Id == "staged").NumericId);
        Assert.Null(command.AcceptedId);
        NativeDocumentSnapshot.PublishWriteSet(f.Document, staged.Candidate, staged.WriteSet);
        staged.Accept();
        Assert.Equal("staged", command.AcceptedId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingPlacement_ReplayRefusesAndCanRetry(bool redo)
    {
        using var f = new NativePlacementHistoryFixture();
        f.Editor.Execute(new MovePlacementCommand("existing", 3, -4, 5));
        if (redo) Assert.True(f.Editor.Undo());
        var owner = f.Document.Placements.Single();
        f.Document.Placements.Remove(owner);
        string before = MapDocumentFile.SaveText(f.Document);
        var effects = f.Editor.LastNativeEffects;
        var error = Assert.Throws<MapDocumentException>(() => { if (redo) f.Editor.Redo(); else f.Editor.Undo(); });
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal("existing", error.EntityId);
        Assert.Equal(f.Document.Id, error.DocumentId);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        Assert.Same(effects, f.Editor.LastNativeEffects);
        f.Document.Placements.Add(owner);
        Assert.True(redo ? f.Editor.Redo() : f.Editor.Undo());
    }
    [Fact]
    public void PartialDocument_NativeTerrainCommandRefuses() => TiledDocFixture.InDirectory(dir =>
    {
        MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
        MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 300);
        Assert.False(window.Tiles!.HasUnloadedTiles);                                  // surfaces-only window
        var e = new EditorDocument(window);
        string State() => System.Text.Json.JsonSerializer.Serialize(window) + "\n" + MapSurfaceSemantics.RootDigest(window);
        string before = State();
        var error = Assert.Throws<MapDocumentException>(() => e.Execute(new TestPatchHeightCommand(new("far", 300, 0), 0, 2100)));
        Assert.Equal(MapDocumentRefusal.CompleteDocumentRequired, error.Reason);
        Assert.Equal(window.Id, error.DocumentId);
        Assert.Equal("Stage", error.Operation);
        var snapshot = Assert.Throws<MapDocumentException>(() => NativeDocumentSnapshot.Clone(window, e.Registry));
        Assert.Equal(MapDocumentRefusal.CompleteDocumentRequired, snapshot.Reason);
        Assert.Equal(window.Id, snapshot.DocumentId);
        Assert.Equal("Clone", snapshot.Operation);
        Assert.Equal(before, State());
        var whole = MapDocumentFile.LoadTiled(dir);
        Assert.NotNull(NativeDocumentSnapshot.Clone(whole, e.Registry));
        var control = new EditorDocument(whole);
        control.BindNativeAssets(SurfaceStorageFixtures.Assets());
        control.Execute(new TestPatchHeightCommand(new("far", 300, 0), 0, 2100));
    });
}
