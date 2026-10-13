using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public class MapPrefabExtractionTests
{
    [Fact]
    public void MaterializedUnpackExtractReloadReplace_PreservesCapturedSupportAndLight()
    {
        var f = PrefabFixtures.MaterializedExtraction();
        var asset = MapPrefabExtraction.Extract(f.Document, f.Assets, f.Selection, f.Origin, "captured");
        var reload = MapPrefabFile.Read(MapPrefabFile.Write(asset), "captured");
        Assert.Equal(2, reload.MaterializedAttachments.Count);
        Assert.All(reload.MaterializedAttachments, x => Assert.Equal(MapPrefabReferenceScope.Local, x.Placement.Scope));
        Assert.All(reload.MaterializedAttachments.SelectMany(r => r.Children), x => Assert.Equal(MapPrefabReferenceScope.Local, x.Child.Scope));
        PrefabFixtures.AssertCapturedMaterializedSet(PrefabFixtures.PlaceExtracted(reload));
        var light = reload.Children.OfType<MapPrefabLightChild>().Single();
        MapPrefabAsset replacement = MapPrefabAssetEdits.Replace(reload, light with { Value = light.Value with { Intensity = 2 } });
        Assert.Equal(reload.MaterializedAttachments.Select(r => r.PlacementFrame), replacement.MaterializedAttachments.Select(r => r.PlacementFrame));
        Assert.Equal(reload.MaterializedAttachments.SelectMany(r => r.Children).Select(c => c.OwnerLocalPose),
            replacement.MaterializedAttachments.SelectMany(r => r.Children).Select(c => c.OwnerLocalPose));
        PrefabFixtures.AssertCapturedMaterializedSet(PrefabFixtures.PlaceExtracted(replacement));
        Assert.Equal(2, PrefabFixtures.PlaceExtracted(replacement).BuildingIntent.Lights.Single().Intensity);
        var placed = PrefabFixtures.ExtractedFixture(reload, f.Assets, new(new(12, 0, -12), 0, 1));
        string lightId = placed.Document.Prefabs!.Instances.Single().Bindings.Entries.Single(b => b.Child == light.Key).WorldId;
        var removed = MapPrefabEdits.Prepare(placed.Document, placed.Assets,
            new MapSetPrefabOverride("copy", new MapPrefabRemoveOverride(light.Key))).After;
        Assert.Empty(MapEffectiveAnnotations.Lights(MapPrefabResolver.Expand(removed, placed.Assets)));
        var restored = MapPrefabEdits.Prepare(MapDocumentFile.LoadText(MapDocumentFile.SaveText(removed)), placed.Assets,
            new MapRestorePrefabChild("copy", light.Key)).After;
        var emission = Assert.Single(MapEffectiveAnnotations.Lights(MapPrefabResolver.Expand(restored, placed.Assets)));
        Assert.Equal(lightId, emission.Id);
        Assert.Equal(3, emission.Radius);
        Assert.Equal(2, emission.Position.Y.ToSingle());
        Assert.Equal(f.Assets.GetResource("deck-support").Reference.Sha256, placed.Assets.GetResource("deck-support").Reference.Sha256);
        Assert.Equal(f.Assets.GetResource("glow-light").Reference.Sha256, placed.Assets.GetResource("glow-light").Reference.Sha256);
    }

    [Fact]
    public void MaterializedExtraction_RefusesMissingLiveChild()
    {
        var f = PrefabFixtures.MaterializedExtraction();
        Assert.Throws<MapDocumentException>(() => MapPrefabExtraction.Extract(f.Document, f.Assets,
            f.Selection.Where(r => r.Kind != MapPrefabChildKind.Light).ToArray(), f.Origin, "partial"));
    }

    [Fact]
    public void AssetChildRemoval_KeepsEmptySuppressedEdgeAndCanRestoreCapturedDefinition()
    {
        var f = PrefabFixtures.MaterializedExtraction();
        var asset = MapPrefabExtraction.Extract(f.Document, f.Assets, f.Selection, f.Origin, "captured");
        var light = asset.Children.OfType<MapPrefabLightChild>().Single();
        MapPrefabAsset removed = MapPrefabAssetEdits.Replace(asset, light, remove: true);
        Assert.Empty(removed.MaterializedAttachments.Single(r => r.Kind == MapPrefabAttachmentKind.Light).Children);
        Assert.Empty(MapEffectiveAnnotations.Lights(PrefabFixtures.PlaceExtracted(removed)));
        MapPrefabAsset restored = MapPrefabAssetEdits.Replace(asset, light);
        PrefabFixtures.AssertCapturedMaterializedSet(PrefabFixtures.PlaceExtracted(restored));
        var floor = asset.Children.OfType<MapPrefabSurfaceChild>().Single();
        MapPrefabAsset moved = MapPrefabAssetEdits.Replace(asset, floor with { LocalTransform = new(new(0, 0.25f, 0), 0, 1) });
        Assert.Equal(0.25, moved.MaterializedAttachments.Single(r => r.Kind == MapPrefabAttachmentKind.Support).Children.Single().OwnerLocalPose.Y);
        Assert.Equal(asset.MaterializedAttachments.Single(r => r.Kind == MapPrefabAttachmentKind.Support).Children.Single().SourceDigest,
            moved.MaterializedAttachments.Single(r => r.Kind == MapPrefabAttachmentKind.Support).Children.Single().SourceDigest);
        var movedResources = PrefabFixtures.PlaceExtracted(moved);
        var movedFloor = movedResources.Surfaces.Refs.Single(s => s.Id != "ground");
        Assert.All(MapEffectiveGeometry.Patch(movedResources.CompleteView(), new(movedFloor.Id, 0, 0)).ExactVertices,
            v => Assert.Equal(MapExactValue.FromSingle(0.75f), v.Y));
        Assert.Equal(MapPrefabFile.Write(asset), MapPrefabFile.Write(restored));
    }

    [Fact]
    public void RemovedCapturedChild_UnpackExtractRetainsReservationAndEmptySuppressedEdge()
    {
        var source = PrefabFixtures.MaterializedExtraction();
        var asset = MapPrefabExtraction.Extract(source.Document, source.Assets, source.Selection, source.Origin, "captured");
        var f = PrefabFixtures.ExtractedFixture(asset, source.Assets, new(new(12, 0, -12), 0, 1));
        var key = asset.Children.OfType<MapPrefabLightChild>().Single().Key;
        var removed = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapSetPrefabOverride("copy", new MapPrefabRemoveOverride(key))).After;
        var unpacked = MapPrefabEdits.Prepare(removed, f.Assets, new MapUnpackPrefab("copy")).After;
        Assert.True(unpacked.Prefabs!.Instances.Single().Removed);
        Assert.True(Assert.Single(unpacked.Prefabs.Instances.Single().Bindings.Entries).Removed);
        Assert.Empty(unpacked.Prefabs.Attachments.Single(r => r.Kind == MapPrefabAttachmentKind.Light).Children);
        Assert.Empty(MapEffectiveAnnotations.Lights(MapPrefabResolver.Expand(unpacked, f.Assets)));
        var selection = unpacked.Prefabs.Transfers.Select(t => PrefabFixtures.World(t.Binding.Child.Kind, t.Binding.WorldId)).ToArray();
        var captured = MapPrefabExtraction.Extract(MapDocumentFile.LoadText(MapDocumentFile.SaveText(unpacked)), f.Assets,
            selection, new(new(12, 0, -12), 0, 1), "suppressed");
        Assert.Equal(2, captured.MaterializedAttachments.Count);
        Assert.Empty(captured.MaterializedAttachments.Single(r => r.Kind == MapPrefabAttachmentKind.Light).Children);
        Assert.Empty(MapEffectiveAnnotations.Lights(PrefabFixtures.PlaceExtracted(captured)));
    }

    [Fact]
    public void Extract_RefusesPartialRoomOrDanglingRoof()
    {
        var f = PrefabFixtures.Create("all-kinds");
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var selected = unpack.Prefabs!.Transfers.Select(t => PrefabFixtures.World(t.Binding.Child.Kind, t.Binding.WorldId)).ToArray();
        foreach (string missing in new[] { "a/floor", "a/room", "a/roof" })
            Assert.Throws<MapDocumentException>(() => MapPrefabExtraction.Extract(unpack, f.Assets,
                selected.Where(r => r.Id != missing).ToArray(), new(Vector3.Zero, 0, 1), "partial"));
    }

    [Fact]
    public void SameTextReferences_ExtractAndUnpackRetainScope()
    {
        var f = PrefabFixtures.RoofInventoryRemoval(true);
        var selected = new[] { PrefabFixtures.World(MapPrefabChildKind.Placement, "roof") };
        var asset = MapPrefabExtraction.Extract(f.Document, f.Assets, selected, new(Vector3.Zero, 0, 1), "scope");
        Assert.Contains(asset.Roofs, r => r.Scope == MapPrefabReferenceScope.Local && r.Id == "roof");
        var original = PrefabFixtures.Create().Assets.GetPrefab("hut");
        var roof = original.Children.OfType<MapPrefabPlacementChild>().Single(c => c.Key.Key == "roof");
        asset = asset with
        {
            Children = new[] { roof },
            Roofs = new[]
        { new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "roof"), PrefabFixtures.World(MapPrefabChildKind.Placement, "roof") }
        };
        var baseFixture = PrefabFixtures.SpacelessRoomless();
        baseFixture.Document.Placements.Add(new() { Id = "roof", Kind = "roof", AssetId = "roof", X = 8, Y = 3, Z = -8, Scale = 1 });
        var assets = PrefabFixtures.PublishPrefab(baseFixture.Assets, asset);
        baseFixture.Document.NativeAssets = assets.Roots.ToList();
        var place = MapPrefabEdits.Prepare(baseFixture.Document, assets, new MapPlacePrefab("a", "scope", new(Vector3.Zero, 0, 1))).After;
        var result = MapPrefabEdits.Prepare(place, assets, new MapUnpackPrefab("a")).After;
        Assert.Equal(new[] { "prefab/a/Placement/roof", "roof" }, MapPrefabResolver.Expand(result, assets).BuildingIntent.Roofs.Select(r => r.Id).Order());
    }
}
