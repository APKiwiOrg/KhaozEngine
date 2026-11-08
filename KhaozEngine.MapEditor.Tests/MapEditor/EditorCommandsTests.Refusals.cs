using System;
using System.Collections.Generic;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public partial class EditorCommandsTests
{
    static MapEditorCommandException AssertIdentityRefusal(EditorDocument editor, IEditorCommand command,
        string kind, string id, IEditorCommand? control = null,
        MapDocumentRefusal reason = MapDocumentRefusal.IdentityDuplicate)
    {
        var doc = editor.Doc;
        string before = Save(doc);
        var history = HistoryState(editor);
        var error = EditorCommandRefusalAssertions.Throws(() => editor.Execute(command), reason, kind, id);
        Assert.Equal(doc.Id, error.DocumentId);
        Assert.Equal(kind is "scatter layer" or "companion layer" ? id : null, error.LayerId);
        Assert.Equal(kind switch
        {
            "placement" => "Placements",
            "spawn" => "Spawns",
            "player spawn" => "PlayerSpawns",
            "region" => "Regions",
            "terrain feature" => "Terrain.Features",
            "exclusion" => "Exclusions",
            "scatter override" => "ScatterOverrides",
            "scatter layer" => "ScatterLayers",
            _ => "CompanionLayers",
        }, error.FieldPath);
        if (reason == MapDocumentRefusal.IdentityDuplicate && kind is "region" or "terrain feature" or "exclusion" or "scatter override" or "scatter layer" or "companion layer")
        {
            int index = Assert.IsType<int>(error.Index);
            Assert.Equal(id, kind switch
            {
                "region" => doc.Regions[index].Name,
                "terrain feature" => doc.Terrain.Features[index].Name,
                "exclusion" => doc.Exclusions[index].Name,
                "scatter override" => doc.ScatterOverrides[index].Name,
                "scatter layer" => doc.ScatterLayers[index].Name,
                _ => doc.CompanionLayers[index].Name,
            });
        }
        Assert.Equal(before, Save(doc));
        Assert.Equal(history, HistoryState(editor));
        if (control is not null)
        {
            var repaired = new EditorDocument(MapDocumentFile.LoadText(before));
            repaired.Execute(control);
            Assert.NotEqual(before, Save(repaired.Doc));
        }
        return error;
    }

    static (bool Undo, bool Redo, string? UndoLabel, string? RedoLabel) HistoryState(EditorDocument editor) =>
        (editor.History.CanUndo, editor.History.CanRedo, editor.History.UndoLabel, editor.History.RedoLabel);

    [Theory]
    [InlineData("placement")]
    [InlineData("spawn")]
    [InlineData("player spawn")]
    [InlineData("region")]
    [InlineData("scatter layer")]
    [InlineData("companion layer")]
    public void MissingCommandTarget_PreservesContentAndBothHistoryStacks(string kind)
    {
        var doc = Sample();
        doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 1, Z = 2 });
        string initial = Save(doc);
        var editor = new EditorDocument(doc);
        editor.Execute(new RenameRegionCommand("town", "previous-town"));
        editor.Execute(new RenamePlacementCommand("inn", "previous-inn"));
        Assert.True(editor.Undo());
        Assert.True(editor.History.CanUndo);
        Assert.True(editor.History.CanRedo);
        string present = kind switch
        {
            "placement" => "inn",
            "spawn" => "wolf-1",
            "player spawn" => "start",
            "region" => "previous-town",
            "scatter layer" => "trees",
            _ => "understory",
        };
        IEditorCommand Rename(string source) => kind switch
        {
            "placement" => new RenamePlacementCommand(source, "new-target"),
            "spawn" => new RenameSpawnCommand(source, "new-target"),
            "player spawn" => new RenamePlayerSpawnCommand(source, "new-target"),
            "region" => new RenameRegionCommand(source, "new-target"),
            "scatter layer" => new RenameScatterLayerCommand(source, "new-target"),
            _ => new RenameCompanionLayerCommand(source, "new-target"),
        };
        AssertIdentityRefusal(editor, Rename("missing-target"), kind, "missing-target", Rename(present),
            MapDocumentRefusal.ReferenceMissing);
        Assert.True(editor.Redo());
        Assert.Equal("previous-inn", doc.Placements[0].Id);
        Assert.True(editor.Undo());
        Assert.True(editor.Undo());
        Assert.Equal(initial, Save(doc));
    }

    [Fact]
    public void ReferencingSources_AreOwnedReadOnlySnapshots()
    {
        string[] sources = ["understory", "ScatterOverrides[0]"];
        var error = new MapEditorCommandException(MapDocumentRefusal.EditorScatterLayerReferenced, "referenced layer")
        { SourceIds = sources };
        sources[0] = "changed";
        Assert.Equal(new[] { "understory", "ScatterOverrides[0]" }, error.SourceIds);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)error.SourceIds!)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)error.SourceIds!).SyncRoot);
    }

    [Fact]
    public void UnnamedScatterReferrer_UsesItsIndexAddress()
    {
        var doc = Sample();
        doc.CompanionLayers.Clear();
        doc.ScatterOverrides.Clear();
        doc.Exclusions[0].Name = null;
        doc.Exclusions[0].Layers = ["trees"];
        var editor = new EditorDocument(doc);
        var error = AssertIdentityRefusal(editor, new RemoveScatterLayerCommand("trees"), "scatter layer", "trees",
            reason: MapDocumentRefusal.EditorScatterLayerReferenced);
        Assert.Equal(new[] { "Exclusions[0]" }, error.SourceIds);
        doc.Exclusions[0].Layers = null;
        editor.Execute(new RemoveScatterLayerCommand("trees"));
        Assert.Empty(doc.ScatterLayers);
    }

    [Fact]
    public void FreezeWithMissingHost_PreservesContentAndHistoryThenAcceptsRepair()
    {
        var doc = Sample();
        var editor = new EditorDocument(doc);
        editor.Execute(new RenamePlacementCommand("inn", "renamed-inn"));
        string host = doc.CompanionLayers[0].HostLayer;
        doc.CompanionLayers[0].HostLayer = "missing-host";
        var options = MapDocumentFile.CreateOptions(editor.Registry, write: false);
        string before = JsonSerializer.Serialize(doc, options);
        var history = HistoryState(editor);
        var command = new FreezeZoneCommand(editor.Registry);
        var error = Assert.Throws<MapDocumentException>(() => editor.Execute(command));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal(doc.Id, error.DocumentId);
        Assert.Equal("understory", error.LayerId);
        Assert.Equal("missing-host", error.EntityId);
        Assert.Equal(before, JsonSerializer.Serialize(doc, options));
        Assert.Equal(history, HistoryState(editor));
        doc.CompanionLayers[0].HostLayer = host;
        string repaired = Save(doc);
        editor.Execute(command);
        Assert.Empty(doc.CompanionLayers);
        Assert.True(editor.Undo());
        Assert.Equal(repaired, Save(doc));
    }

    [Fact]
    public void MissingMarkerClosure_ReportsTheCallbackAndAcceptsItsRepair()
    {
        var doc = Sample();
        string before = Save(doc);
        var error = Assert.Throws<MapDocumentException>(() => MapEditorScene.RequireMarkerAssetsForTests(_ => null!, doc));
        Assert.Equal(MapDocumentRefusal.AssetClosureRequired, error.Reason);
        Assert.Equal(doc.Id, error.DocumentId);
        Assert.Equal("MarkerAssets", error.FieldPath);
        Assert.Equal(before, Save(doc));
        MapAssetClosure assets = PrefabFixtures.Village().Assets;
        MapEditorScene.RequireMarkerAssetsForTests(_ => assets, doc);
    }
}
