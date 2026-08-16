// ke-tileedit MCP server entry point.
// A hosted stdio MCP server: the same composition path the integration tests reuse (AddTileEditServices plus
// WithTileEditTools) behind a stdio transport. Logging routes to stderr so it never corrupts the JSON-RPC stream
// on stdout. When stdin closes the transport ends and the host shuts down cleanly. The world, tile, height,
// object, marker, prefab, collision, and render tool classes all register through WithTileEditTools.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KhaozEngine.TileEdit.Tools;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddTileEditServices();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTileEditTools();
await builder.Build().RunAsync();
return 0;
