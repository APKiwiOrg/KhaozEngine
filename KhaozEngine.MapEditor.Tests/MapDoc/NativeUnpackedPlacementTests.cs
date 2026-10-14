using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeUnpackedPlacementTests
{
    [Theory]
    [InlineData("move")]
    [InlineData("rotate")]
    [InlineData("scale")]
    public void MergedAttachedPlacement_RedoEqualsFinalAcceptedSnapshot(string operation)
    {
        var f = PrefabEditorFixtures.CoalescedAttachedLeaf(operation);
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        string before = MapDocumentFile.SaveText(f.Document);
        h.Execute(f.Document, f.First);
        var middle = NativeDocumentSnapshot.Clone(f.Document, MapDocRegistry.CreateDefault());
        h.Execute(f.Document, f.Second);
        AssertTiles(middle, f.Document, f.Assets, h.LastNativeEffects!);
        string final = MapDocumentFile.SaveText(f.Document);
        Assert.Equal(1, h.UndoDepth);
        Assert.NotNull(h.LastNativeEffects!.OldBounds); Assert.NotNull(h.LastNativeEffects.NewBounds);
        Assert.False(h.LastNativeEffects.Invalidates.HasFlag(MapNativeInvalidation.Unbounded));
        Assert.Throws<MapDocumentException>(() => h.Execute(f.Document, f.Rejected));
        Assert.Equal(final, MapDocumentFile.SaveText(f.Document));
        Assert.Equal(1, h.UndoDepth);
        Assert.True(h.Undo(f.Document));
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Redo(f.Document));
        Assert.Equal(final, MapDocumentFile.SaveText(f.Document));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
    }
    [Theory]
    [InlineData("move", false)]
    [InlineData("rotate", false)]
    [InlineData("scale", false)]
    [InlineData("label", false)]
    [InlineData("rename", false)]
    [InlineData("remap", false)]
    [InlineData("remove", false)]
    [InlineData("same-rotate", false)]
    [InlineData("same-move", false)]
    [InlineData("move", true)]
    [InlineData("rotate", true)]
    [InlineData("scale", true)]
    [InlineData("label", true)]
    [InlineData("remap", true)]
    [InlineData("remove", true)]
    public void UnpackedPreciseLeaf_ExistingCommandsPublishEveryRoot(string operation, bool attached)
    {
        var f = PrefabEditorFixtures.FarLeaf(attached);
        string id = f.Document.Placements.Single().Id;
        var original = f.Document.NativeResourceTransforms!.Single(p => p.Kind == MapPrefabChildKind.Placement);
        var pose = original.Transform;
        var origin = new MapResourceTransform(pose).Point(new(default, default, default));
        Assert.Equal(new MapExactValue(31460408117, 1048576), origin.X);
        Assert.Equal(new MapExactValue(-31463533353, 1048576), origin.Z);
        var presentation = f.Document.Placements.Single();
        IEditorCommand command = operation switch
        {
            "move" => new MovePlacementCommand(id, 30003.25f, -30006.125f, -450),
            "rotate" => new RotatePlacementCommand(id, 0.5f),
            "scale" => new ScalePlacementCommand(id, 0.8f),
            "label" => new SetPlacementLabelCommand(id, "label"),
            "rename" => new RenamePlacementCommand(id, "label"),
            "remap" => new RemapPlacementIdCommand(id, "remapped"),
            "remove" => new RemovePlacementCommand(id),
            "same-rotate" => new RotatePlacementCommand(id, presentation.Yaw),
            "same-move" => new MovePlacementCommand(id, presentation.X, presentation.Z, presentation.Y),
            _ => throw new ArgumentException(operation),
        };
        var expected = operation switch
        {
            "move" => pose with { X = 30003.25f, Y = -450, Z = -30006.125f },
            "rotate" => pose with { YawRadians = 0.5f },
            "scale" => pose with { Scale = 0.8f },
            "same-rotate" => pose with { YawRadians = presentation.Yaw },
            "same-move" => pose with { X = presentation.X, Y = presentation.Y!.Value, Z = presentation.Z },
            _ => pose,
        };
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        string before = MapDocumentFile.SaveText(f.Document);
        long mark = f.Document.NumericIdHighWaterMark;
        h.Execute(f.Document, command);
        if (operation == "remove")
        {
            Assert.Empty(f.Document.Placements);
            Assert.Empty(f.Document.NativeResourceTransforms!);
            Assert.Null(h.LastNativeEffects!.NewBounds);
            Assert.All(f.Document.Prefabs!.Transfers, t => Assert.True(t.Retired));
            Assert.All(f.Document.Prefabs.Attachments, a => Assert.True(a.Retired));
        }
        else
        {
            var actual = f.Document.NativeResourceTransforms!.Single(p => p.Kind == MapPrefabChildKind.Placement);
            Assert.Equal(operation == "remap" ? "remapped" : id, actual.OwnerId);
            Assert.Equal(expected, actual.Transform);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected.X), BitConverter.DoubleToInt64Bits(actual.Transform.X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected.YawRadians), BitConverter.DoubleToInt64Bits(actual.Transform.YawRadians));
            if (attached)
            {
                Assert.Equal(new[] { "captured-floor", "captured-light" }, f.Document.Prefabs!.Attachments.SelectMany(a => a.Children).Select(c => c.WorldId).Order());
                Assert.All(f.Document.Prefabs.Attachments, a => Assert.Equal(actual.OwnerId, a.PlacementId));
                Assert.All(f.Document.NativeResourceTransforms!.Where(p => p.Kind != MapPrefabChildKind.Placement), p => Assert.Equal(expected, p.Transform));
                Assert.Single(MapEffectiveAnnotations.Lights(MapPrefabResolver.Expand(f.Document, f.Assets)));
            }
            if (operation is "move" or "rotate" or "scale" or "same-move" or "same-rotate")
            { Assert.NotNull(h.LastNativeEffects!.OldBounds); Assert.NotNull(h.LastNativeEffects.NewBounds); }
        }
        Assert.Equal(mark, f.Document.NumericIdHighWaterMark);
        if (operation is "move" or "rotate" or "scale" or "same-move" or "same-rotate" or "remove")
            AssertTiles(MapDocumentFile.LoadText(before), f.Document, f.Assets, h.LastNativeEffects!);
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        string final = MapDocumentFile.SaveText(f.Document);
        Assert.True(h.Undo(f.Document));
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
        Assert.True(h.Redo(f.Document));
        Assert.Equal(final, MapDocumentFile.SaveText(f.Document));
        PrefabEditorFixtures.Reload(f.Document, f.Assets);
    }
    [Fact]
    public void FloorReseat_InvalidatesEveryChangedNeighborTile()
    {
        var (f, edit) = PrefabEditorFixtures.SupportReseat();
        var options = new MapWorldBuildOptions(PrefabFixtures.Options(), "fixture", new());
        var before = MapWorldBuilder.Build(f.Document, f.Assets, options);
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        h.Execute(f.Document, new TerrainEditCommand(edit));
        var after = MapWorldBuilder.Build(f.Document, f.Assets, options);
        var grids = new MapWorldGrids(64f, 2, 4, Vector2.Zero);
        var nav = new MapNavTileOptions(0.5f, "test-profile", "test-controller");
        var a = MapNavTiling.Partition(before, grids, nav).ToDictionary(t => t.Coord);
        var b = MapNavTiling.Partition(after, grids, nav).ToDictionary(t => t.Coord);
        var changed = a.Keys.Union(b.Keys).Where(c => !a.ContainsKey(c) || !b.ContainsKey(c) || a[c].CaptureIdentity != b[c].CaptureIdentity).ToArray();
        Assert.Contains(changed, c => c != new MapNavTileCoord(0, 0));
        var affected = MapNavTiling.AffectedTiles(after, grids, nav, h.LastNativeEffects!).ToHashSet();
        Assert.All(changed, c => Assert.Contains(c, affected));
        Assert.False(h.LastNativeEffects!.Invalidates.HasFlag(MapNativeInvalidation.Unbounded));
        Assert.True(h.Undo(f.Document)); Assert.True(h.Redo(f.Document));
    }
    [Fact]
    public void UnpackedMissingY_FloorEditReseatsPreservedDependency()
    {
        var f = PrefabFixtures.SupportAt(true);
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var h = PrefabEditorFixtures.History(unpack, f.Assets);
        var leaf = unpack.Placements.Single(p => p.Id == "low-item");
        Assert.Null(leaf.Y);
        Assert.NotNull(leaf.SupportBinding);
        var before = unpack.NativeResourceTransforms!.Single(p => p.Kind == MapPrefabChildKind.Placement && p.OwnerId == leaf.Id).Transform;
        var patch = unpack.Surfaces.Patches[new(PrefabFixtures.SupportFloorId, 0, 0)];
        h.Execute(unpack, new TerrainEditCommand(new MapSetCornerHeights(patch.Key, 0, 0, patch.Width + 1, patch.Depth + 1,
            Enumerable.Repeat(50, patch.Heights.Length).ToArray())));
        var after = unpack.NativeResourceTransforms!.Single(p => p.Kind == MapPrefabChildKind.Placement && p.OwnerId == leaf.Id).Transform;
        Assert.Equal(before with { Y = (double)-449.4f }, after);
        Assert.Null(unpack.Placements.Single(p => p.Id == leaf.Id).Y);
        PrefabEditorFixtures.Reload(unpack, f.Assets);
        Assert.True(h.Undo(unpack)); Assert.Equal(before, unpack.NativeResourceTransforms!.Single(p => p.Kind == MapPrefabChildKind.Placement && p.OwnerId == leaf.Id).Transform);
        Assert.True(h.Redo(unpack)); Assert.Equal(after, unpack.NativeResourceTransforms!.Single(p => p.Kind == MapPrefabChildKind.Placement && p.OwnerId == leaf.Id).Transform);
    }
    [Fact]
    public void UnpackedSpaceBoundMissingY_FloorEditReseatsWorldRequestAndUndoes()
    {
        // F-R2. Unpack persists the world request from the pre-seat frame, so a floor edit reseats against the same
        // request after the seated pose moved, through history, reload, undo and redo.
        var f = PrefabFixtures.SpaceBoundChild(10);
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var h = PrefabEditorFixtures.History(unpack, f.Assets);
        var leaf = unpack.Placements.Single(p => p.Id == "consumer");
        Assert.Null(leaf.Y);
        var request = leaf.SupportBinding!;
        Assert.Equal((MapSupportBindingKind.Space, 12.64f), (request.Kind, request.ReferenceY!.Value));
        MapEffectiveTransform Pose() => unpack.NativeResourceTransforms!.Single(p => p.Kind == MapPrefabChildKind.Placement && p.OwnerId == leaf.Id).Transform;
        var before = Pose();
        Assert.Equal(12.4f, (float)before.Y, 5);
        var patch = unpack.Surfaces.Patches[new(PrefabFixtures.SupportFloorId, 0, 0)];
        h.Execute(unpack, new TerrainEditCommand(new MapSetCornerHeights(patch.Key, 0, 0, patch.Width + 1, patch.Depth + 1,
            Enumerable.Repeat(50, patch.Heights.Length).ToArray())));
        var after = Pose();
        Assert.Equal(before with { Y = (double)13.0f }, after);
        Assert.Equal(request, unpack.Placements.Single(p => p.Id == leaf.Id).SupportBinding);
        PrefabEditorFixtures.Reload(unpack, f.Assets);
        Assert.True(h.Undo(unpack)); Assert.Equal(before, Pose());
        Assert.Equal(request, unpack.Placements.Single(p => p.Id == leaf.Id).SupportBinding);
        Assert.True(h.Redo(unpack)); Assert.Equal(after, Pose());
        PrefabEditorFixtures.Reload(unpack, f.Assets);
    }
    static void AssertTiles(MapDocument before, MapDocument after, MapAssetClosure assets, MapNativeEditEffects effects)
    {
        var options = new MapWorldBuildOptions(PrefabFixtures.Options(), "fixture", new());
        var a = MapWorldBuilder.Build(before, assets, options);
        var b = MapWorldBuilder.Build(after, assets, options);
        var grids = new MapWorldGrids(64f, 2, 4, Vector2.Zero);
        var nav = new MapNavTileOptions(0.5f, "test-profile", "test-controller");
        var at = MapNavTiling.Partition(a, grids, nav).ToDictionary(t => t.Coord);
        var bt = MapNavTiling.Partition(b, grids, nav).ToDictionary(t => t.Coord);
        var changed = at.Keys.Union(bt.Keys).Where(c => !at.ContainsKey(c) || !bt.ContainsKey(c) || at[c].CaptureIdentity != bt[c].CaptureIdentity).ToArray();
        var affected = MapNavTiling.AffectedTiles(a, b, grids, nav, effects).ToHashSet();
        Assert.All(changed, c => Assert.Contains(c, affected));
        var ar = MapResidencyOwnership.Build(a, grids).ToDictionary(e => e.OwnerId);
        var br = MapResidencyOwnership.Build(b, grids).ToDictionary(e => e.OwnerId);
        var owners = ar.Keys.Union(br.Keys).Where(id => !ar.ContainsKey(id) || !br.ContainsKey(id) || ar[id].WorldBounds != br[id].WorldBounds).ToHashSet();
        var tiles = ar.Values.Concat(br.Values).Where(e => owners.Contains(e.OwnerId)).SelectMany(e => e.Membership).Distinct();
        var residency = MapResidencyOwnership.Affected(a, b, grids, effects).StorageTiles;
        Assert.All(tiles, c => Assert.Contains(c, residency));
    }
    [Fact]
    public void DependentRoomRemoval_RefusesAtomically()
    {
        var f = PrefabFixtures.Create("support-resource");
        var document = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var h = PrefabEditorFixtures.History(document, f.Assets);
        string before = MapDocumentFile.SaveText(document);
        Assert.Throws<MapDocumentException>(() => h.Execute(document, new RemovePlacementCommand(document.Placements.Single(p => p.AssetId == "deck").Id)));
        Assert.Equal(before, MapDocumentFile.SaveText(document));
        Assert.False(h.CanUndo);
    }
}
