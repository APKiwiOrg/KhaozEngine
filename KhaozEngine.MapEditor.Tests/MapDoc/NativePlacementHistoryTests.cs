using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativePlacementHistoryTests
{
    [Fact]
    public void UndoRedoAndBranchRetainAllocationAndSavedHighWater()
    {
        using var f = new NativePlacementHistoryFixture();
        f.Editor.Execute(new AllocateNativePlacementCommand(f.NewProp("a"), true));
        Assert.Equal(11, f.Document.Placements.Last().NumericId);
        Assert.True(f.Editor.Undo());
        Assert.Equal(11, f.Document.NumericIdHighWaterMark);
        Assert.True(f.Editor.IsDirty);
        f.Editor.MarkSaved();
        Assert.False(f.Editor.IsDirty);
        Assert.True(f.Editor.Redo());
        Assert.Equal(11, f.Document.Placements.Last().NumericId);
        Assert.True(f.Editor.Undo());
        Assert.False(f.Editor.IsDirty);
        f.Editor.Execute(new AddPlacementCommand(f.NewProp("b")));
        Assert.Equal(12, f.Document.Placements.Last().NumericId);
        Assert.Same(f.Document, f.Editor.Doc);
    }

    [Fact]
    public void RenameIsLabelAndExplicitRemapPreservesOpaqueNamespaces()
    {
        using var f = new NativePlacementHistoryFixture();
        f.Editor.Execute(new RenamePlacementCommand("existing", "Visible label"));
        Assert.Equal("existing", f.Document.Placements.Single().Id);
        Assert.Equal("Visible label", f.Document.Placements.Single().DisplayName);
        f.Editor.Execute(new RemapPlacementIdCommand("existing", "changed"));
        var p = f.Document.Placements.Single();
        Assert.Equal(10, p.NumericId);
        Assert.Equal("prop", p.AssetId);
        Assert.Equal(new[] { "existing", "prop", "mesh" }, p.Tags);
        Assert.True(f.Editor.Undo());
        Assert.Equal("existing", f.Document.Placements.Single().Id);
        Assert.True(f.Editor.Undo());
        Assert.Null(f.Document.Placements.Single().DisplayName);
        Assert.False(f.Editor.IsDirty);
    }

    [Fact]
    public void RejectedAllocationIsAtomicAndSameCommandCanRetry()
    {
        using var f = new NativePlacementHistoryFixture();
        var p = f.NewProp("a");
        p.AssetId = "unknown";
        var command = new AllocateNativePlacementCommand(p, true);
        AssertRejected(f, () => f.Editor.Execute(command));
        p.AssetId = "prop";
        f.Editor.Execute(command);
        Assert.Equal(11, f.Document.Placements.Last().NumericId);
    }

    [Fact]
    public void CollisionAndExhaustionDoNotAllocateOrDiscardRedo()
    {
        using var f = new NativePlacementHistoryFixture();
        f.Editor.Execute(new SetPlacementLabelCommand("existing", "label"));
        f.Editor.Undo();
        AssertRejected(f, () => f.Editor.Execute(new AddPlacementCommand(f.NewProp("existing"))));
        f.Document.NumericIdHighWaterMark = long.MaxValue;
        AssertRejected(f, () => f.Editor.Execute(new AllocateNativePlacementCommand(f.NewProp("overflow"), true)), typeof(OverflowException));
    }

    [Fact]
    public void FailedRedoRetainsStacksAndCanRetry()
    {
        using var f = new NativePlacementHistoryFixture();
        f.Editor.Execute(new AllocateNativePlacementCommand(f.NewProp("a"), true));
        f.Editor.Undo();
        var collision = f.NewProp("a");
        f.Document.Placements.Add(collision);
        AssertRejected(f, () => f.Editor.Redo());
        f.Document.Placements.Remove(collision);
        Assert.True(f.Editor.Redo());
        Assert.Equal(11, f.Document.Placements.Last().NumericId);
    }

    [Fact]
    public void FailedUndoRetainsStacksAndCanRetry()
    {
        using var f = new NativePlacementHistoryFixture();
        f.Editor.Execute(new RemovePlacementCommand("existing"));
        var collision = f.NewProp("existing");
        f.Document.Placements.Add(collision);
        AssertRejected(f, () => f.Editor.Undo());
        f.Document.Placements.Remove(collision);
        Assert.True(f.Editor.Undo());
        Assert.Equal(10, f.Document.Placements.Single().NumericId);
    }

    [Fact]
    public void BoundariesRejectMissingAndMismatchedClosureWithoutStateChanges()
    {
        using var f = new NativePlacementHistoryFixture();
        var unbound = new EditorDocument(f.Document);
        string before = MapDocumentFile.SaveText(f.Document);
        Assert.ThrowsAny<Exception>(() => unbound.Execute(new AddPlacementCommand(f.NewProp("a"))));
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        Assert.False(unbound.History.CanUndo);
        var empty = MapAssetClosure.Load(Array.Empty<MapAssetRef>(), new MapDirectoryAssetSource(System.IO.Path.GetTempPath()));
        Assert.ThrowsAny<Exception>(() => f.Editor.BindNativeAssets(empty));
        Assert.ThrowsAny<Exception>(() => f.Session.BindNativeAssets(empty));
        Assert.False(f.Editor.IsDirty);
        f.Editor.Execute(new AddPlacementCommand(f.NewProp("a")));
        Assert.Equal(11, f.Document.Placements.Last().NumericId);
    }

    [Fact]
    public void SessionDuplicateAndTransformsPreserveNativeFieldsThroughReload()
    {
        using var f = new NativePlacementHistoryFixture();
        var doc = f.SessionDocument;
        string id = f.Service.ElementDuplicate("placement", "existing").Id!;
        f.Service.PlacementRename(id, "label");
        f.Service.PlacementMove(id, 4, 5, -999);
        f.Service.PlacementRotate(id, 0.7f);
        f.Service.PlacementScale(id, 2);
        f.Service.PlacementRemapId(id, "remapped");
        Assert.Same(doc, f.SessionDocument);
        f.Session.Save();
        f.Session.Open(f.PathName);
        var p = f.SessionDocument.Placements.Single(x => x.Id == "remapped");
        Assert.Equal(11, p.NumericId);
        Assert.Equal("prop", p.AssetId);
        Assert.Equal("label", p.DisplayName);
        Assert.Equal(-999, p.Y);
        Assert.Equal(0.7f, p.Yaw);
        Assert.Equal(2, p.Scale);
        Assert.Equal(new[] { "existing", "prop", "mesh" }, p.Tags);
        // Open verified the reloaded document against its resources and bound that fresh closure.
        f.Service.PlacementLabel("remapped", "bound by open");
        f.Service.PlacementRemove("remapped");
        Assert.Equal(11, f.SessionDocument.NumericIdHighWaterMark);
    }

    [Fact]
    public void LocalCommandsRejectInvalidTransformAndNumericCollisionAtomically()
    {
        using var f = new NativePlacementHistoryFixture();
        string before = MapDocumentFile.SaveText(f.Document);
        Assert.ThrowsAny<Exception>(() => new ScalePlacementCommand("existing", -1).Apply(f.Document));
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        var p = f.NewProp("collision");
        p.NumericId = 10;
        Assert.ThrowsAny<Exception>(() => new AllocateNativePlacementCommand(p, false).Apply(f.Document));
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
    }

    [Fact]
    public void LegacyRenameStillChangesStableId()
    {
        var doc = MapDocumentFileTests.SampleDoc();
        string id = doc.Placements[0].Id;
        var ed = new EditorDocument(doc);
        ed.Execute(new RenamePlacementCommand(id, "renamed"));
        Assert.Equal("renamed", doc.Placements[0].Id);
        ed.Undo();
        Assert.Equal(id, doc.Placements[0].Id);
    }

    [Fact]
    public void AssetMembershipComesFromManifestAndOptionalNumericIdsRemainOptional()
    {
        using var f = new NativePlacementHistoryFixture();
        var p = f.NewProp("first-use");
        p.AssetId = "unplaced";
        f.Editor.Execute(new AllocateNativePlacementCommand(p, false));
        Assert.Null(f.Document.Placements.Last().NumericId);
        Assert.Equal(10, f.Document.NumericIdHighWaterMark);
        Assert.True(f.Editor.Undo());
        Assert.False(f.Editor.IsDirty);
    }

    [Fact]
    public void SessionCreateClearsBindingAndRejectedMutationKeepsDirtyFalse()
    {
        using var f = new NativePlacementHistoryFixture();
        string before = MapDocumentFile.SaveText(f.SessionDocument);
        Assert.ThrowsAny<Exception>(() => f.Service.PlacementScale("existing", -1));
        Assert.Equal(before, MapDocumentFile.SaveText(f.SessionDocument));
        Assert.False(f.Session.Summary().Dirty);
        f.Session.Create(f.PathName, "replacement", "Replacement", -1000, -1000, 1000, 1000, overwrite: true);
        var replacement = f.SessionDocument;
        replacement.ResolverIdentity = new(1, 1);
        replacement.PlayableBounds = replacement.Bounds;
        replacement.NativeAssets = f.Document.NativeAssets.ToList();
        replacement.Placements.Add(f.NewProp("replacement"));
        Assert.ThrowsAny<Exception>(() => f.Service.PlacementLabel("replacement", "unbound"));
        Assert.False(f.Session.Summary().Dirty);
    }

    [Fact]
    public void GuiDuplicateAllocatesOnceAndRedoRestoresAcceptedId()
    {
        using var f = new NativePlacementHistoryFixture();
        f.Editor.Selection.Set(SelectionKind.Placement, "existing");
        var controller = new EditorToolController(f.Editor);
        Assert.NotNull(controller.DuplicateSelection());
        var added = f.Document.Placements.Last();
        Assert.Equal(11, added.NumericId);
        Assert.Equal("prop", added.AssetId);
        Assert.Equal(new[] { "existing", "prop", "mesh" }, added.Tags);
        f.Editor.Undo();
        f.Editor.Redo();
        Assert.Equal(11, f.Document.Placements.Last().NumericId);
        Assert.Equal(added.Id, f.Document.Placements.Last().Id);
    }

    [Fact]
    public void RejectedMoveDoesNotCaptureOldCoordinatesBeforeRetry()
    {
        using var f = new NativePlacementHistoryFixture();
        var command = new MovePlacementCommand("existing", 12, 13, -2000);
        var root = f.Document.NativeAssets[0];
        f.Document.NativeAssets.Clear();
        AssertRejected(f, () => f.Editor.Execute(command));
        f.Document.NativeAssets.Add(root);
        f.Document.Placements[0].X = 7;
        f.Editor.Execute(command);
        f.Editor.Undo();
        Assert.Equal(7, f.Document.Placements[0].X);
        Assert.Equal(-123.5f, f.Document.Placements[0].Y);
    }

    [Fact]
    public void GuiPlaceAndAdjustAllocatesOnceAndPreservesAcceptedTransformOnRedo()
    {
        using var f = new NativePlacementHistoryFixture();
        var c = new EditorToolController(f.Editor)
        {
            Field = MapRuntime.BuildField(f.Document, f.Editor.Registry),
            Mode = EditorToolMode.PlacePlacement,
            PlaceKind = "unplaced",
        };
        c.Update(new EditorFrameInput(new Vector3(30, 100, 30), -Vector3.UnitY, pointerPressed: true, pointerDown: true));
        c.Update(new EditorFrameInput(new Vector3(35, 100, 35), -Vector3.UnitY, pointerDown: true));
        c.Update(new EditorFrameInput(new Vector3(35, 100, 35), -Vector3.UnitY, pointerReleased: true));
        var added = f.Document.Placements.Last();
        Assert.Equal("unplaced", added.AssetId);
        Assert.Equal(11, added.NumericId);
        Assert.Equal(35, added.X);
        Assert.True(f.Editor.Undo());
        Assert.Single(f.Document.Placements);
        Assert.True(f.Editor.Redo());
        Assert.Equal(11, f.Document.Placements.Last().NumericId);
        Assert.Equal(35, f.Document.Placements.Last().X);
    }

    [Fact]
    public void SessionMutationCallbackCannotPublishInvalidOrPartiallyThrownNativeState()
    {
        using var f = new NativePlacementHistoryFixture();
        string before = MapDocumentFile.SaveText(f.SessionDocument);
        Assert.ThrowsAny<Exception>(() => f.Session.Mutate<int>((doc, _) =>
        {
            doc.NumericIdHighWaterMark = 100;
            doc.Placements.Clear();
            throw new InvalidOperationException("rejected");
        }, false));
        Assert.Equal(before, MapDocumentFile.SaveText(f.SessionDocument));
        Assert.False(f.Session.Summary().Dirty);
        Assert.ThrowsAny<Exception>(() => f.Session.Mutate((doc, _) =>
        {
            doc.Placements[0].AssetId = "missing";
            return 0;
        }, false));
        Assert.Equal(before, MapDocumentFile.SaveText(f.SessionDocument));
        Assert.False(f.Session.Summary().Dirty);
    }

    [Fact]
    public void FailedAddDoesNotCaptureAllocationChoiceBeforeAcceptedRetry()
    {
        using var f = new NativePlacementHistoryFixture();
        var p = f.NewProp("a");
        p.AssetId = "unknown";
        var command = new AddPlacementCommand(p);
        AssertRejected(f, () => f.Editor.Execute(command));
        p.AssetId = "prop";
        p.NumericId = 77;
        f.Editor.Execute(command);
        Assert.Equal(77, f.Document.Placements.Last().NumericId);
        Assert.Equal(77, f.Document.NumericIdHighWaterMark);
    }

    [Fact]
    public void LabelAndDeleteUndoPreservePlacementOrdering()
    {
        using var f = new NativePlacementHistoryFixture();
        f.Document.Placements.Insert(0, f.NewProp("z-first"));
        f.Document.Placements.Add(f.NewProp("a-last"));
        f.Editor.Execute(new SetPlacementLabelCommand("existing", "label"));
        Assert.Equal(new[] { "z-first", "existing", "a-last" }, f.Document.Placements.Select(p => p.Id));
        f.Editor.Execute(new RemovePlacementCommand("existing"));
        f.Editor.Undo();
        Assert.Equal(new[] { "z-first", "existing", "a-last" }, f.Document.Placements.Select(p => p.Id));
    }

    static void AssertRejected(NativePlacementHistoryFixture f, Action action, Type? exception = null)
    {
        string before = MapDocumentFile.SaveText(f.Document);
        string? undo = f.Editor.History.UndoLabel, redo = f.Editor.History.RedoLabel;
        bool dirty = f.Editor.IsDirty, rebuild = f.Editor.WorldRebuildPending;
        int events = 0;
        void Changed() => events++;
        f.Editor.DocumentChanged += Changed;
        var error = Record.Exception(action);
        f.Editor.DocumentChanged -= Changed;
        Assert.NotNull(error);
        if (exception is not null) Assert.IsType(exception, error);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        Assert.Equal(undo, f.Editor.History.UndoLabel);
        Assert.Equal(redo, f.Editor.History.RedoLabel);
        Assert.Equal(dirty, f.Editor.IsDirty);
        Assert.Equal(rebuild, f.Editor.WorldRebuildPending);
        Assert.Equal(0, events);
    }
}
