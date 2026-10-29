# KhaozEngine.StatusEffects.Netcode

The `Server` umbrella adapter for [StatusEffects](../KhaozEngine.StatusEffects/README.md). It depends on
StatusEffects, Ecs, Replication and Sharding. Clients referencing only `Game2D` or `Game3D` add this
package explicitly to read the replicated component and register the matching codec.

The book owns stacking, refresh, periodic events and expiry. The game owns definitions, channel units,
the clock, modifier application, death and despawn. This package projects presentation state without
moving the authoritative book into ECS or applying damage, healing or movement itself.

## Registration and wire state

Register `StatusEffectsReplication.Register(registry, typeId)` with the same unused extension type id
on the server and every client. `StatusEffectsReplication.Channels` is
`ReplicationChannels.Replicate | ReplicationChannels.Migrate`, with no `Persist`. Shown effects reach
the owning client and observers in interest and follow cell handoff, but never survive persistence.

`ActiveStatusEffects.Entries` is an `ImmutableArray<StatusEffectEntry>`. A default component is empty.
Each entry carries effect id, source, stacks, start tick and end tick. No periodic anchor, magnitude,
modifier lines or hidden effects are sent. The codec bounds count to `MaxEntries` (255), requires a
nonzero id, stacks from 1 to 255 and an end tick at or after the start tick. Each publication copies
entries into a fresh immutable snapshot. No per-tick countdown updates are needed.

## Projection and ownership gaps

`StatusEffectProjection.Publish` drains changed actors and merges them with deferred actors from
previous calls. It publishes current shown entries in ascending actor order, including an empty
snapshot when the last shown effect ends. The projection owns the book's changed-actor drain.

`IStatusEffectProjectionTarget.Publish(long actor, in ActiveStatusEffects state)` returns a
`StatusEffectPublishResult`:

| Result | Projection response |
|---|---|
| `Published` | Remove the actor from the retry set. |
| `Deferred` | Keep the actor pending and retry its current entries next call. |
| `Gone` | Call `book.Forget(actor)` and remove it from the retry set. |

`ShardHostStatusEffectTarget` writes only to the actor's current owning entity. When ownership is
temporarily unavailable, `ShardHost.IsInHandoff` distinguishes a live frozen source from a despawn.
Deferred admission and a delayed cell link preserve the book and pending publication. After adoption,
the new owner receives current entries, including expiries during the gap. Ghosts and migrating
copies are never written by the target.

The game must `Clear` on death and despawn, or `Forget` when the entity is already gone. The `Gone`
backstop only runs for changed or pending actors. It is not a global despawn sweep. The book and
projection are single-threaded and shared across the host's cells.

## Wiring

Create matching registries before constructing a `ShardedWorldServer` and its `WorldClient`s. Reserve
the chosen id across all other component registrations:

```csharp
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.StatusEffects;
using KhaozEngine.StatusEffects.Netcode;

ReplicationRegistry CreateRegistry() => MoveProtocol.CreateRegistry(registry =>
    StatusEffectsReplication.Register(registry, ReplicationRegistry.FirstExtensionTypeId));

// server uses CreateRegistry(). catalog is the game's shared definition catalog.
var book = new StatusEffectBook(catalog);
var projection = new StatusEffectProjection(book, new ShardHostStatusEffectTarget(server.Host));
server.OnBeforeTick += _ =>
{
    book.Advance(server.ServerTick);
    // Apply validated abilities, drain events, respond to them and fold modifier totals here.
    projection.Publish();
};
```

Call `Publish` once each tick after `Advance` and gameplay mutations, even if no new actor changed,
because pending admissions still need retries. Durations use the supplied tick, `ServerTick` with
NetWorld. Draining events after the step lets handlers safely `Clear` or `Apply` before publication.
Refresh never shortens a window or moves its periodic anchor. The core README describes those rules.

For a client's `TryGetComponent<ActiveStatusEffects>` result, pass `state.Entries.AsSpan()` to
`StatusEffectBar.Build` with the shared catalog and a server tick estimate. Enable `ReceiveServerTick`
for `LatestServerTick` or `RemoteRenderTick` and wait until it is known. The game resolves icons and
localized labels. A movement speed channel folded through `SetSpeedScale` still replicates in 1/16
steps, with finer precision tracked in [#1652](https://github.com/APKiwiOrg/KhaozEngine/issues/1652).

The [consumer recipe](../docs/USING-KHAOZENGINE.md#status-effects) includes event handling, cleanup and
client bar construction. The [design rationale](../docs/design/STATUS-EFFECTS.md) explains the split.
