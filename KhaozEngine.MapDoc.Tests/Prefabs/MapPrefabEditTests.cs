using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public class MapPrefabEditTests
{
    [Fact]
    public void Unpack_PreservesAllTypedReferencesAndIds()
    {
        var f = PrefabFixtures.Create("all-kinds");
        var before = PrefabFixtures.Effective(f);
        var result = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a"));
        var after = MapPrefabResolver.Expand(result.After, f.Assets);
        Assert.Equal(before.Placements.Select(x => (x.PlacementId, x.NumericId)), after.Placements.Select(x => (x.PlacementId, x.NumericId)));
        Assert.Equal(before.Water!.Digest(), after.Water!.Digest());
        Assert.Equal(PrefabFixtures.SemanticIntent(before), PrefabFixtures.SemanticIntent(after));
        Assert.Equal(before.Surfaces.AllRecords().Select(x => x.Id).Order(), after.Surfaces.AllRecords().Select(x => x.Id).Order());
        Assert.Equal(before.BuildingIntent.Storeys.Select(x => (x.Id, x.Name)), after.BuildingIntent.Storeys.Select(x => (x.Id, x.Name)));
        Assert.Equal(before.Placements.Count, after.Placements.Count);
        Assert.True(result.After.Prefabs!.Instances.Single().Removed);
        Assert.Empty(result.After.Prefabs.Instances.Single().Bindings.Entries);
        Assert.Equal(PrefabFixtures.LiveBindings(f.Document).Count, result.After.Prefabs.Transfers.Count);
        Assert.True(result.WriteSet.Prefabs && result.WriteSet.BuildingIntent && result.WriteSet.NativeResourceTransforms && result.WriteSet.NumericReservations);
        Assert.NotEmpty(result.Effects.RoofIds);
        Assert.NotEmpty(result.Effects.LightIds);
        Assert.NotEmpty(result.Effects.MarkerIds);
    }

    [Fact]
    public void RoomlessPrefab_PlacesInSpacelessMap()
    {
        var f = PrefabFixtures.SpacelessRoomless();
        Assert.Empty(f.Document.Surfaces.AllRecords().OfType<MapSpaceDoc>());
        var canopy = f.Assets.GetPrefab("hut") with { Id = "roomless-canopy" };
        var room = PrefabFixtures.Create().Assets.GetPrefab("hut") with { Id = "room-bearing" };
        var assets = PrefabFixtures.PublishPrefab(PrefabFixtures.PublishPrefab(f.Assets, canopy), room);
        f.Document.NativeAssets = assets.Roots.ToList();
        var result = MapPrefabEdits.Prepare(f.Document, assets,
            new MapPlacePrefab("canopy", "roomless-canopy", new MapTransform(Vector3.Zero, 0, 1)));
        Assert.Null(result.After.Prefabs!.Instances.Single().ParentSpace);
        Assert.NotEmpty(MapPrefabResolver.Expand(result.After, assets).Placements);
        long mark = f.Document.NumericIdHighWaterMark;
        Assert.Contains("ParentSpace", Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(f.Document, assets,
            new MapPlacePrefab("room", "room-bearing", new MapTransform(Vector3.Zero, 0, 1)))).Message);
        Assert.Equal(mark, f.Document.NumericIdHighWaterMark);
    }

    [Theory]
    [InlineData("owner-placement", 7.25f, 0.5f, -2.5f, 7.25f, 2f, -2.5f)]
    [InlineData("owner-transform", 7.25f, 0.5f, -2.5f, 7.25f, 2f, -2.5f)]
    [InlineData("floor", 3.5f, 0.75f, -3.25f, 4f, 2f, -4f)]
    [InlineData("light", 4f, 0.5f, -4f, 4.75f, 2.5f, -5.25f)]
    public void MaterializedOverridePreparation_SaveReloadPreservesFramesAndIds(string kind,
        float fx, float fy, float fz, float lx, float ly, float lz)
    {
        var f = PrefabFixtures.MaterializedOverrideFixture();
        var unpacked = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var selected = unpacked.Prefabs!.Transfers.Select(t => PrefabFixtures.World(t.Binding.Child.Kind, t.Binding.WorldId)).ToArray();
        var asset = MapPrefabExtraction.Extract(unpacked, f.Assets, selected, new(Vector3.Zero, 0, 1), "captured");
        var placed = PrefabFixtures.ExtractedFixture(MapPrefabFile.Read(MapPrefabFile.Write(asset), "captured"), f.Assets, new(Vector3.Zero, 0, 1));
        var bytes = placed.Assets.GetResource("captured").Bytes.ToArray();
        var leafBytes = placed.Assets.GetResource("deck-support").Bytes.ToArray();
        var instance = placed.Document.Prefabs!.Instances.Single();
        var requested = PrefabFixtures.MaterializedTransformOverride(kind);
        var changed = MapPrefabEdits.Prepare(placed.Document, placed.Assets, new MapSetPrefabOverride("copy", requested)).After;
        var r = MapPrefabResolver.Expand(changed, placed.Assets);
        string floorId = instance.Bindings.Entries.Single(b => b.Child.Key == "captured-floor").WorldId;
        string lightId = instance.Bindings.Entries.Single(b => b.Child.Key == "captured-light").WorldId;
        var geometry = MapEffectiveGeometry.Patch(r.CompleteView(), new(floorId, 0, 0));
        Assert.Equal(new MapExactPoint(MapExactValue.FromSingle(fx), MapExactValue.FromSingle(fy), MapExactValue.FromSingle(fz)), geometry.ExactVertices[0]);
        Assert.Equal(new MapExactPoint(MapExactValue.FromSingle(lx), MapExactValue.FromSingle(ly), MapExactValue.FromSingle(lz)), MapEffectiveAnnotations.Lights(r).Single().Position);
        Assert.Single(r.Surfaces.Refs, s => s.Id != "ground");
        Assert.Single(MapEffectiveAnnotations.Lights(r));
        var reload = MapDocumentFile.LoadText(MapDocumentFile.SaveText(changed));
        Assert.Equal(changed.Prefabs!.Instances.Single().Bindings.Entries, reload.Prefabs!.Instances.Single().Bindings.Entries);
        Assert.Equal(r.Transforms.OrderBy(t => t.Key), MapPrefabResolver.Expand(reload, placed.Assets).Transforms.OrderBy(t => t.Key));
        Assert.Equal(bytes, placed.Assets.GetResource("captured").Bytes.ToArray());
        Assert.Equal(leafBytes, placed.Assets.GetResource("deck-support").Bytes.ToArray());
        var removed = MapPrefabEdits.Prepare(changed, placed.Assets,
            new MapSetPrefabOverride("copy", new MapPrefabRemoveOverride(requested.Child))).After;
        Assert.IsType<MapPrefabRemoveOverride>(removed.Prefabs!.Instances.Single().Overrides.Single(o => o.Child == requested.Child));
        var restored = MapPrefabEdits.Prepare(MapDocumentFile.LoadText(MapDocumentFile.SaveText(removed)), placed.Assets,
            new MapRestorePrefabChild("copy", requested.Child)).After;
        Assert.Equal(r.Transforms.OrderBy(t => t.Key), MapPrefabResolver.Expand(restored, placed.Assets).Transforms.OrderBy(t => t.Key));
        Assert.Equal(instance.Bindings.Entries.Select(b => b.WorldId), restored.Prefabs!.Instances.Single().Bindings.Entries.Select(b => b.WorldId));
    }

    [Fact]
    public void UnpackAttachedSupport_HasExactlyOneProducer()
    {
        var f = PrefabFixtures.SupportWithEmbeddedRecord();
        var armed = new PrefabFixtures.ArmedAssetSource();
        var assets = PrefabFixtures.PublishPrefab(f.Assets, f.Assets.GetPrefab("hut"), source: armed);
        f.Document.NativeAssets = assets.Roots.ToList();
        var before = MapPrefabResolver.Expand(f.Document, assets);
        var afterDocument = MapPrefabEdits.Prepare(f.Document, assets, new MapUnpackPrefab("a")).After;
        armed.Armed = true;
        foreach (var doc in new[] { afterDocument, MapDocumentFile.LoadText(MapDocumentFile.SaveText(afterDocument)) })
        {
            var after = MapPrefabResolver.Expand(doc, assets);
            Assert.Equal(before.Surfaces.Refs.Select(s => s.Id).Order(), after.Surfaces.Refs.Select(s => s.Id).Order());
            Assert.Equal(before.Surfaces.Patches.Keys.Order(), after.Surfaces.Patches.Keys.Order());
            Assert.Equal(before.Surfaces.AllRecords().Select(r => r.Id).Order(), after.Surfaces.AllRecords().Select(r => r.Id).Order());
            Assert.Single(after.Surfaces.Refs, s => s.Id == PrefabFixtures.SupportFloorId);
            const string edge = "prefab/a/Record/deck%2Fdeck-support%2Fdeck-edge";
            Assert.Single(after.Surfaces.AllRecords(), r => r.Id == edge);
            Assert.Equal(Candidates(before), Candidates(after));
            Assert.NotEmpty(Candidates(after));
            Assert.True(doc.Prefabs!.Instances.Single().Removed);
            Assert.NotEmpty(doc.Prefabs.Attachments);
        }
    }

    internal static MapSupportCandidate[] Candidates(MapEffectiveResources resources)
    {
        var buffer = new MapSupportCandidate[64];
        var query = new MapSupportQuery(resources.CompleteView());
        var result = query.EnumerateCandidates(new(PrefabFixtures.Point(resources, 2, 1, -2), PrefabFixtures.SupportFloorId,
            "a/room", null, 1, 4, null), buffer);
        Assert.Equal(MapSupportStatus.Supported, result.Status);
        return buffer.Take(result.Count).OrderBy(c => c.Face).ToArray();
    }

    [Fact]
    public void EmbeddedSupportRecord_UnpacksOnce()
    {
        var f = PrefabFixtures.SupportWithEmbeddedRecord();
        var result = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a"));
        var reload = MapDocumentFile.LoadText(MapDocumentFile.SaveText(result.After));
        const string edge = "prefab/a/Record/deck%2Fdeck-support%2Fdeck-edge";
        var row = Assert.Single(reload.Prefabs!.Attachments, a => a.Kind == MapPrefabAttachmentKind.Support);
        var record = Assert.Single(row.Children, c => c.LocalChild.Kind == MapPrefabChildKind.Record);
        Assert.Equal(edge, record.WorldId);
        Assert.Equal(new MapPatchKey(PrefabFixtures.SupportFloorId, 0, 0), record.Anchor);
        Assert.Single(MapPrefabResolver.Expand(reload, f.Assets).Surfaces.AllRecords(), r => r.Id == edge);
    }

    [Fact]
    public void StoreyUnpack_ReloadKeepsIdNameAndRoomOwner()
    {
        var f = PrefabFixtures.Create("all-kinds");
        var d = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var reload = MapDocumentFile.LoadText(MapDocumentFile.SaveText(d));
        var storey = Assert.Single(reload.BuildingIntent!.Storeys);
        Assert.Equal("a/lower", storey.Id);
        Assert.Equal("Lower", storey.Name);
        Assert.Null(storey.InstanceId);
        Assert.All(reload.Surfaces.AllRecords().OfType<MapPrismDomain>(), p =>
        { Assert.Equal("", p.InstanceId); Assert.Equal("a/lower", p.StoreyId); });
        Assert.Equal("a/room", new MapSpaceMembership(MapPrefabResolver.Expand(reload, f.Assets).CompleteView())
            .Query(PrefabFixtures.Point(MapPrefabResolver.Expand(reload, f.Assets), 1, 1, -1)).SpaceId);
        var asset = f.Assets.GetPrefab("hut") with
        {
            Children = f.Assets.GetPrefab("hut").Children.Select(c =>
            c is MapPrefabStoreyChild s ? s with { Name = "Changed" } : c).ToArray()
        };
        var next = PrefabFixtures.PublishPrefab(f.Assets, asset);
        var updated = MapPrefabEdits.PrepareAssetUpdate(reload, f.Assets, next,
            new("hut", f.Assets.GetResource("hut").Reference.Sha256, "hut", next.GetResource("hut").Reference.Sha256)).After;
        Assert.Equal("Lower", updated.BuildingIntent!.Storeys.Single().Name);
    }

    [Fact]
    public void ImplicitLeafLights_UnpackAndAssetUpdateKeepOwners()
    {
        var f = PrefabFixtures.TwoLeafLamps();
        var before = PrefabFixtures.Effective(f);
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        Assert.Equal(PrefabFixtures.SemanticIntent(before), PrefabFixtures.SemanticIntent(MapPrefabResolver.Expand(unpack, f.Assets)));
        var original = f.Assets.GetPrefab("hut");
        var source = f.Assets.BorrowLight("lamp-light");
        var next = PrefabFixtures.PublishPrefab(f.Assets, original, new System.Collections.Generic.Dictionary<string, byte[]>
        { ["lamp-light"] = MapLightFile.Write(source with { Radius = 7 }) });
        var update = MapPrefabEdits.PrepareAssetUpdate(unpack, f.Assets, next,
            new("hut", f.Assets.GetResource("hut").Reference.Sha256, "hut", next.GetResource("hut").Reference.Sha256));
        Assert.True(update.WriteSet.NativeAssets);
        Assert.Equal(next.Roots, update.After.NativeAssets);
        Assert.Equal(new[] { 2.4f, 2.4f }, MapEffectiveAnnotations.Lights(MapPrefabResolver.Expand(update.After, next)).Select(l => l.Radius));
        var live = MapPrefabEdits.PrepareAssetUpdate(f.Document, f.Assets, next,
            new("hut", f.Assets.GetResource("hut").Reference.Sha256, "hut", next.GetResource("hut").Reference.Sha256));
        Assert.All(MapEffectiveAnnotations.Lights(MapPrefabResolver.Expand(live.After, next)), l => Assert.Equal(8.4f, l.Radius, 5));
    }

    [Fact]
    public void SupportAssetUpdate_ChangesLiveInstancesAndKeepsTransferredDescriptors()
    {
        var f = PrefabFixtures.SupportWithEmbeddedRecord();
        var unpack = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var support = MapSupportResourceFile.Read(f.Assets.GetResource("deck-support").Bytes, "deck-support");
        foreach (var patch in support.Surfaces.Patches.Values)
        {
            Array.Fill(patch.Heights, 25);
            for (int index = 0; index < patch.Records.Count; index++)
                if (patch.Records[index] is MapBoundaryChain chain)
                    patch.Records[index] = chain with { Vertices = chain.Vertices.Select(v => v with { HeightUnits = 25 }).ToArray() };
        }
        var next = PrefabFixtures.PublishPrefab(f.Assets, f.Assets.GetPrefab("hut"), new System.Collections.Generic.Dictionary<string, byte[]>
        { ["deck-support"] = MapSupportResourceFile.Write(support) });
        var edit = new MapUpdatePrefabAsset("hut", f.Assets.GetResource("hut").Reference.Sha256, "hut", next.GetResource("hut").Reference.Sha256);
        var transferred = MapPrefabEdits.PrepareAssetUpdate(unpack, f.Assets, next, edit).After;
        Assert.Equal(MapPrefabJson.Bytes(unpack.Prefabs!.Attachments), MapPrefabJson.Bytes(transferred.Prefabs!.Attachments));
        Assert.Equal(unpack.NativeResourceTransforms, transferred.NativeResourceTransforms);
        Assert.All(MapEffectiveGeometry.Patch(MapPrefabResolver.Expand(transferred, next).CompleteView(),
            new(PrefabFixtures.SupportFloorId, 0, 0)).ExactVertices, v => Assert.Equal(MapExactValue.FromSingle(0), v.Y));
        var live = MapPrefabEdits.PrepareAssetUpdate(f.Document, f.Assets, next, edit).After;
        Assert.All(MapEffectiveGeometry.Patch(MapPrefabResolver.Expand(live, next).CompleteView(),
            new(PrefabFixtures.SupportFloorId, 0, 0)).ExactVertices, v => Assert.Equal(MapExactValue.FromSingle(0.25f), v.Y));
    }

    [Theory]
    [InlineData(0.8f)]
    [InlineData(1.2f)]
    public void TransformedUnpack_PreservesPreciseGeometryAndOccupiedRooms(float scale)
    {
        var f = PrefabFixtures.Transformed(0.371f, scale, new(30000.25f, -450, -29999.75f));
        var before = PrefabFixtures.Effective(f);
        var unpacked = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var after = MapPrefabResolver.Expand(MapDocumentFile.LoadText(MapDocumentFile.SaveText(unpacked)), f.Assets);
        Assert.Equal(before.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)), after.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)));
        foreach (var key in before.Surfaces.Patches.Keys)
            Assert.Equal(MapEffectiveGeometry.Patch(before.CompleteView(), key).ExactVertices,
                MapEffectiveGeometry.Patch(after.CompleteView(), key).ExactVertices);
        Assert.Equal(PrefabFixtures.SemanticIntent(before), PrefabFixtures.SemanticIntent(after));
        var beforeWater = KhaozEngine.MapDoc.Water.MapWaterCompiler.Compile(before.CompleteView(), before.Water!);
        var afterWater = KhaozEngine.MapDoc.Water.MapWaterCompiler.Compile(after.CompleteView(), after.Water!);
        Assert.Equal(beforeWater.Select(b => (b.BodyId, b.SurfaceY, b.Status)), afterWater.Select(b => (b.BodyId, b.SurfaceY, b.Status)));
        Assert.All(afterWater, b => Assert.Equal(KhaozEngine.MapDoc.Water.MapWaterCompileStatus.Complete, b.Status));
    }

    [Fact]
    public void RemovedPreviousOverride_RestoreKeepsCustomization()
    {
        var f = PrefabFixtures.Create();
        var key = new MapPrefabChildKey(MapPrefabChildKind.Marker, "entry");
        var customized = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapSetPrefabOverride("a",
            new MapPrefabTransformOverride(key, new(new(0.5f, 0, 0), 0.25f, 1)))).After;
        var removed = MapPrefabEdits.Prepare(customized, f.Assets, new MapSetPrefabOverride("a", new MapPrefabRemoveOverride(key))).After;
        var restored = MapPrefabEdits.Prepare(MapDocumentFile.LoadText(MapDocumentFile.SaveText(removed)), f.Assets,
            new MapRestorePrefabChild("a", key)).After;
        Assert.Equal(PrefabFixtures.SemanticIntent(MapPrefabResolver.Expand(customized, f.Assets)),
            PrefabFixtures.SemanticIntent(MapPrefabResolver.Expand(restored, f.Assets)));
        Assert.Equal(customized.Prefabs!.Instances.Single().Bindings.Entries, restored.Prefabs!.Instances.Single().Bindings.Entries);
    }

    [Fact]
    public void PrepareReject_DoesNotMutateInputOrAllocator()
    {
        var f = PrefabFixtures.Create();
        string original = MapDocumentFile.SaveText(f.Document);
        Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(f.Document, f.Assets,
            new MapMovePrefab("a", new(new(1, 0, 0), 0, -1))));
        Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(f.Document, f.Assets,
            new MapPlacePrefab("a", "hut", new(Vector3.Zero, 0, 1))));
        Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(f.Document, f.Assets,
            new MapSetPrefabOverride("a", new MapPrefabRemoveOverride(new(MapPrefabChildKind.Surface, "floor")))));
        Assert.Equal(original, MapDocumentFile.SaveText(f.Document));
    }

    [Fact]
    public void AssetUpdate_ChangesEveryLiveInstanceWithStableBindingsAndRejectsWrongPins()
    {
        var f = PrefabFixtures.SpacelessRoomless();
        var first = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapPlacePrefab("first", "hut", new(Vector3.Zero, 0, 1))).After;
        var second = MapPrefabEdits.Prepare(first, f.Assets, new MapPlacePrefab("second", "hut", new(new(4, 0, -4), 0.371f, 0.8f))).After;
        var asset = f.Assets.GetPrefab("hut");
        var placement = asset.Children.OfType<MapPrefabPlacementChild>().Single();
        var copy = MapPrefabReferenceWalker.Copy(placement.Value, placement.Value.Id, placement.Value.SupportBinding);
        copy.Tags.Add("changed");
        var next = PrefabFixtures.PublishPrefab(f.Assets, asset with { Children = new[] { placement with { Value = copy } } });
        var edit = new MapUpdatePrefabAsset("hut", f.Assets.GetResource("hut").Reference.Sha256, "hut", next.GetResource("hut").Reference.Sha256);
        string original = MapDocumentFile.SaveText(second);
        var result = MapPrefabEdits.PrepareAssetUpdate(second, f.Assets, next, edit);
        Assert.Equal(new[] { "first", "second" }, result.Effects.InstanceIds);
        Assert.All(MapPrefabResolver.Expand(result.After, next).Placements, p => Assert.Contains("changed", p.Tags));
        Assert.Equal(second.Prefabs!.Instances.SelectMany(i => i.Bindings.Entries), result.After.Prefabs!.Instances.SelectMany(i => i.Bindings.Entries));
        Assert.Throws<MapDocumentException>(() => MapPrefabEdits.PrepareAssetUpdate(second, f.Assets, next, edit with { OldSha256 = new string('0', 64) }));
        Assert.Equal(original, MapDocumentFile.SaveText(second));
        var removed = MapPrefabEdits.Prepare(second, f.Assets, new MapRemovePrefab("first")).After;
        Assert.True(removed.Prefabs!.Instances.Single(i => i.Id == "first").Removed);
        Assert.NotEmpty(removed.Prefabs.Instances.Single(i => i.Id == "first").Bindings.Entries);
        Assert.Single(MapPrefabResolver.Expand(removed, f.Assets).Placements);
    }

    [Fact]
    public void InteriorPreparation_ChangesOnlyCandidateAndReportsEffectiveIntent()
    {
        var f = PrefabFixtures.Create();
        var unpacked = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var prism = unpacked.Surfaces.AllRecords().OfType<MapPrismDomain>().Single();
        var edited = MapInteriorEdits.Prepare(unpacked, f.Assets, new MapSetInterior(prism with { UpperY = 2.5f }));
        Assert.Equal(3, unpacked.Surfaces.AllRecords().OfType<MapPrismDomain>().Single().UpperY);
        Assert.Equal(2.5f, edited.After.Surfaces.AllRecords().OfType<MapPrismDomain>().Single().UpperY);
        Assert.Contains("a/room", edited.Effects.SpaceIds);
        var link = edited.After.BuildingIntent!.RoofLinks.Single();
        var roofEdit = MapInteriorEdits.Prepare(edited.After, f.Assets, new MapSetRoofLink(link with { AboveY = 2.5f }));
        Assert.Contains("a/roof", roofEdit.Effects.RoofIds);
        Assert.Throws<MapDocumentException>(() => MapInteriorEdits.Prepare(roofEdit.After, f.Assets, new MapRemoveInterior("a/room")));
    }
}
