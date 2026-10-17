using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>Runs the MCP wire tests that time a whole client and server bring-up on their own, after the parallel
/// rows of this assembly, so their hang guards never compete with synchronous CPU-bound rows for thread-pool
/// threads.</summary>
[CollectionDefinition("McpWire", DisableParallelization = true)]
public sealed class McpWireCollection { }

/// <summary>The arrival-ordering contract of the ke-tileedit MCP server: tool calls run one at a time in the order
/// their requests left the transport, a failed or cancelled call releases its successor without letting it
/// overtake an earlier call, and a call whose cancellation the server read before the call was released never
/// starts. The server here carries only the ordering probe tool, because ordering lives in the transport and
/// filter and does not depend on which verbs are registered.</summary>
[Collection("McpWire")]
public class McpToolCallOrderingTests
{
    static readonly RequestId MiddleId = new("cancelled-middle");

    [Fact]
    public async Task ToolCalls_ExecuteInTransportArrivalOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var probe = new OrderingProbe();
        await using McpHarness harness = await StartAsync(probe, gate: null, cts.Token);

        ValueTask<CallToolResult> first = harness.CallAsync("ordering_probe", cts.Token, ("position", 1));
        await probe.FirstEntered.Task.WaitAsync(cts.Token);
        ValueTask<CallToolResult> second = harness.CallAsync("ordering_probe", cts.Token, ("position", 2));

        try
        {
            await AssertNotEnteredAsync(probe.SecondEntered.Task, cts.Token);
        }
        finally
        {
            probe.ReleaseFirst.TrySetResult();
        }

        await first;
        await second;
        Assert.Equal(new[] { "start-1", "finish-1", "start-2", "finish-2" }, probe.Events);
    }

    [Fact]
    public async Task ThrowingMiddleToolCall_ReleasesTheNextArrivalWithoutOvertakingTheFirst()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var probe = new OrderingProbe();
        await using McpHarness harness = await StartAsync(probe, gate: null, cts.Token);

        ValueTask<CallToolResult> first = harness.CallAsync("ordering_probe", cts.Token, ("position", 1));
        await probe.FirstEntered.Task.WaitAsync(cts.Token);
        ValueTask<CallToolResult> second = harness.CallAsync("ordering_probe", cts.Token,
            ("position", 2), ("behavior", "throw"));
        ValueTask<CallToolResult> third = harness.CallAsync("ordering_probe", cts.Token, ("position", 3));

        try
        {
            await AssertNotEnteredAsync(probe.SecondEntered.Task, cts.Token);
            await AssertNotEnteredAsync(probe.ThirdEntered.Task, cts.Token);
        }
        finally
        {
            probe.ReleaseFirst.TrySetResult();
        }

        Assert.NotEqual(true, (await first).IsError);
        Assert.True((await second).IsError);
        Assert.NotEqual(true, (await third).IsError);
        Assert.Equal(new[]
        {
            "start-1", "finish-1", "start-2", "throw-2", "start-3", "finish-3",
        }, probe.Events);
    }

    /// <summary>The realistic path: the SDK handles the cancellation as soon as it likes. The first call is
    /// released only after the server has read the cancellation, which is the precondition the contract names.
    /// Waiting on the client's send, or on a delay, would not establish it.</summary>
    [Fact]
    public async Task CancelledMiddleToolCall_ReleasesTheNextArrivalWithoutOvertakingTheFirst()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var middleCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var probe = new OrderingProbe();
        var gate = CancellationNotificationGate.PassThrough();
        await using McpHarness harness = await StartAsync(probe, gate, cts.Token);

        ValueTask<CallToolResult> first = harness.CallAsync("ordering_probe", cts.Token, ("position", 1));
        await probe.FirstEntered.Task.WaitAsync(cts.Token);
        Task<CallToolResult> second = harness.CallWithIdAsync("ordering_probe", MiddleId, middleCts.Token,
            ("position", 2), ("behavior", "wait-for-cancel")).AsTask();
        ValueTask<CallToolResult> third = harness.CallAsync("ordering_probe", cts.Token, ("position", 3));

        try
        {
            await AssertNotEnteredAsync(probe.SecondEntered.Task, cts.Token);
            await AssertNotEnteredAsync(probe.ThirdEntered.Task, cts.Token);
            await harness.CancelAsync(MiddleId, cts.Token);
            await gate.Observed.Task.WaitAsync(cts.Token);
            middleCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);
            await AssertNotEnteredAsync(probe.ThirdEntered.Task, cts.Token);
        }
        finally
        {
            probe.ReleaseFirst.TrySetResult();
        }

        Assert.NotEqual(true, (await first).IsError);
        Assert.NotEqual(true, (await third).IsError);
        Assert.Equal(new[] { "start-1", "finish-1", "start-3", "finish-3" }, probe.Events);
    }

    /// <summary>The race the load-sensitive failure hit, made deterministic. The server has read the cancellation
    /// but the SDK has not yet cancelled the request's token when the first call finishes, because the gate holds
    /// the notification inside the SDK's pipeline. Cancellation is a transport-order fact, so the middle call must
    /// still never start.</summary>
    [Fact]
    public async Task CancellationReadBeforeTheRelease_KeepsTheMiddleCallUnstartedWhileTheSdkStillHoldsIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var middleCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var probe = new OrderingProbe();
        var gate = new CancellationNotificationGate();
        await using McpHarness harness = await StartAsync(probe, gate, cts.Token);

        ValueTask<CallToolResult> first = harness.CallAsync("ordering_probe", cts.Token, ("position", 1));
        await probe.FirstEntered.Task.WaitAsync(cts.Token);
        Task<CallToolResult> second = harness.CallWithIdAsync("ordering_probe", MiddleId, middleCts.Token,
            ("position", 2), ("behavior", "wait-for-cancel")).AsTask();
        ValueTask<CallToolResult> third = harness.CallAsync("ordering_probe", cts.Token, ("position", 3));

        try
        {
            await harness.CancelAsync(MiddleId, cts.Token);
            await gate.Observed.Task.WaitAsync(cts.Token);
        }
        finally
        {
            probe.ReleaseFirst.TrySetResult();
        }

        try
        {
            Assert.NotEqual(true, (await first).IsError);
            await Task.WhenAny(probe.SecondEntered.Task, probe.ThirdEntered.Task).WaitAsync(cts.Token);
        }
        finally
        {
            gate.Release.TrySetResult();
            middleCts.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);
        Assert.NotEqual(true, (await third).IsError);
        Assert.Equal(new[] { "start-1", "finish-1", "start-3", "finish-3" }, probe.Events);
    }

    /// <summary>A cancellation the server cannot map to a queued call must be ignored in the transport read path.
    /// Anything thrown there would end the session, so a later ordinary call proves the session still serves.</summary>
    [Fact]
    public async Task MalformedOrUnknownCancellations_LeaveTheSessionServingCalls()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var probe = new OrderingProbe();
        await using McpHarness harness = await StartAsync(probe, gate: null, cts.Token);

        JsonNode?[] cancellationParams =
        {
            new JsonObject(),
            JsonNode.Parse("""{"requestId":1.5}"""),
            JsonNode.Parse("""{"requestId":{"nested":1}}"""),
            JsonNode.Parse("""{"requestId":"never-sent"}"""),
            JsonNode.Parse("""{"requestId":987654}"""),
            null,
        };
        foreach (JsonNode? parameters in cancellationParams)
        {
            await harness.Client.SendMessageAsync(new JsonRpcNotification
            {
                Method = NotificationMethods.CancelledNotification,
                Params = parameters,
            }, cts.Token);
        }

        ValueTask<CallToolResult> first = harness.CallAsync("ordering_probe", cts.Token, ("position", 1));
        await probe.FirstEntered.Task.WaitAsync(cts.Token);
        probe.ReleaseFirst.TrySetResult();

        Assert.NotEqual(true, (await first).IsError);
        Assert.Equal(new[] { "start-1", "finish-1" }, probe.Events);
    }

    static Task<McpHarness> StartAsync(OrderingProbe probe, CancellationNotificationGate? gate,
        CancellationToken cancellationToken) =>
        McpHarness.StartAsync(server =>
        {
            server.WithTools(new OrderingProbeTools(probe));
            gate?.Install(server);
        }, cancellationToken);

    static async Task AssertNotEnteredAsync(Task entered, CancellationToken cancellationToken) =>
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await entered.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken));

    /// <summary>An incoming message filter that reports when the server has read a cancellation notification and
    /// holds it there, before the SDK cancels the named request, until <see cref="Release"/> completes. The SDK
    /// reaches its filters only for messages it has already read from the transport, so <see cref="Observed"/>
    /// is the server-side proof that the cancellation arrived.</summary>
    sealed class CancellationNotificationGate
    {
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static CancellationNotificationGate PassThrough()
        {
            var gate = new CancellationNotificationGate();
            gate.Release.TrySetResult();
            return gate;
        }

        public void Install(IMcpServerBuilder server) =>
            server.WithMessageFilters(filters => filters.AddIncomingFilter(next => async (context, cancellationToken) =>
            {
                if (context.JsonRpcMessage is JsonRpcNotification { Method: NotificationMethods.CancelledNotification })
                {
                    Observed.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }
                await next(context, cancellationToken);
            }));
    }

    sealed class OrderingProbe
    {
        readonly object _lock = new();

        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Events { get; } = new();

        public async Task RunAsync(int position, string behavior, CancellationToken cancellationToken)
        {
            Add($"start-{position}");
            if (position == 1)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                if (position == 2) SecondEntered.TrySetResult();
                if (position == 3) ThirdEntered.TrySetResult();
            }

            if (behavior == "throw")
            {
                Add($"throw-{position}");
                throw new InvalidOperationException("scripted ordering probe failure");
            }
            if (behavior == "wait-for-cancel")
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Add($"cancel-{position}");
                    throw;
                }
            }
            Add($"finish-{position}");
        }

        void Add(string value)
        {
            lock (_lock) Events.Add(value);
        }
    }

    [McpServerToolType]
    sealed class OrderingProbeTools(OrderingProbe probe)
    {
        [McpServerTool(Name = "ordering_probe"), Description("Blocks the first call so transport ordering can be observed.")]
        public Task Probe(int position, string behavior = "complete", CancellationToken cancellationToken = default) =>
            probe.RunAsync(position, behavior, cancellationToken);
    }
}
