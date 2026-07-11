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
        /// <summary>All 39 verb names, spelled exactly as the plan header's verb table, including the two render
        /// verbs added in Task 6.</summary>
        static readonly string[] ExpectedVerbs =
        {
            // Document
            "map_open", "map_create", "map_save", "map_validate", "map_summary",
            // Query
            "ground_height", "is_walkable", "placements_in_rect", "scatter_preview_in_rect", "find_flat_area",
            // Placements
            "placement_add", "placement_move", "placement_rotate", "placement_scale", "placement_rename",
            "placement_remove",
            // Spawns
            "spawn_add", "spawn_move", "spawn_set_enabled", "spawn_rename", "spawn_remove",
            // Terrain
            "terrain_edit", "feature_add", "feature_edit", "feature_remove", "feature_reorder",
            // Scatter
            "exclusion_add", "exclusion_edit", "exclusion_remove", "scatter_override_add", "scatter_override_edit",
            "scatter_override_remove", "bake_region",
            // Regions
            "region_add", "region_edit_shape", "region_rename", "region_remove",
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
