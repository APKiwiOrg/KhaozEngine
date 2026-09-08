using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>Sequences tool calls by the order in which their requests leave the transport. The MCP SDK starts
/// every received message handler independently, so session locks protect state but do not preserve arrival
/// order. This transport decorator assigns each tool call a predecessor before the SDK can schedule it, and the
/// message filter waits for that predecessor to finish before invoking the next tool.</summary>
internal static class McpToolCallOrdering
{
    const string TurnKey = "KhaozEngine.TileEdit.ToolCallTurn";

    /// <summary>Decorates the transport already registered on <paramref name="builder"/> and adds the matching
    /// incoming filter. Call this after selecting the stdio or stream transport.</summary>
    public static IMcpServerBuilder WithArrivalOrderedToolCalls(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ServiceDescriptor registration = builder.Services.LastOrDefault(
            d => d.ServiceType == typeof(ITransport))
            ?? throw new InvalidOperationException("Configure an MCP transport before tool-call ordering.");
        builder.Services.Remove(registration);
        builder.Services.AddSingleton<ITransport>(services =>
            new ArrivalOrderedTransport(CreateTransport(registration, services)));

        builder.WithMessageFilters(filters => filters.AddIncomingFilter(next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is JsonRpcRequest { Method: RequestMethods.ToolsCall } &&
                context.Items.TryGetValue(TurnKey, out object? value) && value is ToolCallTurn turn)
            {
                await turn.Predecessor.ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await next(context, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    turn.Complete();
                }
                return;
            }

            await next(context, cancellationToken).ConfigureAwait(false);
        }));
        return builder;
    }

    static ITransport CreateTransport(ServiceDescriptor registration, IServiceProvider services)
    {
        if (registration.ImplementationInstance is ITransport instance)
            return instance;
        if (registration.ImplementationFactory is not null)
            return (ITransport)registration.ImplementationFactory(services);
        if (registration.ImplementationType is not null)
            return (ITransport)ActivatorUtilities.CreateInstance(services, registration.ImplementationType);
        throw new InvalidOperationException("The MCP transport registration has no implementation.");
    }

    sealed class ArrivalOrderedTransport : ITransport
    {
        readonly ITransport _inner;

        public ArrivalOrderedTransport(ITransport inner)
        {
            _inner = inner;
            MessageReader = new ArrivalOrderedReader(inner.MessageReader);
        }

        public string? SessionId => _inner.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader { get; }

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            _inner.SendMessageAsync(message, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    sealed class ArrivalOrderedReader(ChannelReader<JsonRpcMessage> inner) : ChannelReader<JsonRpcMessage>
    {
        readonly object _lock = new();
        Task _tail = Task.CompletedTask;

        public override Task Completion => inner.Completion;

        public override bool TryRead(out JsonRpcMessage item)
        {
            if (!inner.TryRead(out item!))
                return false;

            if (item is JsonRpcRequest { Method: RequestMethods.ToolsCall })
            {
                ToolCallTurn turn;
                lock (_lock)
                {
                    turn = new ToolCallTurn(_tail);
                    _tail = turn.Completion;
                }
                item.Context ??= new JsonRpcMessageContext();
                item.Context.Items ??= new Dictionary<string, object?>();
                item.Context.Items.Add(TurnKey, turn);
            }
            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToReadAsync(cancellationToken);
    }

    sealed class ToolCallTurn
    {
        readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ToolCallTurn(Task predecessor) => Predecessor = predecessor;

        public Task Predecessor { get; }
        public Task Completion => _completion.Task;
        public void Complete() => _completion.TrySetResult();
    }
}
