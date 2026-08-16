using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.TileEdit;
using KhaozEngine.TileEdit.Tools;
using KhaozEngine.Tests.Gpu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>In-process MCP integration tests for the ke-tileedit adapter layer. Each test stands up the real
/// composition path (<see cref="McpBootstrap.AddTileEditServices"/> plus the same
/// <see cref="McpBootstrap.WithTileEditTools"/> registration the stdio host uses) behind a paired-stream
/// transport, connects a real <see cref="McpClient"/> over it, and drives the verbs through the wire: list, call
/// with JSON arguments, read the returned content. That exercises the attribute discovery, the DI construction,
/// the JSON schema generation for every parameter, the (de)serialization of the result records, and the
/// McpException error mapping end to end, none of which a direct service call touches.
///
/// <para>In <c>NativeDeviceLifecycle</c> because the render row builds a whole GPU device through the wire, the
/// same reason <c>RenderServiceTests</c> is. See <see cref="NativeDeviceLifecycleCollection"/>.</para></summary>
[Collection("NativeDeviceLifecycle")]
public class McpAdapterTests
{
    /// <summary>All 43 verb names, spelled exactly as the design's verb table: the world lifecycle plus catalog,
    /// region and history verbs, the tile layers, the corner-height lattice and its brushes, the object family
    /// with its two batch placers, markers, prefabs, the derived collision queries, and the two renders. This
    /// array is the contract: a verb renamed or added without a matching edit here fails the set assertion
    /// below, which is the point. Note what is NOT here: <c>MutationService.TilesClear</c> exists on the service
    /// but is deliberately not a verb, because passing underlay 0, overlay 0, shape Full, rotation 0 and settings
    /// none to <c>tiles_fill</c> already clears a rect, and a second name for it would only be a second thing to
    /// learn.</summary>
    static readonly string[] ExpectedVerbs =
    {
        // World lifecycle, catalogs, regions, history
        "world_open", "world_create", "world_save", "world_summary", "world_validate", "catalog_list",
        "region_create", "region_delete", "region_list", "undo", "redo",
        // Tile layers
        "tile_get", "tile_set", "tiles_fill", "tiles_get_rect",
        // Corner heights
        "height_set", "height_raise", "height_flatten", "height_smooth", "height_get_rect", "height_import",
        // Objects
        "object_place", "object_move", "object_rotate", "object_remove", "object_get", "objects_in_rect",
        "object_find", "objects_line", "objects_scatter", "object_set_tags",
        // Markers
        "marker_set", "marker_remove", "marker_list",
        // Prefabs
        "prefab_save", "prefab_place", "prefab_list",
        // Derived collision
        "collision_at", "is_walkable", "path", "walkable_rect",
        // Renders
        "render_topdown", "render_view",
    };

    [Fact]
    public async Task ToolRegistry_ExposesExactlyTheVerbSet()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using McpHarness harness = await McpHarness.StartAsync(cts.Token);

        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        HashSet<string> names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(ExpectedVerbs.Length, ExpectedVerbs.ToHashSet(StringComparer.Ordinal).Count);
        Assert.Equal(ExpectedVerbs.ToHashSet(StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task WorldCreateThenSummary_RoundTripsThroughMcpCalls()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var temp = new TempDir();
        string world = temp.Sub("world");
        TileEditTestWorld.WriteCatalog(Path.Combine(world, TileEditTestWorld.CatalogFileName));

        await using McpHarness harness = await McpHarness.StartAsync(cts.Token);

        CallToolResult created = await harness.CallAsync("world_create", cts.Token,
            ("path", world), ("id", "wire-world"), ("displayName", "Wire World"),
            ("catalogPaths", new[] { TileEditTestWorld.CatalogFileName }), ("planeCount", 2), ("tileSize", 1.0));
        Assert.NotEqual(true, created.IsError);
        Assert.Equal("wire-world", Deserialize<OpenResult>(created).Id);

        CallToolResult summary = await harness.CallAsync("world_summary", cts.Token);
        Assert.NotEqual(true, summary.IsError);

        WorldSummary parsed = Deserialize<WorldSummary>(summary);
        Assert.Equal("wire-world", parsed.Id);
        Assert.Equal("Wire World", parsed.DisplayName);
        Assert.Equal(2, parsed.PlaneCount);
        Assert.Equal(1, parsed.RegionCount);
        Assert.Equal(0, parsed.UndoDepth);
        Assert.Equal(new[] { TileEditTestWorld.CatalogFileName }, parsed.CatalogPaths);

        // The verbs reached the same session singleton the tool classes resolved from DI, which is what makes an
        // open world survive from one call to the next.
        var session = harness.Services.GetRequiredService<TileEditSession>();
        Assert.Equal(world, session.DocumentPath);
    }

    [Fact]
    public async Task TilesFillThenUndo_RoundTripsThroughMcpCalls()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var temp = new TempDir();
        await using McpHarness harness = await McpHarness.StartAsync(cts.Token);
        await harness.CreateWorldAsync(temp.Sub("world"), cts.Token);

        CallToolResult filled = await harness.CallAsync("tiles_fill", cts.Token,
            ("x", 0), ("z", 0), ("width", 4), ("height", 4), ("plane", 0), ("underlay", 1));
        Assert.NotEqual(true, filled.IsError);

        MutationResult mutation = Deserialize<MutationResult>(filled);
        Assert.Equal(1, mutation.UndoDepth);
        Assert.True(mutation.Dirty);
        Assert.NotEmpty(mutation.DirtyRects);

        // The fill actually landed, read back through a second verb rather than trusted from the first's result.
        TileInfo painted = Deserialize<TileInfo>(await harness.CallAsync("tile_get", cts.Token,
            ("x", 2), ("z", 2), ("plane", 0)));
        Assert.Equal(1, painted.Underlay);
        Assert.Equal("grass", painted.UnderlayName);

        CallToolResult undone = await harness.CallAsync("undo", cts.Token);
        Assert.NotEqual(true, undone.IsError);
        Assert.Equal(1, Deserialize<UndoResult>(undone).Steps);

        WorldSummary after = Deserialize<WorldSummary>(await harness.CallAsync("world_summary", cts.Token));
        Assert.Equal(0, after.UndoDepth);
        Assert.Equal(1, after.RedoDepth);
        Assert.Equal(0, Deserialize<TileInfo>(await harness.CallAsync("tile_get", cts.Token,
            ("x", 2), ("z", 2), ("plane", 0))).Underlay);
    }

    [Fact]
    public async Task FailedVerb_SurfacesTheTileWorldExceptionMessage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using McpHarness harness = await McpHarness.StartAsync(cts.Token);

        // world_save with nothing open: the session throws TileWorldException, ToolGuard maps it to an
        // McpException, and the SDK surfaces its verbatim message (which names the two opening verbs) rather
        // than masking it behind a generic failure.
        CallToolResult closed = await harness.CallAsync("world_save", cts.Token);

        Assert.True(closed.IsError);
        Assert.Contains("world_open", ErrorText(closed), StringComparison.Ordinal);

        // And a message the service composed at runtime, naming the id that was not found, reaches the client
        // just as intact.
        using var temp = new TempDir();
        await harness.CreateWorldAsync(temp.Sub("world"), cts.Token);
        CallToolResult missing = await harness.CallAsync("object_get", cts.Token, ("id", 9999L));

        Assert.True(missing.IsError);
        Assert.Contains("9999", ErrorText(missing), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TileSet_ParsesAShapeNameCaseInsensitivelyAndNamesTheLegalOnes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var temp = new TempDir();
        await using McpHarness harness = await McpHarness.StartAsync(cts.Token);
        await harness.CreateWorldAsync(temp.Sub("world"), cts.Token);

        CallToolResult set = await harness.CallAsync("tile_set", cts.Token,
            ("x", 1), ("z", 1), ("plane", 0), ("overlay", 2), ("shape", "diagonalHALF"), ("rotation", 1),
            ("settings", "Blocked,Indoors"));
        Assert.NotEqual(true, set.IsError);

        TileInfo tile = Deserialize<TileInfo>(await harness.CallAsync("tile_get", cts.Token,
            ("x", 1), ("z", 1), ("plane", 0)));
        Assert.Equal("DiagonalHalf", tile.Shape);
        Assert.Equal(1, tile.Rotation);
        Assert.Equal("Blocked,Indoors", tile.Settings);

        CallToolResult bad = await harness.CallAsync("tile_set", cts.Token,
            ("x", 1), ("z", 1), ("plane", 0), ("shape", "triangle"));
        Assert.True(bad.IsError);
        string text = ErrorText(bad);
        Assert.Contains("triangle", text, StringComparison.Ordinal);
        Assert.Contains("CornerThreeQuarter", text, StringComparison.Ordinal);
    }

    [GpuFact]
    public async Task RenderTopDown_ReturnsAFramingLineThenThePngImage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var temp = new TempDir();
        await using McpHarness harness = await McpHarness.StartAsync(cts.Token);
        await harness.CreateWorldAsync(temp.Sub("world"), cts.Token);
        await harness.CallAsync("tiles_fill", cts.Token,
            ("x", 0), ("z", 0), ("width", 8), ("height", 8), ("plane", 0), ("underlay", 1));

        CallToolResult result = await harness.CallAsync("render_topdown", cts.Token,
            ("x", 0), ("z", 0), ("width", 8), ("height", 8), ("plane", 0), ("pxPerTile", 4), ("overlays", "grid"));
        Assert.NotEqual(true, result.IsError);

        Assert.Equal(2, result.Content.Count);
        var framing = Assert.IsType<TextContentBlock>(result.Content[0]);
        Assert.Contains("north up", framing.Text, StringComparison.Ordinal);
        Assert.Contains("overlays grid", framing.Text, StringComparison.Ordinal);

        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        // The block's Data is the BASE64 TEXT the wire carried, held as its ASCII bytes, so the PNG signature
        // only shows up after decoding it. Reading Data directly hands back "iVBO", which is exactly what a
        // client would misread as a corrupt image.
        byte[] png = Convert.FromBase64String(Encoding.ASCII.GetString(image.Data.Span));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }

    static string ErrorText(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary>Deserializes a tool result's payload back into <typeparamref name="T"/>. Prefers the text content
    /// block (the default serialization the SDK emits for a returned record) and falls back to the structured
    /// content when present. Web defaults make the camelCase MCP payload bind to the PascalCase record
    /// members.</summary>
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

    /// <summary>A live in-process MCP client and server, wired over a pair of pipes through the same composition
    /// path the stdio host uses. Disposing tears the client down first, then the host.</summary>
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
            builder.Services.AddTileEditServices();
            builder.Services.AddMcpServer()
                .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
                .WithTileEditTools();

            IHost host = builder.Build();
            await host.StartAsync(cancellationToken);

            var transport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream());
            McpClient client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            return new McpHarness(host, client);
        }

        /// <summary>Calls a verb with named arguments, which is how an MCP client passes them.</summary>
        public ValueTask<CallToolResult> CallAsync(string verb, CancellationToken cancellationToken,
            params (string Name, object? Value)[] arguments) =>
            Client.CallToolAsync(verb, arguments.ToDictionary(a => a.Name, a => a.Value),
                cancellationToken: cancellationToken);

        /// <summary>Writes the greybox catalog into a fresh directory and opens a world there THROUGH THE WIRE,
        /// so even the setup of a later test goes over MCP rather than around it.</summary>
        public async Task CreateWorldAsync(string directory, CancellationToken cancellationToken)
        {
            TileEditTestWorld.WriteCatalog(Path.Combine(directory, TileEditTestWorld.CatalogFileName));
            CallToolResult created = await CallAsync("world_create", cancellationToken,
                ("path", directory), ("id", "wire-world"), ("displayName", "Wire World"),
                ("catalogPaths", new[] { TileEditTestWorld.CatalogFileName }));
            Assert.NotEqual(true, created.IsError);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
