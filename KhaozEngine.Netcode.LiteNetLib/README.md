# KhaozEngine.Netcode.LiteNetLib

LiteNetLib transport binding for the channel-split kernel: send position/transient state on an
unreliable Sequenced channel and reliable events on a ReliableOrdered channel, so a lost reliable
packet never head-of-line-blocks position updates.

The split contract itself - `IChannelSplittable<T>` and the `NetChannelReliability` enum - lives in
the transport-free **`KhaozEngine.Netcode`** package, so your batch DTO can implement it without
referencing LiteNetLib. This package adds `ChannelSplitter`, which maps to LiteNetLib's
`DeliveryMethod` and drives the send.

```csharp
using KhaozEngine.Netcode;              // IChannelSplittable<T>, NetChannelReliability
using KhaozEngine.Netcode.LiteNetLib;   // ChannelSplitter
```

Implement `IChannelSplittable<T>` on your entity-update batch DTO, then let `ChannelSplitter` drive it:

```csharp
readonly record struct EntityBatch(/* ...fields... */) : IChannelSplittable<EntityBatch>
{
    public bool HasUnreliableContent => /* any position/transient field set */;
    public bool HasReliableContent   => /* any event field set */;
    public EntityBatch ExtractUnreliable() => /* copy with event fields nulled */;
    public EntityBatch ExtractReliable()   => /* copy with position fields nulled */;
}

ChannelSplitter.Send(batch, (part, delivery) => netManager.SendToAll(Serialize(part), delivery));
// -> unreliable part on DeliveryMethod.Sequenced, reliable part on DeliveryMethod.ReliableOrdered
```

`ChannelSplitter.ToDeliveryMethod(NetChannelReliability)` exposes the mapping if you send by hand.

## LiteNetLibServerTransport / LiteNetLibClientTransport

The package's actual `INetTransport` implementation: a real UDP binding over LiteNetLib, for the
`KhaozEngine.Netcode` session layer (`NetServer`/`NetClient`) or anything else coded against
`INetTransport`. Each peer is surfaced as a `NetConnectionId` (`peer.Id + 1`, so a valid id is always
positive), and each holds its own bounded raw-event inbox (`BoundedEventQueue<NetEvent>`, drop-oldest
under an undrained flood, counted in `DroppedEventCount`) the same way the transport-free package's own
primitives are bounded. Single-threaded by the `INetTransport` contract: call `Poll()` then drain with
`TryDequeueEvent` from the host-loop thread.

```csharp
using KhaozEngine.Netcode;              // NetServer, NetClient, INetTransport
using KhaozEngine.Netcode.LiteNetLib;   // LiteNetLibServerTransport, LiteNetLibClientTransport

// Server: listens on a UDP port, accepts a peer whose connection key matches.
var serverTransport = new LiteNetLibServerTransport(port: 7777, connectionKey: "khaoz");
var server = new NetServer(serverTransport, maxPlayers: 16, authenticator);

// Client: connects on construction.
var clientTransport = new LiteNetLibClientTransport(host: "127.0.0.1", port: 7777, connectionKey: "khaoz");
var client = new NetClient(clientTransport);

// Host loop, each tick:
serverTransport.Poll();
while (serverTransport.TryDequeueEvent(out NetEvent ev)) { /* handle Connected/Disconnected/Data */ }
```

Both constructors take an optional `maxQueuedEvents` (default `BoundedEventQueue<NetEvent>.DefaultCapacity`)
to size the inbox cap, and `connectionKey` defaults to `"khaoz"`. `Send`/`Disconnect` implement the
`INetTransport` seam directly. A second `Disconnect(NetConnectionId, ReadOnlySpan<byte> reason)` overload
carries a reject reason on LiteNetLib's own disconnect handshake, so it survives even when a
separately-sent reliable frame would be lost to the teardown.

`LiteNetLibClientTransport` additionally turns on `NetManager.EnableStatistics` and exposes `Stats`
(`NetTransportStats`: `Connected`, `RttMs`, `PacketLoss`, cumulative `BytesReceivedTotal`/`BytesSentTotal`)
for the server peer, read by `NetClient.TransportStats` and, in turn, `KhaozEngine.NetWorld`'s
`WorldClient.NetStats` for a connection-health overlay.
