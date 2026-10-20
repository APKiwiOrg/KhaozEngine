using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Markers;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

/// <summary>The GUI command, the service and the MCP verbs publish identical marker documents, effects and refusals.</summary>
[Collection(McpWireCollection.Name)]
public sealed class MarkerWireTests
{
    [Fact]
    public async Task GuiServiceAndWire_HaveIdenticalMarkerDocumentsEffectsAndRejection()
    {
        MarkerFixture fixture = MarkerEditorFixtures.TwoPoints();
        MapDocument gui = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        var history = PrefabEditorFixtures.History(gui, fixture.Assets);
        MapEditSession direct = MarkerEditorFixtures.Session(fixture);
        var mutation = new MutationService(direct);
        MapEditSession seed = MarkerEditorFixtures.Session(fixture);
        await using var harness = await McpWire.StartAsync();
        await Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });
        var live = harness.Services.GetRequiredService<MapEditSession>();
        AssertSame(gui, history, fixture.Assets, direct, live);

        MapLocalMarkerDoc fresh = MapMarkerHeights.Apply(new(1, "n", "n-name", new(1.5f, 0, -2.5f), 0.25f, "guard-role", true, new[] { "new" }), new(4.5f, null));
        MapLocalMarkerDoc moved = fresh with { Name = "n-renamed", Position = fresh.Position with { X = 2.5f } };
        var steps = new MapMarkerEdit[]
        {
            new MapSetMarker(fresh),
            new MapSetMarker(fresh with { Name = "n-renamed" }),
            new MapSetMarker(moved),
            new MapSetMarker(MarkerFixtures.BasicMarker() with { Enabled = false }),
            new MapRemoveMarker("other"),
        };
        var effects = new List<MapNativeEditEffects>();
        foreach (MapMarkerEdit edit in steps)
        {
            history.Execute(gui, new MarkerEditCommand(edit));
            MapPrefabMutationResult service = mutation.MarkerApply(edit);
            JsonNode wire = Node(await Call(harness, edit is MapRemoveMarker ? "marker_remove" : "marker_set", Args(edit)));
            AssertEffects(history.LastNativeEffects!, service.Effects, wire["effects"]);
            effects.Add(service.Effects);
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(service.Mutation, MapPrefabJson.ToolOptions), wire["mutation"]));
            AssertSame(gui, history, fixture.Assets, direct, live);
        }
        // The metadata rename carries only its row, and the spatial steps carry Residency alone.
        Assert.Equal(MapNativeInvalidation.None, effects[1].Invalidates);
        Assert.Equal(new MapMarkerNameChange("n", "n-name", "n-renamed"), Assert.Single(effects[1].MarkerNameChanges));
        Assert.All(new[] { effects[0], effects[2], effects[3], effects[4] }, e => Assert.Equal(MapNativeInvalidation.Residency, e.Invalidates));
        Assert.Equal(new MapMarkerNameChange("other", "other-name", null), Assert.Single(effects[4].MarkerNameChanges));

        Assert.True(history.Undo(gui));
        MapNativeEditEffects undo = direct.UndoNative()!;
        AssertEffects(history.LastNativeEffects!, undo, Node(await Call(harness, "prefab_undo", new())));
        AssertSame(gui, history, fixture.Assets, direct, live);
        Assert.True(history.Redo(gui));
        MapNativeEditEffects redo = direct.RedoNative()!;
        AssertEffects(history.LastNativeEffects!, redo, Node(await Call(harness, "prefab_redo", new())));
        AssertSame(gui, history, fixture.Assets, direct, live);

        // A duplicate name refuses identically and writes nothing anywhere.
        var rejected = new MapSetMarker(moved with { Name = "arrival" });
        string guiBefore = MapDocumentFile.SaveText(gui);
        string directBefore = MarkerEditorFixtures.State(direct), liveBefore = MarkerEditorFixtures.State(live);
        var a = Assert.Throws<MapDocumentException>(() => history.Execute(gui, new MarkerEditCommand(rejected)));
        var b = Assert.Throws<MapDocumentException>(() => mutation.MarkerApply(rejected));
        CallToolResult c = await harness.Client.CallAsync("marker_set", Args(rejected));
        Assert.True(c.IsError == true);
        Assert.Contains("duplicate marker name 'arrival'", b.Message, StringComparison.Ordinal);
        Assert.Equal(a.Message, b.Message);
        Assert.Contains(b.Message, Text(c), StringComparison.Ordinal);
        Assert.Equal(guiBefore, MapDocumentFile.SaveText(gui));
        Assert.Equal(directBefore, MarkerEditorFixtures.State(direct));
        Assert.Equal(liveBefore, MarkerEditorFixtures.State(live));
        AssertSame(gui, history, fixture.Assets, direct, live);
    }

    /// <summary>Review Focus 2 continued through the service and the wire. A world-terms metadata set and a move of the
    /// transferred Space-bound marker publish the same document on every path, keep the stored frame-local binding
    /// bytes, height carrier and yaw, and change only the moved point.</summary>
    [Fact]
    public async Task TransferredSpaceBoundMarker_WireSetAndMoveMatchGui()
    {
        const string id = "a/space-marker";
        MarkerFixture packed = MarkerFixtures.TransferredSpace();
        MarkerFixture fixture = packed with { Document = MapPrefabEdits.Prepare(packed.Document, packed.Assets, new MapUnpackPrefab("a")).After };
        MapDocument gui = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        var history = PrefabEditorFixtures.History(gui, fixture.Assets);
        MapEditSession direct = MarkerEditorFixtures.Session(fixture);
        var mutation = new MutationService(direct);
        MapEditSession seed = MarkerEditorFixtures.Session(fixture);
        await using var harness = await McpWire.StartAsync();
        await Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });
        var live = harness.Services.GetRequiredService<MapEditSession>();
        AssertSame(gui, history, fixture.Assets, direct, live);
        MapLocalMarkerDoc local = Stored(live, id);

        MapLocalMarkerDoc world = MarkerEditorFixtures.WorldEntry(gui, fixture.Assets, id) with { Name = "space-renamed" };
        var steps = new MapMarkerEdit[]
        {
            new MapSetMarker(world),
            new MapSetMarker(world with { Position = world.Position with { X = world.Position.X + 0.5f } }),
        };
        for (int step = 0; step < steps.Length; step++)
        {
            history.Execute(gui, new MarkerEditCommand(steps[step]));
            MapPrefabMutationResult service = mutation.MarkerApply(steps[step]);
            JsonNode wire = Node(await Call(harness, "marker_set", Args(steps[step])));
            AssertEffects(history.LastNativeEffects!, service.Effects, wire["effects"]);
            AssertSame(gui, history, fixture.Assets, direct, live);
            MapLocalMarkerDoc stored = Stored(live, id);
            Assert.Equal(MapPrefabJson.Bytes(local.SupportBinding), MapPrefabJson.Bytes(stored.SupportBinding));
            Assert.Equal(0u, BitConverter.SingleToUInt32Bits(stored.Position.Y));
            Assert.Equal(BitConverter.SingleToUInt32Bits(local.YawRadians), BitConverter.SingleToUInt32Bits(stored.YawRadians));
            Assert.Equal(step == 0, BitConverter.SingleToUInt32Bits(local.Position.X) == BitConverter.SingleToUInt32Bits(stored.Position.X));
            if (step == 0) Assert.Equal(BitConverter.SingleToUInt32Bits(local.Position.Z), BitConverter.SingleToUInt32Bits(stored.Position.Z));
        }
    }

    static MapLocalMarkerDoc Stored(MapEditSession session, string id) =>
        PrefabEditorFixtures.Document(session).BuildingIntent!.Markers.Single(m => m.Id == id);

    [Fact]
    public async Task MarkerListRect_WireMatchesService()
    {
        MarkerFixture fixture = MarkerFixtures.RectPoints();
        MapEditSession direct = MarkerEditorFixtures.Session(fixture);
        var query = new QueryService(direct);
        MapEditSession seed = MarkerEditorFixtures.Session(fixture);
        await using var harness = await McpWire.StartAsync();
        await Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });

        IReadOnlyList<MapEffectiveMarker> service = query.MarkerList(null, null, null, false, new MapMarkerRect(-1, -1, 0, 0));
        JsonNode wire = Node(await Call(harness, "marker_list", new() { ["minX"] = -1.0, ["minZ"] = -1.0, ["maxX"] = 0.0, ["maxZ"] = 0.0 }));
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(service, MapPrefabJson.ToolOptions), wire));

        // An independent half-open predicate over every marker: minima included, maxima excluded on both axes.
        static bool Inside(MapEffectiveMarker m) => m.Position.X.ToDouble() >= -1 && m.Position.X.ToDouble() < 0 &&
            m.Position.Z.ToDouble() >= -1 && m.Position.Z.ToDouble() < 0;
        IReadOnlyList<MapEffectiveMarker> all = query.MarkerList(null, null, null, false, null);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, all.Select(m => m.Id));
        Assert.Equal(new[] { "a", "e" }, service.Select(m => m.Id));
        Assert.Equal(all.Where(Inside).Select(m => m.Id), service.Select(m => m.Id));
        Assert.DoesNotContain(service, m => m.Id is "b" or "c" or "d");

        CallToolResult partial = await harness.Client.CallAsync("marker_list", new Dictionary<string, object?> { ["minX"] = -1.0 });
        Assert.True(partial.IsError == true);
        Assert.Contains("minX, minZ, maxX and maxZ", Text(partial), StringComparison.Ordinal);
    }

    static void AssertSame(MapDocument gui, EditorHistory history, MapAssetClosure assets, MapEditSession direct, MapEditSession live)
    {
        string document = MapDocumentFile.SaveText(gui);
        Assert.Equal(document, MarkerEditorFixtures.Bytes(direct));
        Assert.Equal(document, MarkerEditorFixtures.Bytes(live));
        byte[] markers = MapPrefabJson.Bytes(MapEffectiveAnnotations.Markers(MapPrefabResolver.Expand(gui, assets)).ToArray());
        Assert.Equal(markers, MapPrefabJson.Bytes(MarkerEditorFixtures.Markers(direct)));
        Assert.Equal(markers, MapPrefabJson.Bytes(MarkerEditorFixtures.Markers(live)));
        string identity = MapNativeResolution.Resolve(gui, assets, NativeDocumentService.SessionOptionsV2).AuthoredHash;
        Assert.Equal(identity, PrefabEditorFixtures.State(direct).Identity);
        Assert.Equal(identity, PrefabEditorFixtures.State(live).Identity);
        Assert.Equal((history.UndoLabel, history.RedoLabel), direct.NativeHistoryLabels);
        Assert.Equal((history.UndoLabel, history.RedoLabel), live.NativeHistoryLabels);
    }

    static void AssertEffects(MapNativeEditEffects gui, MapNativeEditEffects service, JsonNode? wire)
    {
        Assert.Equal(gui.Describe(), service.Describe());
        Assert.Equal(gui.MarkerNameChanges, service.MarkerNameChanges);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(service, MapPrefabJson.ToolOptions), wire));
    }

    static Dictionary<string, object?> Args(MapMarkerEdit edit) => edit switch
    {
        MapSetMarker set => new() { ["markerJson"] = Encoding.UTF8.GetString(MapPrefabJson.Bytes(set.Marker)) },
        MapRemoveMarker remove => new() { ["id"] = remove.Id },
        _ => throw new ArgumentException("Unknown marker edit."),
    };

    static async Task<CallToolResult> Call(McpAdapterTests.McpHarness harness, string name, Dictionary<string, object?> args)
    {
        CallToolResult result = await harness.Client.CallAsync(name, args);
        Assert.False(result.IsError == true, name + ": " + Text(result));
        return result;
    }

    static JsonNode Node(CallToolResult result) => JsonNode.Parse(Text(result))!;
    static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
