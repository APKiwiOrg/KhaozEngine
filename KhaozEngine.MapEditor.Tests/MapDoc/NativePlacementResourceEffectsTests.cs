using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativePlacementResourceEffectsTests
{
    static readonly MapWorldGrids Grids = new(64f, 1, 4, Vector2.Zero);
    static readonly MapNavTileOptions Nav = new(0.5f, "resource-effects", "resource-controller");
    const MapNativeInvalidation Physical = MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency;

    [Theory]
    [InlineData("support")]
    [InlineData("light")]
    [InlineData("world-light")]
    public void MeshOnlyResource_MoveRemoveUndoRedoInvalidatesEveryChangedTile(string kind)
    {
        var f = PrefabEditorFixtures.MeshOnlyResource(kind);
        var placement = Assert.Single(f.Document.Placements);
        var leaf = f.Assets.GetAsset(placement.AssetId!);
        Assert.Null(leaf.CollisionResourceId);
        Assert.Null(leaf.SelectionResourceId);
        Assert.Equal(default, NativePlacementBoundsProvider.Instance.Bounds(f.Document, f.Assets, MapDocRegistry.CreateDefault(), new[] { placement.Id }));
        if (kind != "world-light") Assert.Single(f.Document.Prefabs!.Attachments);
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        var original = Build(f);
        if (kind == "support")
        {
            Assert.NotEmpty(original.Statics);
            Assert.Empty(original.Lights);
        }
        else
        {
            Assert.Empty(original.Statics);
            Assert.Single(original.Lights);
        }
        var before = original;
        Change(() => h.Execute(f.Document, new MovePlacementCommand(placement.Id, 131, -1, 0)));
        Assert.Equal(131, f.Document.Placements.Single().X);
        var oldMembership = MapResidencyOwnership.Build(original, Grids).Concat(MapResidencyOwnership.BuildAnnotations(original, Grids)).SelectMany(e => e.Membership);
        var movedMembership = MapResidencyOwnership.Build(before, Grids).Concat(MapResidencyOwnership.BuildAnnotations(before, Grids)).SelectMany(e => e.Membership).ToArray();
        Assert.Contains(oldMembership, t => t.X == 0);
        Assert.Contains(oldMembership, t => t.X == 1);
        Assert.All(movedMembership, t => Assert.Equal(2, t.X));
        Change(() => Assert.True(h.Undo(f.Document)));
        Change(() => Assert.True(h.Redo(f.Document)));
        h.SealGesture();
        Change(() => h.Execute(f.Document, new RemovePlacementCommand(placement.Id)));
        Assert.Empty(f.Document.Placements);
        Assert.Null(h.LastNativeEffects!.NewBounds);
        Change(() => Assert.True(h.Undo(f.Document)));
        Change(() => Assert.True(h.Redo(f.Document)));

        void Change(Action change)
        {
            change();
            var after = Build(f);
            var effects = h.LastNativeEffects!;
            Assert.Equal(MapNativeInvalidation.Placements | (kind == "support" ? Physical : MapNativeInvalidation.Residency), effects.Invalidates);
            Assert.False(effects.Invalidates.HasFlag(MapNativeInvalidation.Unbounded));
            AssertMembership(before, after, effects, kind);
            PrefabEditorFixtures.Reload(f.Document, f.Assets);
            before = after;
        }
    }

    [Theory]
    [InlineData("support")]
    [InlineData("light")]
    public void UnknownResourceBounds_RepairCarriesMatchingSpatialReset(string kind)
    {
        var f = PrefabEditorFixtures.MeshOnlyResource(kind);
        var owner = f.Document.Placements.Single();
        var child = f.Document.Prefabs!.Attachments.Single().Children.Single();
        f.Document.NativeResourceTransforms = f.Document.NativeResourceTransforms!.Select(p => p.Kind == child.LocalChild.Kind && p.OwnerId == child.WorldId
            ? p with { Transform = p.Transform with { X = 1_000_001 } } : p).ToArray();
        MapPrefabAttachmentDescriptors.Refresh(f.Document);
        MapBoundDocumentValidation.Validate(f.Document, f.Assets);
        Assert.Throws<MapDocumentException>(() => Build(f));
        var h = PrefabEditorFixtures.History(f.Document, f.Assets);
        h.Execute(f.Document, new MovePlacementCommand(owner.Id, 131, -1, 0));
        Assert.Equal(MapNativeInvalidation.Placements | MapNativeInvalidation.Unbounded |
            (kind == "support" ? Physical : MapNativeInvalidation.Residency), h.LastNativeEffects!.Invalidates);
        var repaired = Build(f);
        var affected = MapResidencyOwnership.Affected(repaired, Grids, h.LastNativeEffects!);
        var expected = kind == "support" ? MapResidencyOwnership.Build(repaired, Grids) : MapResidencyOwnership.BuildAnnotations(repaired, Grids);
        Assert.All(expected.SelectMany(e => e.Membership), tile => Assert.Contains(tile, affected.StorageTiles));
    }

    static MapBuiltWorld Build(PrefabFixture f)
    {
        // The released full builder requires an owner interaction source. Build the physical resources independently,
        // carrying their exact effective frames instead of inventing a collider or selection volume for the owner.
        var effective = MapPrefabResolver.Expand(f.Document, f.Assets);
        var projection = NativeDocumentSnapshot.Clone(f.Document, MapDocRegistry.CreateDefault());
        projection.Placements.Clear();
        projection.Prefabs = null;
        var frames = (projection.NativeResourceTransforms ?? []).Where(p => p.Kind != MapPrefabChildKind.Placement).ToList();
        if (projection.BuildingIntent is { } intent)
        {
            foreach (var light in intent.Lights)
                if (!frames.Any(p => p.Kind == MapPrefabChildKind.Light && p.OwnerId == light.Id) &&
                    MapEffectiveAnnotations.LightFrame(effective.Transforms, light) is { } frame)
                    frames.Add(new(MapPrefabChildKind.Light, light.Id, frame, new string('0', 64)));
            projection.BuildingIntent = new(intent.PayloadVersion, intent.RoofLinks, intent.RoofGroups,
                intent.Lights.Select(l => l with { Owner = null }).ToArray(), intent.Markers)
            { Roofs = intent.Roofs, Storeys = intent.Storeys };
        }
        projection.NativeResourceTransforms = frames;
        MapPrefabAttachmentDescriptors.Refresh(projection);
        var built = MapWorldBuilder.Build(projection, f.Assets, new(PrefabFixtures.Options(), "mesh-only-resource-effects", new()));
        Assert.Equal(MapEffectiveAnnotations.Lights(effective).Select(l => (l.Id, l.Position, l.Radius)),
            built.Lights.Select(l => (l.Id, l.Position, l.Radius)));
        return built;
    }

    static void AssertMembership(MapBuiltWorld before, MapBuiltWorld after, MapNativeEditEffects effects, string kind)
    {
        var a = before.Statics.ToDictionary(s => s.OwnerId);
        var b = after.Statics.ToDictionary(s => s.OwnerId);
        var changedStatics = a.Keys.Union(b.Keys).Where(id => !a.ContainsKey(id) || !b.ContainsKey(id) || a[id].Digest != b[id].Digest).ToHashSet();
        var oldAnnotations = MapResidencyOwnership.BuildAnnotations(before, Grids).ToDictionary(e => e.OwnerId);
        var newAnnotations = MapResidencyOwnership.BuildAnnotations(after, Grids).ToDictionary(e => e.OwnerId);
        var changedAnnotations = oldAnnotations.Keys.Union(newAnnotations.Keys).Where(id => !oldAnnotations.ContainsKey(id) ||
            !newAnnotations.ContainsKey(id) || oldAnnotations[id].WorldBounds != newAnnotations[id].WorldBounds).ToHashSet();
        if (kind == "support") Assert.NotEmpty(changedStatics);
        else Assert.NotEmpty(changedAnnotations);
        var expected = MapResidencyOwnership.Build(before, Grids).Concat(MapResidencyOwnership.Build(after, Grids))
            .Where(e => changedStatics.Contains(e.OwnerId)).Concat(oldAnnotations.Values.Concat(newAnnotations.Values)
                .Where(e => changedAnnotations.Contains(e.OwnerId))).ToArray();
        var affected = MapResidencyOwnership.Affected(before, after, Grids, effects);
        Assert.All(expected, e => Assert.Contains(e.OwnerId, affected.Owners));
        Assert.All(expected.SelectMany(e => e.Membership).Distinct(), tile => Assert.Contains(tile, affected.StorageTiles));
        if (kind == "support")
        {
            var oldNav = MapNavTiling.Partition(before, Grids, Nav).ToDictionary(t => t.Coord);
            var newNav = MapNavTiling.Partition(after, Grids, Nav).ToDictionary(t => t.Coord);
            var changed = oldNav.Keys.Union(newNav.Keys).Where(c => !oldNav.ContainsKey(c) || !newNav.ContainsKey(c) ||
                oldNav[c].CaptureIdentity != newNav[c].CaptureIdentity).ToArray();
            Assert.NotEmpty(changed);
            var affectedNav = MapNavTiling.AffectedTiles(before, after, Grids, Nav, effects).ToHashSet();
            Assert.All(changed, c => Assert.Contains(c, affectedNav));
        }
    }
}
