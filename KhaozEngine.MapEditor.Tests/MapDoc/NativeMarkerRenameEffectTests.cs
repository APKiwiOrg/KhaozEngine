using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Prefab override and asset update transactions report one rename row from the shared marker diff, reversed
/// by undo and restored by redo.</summary>
public sealed class NativeMarkerRenameEffectTests
{
    static readonly MapPrefabChildKey EntryKey = new(MapPrefabChildKind.Marker, "entry");

    [Fact]
    public void PrefabOverrideRename_ReportsOldAndNewNames()
    {
        var fixture = MarkerFixtures.Create("prefab-basic");
        var history = PrefabEditorFixtures.History(fixture.Document, fixture.Assets);
        var entry = (MapPrefabMarkerChild)fixture.Assets.GetPrefab(MarkerFixtures.GoldenResource).Children.Single(c => c.Key == EntryKey);
        var before = Snapshot(fixture.Document);

        history.Execute(fixture.Document, new PrefabEditCommand(new MapSetPrefabOverride("a",
            new MapPrefabReplaceOverride(EntryKey, entry with { Value = entry.Value with { Name = "arrival-b" } }))));
        var overrideEffects = history.LastNativeEffects!;
        var after = Snapshot(fixture.Document);
        Assert.True(history.Undo(fixture.Document));
        var undoEffects = history.LastNativeEffects!;
        Assert.True(history.Redo(fixture.Document));
        var redoEffects = history.LastNativeEffects!;

        Assert.Equal(new MapMarkerNameChange("a/entry", "arrival-a", "arrival-b"), Assert.Single(overrideEffects.MarkerNameChanges));
        Assert.Equal(new MapMarkerNameChange("a/entry", "arrival-b", "arrival-a"), Assert.Single(undoEffects.MarkerNameChanges));
        Assert.Equal(new MapMarkerNameChange("a/entry", "arrival-a", "arrival-b"), Assert.Single(redoEffects.MarkerNameChanges));
        Assert.Equal(new[] { "a/entry" }, overrideEffects.MarkerIds);
        // The marker diff of a rename adds no bounds and no invalidation. The prefab edit's own flags are its own.
        var renameOnly = NativeMarkerEffects.Combine(new(null, null, [], [], [], [], MapNativeInvalidation.None),
            MarkerFixtures.Effective(new(before, fixture.Assets)), MarkerFixtures.Effective(new(after, fixture.Assets)));
        Assert.Equal(MapNativeInvalidation.None, renameOnly.Invalidates & (MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Unbounded));
        Assert.Equal(MapNativeInvalidation.None, renameOnly.Invalidates);
        Assert.Null(renameOnly.OldBounds);
        Assert.Null(renameOnly.NewBounds);
        Assert.Equal(overrideEffects.MarkerNameChanges, renameOnly.MarkerNameChanges);
    }

    [Fact]
    public void AssetUpdateRename_ReportsOldAndNewNames()
    {
        var fixture = MarkerFixtures.Create("prefab-basic");
        var history = PrefabEditorFixtures.History(fixture.Document, fixture.Assets);
        var renamed = MarkerFixtures.RenamedAsset(fixture, "arrival-renamed");
        string oldSha = fixture.Assets.GetResource(MarkerFixtures.GoldenResource).Reference.Sha256;
        string newSha = renamed.GetResource(MarkerFixtures.GoldenResource + "-2").Reference.Sha256;

        history.Execute(fixture.Document, new PrefabAssetEditCommand(
            new MapUpdatePrefabAsset(MarkerFixtures.GoldenResource, oldSha, MarkerFixtures.GoldenResource + "-2", newSha), fixture.Assets, renamed));
        var assetEffects = history.LastNativeEffects!;
        Assert.True(history.Undo(fixture.Document));
        var undoEffects = history.LastNativeEffects!;
        Assert.True(history.Redo(fixture.Document));
        var redoEffects = history.LastNativeEffects!;

        Assert.Equal(new MapMarkerNameChange("a/entry", "arrival-a", "arrival-renamed"), Assert.Single(assetEffects.MarkerNameChanges));
        Assert.Equal(new MapMarkerNameChange("a/entry", "arrival-renamed", "arrival-a"), Assert.Single(undoEffects.MarkerNameChanges));
        Assert.Equal(new MapMarkerNameChange("a/entry", "arrival-a", "arrival-renamed"), Assert.Single(redoEffects.MarkerNameChanges));
        Assert.Equal(new[] { "a/entry" }, assetEffects.MarkerIds);
    }

    static MapDocument Snapshot(MapDocument document) => NativeDocumentSnapshot.Clone(document, MapDocRegistry.CreateDefault());
}
