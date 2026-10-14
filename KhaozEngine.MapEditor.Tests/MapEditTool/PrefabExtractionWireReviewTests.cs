using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

[Collection(McpWireCollection.Name)]
public sealed class PrefabExtractionWireReviewTests
{
    [Fact]
    public async Task ExtractRoomAndWater_WireCarriesParentAndFarOriginThroughEditExportSaveAndReopen()
    {
        var f = PrefabFixtures.Transformed(0.371f, 1.2f, new(30000.25f, -450, -29999.75f));
        var stored = PrefabEditorFixtures.Session(f);
        await using var harness = await McpWire.StartAsync();
        async Task<CallToolResult> Call(string name, Dictionary<string, object?> args)
        {
            var result = await harness.Client.CallAsync(name, args);
            Assert.NotEqual(true, result.IsError);
            return result;
        }
        static string Text(CallToolResult result) => Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        await Call("map_open", new() { ["path"] = stored.DocumentPath });
        var live = harness.Services.GetRequiredService<MapEditSession>();
        var before = PrefabEditorFixtures.State(live);
        var selected = PrefabAuthoringReviewTests.Selection(f.Document);
        var expected = MapPrefabExtraction.Extract(f.Document, f.Assets, selected, PrefabEditorFixtures.Far, "hut");
        var extracted = MapPrefabFile.Read(Encoding.UTF8.GetBytes(Text(await Call("prefab_extract", new()
        {
            ["selectedJson"] = Encoding.UTF8.GetString(MapPrefabJson.Bytes(selected)),
            ["x"] = 30000.25f,
            ["y"] = -450f,
            ["z"] = -29999.75f,
            ["yaw"] = 0.371f,
            ["scale"] = 1.2f,
            ["assetId"] = "hut"
        }))), "hut");
        Assert.Equal(MapPrefabFile.Write(expected), MapPrefabFile.Write(extracted));
        var marker = extracted.Children.OfType<MapPrefabMarkerChild>().Single();
        var exported = MapPrefabFile.Read(Encoding.UTF8.GetBytes(Text(await Call("prefab_edit", new()
        { ["childJson"] = Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapPrefabChild>(marker with { Value = marker.Value with { Name = "Wire extracted" } })) }))), "hut");
        Assert.Equal(0.5f, exported.Children.OfType<MapPrefabWaterChild>().Single().Value.SurfaceY);
        Assert.Equal(before, PrefabEditorFixtures.State(live));
        using var saved = JsonDocument.Parse(Text(await Call("prefab_save", new() { ["resourceId"] = "hut", ["relativeDirectory"] = "published" })));
        string version = saved.RootElement.GetProperty("resource").GetProperty("id").GetString()!;
        live.Save();
        await Call("map_open", new() { ["path"] = live.DocumentPath });
        var reopened = MapPrefabFile.Read(Encoding.UTF8.GetBytes(Text(await Call("prefab_open", new() { ["resourceId"] = version }))), "hut");
        Assert.Equal(MapPrefabFile.Write(exported), MapPrefabFile.Write(reopened));
    }
}
