using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

public sealed class MapMarkerRemapTests
{
    [Fact]
    public void RemapWorld_RenamesOnlyTheActiveMarkerOwner()
    {
        MapDocument original = Standalone();
        string originalText = MapDocumentFile.SaveText(original);
        string storedBindingBytes = BindingBytes(original);
        MapSupportBinding surfaceBefore = Binding(original, "s"), spaceBefore = Binding(original, "v");

        MapDocument surfaceRenamed = MapPrefabReferences.RemapWorld(original, MapPrefabChildKind.Surface, "x", "y");
        MapDocument recordRenamed = MapPrefabReferences.RemapWorld(original, MapPrefabChildKind.Record, "x", "y");

        Assert.Equal("y", Binding(surfaceRenamed, "s").SurfaceId);
        Assert.Equal("x", Binding(surfaceRenamed, "v").SpaceId);
        Assert.Equal("y", Binding(recordRenamed, "v").SpaceId);
        Assert.Equal("x", Binding(recordRenamed, "s").SurfaceId);
        Assert.Equal("x", Authored(surfaceRenamed, "e").Name);
        Assert.Equal(new[] { "x" }, Authored(surfaceRenamed, "e").Tags);
        Assert.Equal(new[] { "x" }, Authored(recordRenamed, "e").Tags);
        Assert.Equal(surfaceBefore with { SurfaceId = "y" }, Binding(surfaceRenamed, "s"));
        AssertBits(spaceBefore with { SpaceId = "y" }, Binding(recordRenamed, "v"));
        Assert.Equal(spaceBefore, Binding(surfaceRenamed, "v"));
        foreach (MapDocument renamed in new[] { surfaceRenamed, recordRenamed })
        {
            Assert.Equal(Json(Authored(original, "e")), Json(Authored(renamed, "e")));
            Assert.All(renamed.BuildingIntent!.Markers.Where(m => m.HeightMode == MapMarkerHeightMode.Support),
                m => Assert.Equal(0u, BitConverter.SingleToUInt32Bits(m.Position.Y)));
        }
        Assert.Equal(originalText, MapDocumentFile.SaveText(original));
        Assert.Equal(storedBindingBytes, BindingBytes(MapDocumentFile.LoadText(MapDocumentFile.SaveText(original))));

        foreach (var (fixture, form, kind) in new[]
        {
            (MarkerFixtures.TransferredSurface(), "surface", MapPrefabChildKind.Surface),
            (MarkerFixtures.TransferredSpace(), "space", MapPrefabChildKind.Record),
        })
        {
            string transferredId = "a/" + form + "-marker", oldOwner = form == "surface" ? "a/floor" : "a/room", renamedOwner = "renamed-owner";
            string placedBytes = BindingBytes(fixture.Document);
            _ = MarkerFixtures.Effective(fixture);
            Assert.Equal(placedBytes, BindingBytes(fixture.Document));

            MapDocument unpacked = MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapUnpackPrefab("a")).After;
            MapSupportBinding localBindingBefore = Binding(unpacked, transferredId);
            MapEffectiveTransform frameBefore = FrameOf(unpacked, transferredId);
            string unpackedText = MapDocumentFile.SaveText(unpacked), unpackedBytes = BindingBytes(unpacked);
            Assert.Equal(oldOwner, form == "surface" ? localBindingBefore.SurfaceId : localBindingBefore.SpaceId);

            MapDocument transferredRenamed = MapPrefabReferences.RemapWorld(unpacked, kind, oldOwner, renamedOwner);
            MapSupportBinding expected = form == "surface" ? localBindingBefore with { SurfaceId = renamedOwner } : localBindingBefore with { SpaceId = renamedOwner };
            Assert.Equal(expected, Binding(transferredRenamed, transferredId));
            AssertBits(expected, Binding(transferredRenamed, transferredId));
            Assert.Equal(frameBefore, FrameOf(transferredRenamed, transferredId));
            Assert.Equal(localBindingBefore, Binding(MapPrefabReferences.RemapWorld(unpacked,
                kind == MapPrefabChildKind.Surface ? MapPrefabChildKind.Record : MapPrefabChildKind.Surface, oldOwner, renamedOwner), transferredId));

            Assert.Equal(unpackedText, MapDocumentFile.SaveText(unpacked));
            Assert.Equal(unpackedBytes, BindingBytes(MapDocumentFile.LoadText(MapDocumentFile.SaveText(unpacked))));
            _ = MarkerFixtures.Effective(fixture with { Document = unpacked });
            Assert.Equal(unpackedBytes, BindingBytes(unpacked));
        }
    }

    [Fact]
    public void BoundMarkerUnpackExtract_KeepsIntentAndTransformsOnce()
    {
        MarkerFixture fixture = MarkerFixtures.TransferredSpace();
        const string id = "a/space-marker";
        MapEffectiveMarker before = Marker(MarkerFixtures.Effective(fixture), id);
        // The effective request is the frame Y plus the scaled local reference, with scaled searches.
        float referenceY = Frozen(-450d + 0.5d * 1.2f), search = (float)(1d * 1.2f);
        Assert.Equal(MarkerFixtures.SpaceBinding("a/room", referenceY, search, search), before.HeightProvenance!.Binding);
        Assert.Equal(MapExactValue.FromSingle(-450f), before.Position.Y);

        MapDocument unpacked = MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapUnpackPrefab("a")).After;
        // The local child with its owner written as the bound world ID. Reference and searches stay frame-local.
        const string expectedBytes = """{"kind":"Space","spaceId":"a/room","referenceY":0.5,"searchBelow":1,"searchAbove":1}""";
        foreach (MapDocument stored in new[]
        {
            unpacked, MapDocumentFile.LoadText(MapDocumentFile.SaveText(unpacked)),
            MarkerFixtures.Reload(fixture with { Document = unpacked }, true).Document,
        })
        {
            Assert.Equal(expectedBytes, StoredBinding(stored, id));
            MapLocalMarkerDoc authored = Authored(stored, id);
            Assert.Equal(MapMarkerHeightMode.Support, authored.HeightMode);
            Assert.Equal(0u, BitConverter.SingleToUInt32Bits(authored.Position.Y));
            Assert.Equal(MapEffectiveTransform.From(MarkerFixtures.TransferredFrame), FrameOf(stored, id));
            MapEffectiveMarker after = Marker(MarkerFixtures.Effective(fixture with { Document = stored }), id);
            Assert.Equal(before.HeightProvenance, after.HeightProvenance);
            Assert.Equal(before.Position, after.Position);
            Assert.Equal(before.Tags, after.Tags);
            Assert.Equal("a", after.InstanceId);
            Assert.InRange(Math.Abs(Math.IEEERemainder(before.YawRadians - after.YawRadians, 2 * Math.PI)), 0, 0.000001);
        }

        // A regraded transferred floor moves the marker with it, through the same single frame.
        MapDocument regraded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(unpacked));
        MapPatchKey floor = regraded.Surfaces.Patches.Keys.Single(k => k.SurfaceId == "a/floor");
        regraded.Surfaces = MapTerrainEdits.Prepare(regraded.Surfaces, new MapSetCornerHeights(floor, 0, 0, 5, 5, Enumerable.Repeat(25, 25).ToArray())).Candidate;
        MapEffectiveMarker moved = Marker(MarkerFixtures.Effective(fixture with { Document = regraded }), id);
        Assert.Equal(MapExactValue.FromSingle(Frozen(-450d + 0.25d * 1.2f)), moved.Position.Y);
        Assert.Equal(before.HeightProvenance.Binding, moved.HeightProvenance!.Binding);

        // Extraction at the instance frame captures the local child exactly, and a replacement instance reproduces it.
        var selection = unpacked.Prefabs!.Transfers.Select(t => PrefabFixtures.World(t.Binding.Child.Kind, t.Binding.WorldId)).ToArray();
        MapPrefabAsset extracted = MapPrefabExtraction.Extract(MapDocumentFile.LoadText(MapDocumentFile.SaveText(unpacked)), fixture.Assets,
            selection, MarkerFixtures.TransferredFrame, "captured");
        var child = extracted.Children.OfType<MapPrefabMarkerChild>().Single(c => c.Key.Key == "space-marker");
        MapLocalMarkerDoc local = MarkerFixtures.SpaceChild().Value;
        Assert.Equal((local.HeightMode, local.SupportBinding, local.Position, local.YawRadians), (child.Value.HeightMode, child.Value.SupportBinding, child.Value.Position, child.Value.YawRadians));
        Assert.Equal(local.Tags, child.Value.Tags);
        Assert.Equal(new MapTransform(Vector3.Zero, 0, 1), child.LocalTransform);
        Assert.Equal(MarkerFixtures.SpaceChild().References, child.References);

        MapPrefabAsset replaced = MapPrefabAssetEdits.Replace(extracted, child with { Value = child.Value with { Name = "replaced-name" } });
        Assert.Equal(child.References, replaced.Children.OfType<MapPrefabMarkerChild>().Single(c => c.Key.Key == "space-marker").References);
        var assets = PrefabFixtures.PublishPrefab(fixture.Assets, replaced);
        MapDocument host = MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapRemovePrefab("a")).After;
        host.NativeAssets = assets.Roots.ToList();
        MapRecordRef parent = fixture.Document.Prefabs!.Instances.Single().ParentSpace!;
        MapDocument placed = MapPrefabEdits.Prepare(host, assets, new MapPlacePrefab("copy", "captured", MarkerFixtures.TransferredFrame, parent)).After;
        var copyBindings = placed.Prefabs!.Instances.Single(i => i.Id == "copy").Bindings.Entries.ToDictionary(b => b.Child, b => b.WorldId);
        string copyRoom = copyBindings[new(MapPrefabChildKind.Record, "room")];
        MapEffectiveMarker copy = Marker(MarkerFixtures.Effective(new(placed, assets)), copyBindings[child.Key]);
        Assert.Equal("replaced-name", copy.Name);
        Assert.Equal("copy", copy.InstanceId);
        Assert.Equal(before.Position, copy.Position);
        Assert.Equal(before.HeightProvenance.WorldY, copy.HeightProvenance!.WorldY);
        Assert.Equal(before.HeightProvenance.Binding! with { SpaceId = copyRoom }, copy.HeightProvenance.Binding);
        Assert.Equal(copyRoom, copy.HeightProvenance.SpaceId);
    }

    [Fact]
    public void MarkerBindingSlots_RemapOnlyTheDeclaredSlotAndRefuseInvalidSlots()
    {
        MapPrefabAsset hut = PrefabFixtures.Create().Assets.GetPrefab("hut");
        MapPrefabMarkerChild local = Child("local-floor", MarkerFixtures.SurfaceBinding("floor"), Slot("surfaceId", MapPrefabReferenceScope.Local, MapPrefabChildKind.Surface, "floor"));
        MapPrefabMarkerChild world = Child("world-floor", MarkerFixtures.SurfaceBinding("floor"), Slot("surfaceId", MapPrefabReferenceScope.World, MapPrefabChildKind.Surface, "floor"));
        MapPrefabMarkerChild parentSpace = Child("parent-space", MarkerFixtures.SpaceBinding("$parent", 0.5f, 1, 1), Slot("spaceId", MapPrefabReferenceScope.Local, MapPrefabChildKind.Record, "$parent"));
        MapPrefabAsset asset = MapPrefabFile.Read(MapPrefabFile.Write(hut with { Children = hut.Children.Concat(new[] { local, world, parentSpace }).ToArray() }), "hut");

        var bindings = asset.Children.ToDictionary(c => c.Key, c => "a/" + c.Key.Key);
        MapPrefabAsset remapped = MapPrefabReferences.Remap(asset, bindings, new MapRecordRef("outer", new("outer-floor", 0, 0)));
        MapLocalMarkerDoc Remapped(string key) => remapped.Children.OfType<MapPrefabMarkerChild>().Single(c => c.Value.Id == "a/" + key).Value;
        Assert.Equal("a/floor", Remapped("local-floor").SupportBinding!.SurfaceId);
        Assert.Equal("floor", Remapped("world-floor").SupportBinding!.SurfaceId);
        AssertBits(MarkerFixtures.SpaceBinding("outer", 0.5f, 1, 1), Remapped("parent-space").SupportBinding!);
        Assert.All(remapped.Children.OfType<MapPrefabMarkerChild>().Where(c => c.Value.HeightMode == MapMarkerHeightMode.Support),
            c => Assert.Equal(0u, BitConverter.SingleToUInt32Bits(c.Value.Position.Y)));

        MapPrefabMarkerChild undeclared = Child("undeclared", MarkerFixtures.SurfaceBinding("nowhere"), Slot("surfaceId", MapPrefabReferenceScope.Local, MapPrefabChildKind.Surface, "nowhere"));
        MapPrefabMarkerChild explicitMarker = new(new(MapPrefabChildKind.Marker, "explicit"), MapMarkerHeights.Apply(
            new(1, "explicit", "explicit-name", new Vector3(1, 0, -1), 0, "guard-role", true, Array.Empty<string>()), MarkerFixtures.ExplicitHeight()));
        foreach (MapPrefabChild invalid in new MapPrefabChild[]
        {
            local with { References = [] },
            local with { References = [Slot("spaceId", MapPrefabReferenceScope.Local, MapPrefabChildKind.Surface, "floor")] },
            local with { References = [Slot("surfaceId", MapPrefabReferenceScope.Local, MapPrefabChildKind.Record, "floor")] },
            local with { References = [.. local.References, Slot("spaceId", MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "floor")] },
            parentSpace with { References = [Slot("spaceId", MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "$parent")] },
            undeclared,
            explicitMarker with { References = [Slot("surfaceId", MapPrefabReferenceScope.World, MapPrefabChildKind.Surface, "floor")] },
        })
            Assert.Throws<MapDocumentException>(() => MapPrefabFile.Write(hut with { Children = hut.Children.Append(invalid).ToArray() }));
        Assert.NotNull(MapPrefabFile.Read(MapPrefabFile.Write(hut with { Children = hut.Children.Append(explicitMarker).ToArray() }), "hut"));
    }

    [Fact]
    public void BoundOwnerRemoval_RefusesWhileBoundAndRestoreResolves()
    {
        MarkerFixture fixture = MarkerFixtures.PaddedSurface();
        MapEffectiveMarker before = Marker(MarkerFixtures.Effective(fixture), "a/pad-marker");
        Assert.Equal("a/pad", before.HeightProvenance!.Face!.Value.OwnerId);
        var pad = new MapPrefabChildKey(MapPrefabChildKind.Surface, "pad");
        var marker = new MapPrefabChildKey(MapPrefabChildKind.Marker, "pad-marker");

        Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(fixture.Document, fixture.Assets,
            new MapSetPrefabOverride("a", new MapPrefabRemoveOverride(pad))));

        // Without the bound marker the same owner removal succeeds, and restoring both resolves the marker again.
        MapDocument unbound = MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapSetPrefabOverride("a", new MapPrefabRemoveOverride(marker))).After;
        MapDocument removed = MapPrefabEdits.Prepare(unbound, fixture.Assets, new MapSetPrefabOverride("a", new MapPrefabRemoveOverride(pad))).After;
        Assert.DoesNotContain(MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(fixture with { Document = removed })), m => m.Id == "a/pad-marker");
        Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(removed, fixture.Assets, new MapRestorePrefabChild("a", marker)));
        MapDocument restoredOwner = MapPrefabEdits.Prepare(removed, fixture.Assets, new MapRestorePrefabChild("a", pad)).After;
        MapDocument restored = MapPrefabEdits.Prepare(restoredOwner, fixture.Assets, new MapRestorePrefabChild("a", marker)).After;
        MapEffectiveMarker after = Marker(MarkerFixtures.Effective(fixture with { Document = restored }), "a/pad-marker");
        Assert.Equal((before.Position, before.HeightProvenance), (after.Position, after.HeightProvenance));
    }

    static MapDocument Standalone()
    {
        MapDocument document = MarkerFixtures.Create("basic").Document;
        document.BuildingIntent = new(1, [], [], [], new[]
        {
            MarkerFixtures.Marker("s", 1, -1, new(null, MarkerFixtures.SurfaceBinding("x"))),
            MarkerFixtures.Marker("v", 2, -2, new(null, MarkerFixtures.SpaceBinding("x", 3.5f, 0.25f, 0.75f))),
            MapMarkerHeights.Apply(new(1, "e", "x", new Vector3(3, 0, -3), 0.5f, "guard-role", true, new[] { "x" }), new(1.5f, null)),
        });
        return MapDocumentFile.LoadText(MapDocumentFile.SaveText(document));
    }

    static MapPrefabMarkerChild Child(string key, MapSupportBinding binding, MapPrefabReferenceSlot slot) =>
        new(new(MapPrefabChildKind.Marker, key), MarkerFixtures.Marker(key, 1, -1, new(null, binding))) { References = [slot] };

    static MapPrefabReferenceSlot Slot(string member, MapPrefabReferenceScope scope, MapPrefabChildKind kind, string id) =>
        new("/value/supportBinding/" + member, new(scope, kind, id));

    static MapEffectiveMarker Marker(MapEffectiveResources resources, string id) => MapEffectiveAnnotations.Markers(resources).Single(m => m.Id == id);

    // Local readers of the stored building-intent marker, its frame entry and its canonical JSON. None remaps anything.
    static MapLocalMarkerDoc Authored(MapDocument document, string id) => document.BuildingIntent!.Markers.Single(m => m.Id == id);
    static MapSupportBinding Binding(MapDocument document, string id) => Authored(document, id).SupportBinding!;
    static MapEffectiveTransform FrameOf(MapDocument document, string id) =>
        document.NativeResourceTransforms!.Single(p => p.Kind == MapPrefabChildKind.Marker && p.OwnerId == id).Transform;
    static IEnumerable<JsonNode> StoredMarkers(MapDocument document) =>
        JsonNode.Parse(MapDocumentFile.SaveText(document))!["buildingIntent"]?["markers"]?.AsArray().Select(m => m!) ?? [];
    static string StoredBinding(MapDocument document, string id) =>
        StoredMarkers(document).Single(m => m["id"]!.GetValue<string>() == id)["supportBinding"]!.ToJsonString();
    static string BindingBytes(MapDocument document) => string.Join("\n", StoredMarkers(document)
        .Select(m => m["id"]!.GetValue<string>() + "=" + (m["supportBinding"]?.ToJsonString() ?? "none")));
    static string Json(MapLocalMarkerDoc marker) => System.Text.Encoding.UTF8.GetString(MapPrefabFile.Write(new(1, "probe",
        new MapPrefabChild[] { new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, marker.Id), marker) })));

    static void AssertBits(MapSupportBinding expected, MapSupportBinding actual)
    {
        Assert.Equal(expected, actual);
        foreach (var (a, b) in new[] { (expected.ReferenceY, actual.ReferenceY), (expected.SearchBelow, actual.SearchBelow), (expected.SearchAbove, actual.SearchAbove) })
            Assert.Equal(a is { } x ? BitConverter.SingleToUInt32Bits(x) : (uint?)null, b is { } y ? BitConverter.SingleToUInt32Bits(y) : (uint?)null);
    }

    static float Frozen(double metres) => (float)(Math.Round(metres * 1048576, MidpointRounding.ToEven) / 1048576);
}
