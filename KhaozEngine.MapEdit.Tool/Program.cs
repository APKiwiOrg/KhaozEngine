// ke-mapedit MCP server entry point.
// A hosted stdio MCP server: the same composition path the integration tests reuse (AddMapEditServices plus
// WithMapEditTools) behind a stdio transport. Logging routes to stderr so it never corrupts the JSON-RPC stream
// on stdout. When stdin closes the transport ends and the host shuts down cleanly. The document, query, mutation,
// and render tool classes all register through WithMapEditTools.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KhaozEngine.MapEdit.Tools;
using KhaozEngine.MapEdit;
using KhaozEngine.MapDoc.Render3D;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMapEditServices();
string? nativePolicy = builder.Configuration["NativeRenderPolicy"];
if (!string.IsNullOrWhiteSpace(nativePolicy))
    builder.Services.AddSingleton<IMapNativeRenderHost>(new FileNativeRenderHost(nativePolicy));
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithMapEditTools();
await builder.Build().RunAsync();
return 0;
