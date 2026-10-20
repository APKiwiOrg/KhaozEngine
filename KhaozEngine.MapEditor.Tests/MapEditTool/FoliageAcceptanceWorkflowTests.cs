using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static KhaozEngine.Tests.MapEditTool.FoliageToolTests;

namespace KhaozEngine.Tests.MapEditTool;

/// <summary>The authoring workflow over real MCP calls on the stacked prefab-cave acceptance fixture: discovery, previews
/// that keep each domain on its own support, edits whose wire effects equal the public boundary's and name only their
/// own layer, undo to the opened bytes, and a tiled save that verifies whole.</summary>
[Collection(McpWireCollection.Name)]
public sealed class FoliageAcceptanceWorkflowTests
{
    static readonly (string Layer, string Kind, string Owner)[] Domains =
    [
        (FoliageFixtures.SurfaceLayer, "Surface", FoliageFixtures.SurfaceFloor),
        (FoliageFixtures.BridgeLayer, "Surface", FoliageFixtures.BridgeFloor),
        (FoliageFixtures.RoomLayer, "Space", FoliageFixtures.BridgeFloor),
        (FoliageFixtures.CaveLayer, "Space", FoliageFixtures.DeepFloor),
    ];

    [Fact]
    public async Task StackedDomains_WireEditsInvalidateOnlyTheirOwnLayer()
    {
        FoliageFixture f = FoliageFixtures.Cave();
        MapEditSession seed = FoliageEditorFixtures.Session(f);
        try
        {
            await using var harness = await McpWire.StartAsync();
            await Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });
            var live = harness.Services.GetRequiredService<MapEditSession>();
            string opened = FoliageEditorFixtures.Bytes(live);

            // Discovery lists the four layers in authoring order with their own binding kinds.
            JsonElement list = Body(await Call(harness, "foliage_get", new()));
            Assert.Equal(Domains.Select(d => (d.Layer, d.Kind)), list.GetProperty("layers").EnumerateArray()
                .Select(l => (l.GetProperty("id").GetString()!, l.GetProperty("support").GetProperty("kind").GetString()!)));

            // Each preview of the shared chunk keeps its samples on its own support, never on another domain's floor.
            foreach (var (layer, _, owner) in Domains)
            {
                var args = new Dictionary<string, object?> { ["layerId"] = layer, ["previewChunkJson"] = "{\"x\":0,\"z\":0}" };
                JsonElement preview = Body(await Call(harness, "foliage_get", args)).GetProperty("preview");
                Assert.Equal("Complete", preview.GetProperty("status").GetString());
                string[] owners = preview.GetProperty("samples").EnumerateArray()
                    .Select(s => s.GetProperty("key").GetProperty("address").GetProperty("supportOwnerId").GetString()!).Distinct().ToArray();
                Assert.Equal([owner], owners);
            }
            Assert.Equal(opened, FoliageEditorFixtures.Bytes(live));

            // Localized edits over the shared column publish rows for their own layer only, exactly as the public boundary
            // computes them between the documents before and after.
            MapFoliageLayer bridge = f.Document.Foliage!.Layers.Single(l => l.Id == FoliageFixtures.BridgeLayer);
            var steps = new (string Verb, Dictionary<string, object?> Args, string Layer, bool Entry)[]
            {
                ("foliage_paint", new()
                {
                    ["layerId"] = FoliageFixtures.CaveLayer,
                    ["worldX"] = 2f,
                    ["worldZ"] = -2f,
                    ["radiusMetres"] = 1.5f,
                    ["target"] = 0,
                    ["hardness"] = 1f,
                }, FoliageFixtures.CaveLayer, false),
                ("foliage_density_set", new()
                {
                    ["layerId"] = FoliageFixtures.SurfaceLayer,
                    ["x"] = 3,
                    ["z"] = 3,
                    ["width"] = 2,
                    ["depth"] = 2,
                    ["density"] = new[] { 0, 0, 0, 0 },
                }, FoliageFixtures.SurfaceLayer, false),
                ("foliage_layer_set", new() { ["layerJson"] = LayerJson(FoliageStorageFixtures.Change(bridge, l => l["seed"] = 30)) },
                    FoliageFixtures.BridgeLayer, true),
                ("foliage_remove", new() { ["layerId"] = FoliageFixtures.RoomLayer }, FoliageFixtures.RoomLayer, true),
            };
            var published = new List<string> { opened };
            foreach (var (verb, args, layer, entry) in steps)
            {
                FoliageFixture before = FoliageEditorFixtures.Fixture(live);
                JsonElement result = Body(await Call(harness, verb, args));
                Assert.True(result.GetProperty("applied").GetBoolean(), verb);
                FoliageFixture after = FoliageEditorFixtures.Fixture(live);
                MapNativeEditEffects effects = live.LastNativeEffects!;
                Assert.Equal(effects.Describe(), result.GetProperty("effects").GetString());
                Assert.Equal(FoliageEditorFixtures.Expected(before.Document, after.Document, before.Assets, after.Assets),
                    FoliageEditorFixtures.Channel(effects));
                Assert.Equal(MapNativeInvalidation.Foliage, effects.Invalidates);
                if (entry)
                {
                    Assert.Empty(effects.FoliageChunks);
                    Assert.Equal(layer, Assert.Single(effects.FoliageLayers).LayerId);
                }
                else
                {
                    Assert.Empty(effects.FoliageLayers);
                    Assert.NotEmpty(effects.FoliageChunks);
                    Assert.All(effects.FoliageChunks, r => Assert.Equal(layer, r.LayerId));
                }
                // Every other layer's generated batches are unchanged by the edit.
                Dictionary<string, string> a = Generated(before), b = Generated(after);
                foreach (string other in a.Keys.Where(k => k != layer)) Assert.Equal(a[other], b[other]);
                published.Add(FoliageEditorFixtures.Bytes(live));
            }
            Assert.Equal(published.Count, published.Distinct(StringComparer.Ordinal).Count());

            // Undo replays every published state back to the opened bytes.
            for (int i = steps.Length - 1; i >= 0; i--)
            {
                await Call(harness, "prefab_undo", new());
                Assert.Equal(published[i], FoliageEditorFixtures.Bytes(live));
            }
            Assert.Equal(opened, FoliageEditorFixtures.Bytes(live));

            // A tiled save of the redone edits opens again with the same foliage and batches and verifies whole, every
            // region pack included.
            for (int i = 0; i < steps.Length; i++) await Call(harness, "prefab_redo", new());
            Assert.Equal(published[^1], FoliageEditorFixtures.Bytes(live));
            string foliage = FoliageEditorFixtures.Foliage(live);
            Dictionary<string, string> batches = Generated(FoliageEditorFixtures.Fixture(live));
            string root = Path.Combine(PrefabFixtures.TempDirectory(), "world");
            try
            {
                FoliageFixture redone = FoliageEditorFixtures.Fixture(live);
                MapDocumentFile.SaveTiled(redone.Document, root);
                foreach (var resource in redone.Assets.CapturedResources)
                {
                    string target = Path.Combine(root, resource.Reference.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, resource.Bytes.ToArray());
                }
                await Call(harness, "map_open", new() { ["path"] = root });
                JsonElement validation = Body(await Call(harness, "map_validate", new() { ["verifyWholeWorld"] = true }));
                Assert.True(validation.GetProperty("valid").GetBoolean() && validation.GetProperty("wholeWorldValid").GetBoolean(),
                    string.Join("\n", validation.GetProperty("wholeWorldErrors").EnumerateArray().Select(e => e.GetString())));
                Assert.Equal(foliage, FoliageEditorFixtures.Foliage(live));
                Assert.Equal(batches, Generated(FoliageEditorFixtures.Fixture(live)));
            }
            finally { Directory.Delete(Path.GetDirectoryName(root)!, true); }
        }
        finally { FoliageEditorFixtures.Delete(seed); }
    }

    // Each layer's chunk (0, 0) of the fixture, in canonical form.
    static Dictionary<string, string> Generated(FoliageFixture fixture)
    {
        MapFoliageGenerationContext context = FoliageFixtures.Context(fixture);
        return context.Foliage!.Layers.ToDictionary(l => l.Id,
            l => FoliageFixtures.Canonical([MapFoliageDistribution.Generate(context, l.Id, new(0, 0))]), StringComparer.Ordinal);
    }
}
