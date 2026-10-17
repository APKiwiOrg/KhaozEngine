using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.TileEdit.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>A live in-process MCP client and server wired over a pair of pipes. The server is composed from the
/// same <see cref="IMcpServerBuilder"/> extensions the stdio host uses (the stream transport, arrival-ordered tool
/// calls, then the tools) and run straight from its service provider.
///
/// <para>There is deliberately no Generic Host here. A host adds configuration providers, a file watcher on the
/// content root and console lifetime signal handlers, none of which the wire tests exercise, and its bring-up is
/// CPU work inside each test's deadline that grows with machine load. For the same reason the
/// ordering tests register only their probe tool: building the full verb set's schemas is the largest single
/// bring-up cost and has nothing to do with ordering.</para></summary>
sealed class McpHarness : IAsyncDisposable
{
    readonly ServiceProvider _services;
    readonly CancellationTokenSource _stopping;
    readonly Task _serverRun;

    public McpClient Client { get; }
    public IServiceProvider Services => _services;

    McpHarness(ServiceProvider services, CancellationTokenSource stopping, Task serverRun, McpClient client)
    {
        _services = services;
        _stopping = stopping;
        _serverRun = serverRun;
        Client = client;
    }

    /// <summary>Starts a server carrying the whole ke-tileedit verb set and its session services.</summary>
    public static Task<McpHarness> StartAsync(CancellationToken cancellationToken) =>
        StartAsync(server =>
        {
            server.Services.AddTileEditServices();
            server.WithTileEditTools();
        }, cancellationToken);

    /// <summary>Starts a server whose tools and filters <paramref name="configure"/> chooses, behind the real
    /// stream transport and arrival-ordered tool-call decorator.</summary>
    public static async Task<McpHarness> StartAsync(Action<IMcpServerBuilder> configure,
        CancellationToken cancellationToken)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        IMcpServerBuilder server = services.AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithArrivalOrderedToolCalls();
        configure(server);

        ServiceProvider provider = services.BuildServiceProvider();
        var stopping = new CancellationTokenSource();
        Task serverRun = provider.GetRequiredService<McpServer>().RunAsync(stopping.Token);
        try
        {
            var transport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream());
            McpClient client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            return new McpHarness(provider, stopping, serverRun, client);
        }
        catch
        {
            await StopAsync(provider, stopping, serverRun);
            throw;
        }
    }

    /// <summary>Calls a verb with named arguments, which is how an MCP client passes them.</summary>
    public ValueTask<CallToolResult> CallAsync(string verb, CancellationToken cancellationToken,
        params (string Name, object? Value)[] arguments) =>
        Client.CallToolAsync(verb, arguments.ToDictionary(a => a.Name, a => a.Value),
            cancellationToken: cancellationToken);

    public ValueTask<CallToolResult> CallWithIdAsync(string verb, RequestId requestId,
        CancellationToken cancellationToken, params (string Name, object? Value)[] arguments) =>
        Client.SendRequestAsync<CallToolRequestParams, CallToolResult>(RequestMethods.ToolsCall,
            new CallToolRequestParams
            {
                Name = verb,
                Arguments = arguments.ToDictionary(a => a.Name,
                    a => JsonSerializer.SerializeToElement(a.Value)),
            }, requestId: requestId, cancellationToken: cancellationToken);

    public Task CancelAsync(RequestId requestId, CancellationToken cancellationToken) =>
        Client.SendNotificationAsync(NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = requestId },
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

    /// <summary>Tears the client down first, then stops the server run and disposes its services.</summary>
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await StopAsync(_services, _stopping, _serverRun);
    }

    static async Task StopAsync(ServiceProvider services, CancellationTokenSource stopping, Task serverRun)
    {
        await stopping.CancelAsync();
        try
        {
            await serverRun;
        }
        catch (OperationCanceledException)
        {
        }
        stopping.Dispose();
        await services.DisposeAsync();
    }
}
