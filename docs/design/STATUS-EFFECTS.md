# Status effects

The engine owns deterministic timed effect bookkeeping. Games own the meaning of a haste,
damage-taken reduction or bleed, their balance, and every gameplay response. The pure
`KhaozEngine.StatusEffects` book and the `KhaozEngine.StatusEffects.Netcode` projection keep that
boundary explicit. API usage lives in the [core README](../../KhaozEngine.StatusEffects/README.md),
[adapter README](../../KhaozEngine.StatusEffects.Netcode/README.md) and
[consumer recipe](../USING-KHAOZENGINE.md#status-effects).

## A book keyed by actor, with an explicit projection

One single-threaded `StatusEffectBook` tracks actors by their stable `long` net ids across a host's
cells. It can also serve a simulation with no ECS or network head. Keeping authority outside ECS
makes stacking, periodic catch-up, expiry and event ordering testable without constructing a world.
Moving an entity between cells does not move or restart its authoritative effect book entry.

`StatusEffectProjection` turns shown entries into immutable `ActiveStatusEffects` snapshots. ECS
carries that read model, not the periodic anchor, event magnitude, modifier definitions or hidden
effects. The pure package has no engine runtime dependencies and comes through `Foundation`.
The adapter depends on StatusEffects, Ecs, Replication and Sharding and comes through `Server`.
The core never references server packages.

The alternatives were evaluated on a 1 to 10 scale, with a higher effort score meaning less work:

| Approach | Benefit | Cost | Fit | Testability | Layering | Effort | Total |
|---|---|---|---|---|---|---|---|---|
| Actor-keyed book plus projection | Deterministic pure core, reusable by any head, server-only state stays off the wire | Explicit teardown and a projection adapter | 9 | 9 | 9 | 8 | 35 |
| Authoritative ECS component stepped per cell | Despawn and handoff follow ECS lifetime | Core depends on ECS and server stepping, authority needs fields absent from the presentation snapshot | 7 | 6 | 5 | 6 | 24 |
| Add durations to Stats | Reuses an existing package | Changes Stats' source-fold contract, adds actor identity and timing to a float kernel, still needs replication | 4 | 7 | 4 | 7 | 22 |

The first approach is the chosen contract. `Stats` remains a float source fold. A game may map timed
modifier totals into stat sources, but neither package knows how one channel affects another.

## Stable refresh windows and periodic cadence

`PerActor` definitions have one instance per effect id. `PerSource` definitions key by effect id and
source id, allowing independent bleeds from different sources. Reapplication of a live instance
increases stacks up to `MaxStacks`, with 1 meaning refresh only. Source and event magnitude take the
latest application's values. Channel contributions remain the definition's integer `Value` multiplied by stack count.

A refresh restarts the presented window at `now` but chooses
`EndTick = max(previous EndTick, now + duration)`. A short reapplication cannot erase a longer
duration already earned. The periodic anchor remains at the original application tick, preventing
repeated refresh from indefinitely postponing the next bleed tick or changing its cadence.

`Advance` catches up every due periodic tick through the earlier of `now` and `EndTick`, then expires
the effect. A periodic tick exactly at the end fires before expiry. Actors step in ascending id order
and instances in application order. Notifications retain their due tick even when a step catches up
several periods. Determinism depends on the same calls and tick inputs, not wall-clock timing.

`Apply` settles all of its target actor's instances at `now` using that same application-order
traversal before matching an instance or checking capacity. Each instance raises due periodic ticks
through `min(now, EndTick)`, then expires at `EndTick` if due. Settlement notifications precede the
new application or refresh, so a periodic tick due at `now` uses the old stacks, source and magnitude.
An application at or after expiry opens a fresh one-stack window anchored at `now`. It cannot revive
old stacks or generate periodic events in the inactive gap. A later `Advance(now)` adds no duplicate
events. Without intervening mutation, swapping these two calls at the same tick preserves effect
entries and the event multiset. Cross-actor interleaving follows call order because `Apply` settles
only its target actor and does not change the last advanced tick.

## Events after the step

The book queues events instead of invoking gameplay callbacks while traversing its state. Games
drain after stepping and may then apply periodic damage, clear an actor that died, or apply another
effect without re-entering iteration. New notifications remain queued for a later drain. This makes
event ordering explicit and keeps damage, healing and death outside the engine kernel.

Modifier channels and units are game-owned. The engine never applies their totals to movement or
combat. Exact integer accumulation includes cancelling contributions and throws only if the final
sum cannot fit in an `int`. A book-wide `ulong` change stamp supports lazy game-side refolding without
reusing an old nonzero stamp after an actor is forgotten. Checked stamp exhaustion refuses mutation
instead of wrapping.

## Transient state that survives cell handoff

The component codec uses `Replicate | Migrate` without `Persist`. Replication gives the owning client
and observers in interest the same shown windows. Migration carries the presentation snapshot with
an entity through a cell handoff. Excluding persistence prevents effects from surviving a cell save
or server restart. The authoritative book is transient in memory too.

Missing ownership is not proof of despawn. Deferred destination admission or a delayed cell link can
leave zero owners while the frozen source survives as `Migrating`. `ShardHost.IsInHandoff` identifies
that interval. It may remain true after destination adoption until acknowledgement releases the
source, so the target first attempts `TryGetOwner` and writes only to a live owning entity.

`IStatusEffectProjectionTarget` reports `Published`, `Deferred` or `Gone`. The projection retains
deferred actors in a pending set and merges them with new changes on every `Publish`. It retries
current entries, not an old captured snapshot, so refreshes and expiries during an admission gap
reach the adopted owner correctly. Only confirmed `Gone` calls `Forget`. Ghosts and migrating
source copies are never publication targets. Publishing every tick is required for retries even
when no new book change occurred.

The game clears an actor on death and despawn. Once the entity is gone it may instead silently
forget the actor. The projection's `Gone` handling is a backstop for changed and pending actors,
not a despawn scan, so explicit cleanup remains necessary. This is the lifetime cost of keeping
authority independent of ECS. No persistence, death detection or damage rule is implied.

## Absolute ticks and client presentation

The caller supplies the tick domain. With NetWorld heads that is `ServerTick`, normally 30 Hz.
Lower-cadence gameplay converts durations and periods to server ticks. The book refuses calls
before the last advanced tick. It has no wall-clock dependency or timer thread.

Replicating absolute start and end ticks lets a client calculate remaining time from its server tick
estimate without receiving a changing counter every tick. `StatusEffectBar` computes remaining ticks
and a clamped fraction of the current window, skips unknown and expired entries and leaves UI layout,
icon lookup and localization to the game. The server and client use the same definition catalog.

With `ReceiveServerTick`, NetWorld exposes `LatestServerTick` as the latest received server sample
and `RemoteRenderTick` on the remote presentation timeline. Unknown values are -1. A game estimating
time between samples keeps its countdown in that server tick domain. Effect snapshot bytes change
on application, refresh, stacking or ending, not on periodic countdown progress.

## Naming and limits

The package is `StatusEffects`, not `Effects`. The retired `KhaozEngine.Effects` name described visual
effects. Reusing its NuGet id would give it an unrelated meaning across versions and invite confusion
with particles and other rendering packages.

Actor instance counts and stack counts are capped at 255 to match the bounded codec. Hidden effects
still consume the actor's instance cap and contribute modifiers. Permanent effects, immunities,
dispel categories, diminishing returns and exclusivity groups remain outside this contract. Games
can compose `Has` and `Remove` until a concrete reusable rule is needed.

Movement speed is also a game-owned channel mapping. Passing its folded value through `SetSpeedScale`
replicates the scale in 1/16 steps. Finer quantization is an
[open owner decision](https://github.com/APKiwiOrg/KhaozEngine/issues/1652), not an implicit part of
the status effect contract.
