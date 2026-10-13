using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeSnapshotPublicationTests
{
    static MapNativeEditEffects Effects => new(null, null, [], [], [], [], MapNativeInvalidation.Unbounded);
    [Fact]
    public void CompleteSnapshot_PublishesEveryRootAndKeepsReservations()
    {
        var f = PrefabFixtures.CapturedOriginals();
        var before = NativeDocumentSnapshot.Clone(f.Document, MapDocRegistry.CreateDefault());
        var after = MapPrefabEdits.Prepare(before, f.Assets, new MapUnpackPrefab("a")).After;
        after.ScatterOverrides.Add(new() { Name = "density", Shape = new RectShapeDoc { MaxX = 2, MaxZ = 2 }, DensityMultiplier = 0.25f });
        after.Exclusions.Add(new() { Name = "clear", Shape = new RectShapeDoc { MaxX = 2, MaxZ = 2 } });
        after.Terrain.GentleAmplitude = 2;
        after.Regions.Add(new() { Name = "zone", Shape = new RectShapeDoc { MaxX = 2, MaxZ = 2 } });
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        h.Execute(f.Document, new NativeCompleteSnapshotCommand("whole", before, after, f.Assets, f.Assets, Effects));
        Assert.Equal(0.25f, Assert.Single(f.Document.ScatterOverrides).DensityMultiplier);
        Assert.Equal("clear", Assert.Single(f.Document.Exclusions).Name);
        Assert.Equal(2, f.Document.Terrain.GentleAmplitude);
        Assert.Equal("zone", Assert.Single(f.Document.Regions).Name);
        Assert.Equal(after.Prefabs!.Digest(), f.Document.Prefabs!.Digest());
        Assert.Equal(after.NativeResourceTransforms, f.Document.NativeResourceTransforms);
        Assert.True(h.Undo(f.Document));
        Assert.Empty(f.Document.ScatterOverrides); Assert.Empty(f.Document.Exclusions); Assert.Empty(f.Document.Regions);
        Assert.Equal(1.5f, f.Document.Terrain.GentleAmplitude);
        Assert.Empty(f.Document.Placements);
        Assert.Equal(after.NumericIdHighWaterMark, f.Document.NumericIdHighWaterMark);
        Assert.DoesNotContain(f.Document.Prefabs!.Transfers, t => !t.Retired);
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Redo(f.Document));
        Assert.Equal(after.Prefabs.Digest(), f.Document.Prefabs!.Digest());
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
    }
    [Fact]
    public void CompleteSnapshot_UndoKeepsUnpackedTombstonesWithoutLiveOwners()
    {
        var f = PrefabFixtures.SpacelessRoomless();
        var before = NativeDocumentSnapshot.Clone(f.Document, MapDocRegistry.CreateDefault());
        var placed = MapPrefabEdits.Prepare(before, f.Assets, new MapPlacePrefab("new", "hut", new(default, 0, 1))).After;
        var after = MapPrefabEdits.Prepare(placed, f.Assets, new MapUnpackPrefab("new")).After;
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        h.Execute(f.Document, new NativeCompleteSnapshotCommand("whole", before, after, f.Assets, f.Assets, Effects));
        Assert.True(h.Undo(f.Document));
        var tombstone = Assert.Single(f.Document.Prefabs!.Instances);
        Assert.True(tombstone.Removed); Assert.True(tombstone.Unpacked);
        Assert.NotEmpty(tombstone.Bindings.Entries);
        Assert.All(f.Document.Prefabs.Transfers, t => Assert.True(t.Retired));
        Assert.Empty(f.Document.Placements);
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Redo(f.Document));
        Assert.Single(f.Document.Placements);
    }
    [Fact]
    public void CompleteSnapshot_RemapUndoRetainsOneTransferPerSlot()
    {
        var f = PrefabEditorFixtures.FarLeaf(true);
        var before = NativeDocumentSnapshot.Clone(f.Document, MapDocRegistry.CreateDefault());
        var after = NativeDocumentSnapshot.Clone(before, MapDocRegistry.CreateDefault());
        var prepare = PrefabEditorFixtures.History(after, f.Assets);
        prepare.Execute(after, new RemapPlacementIdCommand("deck-owner", "renamed-owner"));
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        h.Execute(f.Document, new NativeCompleteSnapshotCommand("remapped snapshot", before, after, f.Assets, f.Assets, Effects));
        Assert.Equal(MapDocumentFile.SaveText(after), MapDocumentFile.SaveText(f.Document));
        Assert.True(h.Undo(f.Document)); Assert.Equal(MapDocumentFile.SaveText(before), MapDocumentFile.SaveText(f.Document));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Redo(f.Document)); Assert.Equal(MapDocumentFile.SaveText(after), MapDocumentFile.SaveText(f.Document));
    }
    [Fact]
    public void CompleteSnapshot_RejectsEveryRootAtomically()
    {
        var f = PrefabFixtures.Create();
        var before = NativeDocumentSnapshot.Clone(f.Document, MapDocRegistry.CreateDefault());
        var after = NativeDocumentSnapshot.Clone(before, MapDocRegistry.CreateDefault());
        after.ScatterOverrides.Add(new() { DensityMultiplier = float.NaN });
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        string original = MapDocumentFile.SaveText(f.Document);
        Assert.Throws<MapDocumentException>(() => h.Execute(f.Document,
            new NativeCompleteSnapshotCommand("invalid", before, after, f.Assets, f.Assets, Effects)));
        Assert.Equal(original, MapDocumentFile.SaveText(f.Document));
        Assert.False(h.CanUndo);
    }
    [Fact]
    public void SnapshotWriteSet_OmitsUnlistedRootChanges()
    {
        var f = PrefabFixtures.Create();
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        h.Execute(f.Document, new UnlistedCommand());
        Assert.Empty(f.Document.ScatterOverrides);
        Assert.Equal("named", f.Document.Placements.Single().Id);
    }
    sealed class UnlistedCommand : EditorCommand, INativeDocumentCommand
    {
        public override string Label => "isolation";
        internal override bool AffectsWorld => false;
        public override void Apply(MapDocument d) => throw new NotSupportedException();
        public override void Revert(MapDocument d) => throw new NotSupportedException();
        public NativeDocumentPreparation Prepare(MapDocument d, bool undo)
        {
            d.ScatterOverrides.Add(new() { DensityMultiplier = float.NaN });
            d.Placements.Add(new() { Id = "named", Kind = "item", AssetId = "item", Y = 0 });
            return new(MapNativeWriteSet.PlacementsOnly, Effects, () => { });
        }
    }
    [Fact]
    public void StageDoesNotAcceptBeforeOuterPublication()
    {
        var f = PrefabEditorFixtures.Create("empty-map");
        var command = new PrefabEditCommand(new MapPlacePrefab("new", "hut", new(new(10.25f, 5, -3.125f), 0.371f, 1.2f), PrefabEditorFixtures.ParentRef(f)));
        string before = MapDocumentFile.SaveText(f.Document);
        var staged = NativeDocumentTransaction.Stage(f.Document, command, false, f.Assets);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        Assert.Throws<InvalidOperationException>(() => NativeDocumentTransaction.Stage(f.Document, command, true, f.Assets));
        var retry = NativeDocumentTransaction.Stage(f.Document, command, false, f.Assets);
        Assert.Equal(staged.Candidate.Prefabs!.Digest(), retry.Candidate.Prefabs!.Digest());
        NativeDocumentSnapshot.PublishWriteSet(f.Document, retry.Candidate, retry.WriteSet);
        retry.Accept();
        Assert.NotEmpty(f.Document.Prefabs!.Instances);
    }
    [Fact]
    public void OtherNativeEdits_PreservePrefabAndIntentRoots()
    {
        var f = PrefabFixtures.Create();
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        h.Execute(f.Document, new PrefabEditCommand(new MapUnpackPrefab("a")));
        string prefabs = f.Document.Prefabs!.Digest(), intent = f.Document.BuildingIntent!.Digest();
        h.SealGesture();
        h.Execute(f.Document, new SetPlacementLabelCommand("a/item", "label"));
        Assert.Equal(prefabs, f.Document.Prefabs!.Digest()); Assert.Equal(intent, f.Document.BuildingIntent!.Digest());
        Assert.True(h.Undo(f.Document)); Assert.True(h.Redo(f.Document));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
    }
}
