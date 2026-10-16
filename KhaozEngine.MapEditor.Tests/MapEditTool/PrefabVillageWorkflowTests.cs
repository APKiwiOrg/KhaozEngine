using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEdit.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

[Collection(McpWireCollection.Name)]
public sealed class PrefabVillageWorkflowTests
{
    [Fact]
    public void NativePaintAssetUpdate_InvalidatesMaterialAndResidency()
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create("paint-notch"));
        var before = PrefabFixtures.CanonicalPatch(Effective(session), "ground");
        var service = new PrefabService(session);
        var asset = service.Open("hut");
        var paint = asset.Children.OfType<MapPrefabSurfaceChild>().Single();
        foreach (var patch in paint.Patches)
            for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Underlay = 8 };
        service.Edit(paint);
        var published = service.Save("hut", "published");
        var resources = Effective(session);
        Assert.All(resources.PaintRegions["ground"].Regions, r => Assert.Equal((ushort)8, r.Material.Underlay));
        Assert.Equal(before.ExactVertices.ToArray(), PrefabFixtures.CanonicalPatch(resources, "ground").ExactVertices.ToArray());
        Assert.True(published.Mutation!.Effects.Invalidates.HasFlag(MapNativeInvalidation.Material), published.Mutation.Effects.Describe());
        Assert.True(published.Mutation.Effects.Invalidates.HasFlag(MapNativeInvalidation.Residency));
        Assert.False(published.Mutation.Effects.Invalidates.HasFlag(MapNativeInvalidation.Unbounded));
        Assert.InRange(published.Mutation.Effects.OldBounds!.Value.MaxY, 0, 2);
        Assert.InRange(published.Mutation.Effects.NewBounds!.Value.MaxY, 0, 2);
    }

    [Fact]
    public void LocalAssetSession_ExcludesSourceRoomFromContextConcealmentOnly()
    {
        var f = PrefabFixtures.LinkedBuildings();
        var session = PrefabEditorFixtures.Session(f);
        string before = Bytes(session);
        var service = new PrefabService(session);
        var asset = service.Open("hut");
        Assert.Equal(before, Bytes(session));
        Assert.Equal("shared", asset.Children.OfType<MapPrefabRoofChild>().Single().Value.Groups.Single().Id);
        var context = service.LocalSession!.Document;
        var group = context.BuildingIntent!.RoofGroups.Single(g => g.Id == "shared");
        Assert.Equal(new[] { "room-b" }, group.Rooms.Select(r => r.Id).ToArray());
        Assert.Contains(Effective(session).BuildingIntent.RoofGroups.Single(g => g.Id == "shared").Rooms, r => r.Id == "room-a");
        Assert.Contains(context.Prefabs!.Instances, i => i.Id == "b" && !i.Removed);
    }

    [Fact]
    public void WholeBuildingMoveOverrideUndoRedoSaveReload_UsesSharedCommands()
    {
        var f = PrefabFixtures.Village();
        var direct = PrefabEditorFixtures.Session(f);
        var wire = PrefabEditorFixtures.Session(f);
        var mutation = new MutationService(direct);
        var wireMutation = new MutationService(wire);
        var service = new PrefabService(direct);
        var tools = new PrefabTools(new PrefabService(wire), wireMutation, wire);
        string original = Bytes(direct);
        var moved = new MapTransform(new(6.25f, 5, -10.125f), 0.371f, 0.8f);
        EqualMutation(mutation.PrefabApply(new MapMovePrefab("a", moved)), wireMutation.PrefabApply(new MapMovePrefab("a", moved)));
        Assert.Equal(moved, PrefabEditorFixtures.Document(direct).Prefabs!.Instances.Single(i => i.Id == "a").Transform);
        Assert.Equal(original, BytesAfterUndo(direct, wire, tools));
        Assert.NotNull(direct.RedoNative()); Assert.NotNull(tools.PrefabRedo());
        EqualDocuments();
        var change = new MapPrefabPlacementOverride(new(MapPrefabChildKind.Placement, "item"), null, null, new[] { "furnished", "first", "second" });
        EqualMutation(mutation.PrefabApply(new MapSetPrefabOverride("a", change)), tools.PrefabOverride("a", Json<MapPrefabOverride>(change)));
        // A second canopy names its marker in the same placement, because marker names are globally unique.
        var names = PrefabFixtures.MarkerNames(direct.WithNativeAssets(c => c.Assets.GetPrefab("canopy")), "tool-canopy");
        EqualMutation(mutation.PrefabApply(new MapPlacePrefab("tool-canopy", "canopy", new(new(32.25f, 5, -18.125f), 0.371f, 1.2f)) { InitialOverrides = names }),
            tools.PrefabPlaceWithOverrides("tool-canopy", "canopy", 32.25f, 5, -18.125f, 0.371f, 1.2f, overridesJson: Json(names)));
        string packed = Bytes(direct);
        var before = PrefabEditorFixtures.Document(direct).Prefabs!.Instances.Single(i => i.Id == "a");
        EqualMutation(mutation.PrefabApply(new MapUnpackPrefab("a")), tools.PrefabUnpack("a"));
        string unpacked = Bytes(direct);
        var document = PrefabEditorFixtures.Document(direct);
        var source = document.Prefabs!.Instances.Single(i => i.Id == "a");
        Assert.True(source.Removed); Assert.True(source.Unpacked);
        Assert.Equal(before.Bindings.Entries.ToArray(), source.Bindings.Entries.ToArray());
        Assert.Equal(before.Bindings.Entries.Where(b => !b.Removed).Select(b => (b.Child, b.WorldId, b.NumericId)),
            document.Prefabs.Transfers.Where(t => t.InstanceId == "a" && !t.Retired).Select(t => (t.Binding.Child, t.Binding.WorldId, t.Binding.NumericId)));
        var item = document.Placements.Single(p => p.Id == "a/item");
        Assert.Null(item.Y);
        Assert.Equal(PrefabFixtures.SupportFloorId, item.SupportBinding!.SurfaceId);
        Assert.Equal(new[] { "furnished", "first", "second" }, item.Tags.ToArray());
        Assert.Equal(packed, BytesAfterUndo(direct, wire, tools));
        Assert.NotNull(direct.RedoNative()); Assert.NotNull(tools.PrefabRedo());
        Assert.Equal(unpacked, Bytes(direct)); EqualDocuments();
        Assert.Equal(packed, BytesAfterUndo(direct, wire, tools));

        string other = PrefabEditorFixtures.OtherMapBefore(direct);
        var hut = service.Open("hut");
        Assert.Equal(MapPrefabFile.Write(hut), MapPrefabFile.Write(tools.PrefabOpen("hut")));
        var marker = hut.Children.OfType<MapPrefabMarkerChild>().Single();
        Edit(marker with { Value = marker.Value with { Name = "Village arrival", Position = new(0, 1.5f, 0), YawRadians = 0.271f } });
        var paint = hut.Children.OfType<MapPrefabSurfaceChild>().Single(c => c.Key.Key == "paint");
        foreach (var patch in paint.Patches)
            for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Underlay = 8 };
        Edit(paint);
        var a = service.Save("hut", "published");
        var b = tools.PrefabSave("hut", "published");
        Assert.Equal(a.Resource, b.Resource); EqualMutation(a.Mutation!, b.Mutation!);
        Assert.True(a.Mutation!.Effects.Invalidates.HasFlag(MapNativeInvalidation.Material));
        Assert.Equal("prefab/hut/" + a.Resource.Sha256, a.Resource.Id);
        Assert.All(PrefabEditorFixtures.Document(direct).Prefabs!.Instances.Where(i => i.Id is "a" or "b"), i => Assert.Equal(a.Resource.Id, i.ResourceId));
        Assert.Equal(other, PrefabEditorFixtures.OtherMapNow(direct));
        Assert.Equal("hut", MapPrefabFile.Read(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(direct.DocumentPath)!, a.Resource.Path)), a.Resource.Id).Id);

        var bath = service.Open("bath"); tools.PrefabOpen("bath");
        var water = bath.Children.OfType<MapPrefabWaterChild>().Single();
        Edit(water with { Value = water.Value with { SurfaceY = 0.75f } });
        var bathA = service.Save("bath", "published"); var bathB = tools.PrefabSave("bath", "published");
        Assert.Equal(bathA.Resource, bathB.Resource); EqualMutation(bathA.Mutation!, bathB.Mutation!);
        Assert.True(bathA.Mutation!.Effects.Invalidates.HasFlag(MapNativeInvalidation.Medium));
        direct.Save(); wire.Save();
        var reopened = new MapEditSession(); reopened.Open(direct.DocumentPath!);
        Assert.Equal(Bytes(direct), Bytes(reopened));
        var effective = Effective(direct); var reload = Effective(reopened);
        Assert.Equal(effective.Digest, reload.Digest);
        Assert.Equal(effective.Placements.Select(p => (p.PlacementId, p.NumericId)), reload.Placements.Select(p => (p.PlacementId, p.NumericId)));
        Assert.Equal(0.75f, new PrefabService(reopened).Open(bathA.Resource.Id).Children.OfType<MapPrefabWaterChild>().Single().Value.SurfaceY);
        // The asset edit renames a's marker. Repeated instance b keeps its explicit replace override name.
        Assert.Equal(new[] { ("a", "Village arrival"), ("b", "Entry b") },
            MapEffectiveAnnotations.Markers(effective).Where(m => m.InstanceId is "a" or "b").Select(m => (m.InstanceId!, m.Name)));
        Assert.All(effective.PaintRegions["exterior"].Regions, r => Assert.Equal((ushort)8, r.Material.Underlay));

        void Edit(MapPrefabChild child)
        {
            Assert.Equal(MapPrefabFile.Write(service.Edit(child)), MapPrefabFile.Write(tools.PrefabEdit(Json<MapPrefabChild>(child))));
            EqualDocuments();
        }
        void EqualMutation(MapPrefabMutationResult left, MapPrefabMutationResult right)
        {
            Assert.True(left.Mutation.WorldChanged); Assert.True(right.Mutation.WorldChanged);
            Assert.Equal(left.Effects.Describe(), right.Effects.Describe()); EqualDocuments();
        }
        void EqualDocuments() => Assert.Equal(Bytes(direct), Bytes(wire));
    }

    [Fact]
    public void VillageInteriorTools_CreateRoomAndPrismAsOneHistoryEntry()
    {
        var f = PrefabFixtures.Village();
        var direct = PrefabEditorFixtures.Session(f); var wire = PrefabEditorFixtures.Session(f);
        var mutation = new MutationService(direct);
        var tools = new InteriorTools(new MutationService(wire), wire);
        var domain = Hall();
        string before = Bytes(direct);
        var a = mutation.InteriorApply(new MapSetInterior(domain));
        var b = tools.InteriorVolumeAdd(Json<MapTopologyRecord>(domain));
        Assert.Equal(a.Effects.Describe(), b.Effects.Describe()); Assert.Equal(Bytes(direct), Bytes(wire));
        Assert.Equal((1, 1, 1L), wire.NativeHistoryState);
        Assert.Contains(PrefabEditorFixtures.Document(wire).Surfaces.AllRecords(), r => r is MapSpaceDoc { Id: "hall", Kind: MapSpaceKind.Room });
        Assert.Contains(PrefabEditorFixtures.Document(wire).Surfaces.AllRecords(), r => r is MapPrismDomain { Id: "hall-domain" });
        Assert.NotNull(direct.UndoNative()); Assert.NotNull(wire.UndoNative());
        Assert.Equal(before, Bytes(direct)); Assert.Equal(before, Bytes(wire));
        Assert.NotNull(direct.RedoNative()); Assert.NotNull(wire.RedoNative());
        var link = new MapRoofLinkDoc("hall-roof", PrefabFixtures.World(MapPrefabChildKind.Placement, "canopy/roof"),
            new[] { PrefabFixtures.World(MapPrefabChildKind.Record, "hall") }, [], 3);
        Assert.Equal(mutation.InteriorApply(new MapSetRoofLink(link)).Effects.Describe(), tools.RoofLinkSet(Json(link)).Effects.Describe());
        Assert.Equal(mutation.InteriorApply(new MapSetInterior(domain with { UpperY = 2.5f })).Effects.Describe(), tools.InteriorVolumeSet(Json<MapTopologyRecord>(domain with { UpperY = 2.5f })).Effects.Describe());
        Assert.Equal(Bytes(direct), Bytes(wire));
    }

    [Fact]
    public async Task McpVillageWorkflow_PublishesImmutableAssetsAndAtomicInteriors()
    {
        var seed = PrefabEditorFixtures.Session(PrefabFixtures.Village());
        await using var harness = await McpWire.StartAsync();
        await Call("map_open", new() { ["path"] = seed.DocumentPath });
        var live = harness.Services.GetRequiredService<MapEditSession>();
        string before = Bytes(live);
        var opened = await Call("prefab_open", new() { ["resourceId"] = "hut" });
        var hut = MapPrefabFile.Read(Encoding.UTF8.GetBytes(Text(opened)), "hut");
        var marker = hut.Children.OfType<MapPrefabMarkerChild>().Single();
        await Call("prefab_edit", new() { ["childJson"] = Json<MapPrefabChild>(marker with { Value = marker.Value with { Name = "Wire village" } }) });
        Assert.Equal(before, Bytes(live));
        var saved = await Call("prefab_save", new() { ["resourceId"] = "hut", ["relativeDirectory"] = "published" });
        using var json = JsonDocument.Parse(Text(saved));
        string version = json.RootElement.GetProperty("resource").GetProperty("id").GetString()!;
        Assert.StartsWith("prefab/hut/", version);
        Assert.All(PrefabEditorFixtures.Document(live).Prefabs!.Instances.Where(i => i.Id is "a" or "b"), i => Assert.Equal(version, i.ResourceId));
        await Call("prefab_undo", new()); Assert.Equal(before, Bytes(live));
        await Call("prefab_redo", new());
        string packed = Bytes(live);
        await Call("prefab_unpack", new() { ["instanceId"] = "a" });
        Assert.True(PrefabEditorFixtures.Document(live).Prefabs!.Instances.Single(i => i.Id == "a").Unpacked);
        await Call("prefab_undo", new()); Assert.Equal(packed, Bytes(live));
        var added = await Call("interior_volume_add", new() { ["domainJson"] = Json<MapTopologyRecord>(Hall()) });
        using var response = JsonDocument.Parse(Text(added));
        Assert.True(response.RootElement.GetProperty("mutation").GetProperty("worldChanged").GetBoolean());
        Assert.Contains(PrefabEditorFixtures.Document(live).Surfaces.AllRecords(), r => r.Id == "hall");
        Assert.Contains(PrefabEditorFixtures.Document(live).Surfaces.AllRecords(), r => r.Id == "hall-domain");
        await Call("prefab_undo", new()); Assert.Equal(packed, Bytes(live));

        async Task<CallToolResult> Call(string name, Dictionary<string, object?> args)
        {
            var result = await harness.Client.CallAsync(name, args);
            Assert.False(result.IsError == true, name + ": " + string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
            return result;
        }
    }

    [Fact]
    public async Task SecondInstance_InitialNameOverridePlacesAtomically()
    {
        var f = PrefabFixtures.Village();
        var direct = PrefabEditorFixtures.Session(f);
        var mutation = new MutationService(direct);
        var seed = PrefabEditorFixtures.Session(f);
        await using var harness = await McpWire.StartAsync();
        Assert.False((await harness.Client.CallAsync("map_open", new Dictionary<string, object?> { ["path"] = seed.DocumentPath })).IsError == true);
        var live = harness.Services.GetRequiredService<MapEditSession>();
        var place = Assert.Single(await harness.Client.ListAsync(), t => t.Name == "prefab_place");
        Assert.True(place.JsonSchema.GetProperty("properties").TryGetProperty("overridesJson", out _));
        byte[] Canopy(MapEditSession session) => session.WithNativeAssets(c => c.Assets.CapturedResources.Single(r => r.Reference.Id == "canopy").Bytes.ToArray());
        byte[] canopyBytes = Canopy(direct);
        var entry = direct.WithNativeAssets(c => c.Assets.GetPrefab("canopy")).Children.OfType<MapPrefabMarkerChild>().Single();
        MapPrefabOverride Named(string name) => new MapPrefabReplaceOverride(entry.Key, entry with { Value = entry.Value with { Name = name } });
        MapPlacePrefab Request(string id, float x, float z, params MapPrefabOverride[] overrides) =>
            new(id, "canopy", new(new(x, 5, z), 0.371f, 1.2f)) { InitialOverrides = overrides };

        var first = Request("canopy-a", 32.25f, -18.125f, Named("arrival-a"));
        Assert.True(mutation.PrefabApply(first).Mutation.WorldChanged);
        await Place(first, true);
        string afterFirst = Bytes(direct);
        Assert.Equal(afterFirst, Bytes(live));
        var history = direct.NativeHistoryState;
        var liveHistory = live.NativeHistoryState;

        // The same second request without its override, and one with duplicate initial keys, refuse without any trace.
        var second = Request("canopy-b", 28.25f, -26.125f);
        foreach (var (request, fault) in new[] { (second, "duplicate marker name 'Canopy entry'"),
            (second with { InitialOverrides = new[] { Named("arrival-b"), Named("arrival-c") } }, "Duplicate initial override") })
        {
            var refused = Assert.ThrowsAny<Exception>(() => mutation.PrefabApply(request));
            Assert.Contains(fault, refused.Message, StringComparison.Ordinal);
            Assert.Contains(fault, Text(await Place(request, false)), StringComparison.Ordinal);
            Assert.Equal(afterFirst, Bytes(direct)); Assert.Equal(afterFirst, Bytes(live));
            Assert.Equal(history, direct.NativeHistoryState); Assert.Equal(liveHistory, live.NativeHistoryState);
            Assert.DoesNotContain(PrefabEditorFixtures.Document(direct).Prefabs!.Instances, i => i.Id == "canopy-b");
        }

        var accepted = second with { InitialOverrides = new[] { Named("arrival-b") } };
        Assert.True(mutation.PrefabApply(accepted).Mutation.WorldChanged);
        await Place(accepted, true);
        string afterSecond = Bytes(direct);
        Assert.Equal(afterSecond, Bytes(live));
        var instances = PrefabEditorFixtures.Document(direct).Prefabs!.Instances.Where(i => i.Id is "canopy-a" or "canopy-b").ToArray();
        Assert.Equal(2, instances.Length);
        Assert.All(instances, i => Assert.False(i.Removed));
        var markers = MapEffectiveAnnotations.Markers(Effective(direct));
        Assert.Equal(new[] { ("canopy/entry", "Canopy entry"), ("prefab/canopy-a/Marker/entry", "arrival-a"), ("prefab/canopy-b/Marker/entry", "arrival-b") },
            markers.Where(m => m.InstanceId is "canopy" or "canopy-a" or "canopy-b").Select(m => (m.Id, m.Name)));
        Assert.Equal(markers.Count, markers.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count());
        var bindings = instances.Single(i => i.Id == "canopy-b").Bindings.Entries.ToArray();
        Assert.Equal(new MapPrefabOverride[] { Named("arrival-b") }.Select(o => Json(o)), instances.Single(i => i.Id == "canopy-b").Overrides.Select(o => Json(o)));

        Assert.NotNull(direct.UndoNative());
        Assert.False((await harness.Client.CallAsync("prefab_undo", new Dictionary<string, object?>())).IsError == true);
        // Undo keeps the second instance's reservations as a removed tombstone, and its marker leaves the world.
        Assert.Equal(Bytes(direct), Bytes(live));
        var tombstone = Assert.Single(PrefabEditorFixtures.Document(direct).Prefabs!.Instances, i => i.Id == "canopy-b");
        Assert.True(tombstone.Removed);
        Assert.Equal(bindings.Select(b => (b.Child, b.WorldId, b.NumericId)), tombstone.Bindings.Entries.Select(b => (b.Child, b.WorldId, b.NumericId)));
        Assert.DoesNotContain(MapEffectiveAnnotations.Markers(Effective(direct)), m => m.InstanceId == "canopy-b");
        Assert.NotNull(direct.RedoNative());
        Assert.False((await harness.Client.CallAsync("prefab_redo", new Dictionary<string, object?>())).IsError == true);
        Assert.Equal(afterSecond, Bytes(direct)); Assert.Equal(afterSecond, Bytes(live));
        Assert.Equal(bindings, PrefabEditorFixtures.Document(direct).Prefabs!.Instances.Single(i => i.Id == "canopy-b").Bindings.Entries.ToArray());
        Assert.Equal(canopyBytes, Canopy(direct));
        Assert.Equal(canopyBytes, Canopy(live));

        async Task<CallToolResult> Place(MapPlacePrefab request, bool succeeds)
        {
            var args = new Dictionary<string, object?>
            {
                ["instanceId"] = request.InstanceId,
                ["resourceId"] = request.ResourceId,
                ["x"] = request.Transform.Position.X,
                ["y"] = request.Transform.Position.Y,
                ["z"] = request.Transform.Position.Z,
                ["yaw"] = request.Transform.YawRadians,
                ["scale"] = request.Transform.Scale,
            };
            if (request.InitialOverrides.Count != 0) args["overridesJson"] = Json(request.InitialOverrides.ToArray());
            var result = await harness.Client.CallAsync("prefab_place", args);
            Assert.Equal(!succeeds, result.IsError == true);
            return result;
        }
    }

    static MapPrismDomain Hall() => new("hall-domain", 1, new("hall", new("exterior", 0, 0)), new("exterior", new("exterior", 0, 0)),
        "", "village-storey", PrefabFixtures.Domain(36, 24, 40, 28), 0, 3);
    static string Text(CallToolResult result) => Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
    static string Json<T>(T value) => Encoding.UTF8.GetString(MapPrefabJson.Bytes(value));
    static string Bytes(MapEditSession session) => PrefabEditorFixtures.State(session).DocumentBytes;
    static MapEffectiveResources Effective(MapEditSession session) => session.WithNativeAssets(c => MapPrefabResolver.Expand(c.Document, c.Assets));
    static string BytesAfterUndo(MapEditSession direct, MapEditSession wire, PrefabTools tools)
    {
        Assert.NotNull(direct.UndoNative()); Assert.NotNull(tools.PrefabUndo());
        Assert.Equal(Bytes(direct), Bytes(wire)); return Bytes(direct);
    }
}
