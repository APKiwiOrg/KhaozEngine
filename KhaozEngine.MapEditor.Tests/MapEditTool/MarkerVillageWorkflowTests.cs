using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Markers;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

/// <summary>The invented marker village through the shared commands on one session and the real MCP verbs on another,
/// with identical documents, effects, refusals and history at every step, then save and reopen.</summary>
[Collection(McpWireCollection.Name)]
public sealed class MarkerVillageWorkflowTests
{
    [Fact]
    public async Task MarkerVillageWorkflow_SharedCommandsAndWireReload()
    {
        MarkerFixture fixture = MarkerFixtures.VillageWithLegacyLists();
        MapEditSession direct = MarkerEditorFixtures.Session(fixture);
        var mutation = new MutationService(direct);
        MapEditSession seed = MarkerEditorFixtures.Session(fixture);
        await using var harness = await McpWire.StartAsync();
        await Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });
        MapEditSession live = harness.Services.GetRequiredService<MapEditSession>();
        var liveMutation = new MutationService(live);
        Same(direct, live);
        Assert.Equal(MarkerFixtures.VillageIds, Markers(direct).Select(m => m.Id));

        // Creation of a standalone Explicit marker, with Residency only.
        MapLocalMarkerDoc lookout = MapMarkerHeights.Apply(new(1, "lookout", "lookout", new Vector3(-4.75f, 0, -14.5f), 0.371f, "label-role", true,
            new[] { "high", "first" }), new(-447.5f, null));
        var created = await Both(mutation.MarkerApply(new MapSetMarker(lookout)), "marker_set", MarkerArgs(lookout));
        Assert.Equal(MapNativeInvalidation.Residency, created.Effects.Invalidates);
        Assert.Equal(MarkerFixtures.Exact(-4.75f, -447.5f, -14.5f), Marker(direct, "lookout").Position);

        // A duplicate name refuses identically and writes nothing on either session.
        MapLocalMarkerDoc clash = lookout with { Id = "lookout-2", Name = "start" };
        string directBefore = MarkerEditorFixtures.State(direct), liveBefore = MarkerEditorFixtures.State(live);
        var refused = Assert.Throws<MapDocumentException>(() => mutation.MarkerApply(new MapSetMarker(clash)));
        Assert.Contains("duplicate marker name 'start'", refused.Message, StringComparison.Ordinal);
        CallToolResult wireRefusal = await harness.Client.CallAsync("marker_set", MarkerArgs(clash));
        Assert.True(wireRefusal.IsError == true);
        Assert.Contains(refused.Message, Text(wireRefusal), StringComparison.Ordinal);
        Assert.Equal(directBefore, MarkerEditorFixtures.State(direct));
        Assert.Equal(liveBefore, MarkerEditorFixtures.State(live));

        // A local replace override renames and raises the payload-1 child, which stays payload 1.
        MapPrefabMarkerChild entry = direct.WithNativeAssets(c => c.Assets.GetPrefab("hut")).Children.OfType<MapPrefabMarkerChild>()
            .Single(c => c.Key.Key == "entry");
        var relabel = new MapPrefabReplaceOverride(entry.Key, entry with
        { Value = new(1, "entry", "roof-sign", new Vector3(2, 5, -2), 0.5f, "label-role", true, new[] { "roof", "sign" }) });
        await Both(mutation.PrefabApply(new MapSetPrefabOverride("a", relabel)), "prefab_override",
            new() { ["instanceId"] = "a", ["overrideJson"] = Json<MapPrefabOverride>(relabel) });
        Assert.Equal(("roof-sign", 1), (Marker(direct, MarkerFixtures.RoofLabelId).Name, Marker(direct, MarkerFixtures.RoofLabelId).PayloadVersion));

        // Whole-building moves at yaw 0.371 and scales 0.8 and 1.2. Prefab moves have no wire verb, so both sessions use
        // the shared service, as the released prefab workflow does. Local markers follow their frame once.
        foreach (var frame in new[] { new MapTransform(new(-11.75f, -450, 6.625f), 0.371f, 0.8f), new MapTransform(new(-12.75f, -450, 7.625f), 0.371f, 1.2f) })
        {
            MapPrefabMutationResult a = mutation.PrefabApply(new MapMovePrefab("a", frame)), b = liveMutation.PrefabApply(new MapMovePrefab("a", frame));
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(a, MapPrefabJson.ToolOptions), JsonSerializer.SerializeToNode(b, MapPrefabJson.ToolOptions)));
            Same(direct, live);
            Assert.Equal(MarkerFixtures.FramePoint(frame, 2, 5, -2), Marker(direct, MarkerFixtures.RoofLabelId).Position);
            Assert.Equal(MarkerFixtures.FramePoint(frame, 3, 0, -1) with { Y = MapExactValue.FromSingle(-450f) },
                Marker(direct, MarkerFixtures.DisabledLocalId).Position);
            Assert.Equal(-450f, Marker(direct, MarkerFixtures.DisabledLocalId).HeightProvenance!.WorldY);
        }

        // Unpack transfers both local markers to framed standalone homes with unchanged effective values.
        MapEffectiveMarker[] packed = Markers(direct);
        await Both(mutation.PrefabApply(new MapUnpackPrefab("a")), "prefab_unpack", new() { ["instanceId"] = "a" });
        MapEffectiveMarker[] unpacked = Markers(direct);
        Assert.Equal(packed.Select(m => m.Id), unpacked.Select(m => m.Id));
        foreach (var (before, after) in packed.Zip(unpacked))
        {
            Assert.Equal((before.Name, before.Position, before.Role, before.Enabled, before.ArchetypeId, before.HeightProvenance),
                (after.Name, after.Position, after.Role, after.Enabled, after.ArchetypeId, after.HeightProvenance));
            Assert.Equal(before.Tags, after.Tags);
            Assert.True(MarkerFixtures.SameYaw(before.YawRadians, after.YawRadians), before.Id);
        }
        Assert.Contains(PrefabEditorFixtures.Document(direct).NativeResourceTransforms!, t => t.Kind == MapPrefabChildKind.Marker && t.OwnerId == MarkerFixtures.DisabledLocalId);

        // Marker edits in world terms: a metadata rename of the transferred label and a spatial move of start.
        MapLocalMarkerDoc label = WorldEntry(direct, MarkerFixtures.RoofLabelId) with { Name = "roof-board", Tags = new[] { "roof", "board", "roof" } };
        var metadata = await Both(mutation.MarkerApply(new MapSetMarker(label)), "marker_set", MarkerArgs(label));
        Assert.Equal(MapNativeInvalidation.None, metadata.Effects.Invalidates);
        Assert.Equal(new MapMarkerNameChange(MarkerFixtures.RoofLabelId, "roof-sign", "roof-board"), Assert.Single(metadata.Effects.MarkerNameChanges));
        Assert.Equal(unpacked.Single(m => m.Id == MarkerFixtures.RoofLabelId).Position, Marker(direct, MarkerFixtures.RoofLabelId).Position);
        MapLocalMarkerDoc start = WorldEntry(direct, "start");
        MapLocalMarkerDoc movedStart = start with { Position = start.Position with { X = -17.5f } };
        var spatial = await Both(mutation.MarkerApply(new MapSetMarker(movedStart)), "marker_set", MarkerArgs(movedStart));
        Assert.Equal(MapNativeInvalidation.Residency, spatial.Effects.Invalidates);
        Assert.Equal(MarkerFixtures.Exact(-17.5f, -452.4f, -9.25f), Marker(direct, "start").Position);

        // Explicit adoption clears both legacy lists, then undo restores them exactly and redo adopts again.
        string beforeAdoption = MarkerEditorFixtures.Bytes(direct);
        var adopted = await Both(mutation.SpawnsAdopt(MarkerFixtures.VillageChoices()), "spawns_adopt",
            new() { ["choicesJson"] = MarkerFixtures.VillageChoicesJson });
        Assert.True(adopted.Effects.Invalidates.HasFlag(MapNativeInvalidation.Residency));
        Assert.Equal(MapNativeInvalidation.None, adopted.Effects.Invalidates &
            (MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Unbounded));
        MapDocument afterAdoption = PrefabEditorFixtures.Document(direct);
        Assert.Empty(afterAdoption.Spawns);
        Assert.Empty(afterAdoption.PlayerSpawns);
        Assert.Equal(new[] { "invented-actor" }, Markers(direct).Where(m => m.Id == "adopted-actor").Select(m => m.ArchetypeId));
        string adoptedBytes = MarkerEditorFixtures.Bytes(direct);
        Assert.NotNull(direct.UndoNative());
        await Call(harness, "prefab_undo", new());
        Same(direct, live);
        Assert.Equal(beforeAdoption, MarkerEditorFixtures.Bytes(direct));
        Assert.Equal(new[] { "legacy-actor" }, PrefabEditorFixtures.Document(direct).Spawns.Select(s => s.Id));
        Assert.Equal(new[] { "legacy-start" }, PrefabEditorFixtures.Document(direct).PlayerSpawns.Select(s => s.Id));
        Assert.NotNull(direct.RedoNative());
        await Call(harness, "prefab_redo", new());
        Same(direct, live);
        Assert.Equal(adoptedBytes, MarkerEditorFixtures.Bytes(direct));

        // Save, then reopen both saved files through the C# session and the wire.
        direct.Save();
        await Call(harness, "map_save", new());
        var reopened = new MapEditSession();
        reopened.Open(direct.DocumentPath!);
        Assert.Equal(adoptedBytes, MarkerEditorFixtures.Bytes(reopened));
        await Call(harness, "map_open", new() { ["path"] = live.DocumentPath });
        MapEditSession wireReopened = harness.Services.GetRequiredService<MapEditSession>();
        Assert.Equal(adoptedBytes, MarkerEditorFixtures.Bytes(wireReopened));
        byte[] markers = MapPrefabJson.Bytes(Markers(direct));
        Assert.Equal(markers, MapPrefabJson.Bytes(Markers(reopened)));
        Assert.Equal(markers, MapPrefabJson.Bytes(Markers(wireReopened)));
        Assert.Equal(PrefabEditorFixtures.State(reopened).Identity, PrefabEditorFixtures.State(wireReopened).Identity);

        // The reopened village keeps unique names, the cave points and the half-open rectangle on both paths.
        MapEffectiveMarker[] final = Markers(reopened);
        Assert.Equal(new[] { "a/disabled-local", "a/entry", "actor", "adopted-actor", "adopted-start", "cave-bound", "cave-explicit", "lookout", "start" },
            final.Select(m => m.Id));
        Assert.Equal(final.Length, final.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(MarkerFixtures.Exact(2, -57.75f, -2), final.Single(m => m.Id == "cave-explicit").Position);
        Assert.Equal(MarkerFixtures.Exact(2.5f, -39.375f, -1.5f), final.Single(m => m.Id == "cave-bound").Position);
        // Start lies on both minima, adopted-start on the maximum X and adopted-actor on the maximum Z.
        Assert.Equal((-2.5, -7.5), (final.Single(m => m.Id == "adopted-start").Position.X.ToDouble(), final.Single(m => m.Id == "adopted-start").Position.Z.ToDouble()));
        Assert.Equal((-3.5, -6.5), (final.Single(m => m.Id == "adopted-actor").Position.X.ToDouble(), final.Single(m => m.Id == "adopted-actor").Position.Z.ToDouble()));
        IReadOnlyList<MapEffectiveMarker> rect = new QueryService(reopened).MarkerList(null, null, null, false, new MapMarkerRect(-17.5, -9.25, -2.5, -6.5));
        Assert.Equal(new[] { "start" }, rect.Select(m => m.Id));
        JsonNode wireRect = Node(await Call(harness, "marker_list", new() { ["minX"] = -17.5, ["minZ"] = -9.25, ["maxX"] = -2.5, ["maxZ"] = -6.5 }));
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(rect, MapPrefabJson.ToolOptions), wireRect));

        // Runs one shared command result against its wire verb and requires identical results and sessions.
        async Task<MapPrefabMutationResult> Both(MapPrefabMutationResult service, string verb, Dictionary<string, object?> args)
        {
            JsonNode wire = Node(await Call(harness, verb, args));
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(service, MapPrefabJson.ToolOptions), wire), verb + ": " + wire.ToJsonString());
            Same(direct, live);
            return service;
        }
    }

    static void Same(MapEditSession direct, MapEditSession live)
    {
        Assert.Equal(MarkerEditorFixtures.Bytes(direct), MarkerEditorFixtures.Bytes(live));
        Assert.Equal(MapPrefabJson.Bytes(Markers(direct)), MapPrefabJson.Bytes(Markers(live)));
        Assert.Equal(PrefabEditorFixtures.State(direct).Identity, PrefabEditorFixtures.State(live).Identity);
        Assert.Equal(direct.NativeHistoryLabels, live.NativeHistoryLabels);
    }

    static MapEffectiveMarker[] Markers(MapEditSession session) => MarkerEditorFixtures.Markers(session);

    static MapEffectiveMarker Marker(MapEditSession session, string id) => Markers(session).Single(m => m.Id == id);

    static MapLocalMarkerDoc WorldEntry(MapEditSession session, string id) =>
        session.WithNativeAssets(c => MarkerEditorFixtures.WorldEntry(c.Document, c.Assets, id));

    static Dictionary<string, object?> MarkerArgs(MapLocalMarkerDoc marker) =>
        new() { ["markerJson"] = Encoding.UTF8.GetString(MapPrefabJson.Bytes(marker)) };

    static string Json<T>(T value) => Encoding.UTF8.GetString(MapPrefabJson.Bytes(value));

    static async Task<CallToolResult> Call(McpAdapterTests.McpHarness harness, string name, Dictionary<string, object?> args)
    {
        CallToolResult result = await harness.Client.CallAsync(name, args);
        Assert.False(result.IsError == true, name + ": " + Text(result));
        return result;
    }

    static JsonNode Node(CallToolResult result) => JsonNode.Parse(Text(result))!;

    static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
