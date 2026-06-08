# KhaozEngine.Ecs — change detection (design)

**Date:** 2026-06-08
**Status:** Approved (pending written-spec review)
**Package:** `KhaozEngine.Ecs`, additive → version **1.2.0** (independent of the shared engine version).

## Goal

Let systems react to component changes this frame — components **added**, **removed**, or whose
value **changed** — instead of rescanning everything or hand-rolling dirty flags. Second of the
deferred ECS features being folded in (relationships and system ordering/groups follow).

## The constraint that shapes the design

The ECS exposes raw `ref T Get` and `ForEach((ref T) => …)`, so component **value** writes happen
through refs the ECS never observes. It therefore **cannot auto-detect a value change**. Structural
changes (`Add`/`Set`-when-adding/`Remove`/`Despawn`) *do* go through the World and are observable.
So: `Added`/`Removed` are automatic; `Changed` (value) is recorded explicitly by the caller via
`MarkChanged<T>(e)`.

## Mechanism: per-tick event sets on the World (no column stamps)

Detection is kept out of the hot column/archetype paths. The World holds three small collections of
"what happened since the last `AdvanceTick()`":

- `_added`: `HashSet<(Entity, int componentTypeId)>`
- `_changed`: `HashSet<(Entity, int componentTypeId)>`
- `_removed`: `Dictionary<int componentTypeId, List<Entity>>`

`AdvanceTick()` (call once per frame) increments a `ulong Tick` and **clears all three**. So
"this frame" detection needs zero per-component storage and no changes to `MoveEntity`/`SwapRemove`.

### What records into them

- `Add<T>` and `Set<T>` **when it adds** the component → `_added`.
- `Set<T>` **when it overwrites** an existing component → `_changed`.
- `MarkChanged<T>(e)` → `_changed` (the manual hook for `ref` mutations; no-op if the entity lacks `T`).
- `Remove<T>` → `_removed`. `Despawn` → `_removed` for **each** component the entity had.
- The load path (`SetByType`) is exempt — loading is not a gameplay change, and the first
  `AdvanceTick` clears the sets anyway.

## API (on `World`)

```csharp
public ulong Tick { get; }
public void  AdvanceTick();                                          // once per frame; clears the event sets
public void  MarkChanged<T>(Entity e) where T : struct, IComponent;  // record a ref-mutation as changed
public IEnumerable<Entity> Added<T>()   where T : struct, IComponent;  // gained T this tick (live)
public IEnumerable<Entity> Changed<T>() where T : struct, IComponent;  // value-changed this tick (live)
public IEnumerable<Entity> Removed<T>() where T : struct, IComponent;  // lost T this tick (may be dead)
```

- `Added<T>()` / `Changed<T>()` yield the **live** entities recorded for `T` this tick (entries for
  entities despawned later this tick are skipped). They store the full `Entity` (with version), so a
  same-tick despawn-and-recycle of an id cannot mis-report.
- `Removed<T>()` yields the entities that lost `T` this tick. These **may be dead** (a despawned
  entity counts as having lost all its components). Callers that only want survivors filter with
  `.Where(world.IsAlive)`. The component's value is gone (Removed reports the entity, not the value).
- Combining with other components is plain LINQ: `world.Changed<Health>().Where(world.Has<Position>)`.
  Integrating these as `Query().Added<T>()` filters is a clean future option, not needed now.

## Implementation surface

- New partial `World.ChangeTracking.cs`: the three sets, `Tick`, `AdvanceTick`, `MarkChanged`,
  `Added`/`Changed`/`Removed`, and internal `TrackAddedOrChanged(Entity, int, bool adding)` /
  `TrackRemoved(Entity, int)` helpers.
- `Set<T>` (in `World.Components.cs`) computes `adding = !archetype.Has(id)` before the move and calls
  the tracking helper after writing.
- `Remove<T>` calls `TrackRemoved` when it removes the component.
- `Despawn` (in `World.cs`) calls `TrackRemoved(e, tid)` for each `tid` in the entity's archetype
  before swap-removing it.

No existing behavior changes; the hooks are additive bookkeeping.

## Testing (headless)

- `Add<T>` and first-time `Set<T>` → reported by `Added<T>`, not `Changed<T>`.
- `Set<T>` overwriting an existing component, and `MarkChanged<T>`, → reported by `Changed<T>`.
- `MarkChanged<T>` on an entity lacking `T` is a no-op.
- `Remove<T>` → reported by `Removed<T>`; the entity is no longer in `Added`/`Changed` for `T`.
- `Despawn` → the entity appears in `Removed<T>` for each component it had; `Removed` can include the
  (now dead) entity, and `.Where(IsAlive)` filters it out.
- `AdvanceTick` increments `Tick` and clears all three sets (last frame's events are not reported).
- Type isolation: `Added<A>` does not report entities that only gained `B`.
- The load path (`WorldSerializer.Load` / `SetByType`) does not populate the event sets.

## Packaging

Additive → bump `KhaozEngine.Ecs` to `1.2.0`, changelog entry, pack to the local feed cumulatively;
tag `ecs-v1.2.0` and push from `main` after the branch merges (CI publishes).

## Out of scope / deferred

Automatic value-change detection (impossible through `ref` without a wrapper or pessimistic
per-access stamping — both rejected); `Query()` filter integration; per-observer "since I last ran"
ticks (only "since last `AdvanceTick`" / this frame is supported); the remaining deferred features
(relationships, system ordering/groups), each its own cycle.
