using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEdit.Tools;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapEditor;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

/// <summary>map_open with caller marker options and map_summary markers match the service, refusals included.</summary>
[Collection(McpWireCollection.Name)]
public sealed class MarkerConsumerWireTests
{
    const string OptionsJson =
        """{"roles":{"playerStartRole":"start-role","spawnRole":"spawn-role","landmarkRole":"landmark-role"},"legacySpawnSurfaceId":"adoption-floor"}""";

    [Fact]
    public async Task MarkerSummary_WireMatchesService()
    {
        string root = MarkerConsumerFixtures.TempRoot();
        try
        {
            MarkerConsumerFixtures.WindowedNative(root);
            var direct = new MapEditSession();
            direct.Open(root, null, MapPrefabJson.ReadMarkerOptions(OptionsJson));
            await using var harness = await McpWire.StartAsync();
            var live = harness.Services.GetRequiredService<MapEditSession>();
            JsonNode opened = Node(await Call(harness, "map_open", new() { ["path"] = root, ["markerOptionsJson"] = OptionsJson }));
            JsonNode wire = Node(await Call(harness, "map_summary", new()));

            MapSummary service = direct.Summary();
            JsonNode? expected = JsonSerializer.SerializeToNode(service.Markers, McpJsonUtilities.DefaultOptions);
            Assert.True(JsonNode.DeepEquals(expected, wire["markers"]), wire["markers"]?.ToJsonString());
            Assert.True(JsonNode.DeepEquals(expected, opened["summary"]!["markers"]));
            Assert.Equal(new[] { "start-a", "start-disabled", "start-tie" }, wire["markers"]!["playerStartIds"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal(new[] { "start-a", "start-tie" }, wire["markers"]!["enabledPlayerStartIds"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal(service.Markers!.PlayerStartIds.Except(service.Markers.EnabledPlayerStartIds), new[] { "start-disabled" });
            Assert.Equal(direct.MarkerOptions, live.MarkerOptions);

            // A windowed native root keeps the released refusal on both paths, and the live session keeps its document.
            string stateBefore = MarkerEditorFixtures.State(live);
            // Radius zero keeps the window from reaching every occupied tile, so the load stays partial.
            (direct.WholeWorldTileLimit, direct.EditorWindowRadius) = (1, 0);
            (live.WholeWorldTileLimit, live.EditorWindowRadius) = (1, 0);
            var refused = Assert.Throws<MapDocumentException>(() => direct.Open(root, null, MapPrefabJson.ReadMarkerOptions(OptionsJson)));
            CallToolResult windowed = await harness.Client.CallAsync("map_open", new Dictionary<string, object?> { ["path"] = root, ["markerOptionsJson"] = OptionsJson });
            Assert.True(windowed.IsError == true);
            Assert.Contains("Windowed native editing is not supported yet", refused.Message, StringComparison.Ordinal);
            Assert.Contains(refused.Message, Text(windowed), StringComparison.Ordinal);
            Assert.Equal(stateBefore, MarkerEditorFixtures.State(live));

            // Closed options refuse unknown and null members before anything opens.
            CallToolResult foreign = await harness.Client.CallAsync("map_open", new Dictionary<string, object?>
            { ["path"] = root, ["markerOptionsJson"] = """{"legacySpawnSurfaceId":"adoption-floor","spawnSurface":"other"}""" });
            Assert.True(foreign.IsError == true);
            Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarkerOptions("""{"legacySpawnSurfaceId":null}"""));
            Assert.Throws<MapDocumentException>(() => MapPrefabJson.ReadMarkerOptions("""{"legacySpawnSurfaceId":"a","legacySpawnSurfaceId":"b"}"""));
            Assert.Equal(stateBefore, MarkerEditorFixtures.State(live));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NativePlacementsInRect_WireSupportIsLossless()
    {
        string root = MarkerConsumerFixtures.TempRoot();
        try
        {
            MarkerConsumerFixtures.WindowedNative(root);
            var direct = new MapEditSession();
            direct.Open(root, null, MapPrefabJson.ReadMarkerOptions(OptionsJson));
            PlacementsInRectResult service = new QueryService(direct).PlacementsInRect(-2, -2, 2, 2);
            await using var harness = await McpWire.StartAsync();
            await Call(harness, "map_open", new() { ["path"] = root, ["markerOptionsJson"] = OptionsJson });
            JsonNode wire = Node(await Call(harness, "placements_in_rect", new() { ["minX"] = -2f, ["minZ"] = -2f, ["maxX"] = 2f, ["maxZ"] = 2f }));

            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(service, McpBootstrap.QueryToolOptions), wire), wire.ToJsonString());
            foreach (var (expected, node) in new[]
            {
                (Assert.Single(service.Spawns).Support!, wire["spawns"]![0]!["support"]!),
                (Assert.Single(service.PlayerSpawns).Support!, wire["playerSpawns"]![0]!["support"]!),
            })
            {
                // Every member the service exposes crosses the wire with its value, the normal included.
                Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected.Status, McpBootstrap.QueryToolOptions), node["status"]));
                Assert.Equal(MapSupportStatus.Supported, expected.Status);
                Assert.Equal(expected.WorldY, node["worldY"]!.GetValue<float>());
                Assert.Equal(expected.Face!.Value.OwnerId, node["face"]!["ownerId"]!.GetValue<string>());
                Assert.Equal(expected.Face.Value.Primitive, node["face"]!["primitive"]!.GetValue<int>());
                Vector3 normal = expected.Normal!.Value;
                Assert.Equal((normal.X, normal.Y, normal.Z),
                    (node["normal"]!["x"]!.GetValue<float>(), node["normal"]!["y"]!.GetValue<float>(), node["normal"]!["z"]!.GetValue<float>()));
                Assert.Equal(expected.IsCaptureSupport, node["isCaptureSupport"]!.GetValue<bool>());
                Assert.Equal(expected.Witness.SnapshotId, node["witness"]!["snapshotId"]!.GetValue<string>());
                Assert.Equal(expected.Witness.ScopedDigest, node["witness"]!["scopedDigest"]!.GetValue<string>());
                Assert.Equal(expected.Witness.Complete, node["witness"]!["complete"]!.GetValue<bool>());
                Assert.Equal(expected.Witness.QueryPolicyVersion, node["witness"]!["queryPolicyVersion"]!.GetValue<int>());
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static async Task<CallToolResult> Call(McpAdapterTests.McpHarness harness, string name, Dictionary<string, object?> args)
    {
        CallToolResult result = await harness.Client.CallAsync(name, args);
        Assert.False(result.IsError == true, name + ": " + Text(result));
        return result;
    }

    static JsonNode Node(CallToolResult result) => JsonNode.Parse(Text(result))!;
    static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
