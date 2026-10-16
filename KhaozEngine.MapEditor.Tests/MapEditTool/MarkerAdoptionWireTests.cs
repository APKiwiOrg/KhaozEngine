using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Markers;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

[Collection(McpWireCollection.Name)]
public sealed class MarkerAdoptionWireTests
{
    const string ChoicesJson = """
        [{"kind":"Spawn","id":"old","markerId":"actor-new","name":"actor-home","role":"actor-role","height":{"explicitY":12.25}},
         {"kind":"PlayerSpawn","id":"old","markerId":"start-new","name":"player-home","role":"start-role","height":{"supportBinding":{"kind":"Surface","surfaceId":"adoption-floor"}}}]
        """;

    [Fact]
    public async Task SpawnsAdopt_GuiServiceAndWireShareOneCommandAndHistory()
    {
        var fixture = MarkerFixtures.LegacyLists();
        var gui = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        var history = PrefabEditorFixtures.History(gui, fixture.Assets);
        var service = MarkerEditorFixtures.Session(fixture);
        var seed = MarkerEditorFixtures.Session(fixture);
        await using var harness = await McpWire.StartAsync();
        await Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });
        var live = harness.Services.GetRequiredService<MapEditSession>();
        history.Execute(gui, new AdoptSpawnsCommand(MarkerFixtures.Choices()));
        var result = new MutationService(service).SpawnsAdopt(MarkerFixtures.Choices());
        var wire = JsonNode.Parse(Text(await Call(harness, "spawns_adopt", new() { ["choicesJson"] = ChoicesJson })))!;
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(result, MapPrefabJson.ToolOptions), wire));
        Assert.Equal(history.LastNativeEffects!.Describe(), result.Effects.Describe());
        AssertSame(gui, service, live);
        Assert.True(history.Undo(gui));
        Assert.NotNull(service.UndoNative());
        await Call(harness, "prefab_undo", new());
        AssertSame(gui, service, live);
        Assert.True(history.Redo(gui));
        Assert.NotNull(service.RedoNative());
        await Call(harness, "prefab_redo", new());
        AssertSame(gui, service, live);
        Assert.Equal((1, 1, 3L), live.NativeHistoryState);
    }

    [Theory]
    [InlineData("unknown-row")]
    [InlineData("duplicate-field")]
    [InlineData("duplicate-source")]
    [InlineData("unknown-kind")]
    [InlineData("unknown-height")]
    [InlineData("both-height")]
    [InlineData("neither-height")]
    [InlineData("null-height")]
    [InlineData("binding-null")]
    [InlineData("binding-foreign")]
    public async Task ClosedChoices_RefuseWithoutStateAdvance(string variant)
    {
        string json = variant switch
        {
            "unknown-row" => ChoicesJson.Replace("\"id\":\"old\"", "\"extra\":1,\"id\":\"old\"", StringComparison.Ordinal),
            "duplicate-field" => ChoicesJson.Replace("\"id\":\"old\"", "\"id\":\"old\",\"id\":\"old\"", StringComparison.Ordinal),
            "duplicate-source" => ChoicesJson.Replace("\"PlayerSpawn\"", "\"Spawn\"", StringComparison.Ordinal),
            "unknown-kind" => ChoicesJson.Replace("\"PlayerSpawn\"", "\"playerSpawn\"", StringComparison.Ordinal),
            "unknown-height" => ChoicesJson.Replace("\"explicitY\":12.25", "\"explicitY\":12.25,\"extra\":0", StringComparison.Ordinal),
            "both-height" => ChoicesJson.Replace("\"explicitY\":12.25", "\"explicitY\":12.25,\"supportBinding\":{\"kind\":\"Surface\",\"surfaceId\":\"adoption-floor\"}", StringComparison.Ordinal),
            "neither-height" => ChoicesJson.Replace("\"explicitY\":12.25", "", StringComparison.Ordinal),
            "null-height" => ChoicesJson.Replace("\"explicitY\":12.25", "\"explicitY\":null", StringComparison.Ordinal),
            "binding-null" => ChoicesJson.Replace("\"surfaceId\":\"adoption-floor\"", "\"surfaceId\":null", StringComparison.Ordinal),
            _ => ChoicesJson.Replace("\"surfaceId\":\"adoption-floor\"", "\"surfaceId\":\"adoption-floor\",\"spaceId\":null", StringComparison.Ordinal),
        };
        var seed = MarkerEditorFixtures.Session(MarkerFixtures.LegacyLists());
        await using var harness = await McpWire.StartAsync();
        await Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });
        var session = harness.Services.GetRequiredService<MapEditSession>();
        string before = MarkerEditorFixtures.State(session);
        var refused = await harness.Client.CallAsync("spawns_adopt", new Dictionary<string, object?> { ["choicesJson"] = json });
        Assert.True(refused.IsError == true, Text(refused));
        Assert.Equal(before, MarkerEditorFixtures.State(session));
    }

    static void AssertSame(MapDocument gui, MapEditSession service, MapEditSession live)
    {
        string expected = MapDocumentFile.SaveText(gui);
        Assert.Equal(expected, MarkerEditorFixtures.Bytes(service));
        Assert.Equal(expected, MarkerEditorFixtures.Bytes(live));
        Assert.Equal(PrefabEditorFixtures.State(service).Identity, PrefabEditorFixtures.State(live).Identity);
        Assert.Equal(MapPrefabJson.Bytes(MarkerEditorFixtures.Markers(service)), MapPrefabJson.Bytes(MarkerEditorFixtures.Markers(live)));
    }

    static async Task<CallToolResult> Call(McpAdapterTests.McpHarness harness, string name, Dictionary<string, object?> args)
    {
        var result = await harness.Client.CallAsync(name, args);
        Assert.False(result.IsError == true, Text(result));
        return result;
    }
    static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
