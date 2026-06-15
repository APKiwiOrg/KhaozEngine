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
