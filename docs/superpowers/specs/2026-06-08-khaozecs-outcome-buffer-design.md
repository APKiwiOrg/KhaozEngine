# KhaozEngine.Ecs - deterministic outcome/event buffer + RNG timing (design)

**Date:** 2026-06-08
**Status:** Approved (pending written-spec review)
**Package:** `KhaozEngine.Ecs`, additive → version **1.6.0** (independent of the shared engine version).

## Goal

Let systems **record** outcomes during their update and **drain** them in a fixed order afterward,
instead of mutating shared state or firing events mid-loop. This is the keystone of the determinism
work (Cycle B; Cycle A - deterministic iteration - shipped as `1.5.0`). It unblocks SpaceGame's
five-system seam refactor (ContactDamage, Pickup, PowerupEffect, Pilot, Collision) by giving a
deterministic deferred-outcome pattern those systems can mirror in `SimulationWorld`, and that the
engine ECS uses natively.

The engine owns **ordering**; the game owns its RNG and what the outcomes mean. All additive and
opt-in - Hardpoint and Nullwake ignore it.

## Pieces

### 1. Deferred actions in the command buffer

Extend `EntityCommandBuffer` with:
```csharp
public void Defer(Action<World> action);
```
It records into the **same ordered command list** as the structural ops (`Create`/`Despawn`/`Set`/
`Remove`), so on `Playback` everything runs in exact record order. Non-structural deterministic
logic - kill counters, XP grants, **loot RNG rolls** - goes inside a `Defer`. Because Cycle A made
iteration order reproducible, "record order = iteration order" is now a stable anchor, so the order of
`Defer` actions (and any RNG they draw) is deterministic.

### 2. Typed event channel (pull model)

New on `World`:
```csharp
public void Emit<T>(T evt);              // record an event (any type); appended in emission order
public IEnumerable<T> Events<T>();       // read this tick's events of type T, in emission order
```
Events are **pure data**: `Emit` appends to a per-type list; the game reads `Events<T>()` after the
tick and routes them (e.g. SpaceGame forwards to its existing `Action<T>` presentation events).
`AdvanceTick()` (the existing `1.2.0` tick) clears the event lists along with the change sets. No
mid-loop callbacks, no re-entrancy. (Events box when `T` is a struct - acceptable; events are
infrequent. A typed-list optimisation is out of scope, like zero-alloc.)

Pull, not push: the simulation becomes a pure function - `inputs → (new state + ordered event list)` -
which is the clean seam the extraction needs. A push-style `On<T>` could be layered on top later.

### 3. `DeterministicRng`

A standalone seeded RNG with a **pinned algorithm** (xorshift128+, seeded via splitmix64), so draws are
reproducible across .NET versions and platforms - unlike `System.Random`, whose algorithm changed
between .NET Framework and Core.
```csharp
public sealed class DeterministicRng
{
    public DeterministicRng(ulong seed);
    public (ulong, ulong) State { get; set; }     // for save/resume of an in-progress run
    public ulong NextULong();
    public uint  NextUInt();
    public int   Next(int maxExclusive);           // [0, max)
    public int   Next(int minInclusive, int maxExclusive);
    public float NextFloat();                       // [0, 1)
    public double NextDouble();                      // [0, 1)
}
```
Opt-in: the engine provides the type; a game owns an instance (e.g. as a `Resource`) and persists
`State` via its own save. The engine does **not** auto-serialize it. `Next(maxExclusive)` uses modulo
(negligible bias for game-sized ranges; fully deterministic).

### 4. RNG-draw-timing contract

The documented, tested rule:

> RNG is drawn inside `Defer` actions, which run at `Playback` in record order. Record order follows
> the deterministic iteration order (Cycle A). Therefore, for an identical operation sequence, the RNG
> draw sequence - and any state hash folded from the results - is identical, run-to-run and
> client-to-client.

The engine guarantees the ordering; the contract tells games where to put RNG draws so the guarantee
holds.

## Implementation surface

- `EntityCommandBuffer.cs`: add an `Op.Defer` entry + `Defer(Action<World>)`; handle it in `Playback`.
- New `World.Events.cs` partial: the per-type event store, `Emit`/`Events`; `AdvanceTick` (in
  `World.ChangeTracking.cs`) also clears it.
- New `DeterministicRng.cs`: the RNG type.
- `World.Commands` already exists and is flushed after each system, so `world.Commands.Defer(...)`
  works out of the box once `Defer` is added.

## Testing (headless)

- **Defer order:** record an interleaved sequence of `Despawn`/`Set`/`Defer` into an
  `EntityCommandBuffer`; `Playback`; assert effects occurred in exact record order (log-based).
- **Defer sees prior ops:** a `Defer` action reads state changed by an earlier command in the same
  playback.
- **Events:** `Emit` several of two types; `Events<T>()` returns each type's events in emission order;
  `AdvanceTick` clears them; unknown type returns empty.
- **DeterministicRng:** same seed ⇒ identical sequence; a **known-vector** test pins the algorithm
  (assert specific outputs for a fixed seed); `State` round-trips (save mid-sequence, restore, continue
  identically); `Next(min,max)` stays in range.
- **RNG-timing integration:** record N `Defer` "loot roll" actions (each drawing from one shared
  `DeterministicRng`) in a fixed order across two runs; assert both produce the identical draw sequence
  and identical resulting list - the end-to-end determinism guarantee.
- **No regression / opt-in:** full existing suite green; nothing changes for code that doesn't call the
  new APIs.

## Out of scope / deferred

- Zero-alloc (the `Action<World>` closure and struct-event boxing allocate - determinism-irrelevant).
- Push-style event subscriptions (`On<T>`) - layerable on pull later if wanted.
- Auto-serializing `DeterministicRng` state into `WorldSerializer` (game persists it).
- A shared `KhaozEngine.Combat` / roguelike-toolkit layer (where genuinely reusable game logic -
  damage/loot/progression - would live, *above* the game-agnostic core ECS). Noted as a possible future
  package; explicitly **not** in the core ECS.

## Packaging

Additive → bump `KhaozEngine.Ecs` to `1.6.0`, changelog entry, pack to the local feed cumulatively;
tag `ecs-v1.6.0` and push from `main` after the branch merges (CI publishes). This completes the
determinism work (Cycles A + B).
