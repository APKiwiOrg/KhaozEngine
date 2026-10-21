using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

/// <summary>The five foliage verbs over real MCP calls: the exact inventory, detached discovery, parity with the direct
/// command, atomic refusal and dry run, and whole-world verification of region packs.</summary>
[Collection(McpWireCollection.Name)]
public sealed class FoliageToolTests
{
    const string Wire = FoliageFixtures.WireLayerId;

    [Fact]
    public async Task FiveFoliageVerbs_MakeExactInventory101()
    {
        await using var harness = await McpWire.StartAsync();
        IList<McpClientTool> listedTools = await harness.Client.ListAsync();
        Assert.Equal(101, listedTools.Count);
        Assert.Equal(101, listedTools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
        string[] foliage = listedTools.Select(t => t.Name).Where(n => n.StartsWith("foliage", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["foliage_density_set", "foliage_get", "foliage_layer_set", "foliage_paint", "foliage_remove"], foliage);
        var parameters = new Dictionary<string, string[]>
        {
            ["foliage_layer_set"] = ["layerJson", "expectedDifferentialDigest", "dryRun"],
            ["foliage_get"] = ["layerId", "x", "z", "width", "depth", "previewChunkJson"],
            ["foliage_density_set"] = ["layerId", "x", "z", "width", "depth", "density"],
            ["foliage_paint"] = ["layerId", "worldX", "worldZ", "radiusMetres", "target", "hardness"],
            ["foliage_remove"] = ["layerId"],
        };
        foreach (var (name, expected) in parameters)
        {
            JsonElement properties = listedTools.Single(t => t.Name == name).JsonSchema.GetProperty("properties");
            Assert.Equal(expected.Order(StringComparer.Ordinal), properties.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task FoliageGet_OmittedIdListsDetachedMetadataInAuthoringOrder()
    {
        FoliageFixture f = FoliageFixtures.ReachableWirePreview();
        MapFoliageLayer wide = FoliageStorageFixtures.Change(FoliageFixtures.WireLayer("wide-cover"), l => { l["width"] = 4096; l["depth"] = 4096; });
        f.Document.Foliage = new MapFoliageSet(1, [FoliageFixtures.WireLayer(Wire), FoliageFixtures.WireLayer("moss-cover"), wide]);
        MapEditSession seed = FoliageEditorFixtures.Session(f);
        try
        {
            await using var harness = await McpWire.StartAsync();
            await Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });
            var live = harness.Services.GetRequiredService<MapEditSession>();
            string before = FoliageEditorFixtures.State(live);

            // An omitted ID lists every layer's detached metadata in authoring order, never ordinal order, with no density.
            JsonElement listBody = Body(await Call(harness, "foliage_get", new()));
            Assert.Equal("list", listBody.GetProperty("mode").GetString());
            string[] authoringOrderIds = [Wire, "moss-cover", "wide-cover"];
            string[] listedIds = listBody.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("id").GetString()!).ToArray();
            Assert.Equal(authoringOrderIds, listedIds);
            Assert.NotEqual(authoringOrderIds.Order(StringComparer.Ordinal), listedIds);
            Assert.Equal(JsonValueKind.Null, listBody.GetProperty("layer").ValueKind);
            Assert.Equal(JsonValueKind.Null, listBody.GetProperty("preview").ValueKind);
            Assert.Equal(0, listBody.GetProperty("chunks").GetArrayLength());
            foreach (JsonElement layer in listBody.GetProperty("layers").EnumerateArray())
            {
                Assert.False(layer.TryGetProperty("chunks", out _));
                Assert.False(layer.TryGetProperty("density", out _));
                JsonElement key = Assert.Single(layer.GetProperty("chunkKeys").EnumerateArray());
                Assert.Equal((0, 0), (key.GetProperty("x").GetInt32(), key.GetProperty("z").GetInt32()));
                Assert.Equal("Surface", layer.GetProperty("support").GetProperty("kind").GetString());
                Assert.Equal(2, layer.GetProperty("support").EnumerateObject().Count());
                Assert.Equal("PositiveZ", layer.GetProperty("rowDirection").GetString());
            }

            // One layer by ID carries only its metadata until a rectangle or preview is requested.
            JsonElement named = Body(await Call(harness, "foliage_get", new() { ["layerId"] = "moss-cover" }));
            Assert.Equal("layer", named.GetProperty("mode").GetString());
            Assert.Equal(0, named.GetProperty("layers").GetArrayLength());
            Assert.Equal("moss-cover", named.GetProperty("layer").GetProperty("id").GetString());
            Assert.Equal(0, named.GetProperty("chunks").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, named.GetProperty("preview").ValueKind);
            JsonElement raster = Body(await Call(harness, "foliage_get", new() { ["layerId"] = "moss-cover", ["x"] = 1, ["z"] = 1, ["width"] = 1, ["depth"] = 1 }));
            Assert.Equal(FoliageFixtures.WireDensity(), Convert.FromBase64String(Assert.Single(raster.GetProperty("chunks").EnumerateArray()).GetProperty("density").GetString()!));
            Assert.Equal(JsonValueKind.Null, raster.GetProperty("preview").ValueKind);

            // The read limits are the raster edit limits: 64 chunks and 65,536 samples are read, one more of either refuses.
            JsonElement widest = Body(await Call(harness, "foliage_get", new() { ["layerId"] = "wide-cover", ["x"] = 0, ["z"] = 0, ["width"] = 256, ["depth"] = 256 }));
            Assert.Equal((0, 0), (widest.GetProperty("chunks")[0].GetProperty("x").GetInt32(), widest.GetProperty("chunks")[0].GetProperty("z").GetInt32()));
            Assert.Contains("65792 addresses", await Refuse(harness, "foliage_get",
                new() { ["layerId"] = "wide-cover", ["x"] = 0, ["z"] = 0, ["width"] = 257, ["depth"] = 256 }), StringComparison.Ordinal);
            Assert.Contains("65 chunks", await Refuse(harness, "foliage_get",
                new() { ["layerId"] = "wide-cover", ["x"] = 31, ["z"] = 0, ["width"] = 2018, ["depth"] = 1 }), StringComparison.Ordinal);

            // A raster or preview without a layer ID, a partial rectangle, an unknown layer and an out-of-raster rectangle refuse.
            foreach (var args in new Dictionary<string, object?>[]
            {
                new() { ["x"] = 0, ["z"] = 0, ["width"] = 1, ["depth"] = 1 },
                new() { ["previewChunkJson"] = "{\"x\":0,\"z\":0}" },
                new() { ["layerId"] = Wire, ["x"] = 0, ["z"] = 0 },
                new() { ["layerId"] = "absent-cover" },
                new() { ["layerId"] = Wire, ["x"] = 0, ["z"] = 0, ["width"] = 3, ["depth"] = 1 },
                new() { ["layerId"] = Wire, ["previewChunkJson"] = "{\"x\":0,\"z\":0,\"w\":1}" },
            })
                await Refuse(harness, "foliage_get", args);
            Assert.Equal(before, FoliageEditorFixtures.State(live));

            // The direct read is detached: its lists are copies and no chunk array escapes.
            var query = harness.Services.GetRequiredService<QueryService>();
            MapFoliageReadResult direct = query.FoliageGet();
            Assert.Equal(MapFoliageReadMode.List, direct.Mode);
            Assert.Equal(authoringOrderIds, direct.Layers.Select(l => l.Id));
            Assert.Null(direct.Layer);
            Assert.Empty(direct.Chunks);
            Assert.Null(direct.Preview);
            Assert.Throws<MapDocumentException>(() => query.FoliageGet(null, 0, 0, 1, 1));
            Assert.Throws<MapDocumentException>(() => query.FoliageGet(null, previewChunk: new MapFoliageChunkKey(0, 0)));
        }
        finally { FoliageEditorFixtures.Delete(seed); }
    }

    [Fact]
    public async Task FoliageWire_MatchesDirectCommandsBytesEffectsAndHistory()
    {
        FoliageFixture f = FoliageFixtures.ReachableWirePreview();
        var (gui, history) = FoliageEditorFixtures.Gui(f);
        MapEditSession seed = FoliageEditorFixtures.Session(f);
        try
        {
            await using var harness = await McpWire.StartAsync();
            await Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });
            var live = harness.Services.GetRequiredService<MapEditSession>();
            MapFoliageLayer moss = FoliageFixtures.WireLayer("moss-cover");
            var steps = new (MapFoliageEdit Edit, string Verb, Dictionary<string, object?> Args)[]
            {
                (new MapSetFoliageLayer(moss), "foliage_layer_set", new() { ["layerJson"] = LayerJson(moss) }),
                (new MapSetFoliageDensity(Wire, 0, 0, 2, 1, [9, 200]), "foliage_density_set",
                    new() { ["layerId"] = Wire, ["x"] = 0, ["z"] = 0, ["width"] = 2, ["depth"] = 1, ["density"] = new[] { 9, 200 } }),
                (new MapPaintFoliage(Wire, 0.5f, 0.5f, 1, 40, 0.5f), "foliage_paint",
                    new() { ["layerId"] = Wire, ["worldX"] = 0.5f, ["worldZ"] = 0.5f, ["radiusMetres"] = 1f, ["target"] = 40, ["hardness"] = 0.5f }),
                (new MapRemoveFoliage("moss-cover"), "foliage_remove", new() { ["layerId"] = "moss-cover" }),
            };
            // One preview context serves one accepted snapshot: preview, preview again, then every edit captures once more.
            var query = harness.Services.GetRequiredService<QueryService>();
            var chunk = new MapFoliageChunkKey(0, 0);
            Assert.Equal(1, Previews(query, chunk));
            Assert.Equal(0, Previews(query, chunk));
            Assert.True(live.HoldsFoliagePreview);
            var published = new List<string> { MapDocumentFile.SaveText(gui) };
            foreach (var (edit, verb, args) in steps)
            {
                long version = live.SnapshotVersion;
                history.Execute(gui, new FoliageEditCommand(edit));
                JsonElement result = Body(await Call(harness, verb, args));
                MapNativeEditEffects editorEffects = history.LastNativeEffects!;
                Assert.True(result.GetProperty("applied").GetBoolean());
                Assert.Equal("Complete", result.GetProperty("status").GetString());
                Assert.Equal(editorEffects.Describe(), result.GetProperty("effects").GetString());
                Assert.Equal(editorEffects.Describe(), live.LastNativeEffects!.Describe());
                Assert.True(editorEffects.Invalidates.HasFlag(MapNativeInvalidation.Foliage));
                Assert.True(result.GetProperty("dirty").GetBoolean());
                Assert.Equal(edit.Label, result.GetProperty("undoLabel").GetString());
                Assert.Equal(history.UndoLabel, result.GetProperty("undoLabel").GetString());
                Assert.Equal(JsonValueKind.Null, result.GetProperty("redoLabel").ValueKind);
                Assert.Equal(JsonValueKind.Null, result.GetProperty("differential").ValueKind);
                Assert.Equal(PrefabEditorFixtures.State(live).Identity, result.GetProperty("identity").GetString());
                Assert.Equal(MapDocumentFile.SaveText(gui), FoliageEditorFixtures.Bytes(live));
                published.Add(MapDocumentFile.SaveText(gui));

                // Adding or removing a layer with stored chunks publishes a whole-layer entry in the wire effects.
                using (JsonDocument effects = JsonDocument.Parse(result.GetProperty("effects").GetString()!))
                {
                    if (edit is MapSetFoliageLayer or MapRemoveFoliage)
                    {
                        JsonElement entry = Assert.Single(effects.RootElement.GetProperty("foliageLayers").EnumerateArray());
                        Assert.Equal("moss-cover", entry.GetProperty("layerId").GetString());
                        bool added = edit is MapSetFoliageLayer;
                        Assert.Equal(added ? JsonValueKind.Null : JsonValueKind.String, entry.GetProperty("before").ValueKind);
                        Assert.Equal(added ? JsonValueKind.String : JsonValueKind.Null, entry.GetProperty("after").ValueKind);
                    }
                    else Assert.False(effects.RootElement.TryGetProperty("foliageLayers", out _));
                }

                // The accepted edit advances the snapshot once and drops the cached context, so the next preview captures.
                Assert.Equal(version + 1, live.SnapshotVersion);
                Assert.False(live.HoldsFoliagePreview);
                Assert.Equal(1, Previews(query, chunk));
                Assert.Equal(0, Previews(query, chunk));
            }
            Assert.Equal(published.Count, published.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal((4, 4), (live.NativeHistoryState.Cursor, live.NativeHistoryState.Depth));

            // Undo and redo through the wire replay the same bytes and labels as the direct history.
            for (int i = steps.Length - 1; i >= 0; i--)
            {
                await Call(harness, "prefab_undo", new());
                Assert.True(history.Undo(gui));
                Assert.Equal(published[i], FoliageEditorFixtures.Bytes(live));
                Assert.Equal(published[i], MapDocumentFile.SaveText(gui));
                Assert.Equal((history.UndoLabel, history.RedoLabel), live.NativeHistoryLabels);
                Assert.Equal(1, Previews(query, chunk));
            }
            await Call(harness, "prefab_redo", new());
            Assert.True(history.Redo(gui));
            Assert.Equal(published[1], FoliageEditorFixtures.Bytes(live));
            Assert.Equal((history.UndoLabel, history.RedoLabel), live.NativeHistoryLabels);
            Assert.Equal(1, Previews(query, chunk));

            // Save rebinds a freshly verified closure and reload replaces the document. Each advances the snapshot and
            // drops the cached context at once.
            foreach (var (verb, args) in new (string, Dictionary<string, object?>)[]
            {
                ("map_save", new()),
                ("map_open", new() { ["path"] = seed.DocumentPath }),
            })
            {
                Assert.True(live.HoldsFoliagePreview);
                long version = live.SnapshotVersion;
                await Call(harness, verb, args);
                Assert.True(live.SnapshotVersion > version, verb);
                Assert.False(live.HoldsFoliagePreview, verb);
                Assert.Equal(1, Previews(query, chunk));
                Assert.Equal(0, Previews(query, chunk));
            }
        }
        finally { FoliageEditorFixtures.Delete(seed); }
    }

    [Fact]
    public async Task FoliageWire_RefusalAndDryRunChangeNothing()
    {
        FoliageFixture f = FoliageFixtures.ReachableWirePreview();
        MapEditSession seed = FoliageEditorFixtures.Session(f);
        try
        {
            await using var harness = await McpWire.StartAsync();
            await Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });
            var live = harness.Services.GetRequiredService<MapEditSession>();
            string beforeState = FoliageEditorFixtures.State(live);
            string identity = PrefabEditorFixtures.State(live).Identity;
            MapFoliageLayer layer = f.Document.Foliage!.Layers[0];
            MapFoliageLayer policy = FoliageStorageFixtures.Change(layer, l => l["policy"]!["normalBoundaryHeight"] = 0.25f);

            // A policy dry run returns the complete identity-bound differential the direct preview issues, and writes nothing.
            JsonElement dry = Body(await Call(harness, "foliage_layer_set", new() { ["layerJson"] = LayerJson(policy), ["dryRun"] = true }));
            MapFoliageDifferentialResult expected = MapFoliageEdits.PreviewPolicyChange(f.Document, f.Assets, new MapSetFoliageLayer(policy));
            Assert.Equal(MapFoliageStatus.Complete, expected.Status);
            Assert.False(dry.GetProperty("applied").GetBoolean());
            Assert.Equal("Complete", dry.GetProperty("status").GetString());
            Assert.Equal(identity, dry.GetProperty("identity").GetString());
            Assert.False(dry.GetProperty("dirty").GetBoolean());
            JsonElement differential = dry.GetProperty("differential");
            Assert.Equal(expected.Digest, differential.GetProperty("digest").GetString());
            Assert.Equal(expected.BeforeIdentity, differential.GetProperty("beforeIdentity").GetString());
            Assert.Equal(expected.ProposalDigest, differential.GetProperty("proposalDigest").GetString());
            Assert.Equal("Complete", differential.GetProperty("status").GetString());
            string refusedState = FoliageEditorFixtures.State(live);
            Assert.Equal(beforeState, refusedState);

            // A new layer needs no differential, so its dry run refuses before any comparison and changes nothing.
            Assert.Contains("needs no differential", await Refuse(harness, "foliage_layer_set",
                new() { ["layerJson"] = LayerJson(FoliageFixtures.WireLayer("moss-cover")), ["dryRun"] = true }), StringComparison.Ordinal);
            Assert.Equal(beforeState, FoliageEditorFixtures.State(live));

            // Every refusal leaves the session state, bytes, history and identity unchanged.
            foreach (var (verb, args) in new (string, Dictionary<string, object?>)[]
            {
                ("foliage_layer_set", new() { ["layerJson"] = LayerJson(policy) }),
                ("foliage_layer_set", new() { ["layerJson"] = LayerJson(policy), ["expectedDifferentialDigest"] = new string('0', 64) }),
                ("foliage_layer_set", new() { ["layerJson"] = LayerJson(layer).Replace("\"seed\":", "\"extra\":1,\"seed\":", StringComparison.Ordinal) }),
                ("foliage_density_set", new() { ["layerId"] = Wire, ["x"] = 0, ["z"] = 0, ["width"] = 2, ["depth"] = 1, ["density"] = new[] { 9, 256 } }),
                ("foliage_density_set", new() { ["layerId"] = Wire, ["x"] = 1, ["z"] = 0, ["width"] = 2, ["depth"] = 1, ["density"] = new[] { 9, 9 } }),
                ("foliage_paint", new() { ["layerId"] = Wire, ["worldX"] = 0.5f, ["worldZ"] = 0.5f, ["radiusMetres"] = 1f, ["target"] = 256, ["hardness"] = 1f }),
                ("foliage_paint", new() { ["layerId"] = Wire, ["worldX"] = 0.5f, ["worldZ"] = 0.5f, ["radiusMetres"] = 1f, ["target"] = -1, ["hardness"] = 1f }),
                ("foliage_paint", new() { ["layerId"] = Wire, ["worldX"] = 0.5f, ["worldZ"] = 0.5f, ["radiusMetres"] = 1f, ["target"] = 9, ["hardness"] = 2f }),
                ("foliage_remove", new() { ["layerId"] = "absent-cover" }),
            })
            {
                await Refuse(harness, verb, args);
                Assert.Equal(beforeState, FoliageEditorFixtures.State(live));
            }
            Assert.Throws<MapDocumentException>(() => harness.Services.GetRequiredService<MutationService>().FoliageApply(new MapRemoveFoliage(Wire), dryRun: true));
            Assert.Equal(beforeState, FoliageEditorFixtures.State(live));

            // The dry run's digest lands exactly the previewed proposal as one undoable step.
            JsonElement applied = Body(await Call(harness, "foliage_layer_set",
                new() { ["layerJson"] = LayerJson(policy), ["expectedDifferentialDigest"] = expected.Digest }));
            Assert.True(applied.GetProperty("applied").GetBoolean());
            Assert.NotEqual(identity, applied.GetProperty("identity").GetString());
            Assert.Equal("Set foliage layer", applied.GetProperty("undoLabel").GetString());
            Assert.NotEqual(beforeState, FoliageEditorFixtures.State(live));
        }
        finally { FoliageEditorFixtures.Delete(seed); }
    }

    [Fact]
    public async Task FoliageWire_MapValidateChecksRegionPacks()
    {
        await using var harness = await McpWire.StartAsync();
        foreach (string failure in new[] { "clean", "missing", "corrupt", "orphan", "tmp" })
        {
            string root = Path.Combine(PrefabFixtures.TempDirectory(), "world");
            try
            {
                FoliageFixture fixture = FoliageStorageFixtures.RegionFixture();
                MapDocumentFile.SaveTiled(fixture.Document, root);
                foreach (var resource in fixture.Assets.CapturedResources)
                {
                    string target = Path.Combine(root, resource.Reference.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, resource.Bytes.ToArray());
                }
                string[] packs = FoliageStorageFixtures.Packs(root);
                Assert.Equal(2, packs.Length);

                // The valid tiled document opens first, then its storage changes on disk.
                await Call(harness, "map_open", new() { ["path"] = root });
                string pack = packs[0];
                if (failure == "missing") File.Delete(pack);
                if (failure == "corrupt") { byte[] bytes = File.ReadAllBytes(pack); bytes[^1] ^= 1; File.WriteAllBytes(pack, bytes); }
                if (failure == "orphan") File.Copy(pack, Path.Combine(Path.GetDirectoryName(pack)!, new string('f', 64) + ".pack"));
                if (failure == "tmp") File.Copy(pack, pack + ".tmp");

                JsonElement validation = Body(await Call(harness, "map_validate", new() { ["verifyWholeWorld"] = true }));
                Assert.True(validation.GetProperty("wholeWorldChecked").GetBoolean());
                string[] errors = validation.GetProperty("wholeWorldErrors").EnumerateArray().Select(e => e.GetString()!).ToArray();
                if (failure == "clean")
                {
                    Assert.True(validation.GetProperty("valid").GetBoolean() && validation.GetProperty("wholeWorldValid").GetBoolean(), string.Join("\n", errors));
                    Assert.Empty(errors);
                    continue;
                }
                Assert.False(validation.GetProperty("valid").GetBoolean());
                Assert.False(validation.GetProperty("wholeWorldValid").GetBoolean());
                string category = failure switch
                {
                    "missing" => "foliage pack Unavailable",
                    "corrupt" => "foliage pack Invalid",
                    "orphan" => "orphan tile file",
                    _ => "stray temp file",
                };
                Assert.Contains(errors, e => e.StartsWith(category, StringComparison.Ordinal) && e.Contains("foliage", StringComparison.Ordinal));
            }
            finally { Directory.Delete(Path.GetDirectoryName(root)!, true); }
        }
    }

    // The context captures one direct preview read makes on the test thread.
    static long Previews(QueryService query, MapFoliageChunkKey chunk) =>
        FoliageEditorFixtures.Work(() => query.FoliageGet(Wire, previewChunk: chunk)).Captures;

    internal static string LayerJson(MapFoliageLayer layer) =>
        JsonNode.Parse(FoliageStorageFixtures.Json(new MapFoliageSet(1, [layer])))!["layers"]![0]!.ToJsonString();

    internal static JsonElement Body(CallToolResult result)
    {
        using JsonDocument json = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        return json.RootElement.Clone();
    }

    internal static async Task<CallToolResult> Call(McpAdapterTests.McpHarness harness, string name, Dictionary<string, object?> args)
    {
        CallToolResult result = await harness.Client.CallAsync(name, args);
        Assert.False(result.IsError == true, name + ": " + string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        return result;
    }

    static async Task<string> Refuse(McpAdapterTests.McpHarness harness, string name, Dictionary<string, object?> args)
    {
        CallToolResult result = await harness.Client.CallAsync(name, args);
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.True(result.IsError == true, name + " was accepted: " + string.Join(",", args.Keys) + " " + text);
        return text;
    }
}
