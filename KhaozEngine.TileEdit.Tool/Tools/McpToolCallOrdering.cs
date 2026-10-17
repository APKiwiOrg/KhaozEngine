using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>Sequences tool calls by the order in which their requests leave the transport. The MCP SDK starts
/// every received message handler independently, so session locks protect state but do not preserve arrival
/// order. This transport decorator assigns each tool call a predecessor before the SDK can schedule it, and the
/// message filter waits for that predecessor to finish before invoking the next tool.
///
/// <para>Cancellation is settled in the same transport order. A <c>notifications/cancelled</c> read while its
/// call is still queued marks that call before the SDK schedules the notification, and the call's release checks
/// the mark under the same lock, so a call whose cancellation arrived before its predecessor finished never
/// starts. Relying on the SDK's request token alone would leave that outcome to whichever of two independent
/// continuations the thread pool ran first.</para></summary>
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
                    // A call cancelled while queued gets no response, which is what the protocol asks of a
                    // receiver that honours a cancellation.
                    if (!turn.TryStart())
                        return;
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
        readonly ToolCallQueue _queue = new();

        public override Task Completion => inner.Completion;

        public override bool TryRead(out JsonRpcMessage item)
        {
            if (!inner.TryRead(out item!))
                return false;

            if (item is JsonRpcRequest { Method: RequestMethods.ToolsCall } request)
            {
                item.Context ??= new JsonRpcMessageContext();
                item.Context.Items ??= new Dictionary<string, object?>();
                item.Context.Items.Add(TurnKey, _queue.Enqueue(request.Id));
            }
            else if (item is JsonRpcNotification { Method: NotificationMethods.CancelledNotification } notification &&
                     TryGetCancelledRequest(notification, out RequestId cancelled))
            {
                _queue.Cancel(cancelled);
            }
            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToReadAsync(cancellationToken);

        /// <summary>Reads the request id a cancellation names. A malformed notification is ignored here exactly as
        /// the SDK ignores it, since the protocol says invalid cancellations should be.</summary>
        static bool TryGetCancelledRequest(JsonRpcNotification notification, out RequestId requestId)
        {
            requestId = default;
            if (notification.Params is null)
                return false;
            try
            {
                CancelledNotificationParams? parameters = notification.Params.Deserialize<CancelledNotificationParams>(
                    McpJsonUtilities.DefaultOptions);
                if (parameters is null)
                    return false;
                requestId = parameters.RequestId;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>The chain of tool-call turns in transport order, plus the turns not yet released, keyed by request
    /// id so a cancellation can find its call. One lock covers enqueueing, cancelling and releasing, which is what
    /// makes "cancelled before release" and "released before cancel" the only two outcomes.</summary>
    sealed class ToolCallQueue
    {
        readonly object _lock = new();
        readonly Dictionary<RequestId, ToolCallTurn> _queued = new();
        Task _tail = Task.CompletedTask;

        public ToolCallTurn Enqueue(RequestId id)
        {
            lock (_lock)
            {
                var turn = new ToolCallTurn(this, id, _tail);
                _tail = turn.Completion;
                _queued[id] = turn;
                return turn;
            }
        }

        public void Cancel(RequestId id)
        {
            lock (_lock)
            {
                if (_queued.Remove(id, out ToolCallTurn? turn))
                    turn.CancelledWhileQueued = true;
            }
        }

        public bool TryStart(ToolCallTurn turn)
        {
            lock (_lock)
            {
                if (turn.CancelledWhileQueued)
                    return false;
                RemoveIfQueued(turn);
                return true;
            }
        }

        public void Forget(ToolCallTurn turn)
        {
            lock (_lock) RemoveIfQueued(turn);
        }

        // A client that reuses a request id replaces the entry, so remove only the one this turn owns.
        void RemoveIfQueued(ToolCallTurn turn)
        {
            if (_queued.TryGetValue(turn.Id, out ToolCallTurn? current) && ReferenceEquals(current, turn))
                _queued.Remove(turn.Id);
        }
    }

    sealed class ToolCallTurn
    {
        readonly ToolCallQueue _queue;
        readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ToolCallTurn(ToolCallQueue queue, RequestId id, Task predecessor)
        {
            _queue = queue;
            Id = id;
            Predecessor = predecessor;
        }

        public RequestId Id { get; }
        public Task Predecessor { get; }
        public Task Completion => _completion.Task;

        /// <summary>Guarded by the owning queue's lock.</summary>
        public bool CancelledWhileQueued { get; set; }

        /// <summary>Claims the turn for execution unless its cancellation was read first.</summary>
        public bool TryStart() => _queue.TryStart(this);

        public void Complete()
        {
            _queue.Forget(this);
            _completion.TrySetResult();
        }
    }
}
