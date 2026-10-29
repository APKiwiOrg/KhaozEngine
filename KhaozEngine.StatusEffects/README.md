# KhaozEngine.StatusEffects

Deterministic, transient timed effects keyed by an actor's `long` id. The package has no engine runtime
dependencies and arrives through `Foundation` in every umbrella. It owns duration windows, scoped
stacking, periodic notifications, expiry and integer modifier totals. It works without ECS or networking.

The game supplies definitions, the clock, channel meanings, icons and every gameplay response. The
engine never applies a modifier. The game folds `ModifierTotal` into its own damage, movement or other
formulas. A haste, a damage-taken reduction and a bleed are game content, not engine effect types.

## Definitions and instances

`StatusEffectCatalog` implements `IStatusEffectCatalog` and rejects duplicate ids. Build the same catalog
on the server and clients. A `StatusEffectDefinition` copies its modifier lines into an immutable array.
Custom catalogs must keep each id's definition the same for the lifetime of any book using them.
Each instance captures its definition, which governs scope matching, stack cap, default duration,
window rules, periodic cadence, modifiers and visibility. If a custom provider returns a different
definition object, reapplication still uses the matched instance's captured rules without throwing.

| Definition field | Contract |
|---|---|
| `Id` | A `StatusEffectId` with a nonzero `int` value. |
| `DurationTicks` | Positive duration in the caller's tick units. |
| `Scope` | `PerActor` keys by effect id, `PerSource` by effect id and source id. |
| `MaxStacks` | 1 to 255. The default 1 makes reapplication refresh only. |
| `PeriodTicks` | Zero disables periodic events. A positive value sets their cadence. |
| `Polarity` | `Beneficial` or `Harmful`, for presentation. |
| `Shown` | False keeps the instance out of the network projection, but not modifier totals. |
| `IconKey` | A game-resolved presentation key, which may be empty. |
| `Modifiers` | `StatusEffectModifier(Channel, Value)` integer contributions per stack. |

`StatusEffectBook` is single-threaded. Its default cap is 32 instances per actor, configurable from
1 to 255. Actor id 0 is invalid. `Apply` returns `Applied`, `Stacked`, `Refreshed`, `UnknownDefinition`
or `ActorFull`. Invalid arguments and an end tick outside the `long` range throw.

Reapplying a live instance adds a stack up to the cap and replaces its source and event magnitude.
It sets `StartTick = now` and `EndTick = max(previous EndTick, now + duration)`, so a refresh never
shortens an existing window. The original periodic anchor survives a refresh, keeping a bleed's cadence stable.
`Magnitude` is game-owned event data. It does not scale modifier lines and is not replicated.

## Stepping and events

Durations and ticks use whatever server tick the caller passes. With the NetWorld heads that is
`ServerTick`, normally 30 ticks per second. Convert durations to that time base even when gameplay
runs at a lower cadence. `Apply` and `Advance` reject a tick before the last `Advance`.

`Advance(now)` visits actors in ascending id order and instances in application order. It catches up
every periodic tick due through `min(now, EndTick)`, then expires the instance. A periodic tick exactly
at `EndTick` fires before expiry. The same call sequence produces the same event order.

`Apply(actor, effect, now)` first settles every instance of that actor by the same rules, before
matching an instance or checking capacity. Each instance's due periodic events precede its expiry,
and all settlement events precede the application or refresh. A tick due at `now` uses the previous
stacks, source and magnitude. Reapplying at or after `EndTick` starts a fresh one-stack window and
periodic anchor, with no periodic events in the inactive gap. A later `Advance(now)` repeats nothing.
With no intervening mutation, swapping `Apply` and `Advance` at the same tick preserves effect entries
and the event multiset. `Apply` settles only its target actor, so cross-actor event interleaving follows
call order. It does not change the last advanced tick.

Drain events after the step. The book queues notifications and never calls a gameplay handler, so
handlers may `Clear` or `Apply` safely. Their new events wait for a later drain. `DrainEvents` appends
to the supplied list and consumes the queue, so clear a reused list first. Events carry `Kind`, `Actor`,
`Effect`, `Source`, `Stacks`, `Magnitude`, `Tick` and `EndReason`. End reasons are `Expired`, `Removed`
and `Cleared`. `Remove` and `Clear` stamp events with the last advanced tick, or zero before the first
advance. Periodic and expiry events retain their due ticks.

## Modifiers and lifecycle

`ModifierTotal(actor, channel)` sums `Value * Stacks` for every matching line, including hidden effects.
It computes the exact sum including cancelling contributions and throws `OverflowException` only when
the final sum is outside the `int` range. It does not saturate or wrap. `ModifierVersion(actor)` is a
book-wide `ulong` change stamp for lazy game-side refolding, or zero when no instances remain. Stamps
never wrap and checked exhaustion throws.

`Remove(actor, effect, source: null)` ends all matching sources. A supplied source restricts removal.
`Clear(actor)` ends every instance with lifecycle events. Effects are transient and never persisted.
The game clears an actor on death and despawn. `Forget(actor)` silently drops state and its pending
change notification when the entity is already gone. Neither death nor despawn is inferred by the core.

`Has` tests for an effect. `CopyEntries` appends immutable value entries in application order, with an
optional shown-only filter. `DrainChangedActors` appends changed actor ids in ascending order and
drops empty actor records. The network projection owns that drain when one is attached to the book.

A game that maps a movement speed channel through `ShardedWorldServer.SetSpeedScale` must account for
the replicated scale's 1/16 steps. Finer precision remains an
[open question](https://github.com/APKiwiOrg/KhaozEngine/issues/1652).

## Minimal server-side use

```csharp
using System.Collections.Generic;
using KhaozEngine.StatusEffects;

const int MoveSpeedPercent = 1; // Game-defined channel and units.
var haste = new StatusEffectDefinition(new(1), durationTicks: 90,
    iconKey: "haste", modifiers: new[] { new StatusEffectModifier(MoveSpeedPercent, 25) });
var catalog = new StatusEffectCatalog(new[] { haste });
var book = new StatusEffectBook(catalog);
var events = new List<StatusEffectEvent>();
long actor = 42;
long serverTick = 100;

StatusEffectApplyResult result = book.Apply(actor, haste.Id, serverTick, source: 7);
book.Advance(++serverTick);
events.Clear();
book.DrainEvents(events);
// Handle events here. A handler can Clear or Apply without re-entering Advance.
float speedScale = 1f + book.ModifierTotal(actor, MoveSpeedPercent) / 100f;
// The game applies speedScale to its own movement formula or SetSpeedScale call.
book.Clear(actor); // On death or despawn.
```

## Client presentation

`StatusEffectEntry` carries effect id, source, stacks, start tick and end tick. The client computes
remaining time from the replicated end tick and its own server tick estimate. `StatusEffectBar.Build`
appends `StatusEffectBarItem` rows with icon key, polarity, stacks, remaining ticks and a fraction of
the current duration window clamped to [0, 1]. It skips unknown definitions and expired entries,
preserves input order and never clears the destination list. The caller selects the entries to present.

With NetWorld, enable `WorldClientConfig.ReceiveServerTick` and use `WorldClient.LatestServerTick`
as a server tick sample, or `RemoteRenderTick` for the remote presentation timeline. Both are -1 until
known. An advancing countdown between samples uses the game's estimate in that same tick domain.
No per-tick countdown update is required on the wire.

See [StatusEffects.Netcode](../KhaozEngine.StatusEffects.Netcode/README.md) for replication and handoff,
the [consumer recipe](../docs/USING-KHAOZENGINE.md#status-effects) for full wiring, and the
[design rationale](../docs/design/STATUS-EFFECTS.md) for the package split and timing decisions.
