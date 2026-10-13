using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class PrefabCapturedContextReviewTests
{
    [Fact]
    public void WorldCoveredWater_KeepsForeignHydraulicPortalThroughOpenEditAndExport()
    {
        var f = PrefabFixtures.Create();
        var unpacked = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var room = unpacked.Surfaces.AllRecords().OfType<MapSpaceDoc>().Single(r => r.Id == "a/room");
        Assert.Empty(room.Portals);
        var portal = unpacked.Surfaces.AllRecords().OfType<MapCavePortal>().Single();
        Assert.Equal("a/room", portal.FromSpace.Id); Assert.Equal("exterior", portal.ToSpace.Id);
        var roomRef = new MapRecordRef(room.Id, unpacked.Surfaces.Patches.Values.Single(p => p.Records.Contains(room)).Key);
        var parent = PrefabEditorFixtures.ParentRef(new(unpacked, f.Assets));
        var water = new MapPrefabWaterChild(new(MapPrefabChildKind.WaterBody, "observer-water"), new("observer-water", 0.5f,
            "observer-medium", "pool-material", new(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(0, 0, 4, 4) }, []), new[] { roomRef, parent }))
        {
            References = PrefabFixtures.Slots(PrefabFixtures.Slot("/value/medium", MapPrefabChildKind.WaterMedium, "observer-medium"),
            PrefabFixtures.Ref("/value/covers/0", roomRef.Id, roomRef.Anchor.SurfaceId, MapPrefabReferenceScope.World),
            PrefabFixtures.Ref("/value/covers/1", parent.Id, parent.Anchor.SurfaceId, MapPrefabReferenceScope.World))
        };
        var observer = new MapPrefabAsset(1, "observer", new MapPrefabChild[]
        { new MapPrefabMediumChild(new(MapPrefabChildKind.WaterMedium, "observer-medium"), new("observer-medium", MapWaterMediumKind.Water, 1)), water });
        var assets = PrefabFixtures.PublishPrefab(f.Assets, observer);
        unpacked.NativeAssets = assets.Roots.ToList();
        // The same observer is admitted by the full-map water gate before testing detached authoring.
        Assert.NotNull(MapPrefabEdits.Prepare(unpacked, assets, new MapPlacePrefab("observer-control", "observer", new(Vector3.Zero, 0, 1), parent)));
        var session = PrefabEditorFixtures.Session(new(unpacked, assets));
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session); var opened = service.Open("observer", parent);
        Assert.Contains(service.LocalSession!.Document.Surfaces.AllRecords(), r => r.Id == portal.Id);
        service.Edit(opened.Children.OfType<MapPrefabWaterChild>().Single() with { Value = water.Value with { SurfaceY = 0.75f } });
        var exported = service.Export();
        Assert.Equal(0.75f, exported.Children.OfType<MapPrefabWaterChild>().Single().Value.SurfaceY);
        Assert.Equal(water.Value.Covers, exported.Children.OfType<MapPrefabWaterChild>().Single().Value.Covers);
        service.Cancel(); Assert.Equal(before, PrefabEditorFixtures.State(session));
    }

    [Fact]
    public void WorldOwnedLight_KeepsForeignLiveImplicitProducerThroughOpenEditAndExport()
    {
        var f = PrefabFixtures.CapturedOriginals();
        var foreign = f.Document.Prefabs!.Instances.Single();
        Assert.Empty(f.Assets.GetPrefab("hut").MaterializedAttachments);
        var owner = foreign.Bindings.Entries.Single(b => b.Child.Kind == MapPrefabChildKind.Placement).WorldId;
        var light = new MapPrefabLightChild(new(MapPrefabChildKind.Light, "observer-light"), new(1, "observer-light",
            PrefabFixtures.World(MapPrefabChildKind.Placement, owner), new(0, 1, 0), Vector3.One, 1, 2, false));
        var assets = PrefabFixtures.PublishPrefab(f.Assets, new(1, "observer", new[] { light }));
        f.Document.NativeAssets = assets.Roots.ToList();
        Assert.NotNull(MapPrefabEdits.Prepare(f.Document, assets, new MapPlacePrefab("observer-control", "observer", new(Vector3.Zero, 0, 1))));
        var session = PrefabEditorFixtures.Session(new(f.Document, assets));
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session); var opened = service.Open("observer");
        AssertForeignUnchanged(f.Document, service.LocalSession!.Document, foreign.Id);
        service.Edit(opened.Children.OfType<MapPrefabLightChild>().Single() with { Value = light.Value with { Radius = 3 } });
        var exported = service.Export();
        Assert.Equal(3, exported.Children.OfType<MapPrefabLightChild>().Single().Value.Radius);
        Assert.Equal(light.Value.Owner, exported.Children.OfType<MapPrefabLightChild>().Single().Value.Owner);
        AssertForeignUnchanged(f.Document, service.LocalSession.Document, foreign.Id);
        service.Cancel(); Assert.Equal(before, PrefabEditorFixtures.State(session));
    }

    [Fact]
    public void RetainedExternalRecord_ReferencingRemovedSourceChildRefusesClearly()
    {
        var f = PrefabFixtures.Create();
        var unpacked = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var parent = PrefabEditorFixtures.ParentRef(new(unpacked, f.Assets));
        var patch = unpacked.Surfaces.Patches[parent.Anchor];
        patch.Records.Add(new MapBoundaryChain("external-edge", MapChainKind.Authored, null, new[]
        { new MapChainVertex(new("a/floor", MapLatticeAddress.Corner(0, 0)), 0), new MapChainVertex(new("a/floor", MapLatticeAddress.Corner(1, 0)), 0) }));
        PrefabFixtures.SealWorldParent(unpacked);
        var session = PrefabEditorFixtures.Session(new(unpacked, f.Assets));
        var before = PrefabEditorFixtures.State(session);
        var error = Assert.Throws<MapDocumentException>(() => new PrefabService(session).Open("hut"));
        Assert.Contains("external-edge", error.Message); Assert.Contains("a/floor", error.Message);
        Assert.Contains("references removed", error.Message);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
    }

    [Fact]
    public void RetainedForeignLiveRecord_ReferencingRemovedSourceChildRefusesClearly()
    {
        var f = PrefabFixtures.Create();
        var parent = PrefabEditorFixtures.ParentRef(f);
        var anchor = new MapPatchKey("foreign-anchor", 0, 0);
        var alias = new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "alias"),
            new MapSpaceDoc("alias", MapSpaceKind.Room, parent, new("a/room", new("a/floor", 0, 0)), [], [], [], []), anchor)
        {
            References = PrefabFixtures.Slots(PrefabFixtures.Anchor(anchor.SurfaceId),
            PrefabFixtures.Ref("/value/parent", parent.Id, parent.Anchor.SurfaceId, MapPrefabReferenceScope.World),
            PrefabFixtures.Ref("/value/aliasOf", "a/room", "a/floor", MapPrefabReferenceScope.World))
        };
        var surface = PrefabFixtures.SurfaceChild(anchor.SurfaceId, MapSurfaceRole.SupportFloor, PrefabFixtures.Patch(anchor.SurfaceId, 1, 1, -200));
        var assets = PrefabFixtures.PublishPrefab(f.Assets, new(1, "foreign", new MapPrefabChild[] { surface, alias }));
        f.Document.NativeAssets = assets.Roots.ToList();
        var placed = MapPrefabEdits.Prepare(f.Document, assets, new MapPlacePrefab("foreign", "foreign", new(Vector3.Zero, 0, 1), parent)).After;
        var session = PrefabEditorFixtures.Session(new(placed, assets));
        var before = PrefabEditorFixtures.State(session);
        var error = Assert.Throws<MapDocumentException>(() => new PrefabService(session).Open("hut"));
        Assert.Contains("alias", error.Message); Assert.Contains("a/floor", error.Message);
        Assert.Contains("/value/aliasOf/anchor/surfaceId", error.Message);
        Assert.Contains("references removed", error.Message);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
    }

    [Fact]
    public void OpeningCapturedSource_RemovesOnlyItsTransferredChildrenAndKeepsReservations()
    {
        var f = PrefabFixtures.CapturedOriginals();
        var unpacked = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a")).After;
        var source = unpacked.Prefabs!.Instances.Single();
        Assert.Equal(2, unpacked.Prefabs.Attachments.Count);
        var session = PrefabEditorFixtures.Session(new(unpacked, f.Assets));
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session); service.Open("hut");
        var local = service.LocalSession!.Document;
        var reservation = local.Prefabs!.Instances.Single(i => i.Id == source.Id);
        Assert.True(reservation.Removed); Assert.True(reservation.Unpacked);
        Assert.Equal(source.ResourceId, reservation.ResourceId); Assert.Equal(source.ResourceSha256, reservation.ResourceSha256);
        Assert.Equal(source.Bindings.Entries.Select(b => b with { Removed = true }), reservation.Bindings.Entries);
        Assert.Equal(unpacked.NumericIdHighWaterMark, local.NumericIdHighWaterMark);
        Assert.DoesNotContain(local.Prefabs.Transfers, t => t.InstanceId == source.Id);
        Assert.DoesNotContain(local.Prefabs.Attachments, a => source.Bindings.Entries.Any(b => b.WorldId == a.PlacementId));
        Assert.DoesNotContain(local.NativeResourceTransforms!, p => source.Bindings.Entries.Any(b => b.Child.Kind == p.Kind && b.WorldId == p.OwnerId));
        service.Cancel(); Assert.Equal(before, PrefabEditorFixtures.State(session));
    }

    [Fact]
    public void OpeningLiveSource_PreservesUnrelatedCapturedMapContent()
    {
        var f = PrefabEditorFixtures.Create("native-generic");
        f.Document.Terrain.Seed = 17;
        f.Document.Placements.Add(new() { Id = "unrelated", Kind = "item", AssetId = "item", X = 12, Y = 0, Z = -12 });
        var source = f.Document.Prefabs!.Instances.Single();
        var session = PrefabEditorFixtures.Session(f);
        var before = PrefabEditorFixtures.State(session);
        var service = new PrefabService(session); service.Open("hut");
        var local = service.LocalSession!.Document;
        Assert.Equal(MapPrefabJson.Bytes(f.Document.Terrain), MapPrefabJson.Bytes(local.Terrain));
        Assert.Equal(MapPrefabJson.Bytes(f.Document.ScatterLayers), MapPrefabJson.Bytes(local.ScatterLayers));
        Assert.Equal(MapPrefabJson.Bytes(f.Document.ScatterOverrides), MapPrefabJson.Bytes(local.ScatterOverrides));
        Assert.Equal(MapPrefabJson.Bytes(f.Document.Regions), MapPrefabJson.Bytes(local.Regions));
        Assert.Equal(MapPrefabJson.Bytes(f.Document.Placements.Single()), MapPrefabJson.Bytes(local.Placements.Single(p => p.Id == "unrelated")));
        var reservation = local.Prefabs!.Instances.Single(i => i.Id == source.Id);
        Assert.True(reservation.Removed); Assert.False(reservation.Unpacked);
        Assert.Equal(source.Bindings.Entries.Select(b => b with { Removed = true }), reservation.Bindings.Entries);
        service.Cancel(); Assert.Equal(before, PrefabEditorFixtures.State(session));
    }

    internal static void AssertForeignUnchanged(MapDocument before, MapDocument after, string instanceId)
    {
        Assert.Equal(MapPrefabJson.Bytes(before.Prefabs!.Instances.Single(i => i.Id == instanceId)),
            MapPrefabJson.Bytes(after.Prefabs!.Instances.Single(i => i.Id == instanceId)));
        Assert.Equal(MapPrefabJson.Bytes(before.Prefabs.Transfers.Where(t => t.InstanceId == instanceId).ToArray()),
            MapPrefabJson.Bytes(after.Prefabs.Transfers.Where(t => t.InstanceId == instanceId).ToArray()));
        Assert.Equal(MapPrefabJson.Bytes(before.Prefabs.Attachments), MapPrefabJson.Bytes(after.Prefabs.Attachments));
    }
}
