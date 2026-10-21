using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

/// <summary>Every test class that starts an in-process MCP server. They run one at a time, so a slow call is not
/// stretched by other servers starting and serving beside it.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class McpWireCollection
{
    public const string Name = "McpWire";
}

/// <summary>Gives each MCP request its own timeout, sized for one call. A whole workflow has no shared budget, and its
/// work is bounded by the work-counting tests. A timeout names the request that stalled.</summary>
internal static class McpWire
{
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    internal static Task<McpAdapterTests.McpHarness> StartAsync() => Run("server start", McpAdapterTests.McpHarness.StartAsync);

    internal static Task<McpAdapterTests.McpHarness> StartAsync(string nativePolicyPath) => Run("server start", token => McpAdapterTests.McpHarness.StartAsync(token, nativePolicyPath));

    internal static Task<CallToolResult> CallAsync(this McpClient client, string name, IReadOnlyDictionary<string, object?> arguments) =>
        Run(name, async token => await client.CallToolAsync(name, arguments, cancellationToken: token));

    internal static Task<IList<McpClientTool>> ListAsync(this McpClient client) =>
        Run("tools/list", async token => await client.ListToolsAsync(cancellationToken: token));

    static async Task<T> Run<T>(string name, Func<CancellationToken, Task<T>> request)
    {
        using var timeout = new CancellationTokenSource(CallTimeout);
        try
        {
            return await request(timeout.Token);
        }
        catch (OperationCanceledException error) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP request '{name}' did not finish within {CallTimeout.TotalSeconds:0} s.", error);
        }
    }
}
