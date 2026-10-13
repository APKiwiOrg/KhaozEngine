using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public class MapPrefabReviewRegressionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnpackedInventory_LastTransferCannotBecomeOrdinaryDeletion(bool stored, bool removeNative)
    {
        var f = PrefabFixtures.SingleExplicitMarker();
        var live = JsonNode.Parse(MapDocumentFile.SaveText(f.Document))!;
        Assert.Null(live["prefabs"]!["instances"]![0]!["unpacked"]);
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        Assert.Single(unpack.Prefabs!.Transfers);
        string saved = MapDocumentFile.SaveText(unpack);
        var json = JsonNode.Parse(saved)!;
        Assert.True((bool?)json["prefabs"]!["instances"]![0]!["unpacked"] == true);
        unpack = MapDocumentFile.LoadText(saved);
        Assert.Equal(saved, MapDocumentFile.SaveText(unpack));
        string? directory = null;
        if (stored)
        {
            directory = PrefabFixtures.TempDirectory();
            MapDocumentFile.SaveTiled(unpack, directory);
        }
        var set = unpack.Prefabs!;
        unpack.Prefabs = new(1, 1, set.Instances, []) { Attachments = set.Attachments };
        if (removeNative)
        {
            var intent = unpack.BuildingIntent!;
            unpack.BuildingIntent = new(1, intent.RoofLinks, intent.RoofGroups, intent.Lights, [])
            { Roofs = intent.Roofs, Storeys = intent.Storeys };
            unpack.NativeResourceTransforms = [];
        }
        if (stored)
        {
            var manifest = MapTiledFile.ReadManifest(directory!, new(), out var tiles);
            manifest.Prefabs = unpack.Prefabs;
            manifest.BuildingIntent = unpack.BuildingIntent;
            manifest.NativeResourceTransforms = unpack.NativeResourceTransforms;
            var candidate = MapStoredSurfaceSource.FromManifest(directory!, manifest, tiles);
            Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(candidate, f.Assets, PrefabFixtures.Options()));
        }
        else Assert.Throws<MapDocumentException>(() => Identity(unpack, f.Assets, false));
    }

    [Fact]
    public void UnownedLight_ExtractRoundTripAndPlaceUsesAssetOrigin()
    {
        var f = PrefabFixtures.UnownedExplicitLight();
        var before = Assert.Single(MapEffectiveAnnotations.Lights(PrefabFixtures.Effective(f)));
        Assert.Equal(2, before.Position.X.ToSingle());
        Assert.Equal(2, before.Position.Y.ToSingle());
        Assert.Equal(4, before.Radius);
        var origin = new MapTransform(new(3, 1, -1), 0.371f, 0.8f);
        var asset = MapPrefabExtraction.Extract(f.Document, f.Assets,
            new[] { PrefabFixtures.World(MapPrefabChildKind.Light, "a/lamp") }, origin, "free-lamp");
        asset = MapPrefabFile.Read(MapPrefabFile.Write(asset), asset.Id);
        Assert.Null(asset.Children.OfType<MapPrefabLightChild>().Single().Value.Owner);
        var placed = PrefabFixtures.ExtractedFixture(asset, f.Assets, origin);
        var after = Assert.Single(MapEffectiveAnnotations.Lights(PrefabFixtures.Effective(placed)));
        Assert.Equal(before.Position.X.ToSingle(), after.Position.X.ToSingle(), 5);
        Assert.Equal(before.Position.Y.ToSingle(), after.Position.Y.ToSingle(), 5);
        Assert.Equal(before.Position.Z.ToSingle(), after.Position.Z.ToSingle(), 5);
        Assert.Equal(before.Radius, after.Radius, 5);
    }

    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(false, 0, 1)]
    [InlineData(true, 0.371f, 1.2f)]
    [InlineData(false, 0.371f, 1.2f)]
    public void OwnedLight_ExtractRoundTripAndPlaceComposesOwnerOnce(bool selectOwner, float yaw, float scale)
    {
        var f = PrefabFixtures.Create();
        if (yaw != 0 || scale != 1)
            f = new(MapPrefabEdits.Prepare(f.Document, f.Assets, new MapSetPrefabOverride("a",
                new MapPrefabPlacementOverride(new(MapPrefabChildKind.Placement, "doorway"),
                    new(new(2, 0, 0), yaw, scale), null, null))).After, f.Assets);
        var before = MapEffectiveAnnotations.Lights(PrefabFixtures.Effective(f)).Single(l => l.Id == "a/lamp");
        if (yaw == 0)
        {
            Assert.Equal(4, before.Position.X.ToSingle());
            Assert.Equal(2, before.Position.Y.ToSingle());
            Assert.Equal(4, before.Radius);
        }
        var selection = new List<MapPrefabReference> { PrefabFixtures.World(MapPrefabChildKind.Light, "a/lamp") };
        if (selectOwner) selection.Add(PrefabFixtures.World(MapPrefabChildKind.Placement, "a/doorway"));
        var origin = new MapTransform(Vector3.Zero, 0, 1);
        var asset = MapPrefabExtraction.Extract(f.Document, f.Assets, selection, origin, "doorway-lamp");
        asset = MapPrefabFile.Read(MapPrefabFile.Write(asset), asset.Id);
        Assert.Equal(selectOwner ? MapPrefabReferenceScope.Local : MapPrefabReferenceScope.World,
            asset.Children.OfType<MapPrefabLightChild>().Single().Value.Owner!.Scope);
        var assets = PrefabFixtures.PublishPrefab(f.Assets, asset);
        var document = selectOwner ? PrefabFixtures.SpacelessRoomless().Document : f.Document;
        document.NativeAssets = assets.Roots.ToList();
        var placed = MapPrefabEdits.Prepare(document, assets, new MapPlacePrefab("copy", asset.Id, origin)).After;
        string lamp = placed.Prefabs!.Instances.Single(i => i.Id == "copy").Bindings.Entries.Single(b => b.Child.Kind == MapPrefabChildKind.Light).WorldId;
        var after = MapEffectiveAnnotations.Lights(MapPrefabResolver.Expand(placed, assets)).Single(l => l.Id == lamp);
        Assert.Equal(before.Position.X.ToSingle(), after.Position.X.ToSingle(), 5);
        Assert.Equal(before.Position.Y.ToSingle(), after.Position.Y.ToSingle(), 5);
        Assert.Equal(before.Position.Z.ToSingle(), after.Position.Z.ToSingle(), 5);
        Assert.Equal(before.Radius, after.Radius, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnpackedInventory_DependencyAddsChildWithoutChangingPrefab(bool stored)
    {
        var f = PrefabFixtures.SupportWithEmbeddedRecord();
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var next = PrefabFixtures.SupportWithAdditionalRecord(f);
        Assert.Equal(f.Assets.GetResource("hut").Reference.Sha256, next.GetResource("hut").Reference.Sha256);
        Assert.NotEqual(f.Assets.GetResource("deck-support").Reference.Sha256, next.GetResource("deck-support").Reference.Sha256);
        var updated = MapPrefabEdits.PrepareAssetUpdate(unpack, f.Assets, next,
            new("hut", f.Assets.GetResource("hut").Reference.Sha256, "hut", next.GetResource("hut").Reference.Sha256)).After;
        var loaded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(updated));
        Assert.Equal(MapPrefabPreflight.Count(unpack, f.Assets, new()).Children, MapPrefabPreflight.Count(loaded, next, new()).Children);
        var identity = Identity(loaded, next, stored);
        Assert.Equal(Identity(loaded, next, stored: false), identity);
        var before = MapPrefabResolver.Expand(unpack, f.Assets);
        var after = MapPrefabResolver.Expand(loaded, next);
        Assert.Equal(before.Surfaces.AllRecords().Select(r => r.Id).Order(), after.Surfaces.AllRecords().Select(r => r.Id).Order());
        Assert.DoesNotContain(after.Surfaces.AllRecords(), r => r.Id.Contains("later-edge", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnpackedInventory_ChangedPrefabCannotWaiveMissingMarkerTransfer(bool stored, bool removeNative)
    {
        var f = PrefabFixtures.Create();
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var original = f.Assets.GetPrefab("hut");
        var marker = original.Children.OfType<MapPrefabMarkerChild>().Single();
        var next = PrefabFixtures.PublishPrefab(f.Assets, original with
        { Children = original.Children.Select(c => c == marker ? marker with { Value = marker.Value with { Name = "Updated" } } : c).ToArray() });
        var updated = MapPrefabEdits.PrepareAssetUpdate(unpack, f.Assets, next,
            new("hut", f.Assets.GetResource("hut").Reference.Sha256, "hut", next.GetResource("hut").Reference.Sha256)).After;
        Assert.NotEqual(f.Assets.GetResource("hut").Reference.Sha256, next.GetResource("hut").Reference.Sha256);
        Assert.Equal(Identity(updated, next, false), Identity(updated, next, stored));
        var serialized = JsonNode.Parse(MapDocumentFile.SaveText(updated))!;
        string? directory = null;
        if (stored)
        {
            directory = PrefabFixtures.TempDirectory();
            MapDocumentFile.SaveTiled(updated, directory);
        }
        var set = updated.Prefabs!;
        updated.Prefabs = new(1, 1, set.Instances, set.Transfers.Where(t => t.Binding.Child.Kind != MapPrefabChildKind.Marker).ToArray())
        { Attachments = set.Attachments };
        if (removeNative)
        {
            var intent = updated.BuildingIntent!;
            updated.BuildingIntent = new(1, intent.RoofLinks, intent.RoofGroups, intent.Lights, [])
            { Storeys = intent.Storeys, Roofs = intent.Roofs };
            updated.NativeResourceTransforms = updated.NativeResourceTransforms!.Where(p => p.Kind != MapPrefabChildKind.Marker).ToArray();
        }
        serialized["prefabs"] = JsonNode.Parse(MapPrefabJson.Bytes(updated.Prefabs));
        serialized["buildingIntent"] = JsonNode.Parse(MapPrefabJson.Bytes(updated.BuildingIntent));
        serialized["nativeResourceTransforms"] = JsonNode.Parse(MapPrefabJson.Bytes(updated.NativeResourceTransforms));
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(serialized.ToJsonString()));
        if (stored)
        {
            var manifest = MapTiledFile.ReadManifest(directory!, new(), out var tiles);
            manifest.Prefabs = updated.Prefabs;
            manifest.BuildingIntent = updated.BuildingIntent;
            manifest.NativeResourceTransforms = updated.NativeResourceTransforms;
            var candidate = MapStoredSurfaceSource.FromManifest(directory!, manifest, tiles);
            Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(candidate, next, PrefabFixtures.Options()));
        }
        else Assert.Throws<MapDocumentException>(() => Identity(updated, next, false));
    }

    [Fact]
    public void MissingY_UnpackReloadRetainsRequestAndReseatsOnChangedFloor()
    {
        var f = PrefabFixtures.SupportBoundChild(5);
        var before = PrefabFixtures.Effective(f);
        var request = Assert.Single(before.PlacementSupports);
        Assert.Equal(5, request.Support.WorldY);
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        foreach (var document in new[] { unpack, MapDocumentFile.LoadText(MapDocumentFile.SaveText(unpack)) })
        {
            var item = document.Placements.Single(p => p.Id == "low-item");
            Assert.Null(item.Y);
            Assert.Equal(new MapSupportBinding(MapSupportBindingKind.Surface, request.Support.Face!.Value.OwnerId, null, null, null, null), item.SupportBinding);
            Assert.Equal(5, document.NativeResourceTransforms!.Single(p => p.Kind == MapPrefabChildKind.Placement && p.OwnerId == item.Id).Transform.Y);
            var effective = MapPrefabResolver.Expand(document, f.Assets);
            var support = Assert.Single(effective.PlacementSupports);
            Assert.Equal(request.PlacementId, support.PlacementId);
            Assert.Equal(request.Support.Face, support.Support.Face);
            Assert.Equal(request.Support.WorldY, support.Support.WorldY);
            Assert.Equal(5, effective.Placements.Single(p => p.PlacementId == item.Id).Transform.Position.Y);
        }
        var changed = MapDocumentFile.LoadText(MapDocumentFile.SaveText(unpack));
        string floor = request.Support.Face!.Value.OwnerId;
        foreach (var patch in changed.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == floor)) Array.Fill(patch.Heights, 100);
        PrefabFixtures.RefreshCapturedPins(changed);
        for (int index = 0; index < changed.Surfaces.Refs.Count; index++)
        {
            var surface = changed.Surfaces.Refs[index];
            changed.Surfaces.Refs[index] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                changed.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id).Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
        foreach (var document in new[] { changed, MapDocumentFile.LoadText(MapDocumentFile.SaveText(changed)) })
        {
            var effective = MapPrefabResolver.Expand(document, f.Assets);
            Assert.Equal(6.2f, Assert.Single(effective.PlacementSupports).Support.WorldY, 5);
            Assert.Equal(6.2f, effective.Placements.Single(p => p.PlacementId == "low-item").Transform.Position.Y, 5);
        }
    }

    static string Identity(MapDocument document, KhaozEngine.MapDoc.Assets.MapAssetClosure assets, bool stored) => stored
        ? MapAuthoredIdentityV2.Compute(PrefabFixtures.Stored(document), assets, PrefabFixtures.Options())
        : MapAuthoredIdentityV2.Compute(document, assets, PrefabFixtures.Options());
}
