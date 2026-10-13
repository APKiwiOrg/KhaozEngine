using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Water;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativePrefabCommandTests
{
    [Fact]
    public void PrefabPlace_UndoRedoRestoresAllocation()
    {
        var f = PrefabEditorFixtures.Create("empty-map");
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        h.Execute(f.Document, Place(f));
        var accepted = PrefabFixtures.LiveBindings(f.Document).ToArray();
        long mark = f.Document.NumericIdHighWaterMark;
        Assert.NotEmpty(accepted);
        Assert.True(h.Undo(f.Document));
        Assert.Equal(mark, f.Document.NumericIdHighWaterMark);
        Assert.Empty(PrefabFixtures.LiveBindings(f.Document));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Redo(f.Document));
        Assert.Equal(accepted, PrefabFixtures.LiveBindings(f.Document));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
    }
    static PrefabEditCommand Place(PrefabFixture f) => new(new MapPlacePrefab("new", "hut",
        new(new(10.25f, 5, -3.125f), 0.371f, 1.2f), PrefabEditorFixtures.ParentRef(f)));

    [Fact]
    public void BoundPreparation_FreshCommandReceivesClosureAndCanRetry()
    {
        var f = PrefabEditorFixtures.Create("empty-map");
        var expected = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapPlacePrefab("new", "hut",
            new(new(10.25f, 5, -3.125f), 0.371f, 1.2f), PrefabEditorFixtures.ParentRef(f))).After;
        var h = new EditorHistory();
        h.BindNativeAssets(f.Document, f.Assets, new FailOnceBounds());
        var command = Place(f);
        string before = MapDocumentFile.SaveText(f.Document);
        Assert.Throws<InvalidOperationException>(() => h.Execute(f.Document, command));
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        Assert.False(h.CanUndo);
        Assert.Null(h.LastNativeEffects);
        h.Execute(f.Document, command);
        Assert.Equal(expected.Prefabs!.Digest(), f.Document.Prefabs!.Digest());
        Assert.Equal(expected.NumericIdHighWaterMark, f.Document.NumericIdHighWaterMark);
        Assert.Equal(MapPrefabResolver.Expand(expected, f.Assets).Digest, MapPrefabResolver.Expand(f.Document, f.Assets).Digest);
    }
    sealed class FailOnceBounds : INativePlacementBounds
    {
        bool _fail = true;
        public NativePlacementExtent Bounds(MapDocument d, MapAssetClosure a, MapDocRegistry r, System.Collections.Generic.IReadOnlyList<string> ids)
        {
            if (_fail) { _fail = false; throw new InvalidOperationException("one-shot bounds fault"); }
            return NativePlacementBoundsProvider.Instance.Bounds(d, a, r, ids);
        }
    }
    [Fact]
    public void ResourceCommandWithoutClosure_RefusesBeforePreparation()
    {
        var f = PrefabEditorFixtures.Create("empty-map");
        string before = MapDocumentFile.SaveText(f.Document);
        Assert.Equal("Prefab editing requires a verified asset closure.", Assert.Throws<MapDocumentException>(() =>
            NativeDocumentTransaction.Stage(f.Document, Place(f), false, null, localOnly: true)).Message);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
    }
    [Fact]
    public void LegacyLocalOnlyWaterAndPlacement_StillApplyRevert()
    {
        var document = WaterFixtures.OceanOverCaves();
        Assert.Equal(2, document.ResolverIdentity!.ResolverVersion);
        Assert.Null(document.Prefabs); Assert.Null(document.NativeResourceTransforms);
        string original = MapDocumentFile.SaveText(document);
        var water = new WaterEditCommand(new MapUpsertWaterBody(document.Water!.Bodies.Single(b => b.Id == "cave-lake") with { SurfaceY = -33 }));
        water.Apply(document); water.Revert(document);
        Assert.Equal(original, MapDocumentFile.SaveText(document));
        var placement = new AllocateNativePlacementCommand(new() { Id = "local", Kind = "prop", AssetId = "mesh-only", Y = 0 }, true);
        placement.Apply(document);
        long mark = document.NumericIdHighWaterMark;
        placement.Revert(document);
        Assert.Equal(mark, document.NumericIdHighWaterMark);
        Assert.DoesNotContain(document.Placements, p => p.Id == "local");
        document.NumericIdHighWaterMark = MapDocumentFile.LoadText(original).NumericIdHighWaterMark;
        Assert.Equal(original, MapDocumentFile.SaveText(document));
    }
    [Theory]
    [InlineData("owner-placement", 7.25f, 0.5f, -2.5f, 7.25f, 2f, -2.5f)]
    [InlineData("owner-transform", 7.25f, 0.5f, -2.5f, 7.25f, 2f, -2.5f)]
    [InlineData("floor", 3.5f, 0.75f, -3.25f, 4f, 2f, -4f)]
    [InlineData("light", 4f, 0.5f, -4f, 4.75f, 2.5f, -5.25f)]
    public void MaterializedOverrideHistory_UndoRedoRemoveRestorePreservesFrames(string kind,
        float fx, float fy, float fz, float lx, float ly, float lz)
    {
        var f = PrefabEditorFixtures.ReExtracted();
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        var bindings = f.Document.Prefabs!.Instances.Single().Bindings.Entries;
        var baseline = MapPrefabResolver.Expand(f.Document, f.Assets).Transforms.OrderBy(t => t.Key).ToArray();
        var change = PrefabFixtures.MaterializedTransformOverride(kind);
        h.Execute(f.Document, new PrefabEditCommand(new MapSetPrefabOverride("copy", change)));
        var r = MapPrefabResolver.Expand(f.Document, f.Assets);
        string floor = bindings.Single(b => b.Child.Key == "captured-floor").WorldId;
        Assert.Equal(new MapExactPoint(MapExactValue.FromSingle(fx), MapExactValue.FromSingle(fy), MapExactValue.FromSingle(fz)),
            MapEffectiveGeometry.Patch(r.CompleteView(), new(floor, 0, 0)).ExactVertices[0]);
        Assert.Equal(new MapExactPoint(MapExactValue.FromSingle(lx), MapExactValue.FromSingle(ly), MapExactValue.FromSingle(lz)),
            Assert.Single(MapEffectiveAnnotations.Lights(r)).Position);
        Assert.Single(r.Surfaces.Refs, s => s.Id != "ground");
        var moved = r.Transforms.OrderBy(t => t.Key).ToArray();
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Undo(f.Document));
        Assert.Equal(baseline, MapPrefabResolver.Expand(f.Document, f.Assets).Transforms.OrderBy(t => t.Key));
        Assert.True(h.Redo(f.Document));
        Assert.Equal(moved, MapPrefabResolver.Expand(f.Document, f.Assets).Transforms.OrderBy(t => t.Key));
        h.SealGesture();
        h.Execute(f.Document, new PrefabEditCommand(new MapSetPrefabOverride("copy", new MapPrefabRemoveOverride(change.Child))));
        var removed = f.Document.Prefabs!.Instances.Single();
        Assert.Equal(change, Assert.IsType<MapPrefabRemoveOverride>(Assert.Single(removed.Overrides)).Previous);
        Assert.True(removed.Bindings.Entries.Single(b => b.Child == change.Child).Removed);
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Undo(f.Document));
        Assert.Equal(moved, MapPrefabResolver.Expand(f.Document, f.Assets).Transforms.OrderBy(t => t.Key));
        Assert.True(h.Redo(f.Document));
        h.SealGesture();
        h.Execute(f.Document, new PrefabEditCommand(new MapRestorePrefabChild("copy", change.Child)));
        Assert.Equal(moved, MapPrefabResolver.Expand(f.Document, f.Assets).Transforms.OrderBy(t => t.Key));
        Assert.Equal(bindings.Select(b => b.WorldId), f.Document.Prefabs!.Instances.Single().Bindings.Entries.Select(b => b.WorldId));
        Assert.True(h.Undo(f.Document));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Redo(f.Document));
        Assert.Equal(moved, MapPrefabResolver.Expand(f.Document, f.Assets).Transforms.OrderBy(t => t.Key));
    }
    [Theory]
    [InlineData(MapPrefabChildKind.Placement, "item")]
    [InlineData(MapPrefabChildKind.Surface, "pool-ceiling")]
    [InlineData(MapPrefabChildKind.Record, "room-portal")]
    [InlineData(MapPrefabChildKind.WaterMedium, "water")]
    [InlineData(MapPrefabChildKind.Storey, "lower")]
    [InlineData(MapPrefabChildKind.Light, "lamp")]
    [InlineData(MapPrefabChildKind.Marker, "entry")]
    [InlineData(MapPrefabChildKind.RoofLink, "roof-link")]
    [InlineData(MapPrefabChildKind.RoofGroup, "hut-rooms")]
    [InlineData(MapPrefabChildKind.WaterBody, "pool-water")]
    public void EveryChildKind_RemoveRestoreUndoReloadKeepsId(MapPrefabChildKind kind, string key)
    {
        var f = kind == MapPrefabChildKind.Storey ? PrefabEditorFixtures.IsolatedStorey() : PrefabFixtures.Create("all-kinds");
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        if (kind == MapPrefabChildKind.WaterMedium)
        {
            h.Execute(f.Document, new PrefabEditCommand(new MapSetPrefabOverride("a", new MapPrefabRemoveOverride(new(MapPrefabChildKind.WaterBody, "pool-water")))));
            h.SealGesture();
        }
        var child = new MapPrefabChildKey(kind, key);
        var binding = f.Document.Prefabs!.Instances.Single().Bindings.Entries.Single(b => b.Child == child);
        h.Execute(f.Document, new PrefabEditCommand(new MapSetPrefabOverride("a", new MapPrefabRemoveOverride(child))));
        Assert.True(f.Document.Prefabs!.Instances.Single().Bindings.Entries.Single(b => b.Child == child).Removed);
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Undo(f.Document));
        Assert.Equal(binding, f.Document.Prefabs!.Instances.Single().Bindings.Entries.Single(b => b.Child == child));
        Assert.True(h.Redo(f.Document));
        h.SealGesture();
        h.Execute(f.Document, new PrefabEditCommand(new MapRestorePrefabChild("a", child)));
        Assert.Equal(binding, f.Document.Prefabs!.Instances.Single().Bindings.Entries.Single(b => b.Child == child));
        Assert.True(h.Undo(f.Document)); Assert.True(h.Redo(f.Document));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
    }
    [Fact]
    public void StoreyUnpackUndoRedo_ReloadRetainsIdName()
    {
        var f = PrefabFixtures.Create();
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        h.Execute(f.Document, new PrefabEditCommand(new MapUnpackPrefab("a")));
        Assert.Equal(("a/lower", "Lower"), (Assert.Single(f.Document.BuildingIntent!.Storeys).Id, Assert.Single(f.Document.BuildingIntent.Storeys).Name));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Undo(f.Document));
        Assert.DoesNotContain(f.Document.Prefabs!.Transfers, t => !t.Retired);
        Assert.False(f.Document.Prefabs.Instances.Single().Removed);
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Redo(f.Document));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
    }
    [Fact]
    public void InteriorEdit_UndoRedoRestoresAcceptedDomain()
    {
        var f = PrefabFixtures.Create();
        var document = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var domain = document.Surfaces.AllRecords().OfType<KhaozEngine.MapDoc.Spaces.MapPrismDomain>().Single();
        var h = PrefabEditorFixtures.History(document, f.Assets);
        string before = MapDocumentFile.SaveText(document);
        h.Execute(document, new InteriorEditCommand(new MapSetInterior(domain with { UpperY = 2.5f })));
        Assert.Equal(2.5f, document.Surfaces.AllRecords().OfType<KhaozEngine.MapDoc.Spaces.MapPrismDomain>().Single().UpperY);
        PrefabEditorFixtures.Reload(document, f.Assets);
        string after = MapDocumentFile.SaveText(document);
        Assert.True(h.Undo(document)); Assert.Equal(before, MapDocumentFile.SaveText(document));
        Assert.True(h.Redo(document)); Assert.Equal(after, MapDocumentFile.SaveText(document));
    }
    [Fact]
    public void UnpackUndo_ReclaimsTransferredBindings()
    {
        var f = PrefabFixtures.CapturedOriginals();
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        h.Execute(f.Document, new PrefabEditCommand(new MapUnpackPrefab("a")));
        Assert.True(f.Document.Prefabs!.Instances.Single().Unpacked);
        Assert.NotEmpty(f.Document.Placements);
        Assert.True(h.Undo(f.Document));
        Assert.Empty(f.Document.Placements);
        Assert.False(f.Document.Prefabs!.Instances.Single().Unpacked);
        Assert.DoesNotContain(f.Document.Prefabs.Transfers, t => !t.Retired);
        Assert.True(h.Redo(f.Document));
        Assert.Single(f.Document.Placements);
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
    }
}
