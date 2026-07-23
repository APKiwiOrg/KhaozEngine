using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEdit.Tools;
using KhaozEngine.Terrain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;

namespace KhaozEngine.Tests.MapEditTool
{
    /// <summary>In-process MCP integration tests for the ke-mapedit adapter layer. Each test stands up the real
    /// composition path (<see cref="McpBootstrap.AddMapEditServices"/> plus the same tool registration the stdio
    /// host uses) behind a paired-stream transport, connects a real <see cref="McpClient"/> over it, and drives the
    /// tools through the wire: list, call with JSON arguments, and read the returned content. This exercises the
    /// attribute discovery, DI construction, JSON (de)serialization, and the McpException error mapping end to
    /// end, not just the service methods in isolation.</summary>
    public class McpAdapterTests
    {
        /// <summary>All 73 verb names, spelled exactly as the plan header's verb table: the original 39 (including
        /// the two render verbs added in Task 6) plus the Task 5 naming, layer-targeting, and procedural-setup
        /// verbs (feature/exclusion rename, exclusion layer targeting, the biome band and scatter/companion layer
        /// triads plus their rename verbs, and the procedural_info read verb), the scatter_rule
        /// add/edit/remove triad that closes the scatter-layer-rules MCP parity gap, and the Task 6 player_spawn
        /// verb family (add/move/set_yaw/set_enabled/rename/remove). player_spawn_set_yaw stays its own verb
        /// rather than folding yaw into player_spawn_move, mirroring how placement_rotate stays a separate verb
        /// from placement_move (the underlying commands are already distinct with independent TryMerge
        /// coalescing, so the MCP verb granularity follows the command granularity). element_duplicate (editor
        /// round 7, decision 10) closes the duplicate MCP parity gap: one verb spanning all ten duplicatable
        /// kinds, mirroring the editor's own Cmd+D exactly (Terrain has no duplicate, it is a document
        /// singleton). scatter_override_rename and scatter_override_reorder close the last scatter-override MCP
        /// parity gap: every scatter-override verb now routes through the Task 2 EditorCommand classes instead of
        /// a direct list mutation, and element_duplicate's scatter_override case (the tenth duplicatable kind)
        /// reuses those same command classes and the GUI's own clone helpers. Round 8 Task 2 adds exclusions_info
        /// and scatter_overrides_info, the read counterparts to the exclusion_* and scatter_override_* mutation
        /// verbs that procedural_info does not cover (that read only reflects terrain, scatter layers, and
        /// companion layers). The terrain sculpt layer's T3 (#271) adds sculpt_apply (one brush dab),
        /// sculpt_flatten_region (an exact region flatten), sculpt_clear (drop tiles back to analytic), and
        /// sculpt_stats (the read counterpart: tile count, cell size, touched-cell count, delta min/max).</summary>
        static readonly string[] ExpectedVerbs =
        {
            // Document
            "map_open", "map_create", "map_save", "map_validate", "map_summary",
            // Query
            "ground_height", "is_walkable", "placements_in_rect", "scatter_preview_in_rect", "find_flat_area",
            "procedural_info", "exclusions_info", "scatter_overrides_info", "sculpt_stats",
            // Placements
            "placement_add", "placement_move", "placement_rotate", "placement_scale", "placement_rename",
            "placement_remove",
            // Spawns
            "spawn_add", "spawn_move", "spawn_set_enabled", "spawn_rename", "spawn_remove",
            // Player spawns
            "player_spawn_add", "player_spawn_move", "player_spawn_set_yaw", "player_spawn_set_enabled",
            "player_spawn_rename", "player_spawn_remove",
            // Terrain
            "terrain_edit", "feature_add", "feature_edit", "feature_remove", "feature_reorder", "feature_rename",
            "biome_band_add", "biome_band_edit", "biome_band_remove",
            // Scatter
            "exclusion_add", "exclusion_edit", "exclusion_remove", "exclusion_rename", "exclusion_set_layers",
            "scatter_override_add", "scatter_override_edit", "scatter_override_remove", "scatter_override_rename",
            "scatter_override_reorder", "bake_region", "freeze_zone",
            "scatter_layer_add", "scatter_layer_edit", "scatter_layer_remove", "scatter_layer_rename",
            "scatter_rule_add", "scatter_rule_edit", "scatter_rule_remove",
            "companion_layer_add", "companion_layer_edit", "companion_layer_remove", "companion_layer_rename",
            // Regions
            "region_add", "region_edit_shape", "region_rename", "region_remove",
            // Sculpt (terrain height-delta layer, T3, #271)
            "sculpt_apply", "sculpt_flatten_region", "sculpt_clear",
            // Element duplicate (cross-kind: placement, spawn, player spawn, feature, exclusion, scatter
            // override, region, biome band, scatter layer, companion layer)
            "element_duplicate",
            // Renders
            "render_topdown", "render_view",
        };

        [Fact]
        public async Task ToolRegistry_ExposesAllDocumentQueryMutationVerbs()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using McpHarness harness = await McpHarness.StartAsync(cts.Token);

            IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
            HashSet<string> names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

            Assert.Equal(ExpectedVerbs.ToHashSet(StringComparer.Ordinal), names);
        }

        [Fact]
        public async Task MapSummary_RoundTripsThroughMcpCall()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);

                await using McpHarness harness = await McpHarness.StartAsync(cts.Token);

                CallToolResult open = await harness.Client.CallToolAsync("map_open",
                    new Dictionary<string, object?> { ["path"] = path }, cancellationToken: cts.Token);
                Assert.NotEqual(true, open.IsError);

                CallToolResult summary = await harness.Client.CallToolAsync("map_summary",
                    new Dictionary<string, object?>(), cancellationToken: cts.Token);
                Assert.NotEqual(true, summary.IsError);

                MapSummary parsed = Deserialize<MapSummary>(summary);
                Assert.Equal("test-zone", parsed.Id);
                Assert.Equal(2, parsed.FeatureTypes.Count);
                Assert.Equal(1, parsed.PlacementCount);
                Assert.Equal(1, parsed.SpawnCount);
                Assert.Equal(1, parsed.ExclusionCount);
                Assert.Equal(1, parsed.ScatterOverrideCount);
                Assert.Equal(new[] { "trees" }, parsed.ScatterLayers);
                Assert.Equal(new[] { "understory" }, parsed.CompanionLayers);
                Assert.Equal(new[] { "town" }, parsed.RegionNames);
                Assert.False(parsed.Dirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task FailedVerb_SurfacesPreciseMessage()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using McpHarness harness = await McpHarness.StartAsync(cts.Token);

            // map_summary with no document open: the service throws, the adapter maps it to an McpException, and the
            // SDK surfaces the verbatim message (which names map_open) as an error result rather than masking it.
            CallToolResult result = await harness.Client.CallToolAsync("map_summary",
                new Dictionary<string, object?>(), cancellationToken: cts.Token);

            Assert.True(result.IsError);
            string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            Assert.Contains("map_open", text);
        }

        [Fact]
        public async Task PlacementAdd_ThroughMcp_MutatesSessionAndReportsGround()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);

                await using McpHarness harness = await McpHarness.StartAsync(cts.Token);

                await harness.Client.CallToolAsync("map_open",
                    new Dictionary<string, object?> { ["path"] = path }, cancellationToken: cts.Token);

                CallToolResult result = await harness.Client.CallToolAsync("placement_add",
                    new Dictionary<string, object?> { ["kind"] = "hut", ["x"] = 12.0, ["z"] = -3.5 },
                    cancellationToken: cts.Token);
                Assert.NotEqual(true, result.IsError);

                MutationResult mutation = Deserialize<MutationResult>(result);
                Assert.Equal("placement_add", mutation.Verb);
                Assert.Equal("p-hut-1", mutation.Id);
                Assert.NotNull(mutation.GroundY);

                // The mutation reached the same session singleton the tool resolved from DI.
                var session = harness.Services.GetRequiredService<MapEditSession>();
                float expectedGround = session.Field().SampleHeight(12f, -3.5f);
                Assert.Equal(expectedGround, mutation.GroundY!.Value, 3);

                MapPlacement added = session.WithDocument((doc, _) => doc.Placements.Single(p => p.Id == "p-hut-1"));
                Assert.Equal("hut", added.Kind);
                Assert.Equal(12f, added.X);
                Assert.Equal(-3.5f, added.Z);
                Assert.True(session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task PlayerSpawnAdd_ThroughMcp_MutatesSessionAndReportsId()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);

                await using McpHarness harness = await McpHarness.StartAsync(cts.Token);

                await harness.Client.CallToolAsync("map_open",
                    new Dictionary<string, object?> { ["path"] = path }, cancellationToken: cts.Token);

                CallToolResult result = await harness.Client.CallToolAsync("player_spawn_add",
                    new Dictionary<string, object?> { ["x"] = 5.0, ["z"] = 6.0, ["yaw"] = 0.5 },
                    cancellationToken: cts.Token);
                Assert.NotEqual(true, result.IsError);

                MutationResult mutation = Deserialize<MutationResult>(result);
                Assert.Equal("player_spawn_add", mutation.Verb);
                Assert.Equal("player-1", mutation.Id);

                // The mutation reached the same session singleton the tool resolved from DI.
                var session = harness.Services.GetRequiredService<MapEditSession>();
                MapPlayerSpawn added = session.WithDocument((doc, _) => doc.PlayerSpawns.Single(s => s.Id == "player-1"));
                Assert.Equal(5f, added.X);
                Assert.Equal(6f, added.Z);
                Assert.Equal(0.5f, added.Yaw);
                Assert.True(added.Enabled);
                Assert.True(session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task BiomeBandAdd_ThroughMcp_AppendsBandAndReportsIndex()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);

                await using McpHarness harness = await McpHarness.StartAsync(cts.Token);

                await harness.Client.CallToolAsync("map_open",
                    new Dictionary<string, object?> { ["path"] = path }, cancellationToken: cts.Token);

                CallToolResult result = await harness.Client.CallToolAsync("biome_band_add",
                    new Dictionary<string, object?>
                    {
                        ["biome"] = "Desert", ["baseHeight"] = 4.0, ["hillAmplitude"] = 0.5,
                    },
                    cancellationToken: cts.Token);
                Assert.NotEqual(true, result.IsError);

                MutationResult mutation = Deserialize<MutationResult>(result);
                Assert.Equal("biome_band_add", mutation.Verb);
                Assert.True(mutation.WorldChanged);
                // SampleDoc already carries one band (index 0), so the MCP call appends at index 1.
                Assert.Equal(1, mutation.Index);

                var session = harness.Services.GetRequiredService<MapEditSession>();
                MapBiomeBand added = session.WithDocument((doc, _) => doc.Terrain.Biomes[1]);
                Assert.Equal(BiomeId.Desert, added.Biome);
                Assert.Equal(4f, added.BaseHeight);
                Assert.Equal(0.5f, added.HillAmplitude);
                Assert.True(session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task ElementDuplicate_ThroughMcp_DuplicatesPlacementAndReportsId()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);

                await using McpHarness harness = await McpHarness.StartAsync(cts.Token);

                await harness.Client.CallToolAsync("map_open",
                    new Dictionary<string, object?> { ["path"] = path }, cancellationToken: cts.Token);

                CallToolResult result = await harness.Client.CallToolAsync("element_duplicate",
                    new Dictionary<string, object?> { ["kind"] = "placement", ["id"] = "inn" },
                    cancellationToken: cts.Token);
                Assert.NotEqual(true, result.IsError);

                MutationResult mutation = Deserialize<MutationResult>(result);
                Assert.Equal("element_duplicate", mutation.Verb);
                Assert.Equal("placement-1", mutation.Id);

                var session = harness.Services.GetRequiredService<MapEditSession>();
                MapPlacement clone = session.WithDocument((doc, _) => doc.Placements.Single(p => p.Id == "placement-1"));
                Assert.Equal("building_inn", clone.Kind);
                Assert.Equal(-28f, clone.X);
                Assert.Equal(22f, clone.Z);
                Assert.True(session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task SculptApply_ThroughMcp_MutatesSessionAndReportsTouchedCells()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(SampleDocs.SampleDoc(), path);

                await using McpHarness harness = await McpHarness.StartAsync(cts.Token);

                await harness.Client.CallToolAsync("map_open",
                    new Dictionary<string, object?> { ["path"] = path }, cancellationToken: cts.Token);

                CallToolResult result = await harness.Client.CallToolAsync("sculpt_apply",
                    new Dictionary<string, object?>
                    {
                        ["brush"] = "raise", ["x"] = 8.0, ["z"] = 8.0, ["radius"] = 2.5, ["strength"] = 4.0, ["dt"] = 0.5,
                    },
                    cancellationToken: cts.Token);
                Assert.NotEqual(true, result.IsError);

                SculptApplyResult sculpt = Deserialize<SculptApplyResult>(result);
                Assert.True(sculpt.Applied);
                Assert.True(sculpt.TouchedCellCount > 0);

                var session = harness.Services.GetRequiredService<MapEditSession>();
                Assert.NotNull(session.WithDocument((doc, _) => doc.TerrainOverrides));
                Assert.True(session.IsDirty);

                CallToolResult stats = await harness.Client.CallToolAsync("sculpt_stats",
                    new Dictionary<string, object?>(), cancellationToken: cts.Token);
                Assert.NotEqual(true, stats.IsError);
                SculptStatsResult statsResult = Deserialize<SculptStatsResult>(stats);
                Assert.True(statsResult.HasLayer);
                Assert.Equal(sculpt.TouchedCellCount, statsResult.TouchedCellCount);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-mapedit-mcp-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Deserializes a tool result's payload back into <typeparamref name="T"/>. Prefers the text
        /// content block (the default serialization the SDK emits for a returned record) and falls back to the
        /// structured content when present. Web defaults make the camelCase MCP payload bind to the PascalCase
        /// record members.</summary>
        static T Deserialize<T>(CallToolResult result)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            TextContentBlock? text = result.Content.OfType<TextContentBlock>().FirstOrDefault();
            if (text is not null)
                return JsonSerializer.Deserialize<T>(text.Text, options)
                    ?? throw new InvalidOperationException("tool result text deserialized to null.");
            if (result.StructuredContent is JsonElement structured)
                return structured.Deserialize<T>(options)
                    ?? throw new InvalidOperationException("tool result structured content deserialized to null.");
            throw new InvalidOperationException("tool result carried neither text nor structured content.");
        }

        /// <summary>A live in-process MCP client and server, wired over a pair of pipes through the same
        /// composition path the stdio host uses. Disposing tears the client down first, then the host.</summary>
        sealed class McpHarness : IAsyncDisposable
        {
            readonly IHost _host;

            public McpClient Client { get; }
            public IServiceProvider Services => _host.Services;

            McpHarness(IHost host, McpClient client)
            {
                _host = host;
                Client = client;
            }

            public static async Task<McpHarness> StartAsync(CancellationToken cancellationToken)
            {
                var clientToServer = new Pipe();
                var serverToClient = new Pipe();

                HostApplicationBuilder builder = Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                builder.Services.AddMapEditServices();
                builder.Services.AddMcpServer()
                    .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
                    .WithMapEditTools();

                IHost host = builder.Build();
                await host.StartAsync(cancellationToken);

                var transport = new ModelContextProtocol.Protocol.StreamClientTransport(
                    serverInput: clientToServer.Writer.AsStream(),
                    serverOutput: serverToClient.Reader.AsStream());
                McpClient client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
                return new McpHarness(host, client);
            }

            public async ValueTask DisposeAsync()
            {
                await Client.DisposeAsync();
                await _host.StopAsync();
                _host.Dispose();
            }
        }
    }
}
