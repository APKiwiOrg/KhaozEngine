using System;

namespace KhaozEngine.Netcode;

/// <summary>
/// The byte-transport seam: the only thing the netcode stack knows about the wire. Implementations are an
/// in-memory loopback (deterministic; headless tests + local play) or a real UDP binding
/// (KhaozEngine.Netcode.LiteNetLib). Server vs client role is decided at construction; this interface is
/// pure I/O. Single-threaded by contract: call <see cref="Poll"/> then drain with <see cref="TryDequeueEvent"/>
/// from the same thread that owns the host loop.
/// </summary>
public interface INetTransport : IDisposable
{
    /// <summary>Pumps the underlying transport, enqueueing any pending events for <see cref="TryDequeueEvent"/>.</summary>
    void Poll();

    /// <summary>Drains one queued event in arrival order. Returns false when none remain this poll.</summary>
    bool TryDequeueEvent(out NetEvent ev);

    /// <summary>Sends <paramref name="payload"/> to <paramref name="target"/> on the given reliability channel.</summary>
    void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability);

    /// <summary>Disconnects a single connection. No-op if the connection is unknown.</summary>
    void Disconnect(NetConnectionId connection);
}
