# KhaozEngine.Netcode.Abstractions

Zero-dependency channel-split contract for KhaozEngine netcode. BCL only: no MonoGame, no LiteNetLib,
no UDP transport. Reference this package (and nothing else) when a batch DTO lives in a MonoGame-free,
transport-agnostic project, e.g. a contracts assembly shared with a web/leaderboard server.

## What's here

- `IChannelSplittable<TSelf>` - a batch declares its unreliable (position/transient, latest-wins)
  vs reliable (spawns/destroys/events, must-arrive-ordered) content and extracts each sub-batch.
- `NetChannelReliability` (`UnreliableSequenced` / `ReliableOrdered`) - names the two channels.

```csharp
using KhaozEngine.Netcode;   // namespace is stable; only the assembly is separate

readonly record struct EntityBatch(/* ...fields... */) : IChannelSplittable<EntityBatch>
{
    public bool HasUnreliableContent => /* any position/transient field set */;
    public bool HasReliableContent   => /* any event field set */;
    public EntityBatch ExtractUnreliable() => /* copy with event fields nulled */;
    public EntityBatch ExtractReliable()   => /* copy with position fields nulled */;
}
```

## Namespace vs assembly

The namespace is `KhaozEngine.Netcode` even though the assembly is `KhaozEngine.Netcode.Abstractions`.
That is deliberate: `KhaozEngine.Netcode` type-forwards both types here
(`[assembly: TypeForwardedTo(...)]`), so any consumer that already references `KhaozEngine.Netcode`
keeps binding `IChannelSplittable<T>` and `NetChannelReliability` with no source change.

## Sending

This package has no transport. The LiteNetLib `DeliveryMethod` mapping and the `ChannelSplitter.Send`
orchestration live in **`KhaozEngine.Netcode.LiteNetLib`**, so adding the split only pulls in a UDP
transport on the sending side, not in the DTO project.
