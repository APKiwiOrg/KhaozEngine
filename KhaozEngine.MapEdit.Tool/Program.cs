// ke-mapedit MCP server entry point.
// A hosted stdio MCP server: the same composition path the integration tests reuse (AddMapEditServices plus
// WithMapEditTools) behind a stdio transport. Logging routes to stderr so it never corrupts the JSON-RPC stream
// on stdout. When stdin closes the transport ends and the host shuts down cleanly. Renders join in a later task.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KhaozEngine.MapEdit.Tools;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMapEditServices();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithMapEditTools();
await builder.Build().RunAsync();
return 0;
