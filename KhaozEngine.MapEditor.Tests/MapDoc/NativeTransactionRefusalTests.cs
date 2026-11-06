using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeTransactionRefusalTests
{
    [Fact]
    public void BoundMarkerWithoutClosure_ReportsOnlyKnownContextAndAdmitsItsClosure()
    {
        var fixture = MarkerFixtures.Create();
        var command = new MarkerEditCommand(new MapRemoveMarker("m"));
        string before = MapDocumentFile.SaveText(fixture.Document);
        var error = Assert.Throws<MapDocumentException>(() =>
            NativeDocumentTransaction.Stage(fixture.Document, command, false, null, localOnly: true));
        Assert.Equal(MapDocumentRefusal.AssetClosureRequired, error.Reason);
        Assert.Equal(fixture.Document.Id, error.DocumentId);
        Assert.Equal(typeof(MarkerEditCommand), error.CommandType);
        Assert.Null(error.SubjectKind);
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
        Assert.NotNull(NativeDocumentTransaction.Stage(fixture.Document, command, false, fixture.Assets, localOnly: true));
    }

    [Fact]
    public void ForeignMarkerCallbackFailure_RetainsItsCauseAndAdmitsAWorkingCallback()
    {
        var fixture = PrefabFixtures.Create();
        string before = MapDocumentFile.SaveText(fixture.Document);
        var cause = new InvalidOperationException("host failure");
        var error = Assert.Throws<MapDocumentException>(() =>
            MapEditorScene.RequireMarkerAssetsForTests(_ => throw cause, fixture.Document));
        Assert.Equal(MapDocumentRefusal.EditorMarkerAssetsFailed, error.Reason);
        Assert.Equal(fixture.Document.Id, error.DocumentId);
        Assert.Equal("MarkerAssets", error.FieldPath);
        Assert.Same(cause, error.InnerException);
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
        MapEditorScene.RequireMarkerAssetsForTests(_ => fixture.Assets, fixture.Document);
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("undo")]
    [InlineData("redo")]
    [InlineData("export")]
    public void CancelledLocalSession_PreservesContentAndRefusesEveryOperation(string operation)
    {
        var fixture = PrefabFixtures.Create();
        MapPrefabLocalSession Open() => new(fixture.Assets.GetPrefab("hut"), fixture.Assets, fixture.Document, PrefabEditorFixtures.ParentRef(fixture));
        var local = Open();
        var patch = local.Document.Surfaces.Patches.First();
        var command = new TestPatchHeightCommand(patch.Key, 0, patch.Value.Heights[0]);
        string before = MapDocumentFile.SaveText(local.Document);
        local.Cancel();
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            switch (operation)
            {
                case "apply": local.Apply(command); break;
                case "undo": local.Undo(); break;
                case "redo": local.Redo(); break;
                default: local.Export(); break;
            }
        });
        Assert.Equal(MapDocumentRefusal.EditorPrefabSessionCancelled, error.Reason);
        Assert.Equal("hut", error.AssetId);
        Assert.Equal(operation switch { "apply" => "Apply", "undo" => "Undo", "redo" => "Redo", _ => "Export" }, error.Operation);
        Assert.Equal(before, MapDocumentFile.SaveText(local.Document));
        var control = Open();
        switch (operation)
        {
            case "apply": control.Apply(command); break;
            case "undo": Assert.False(control.Undo()); break;
            case "redo": Assert.False(control.Redo()); break;
            default: Assert.NotNull(control.Export()); break;
        }
    }

    [Fact]
    public void LocalSessionWithoutManifestRoot_IdentifiesItsAssetAndAdmitsABoundClosure()
    {
        var fixture = PrefabFixtures.Create();
        var asset = new MapPrefabAsset(1, "empty", []);
        var empty = MapAssetClosure.Load([], new UnusedSource());
        string before = MapDocumentFile.SaveText(fixture.Document);
        var error = Assert.Throws<MapDocumentException>(() => new MapPrefabLocalSession(asset, empty, fixture.Document));
        Assert.Equal(MapDocumentRefusal.ToolAssetRootRequired, error.Reason);
        Assert.Equal("empty", error.AssetId);
        Assert.Equal("Detach", error.Operation);
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
        Assert.NotNull(new MapPrefabLocalSession(asset, fixture.Assets, fixture.Document).Export());
    }

    [Fact]
    public void UnsupportedNativeCommand_LeavesHistoryAndContentAndAdmitsAPlacementCommand()
    {
        var editor = TransactionFixtures.NativeWithSurfaces();
        string before = TransactionFixtures.State(editor);
        var error = Assert.Throws<MapDocumentException>(() => editor.Execute(new UnsupportedCommand()));
        Assert.Equal(MapDocumentRefusal.OperationKindUnsupported, error.Reason);
        Assert.Equal(typeof(UnsupportedCommand), error.CommandType);
        Assert.Equal(editor.Doc.Id, error.DocumentId);
        Assert.Equal(before, TransactionFixtures.State(editor));
        editor.Execute(new TestPatchHeightCommand(new("ground", 0, 0), 0, 1100));
        Assert.True(editor.History.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteContainmentWithoutFindings_UsesItsOwnAdmissionRule(bool diagnostic)
    {
        var error = Assert.Throws<MapDocumentException>(() =>
            NativeDocumentTransaction.RequireWaterContainmentForTests(new(false, diagnostic ? ["consumer failure"] : [])));
        Assert.Equal(MapDocumentRefusal.WaterContainmentUnverified, error.Reason);
        NativeDocumentTransaction.RequireWaterContainmentForTests(new(true, []));
    }

    sealed class UnusedSource : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => throw new InvalidOperationException("No roots were submitted.");
    }

    sealed class UnsupportedCommand : EditorCommand
    {
        public override string Label => "unsupported";
        internal override bool AffectsWorld => false;
        public override void Apply(MapDocument doc) => throw new NotSupportedException();
        public override void Revert(MapDocument doc) => throw new NotSupportedException();
    }
}
