using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativePrefabAssetCommandTests
{
    [Fact]
    public void AssetUndoRedo_RebindsMatchingClosure()
    {
        var f = PrefabFixtures.Create("all-resources");
        var asset = f.Assets.GetPrefab("hut");
        var marker = PrefabEditorFixtures.Marker(asset);
        var changed = MapPrefabAssetEdits.Replace(asset, marker with { Value = marker.Value with { Name = "Changed" } });
        var after = PrefabFixtures.PublishPrefab(f.Assets, changed);
        var command = new PrefabAssetEditCommand(new MapUpdatePrefabAsset("hut", f.Assets.GetResource("hut").Reference.Sha256,
            "hut", after.GetResource("hut").Reference.Sha256), f.Assets, after);
        var history = PrefabEditorFixtures.History(f.Document, f.Assets);
        string beforeBytes = MapDocumentFile.SaveText(f.Document);
        history.Execute(f.Document, command);
        string afterBytes = MapDocumentFile.SaveText(f.Document);
        Assert.NotEqual(beforeBytes, afterBytes);
        Assert.True(history.Undo(f.Document));
        Assert.Equal(beforeBytes, MapDocumentFile.SaveText(f.Document));
        MapBoundDocumentValidation.Validate(f.Document, f.Assets);
        Assert.True(history.Redo(f.Document));
        Assert.Equal(afterBytes, MapDocumentFile.SaveText(f.Document));
        MapBoundDocumentValidation.Validate(f.Document, after);
    }
    [Fact]
    public void CreatedInterior_UndoRedoThroughSnapshotCommandRestoresBothRecords()
    {
        var f = MapInteriorCreationFixtures.Empty();
        var after = MapInteriorEdits.Prepare(f.Document, f.Assets, new MapSetInterior(MapInteriorCreationFixtures.Domain()));
        var history = PrefabEditorFixtures.History(f.Document, f.Assets);
        string before = MapDocumentFile.SaveText(f.Document);
        var command = new NativeSessionSnapshotCommand("Create interior", f.Document, after.After, f.Assets, f.Assets, after.Effects);
        history.Execute(f.Document, command);
        string accepted = MapDocumentFile.SaveText(f.Document);
        Assert.NotEqual(before, accepted);
        Assert.True(history.Undo(f.Document)); Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        Assert.True(history.Redo(f.Document)); Assert.Equal(accepted, MapDocumentFile.SaveText(f.Document));
    }

}
