# KhaozEngine.Ecs — archetype ECS (design)

**Date:** 2026-06-08
**Status:** Approved (pending written-spec review)
**Package:** `KhaozEngine.Ecs`, re-released at its own independent version **1.0.0** (overriding the
shared engine version; Input/Screens/UI stay on 0.2.x).

## Goal

Replace the placeholder component-composition `World` with a real, reusable **archetype ECS** that
can underpin any of the author's MonoGame games. This is a deliberate foundation investment, not a
response to a measured performance problem — Hardpoint's entity counts are modest — so the bar is
"a clean, capable, well-tested ECS worth owning," with a generous-but-bounded core and clearly
deferred advanced features.

## Background

The current `World` (lifted into `KhaozEngine.Ecs` during the engine extraction) is a
dictionary-of-dictionaries store: `Dictionary<Type, Dictionary<int, IComponent>>`, components are
mutable `class`es, `Get<T>` returns the stored instance (mutation sticks), `Query<T...>()` returns
`List<Entity>`. The Hardpoint foundation spec always intended this as a seam with the real ECS as a
later swap. That swap cannot be purely internal: a real archetype ECS stores **struct** components
in contiguous arrays, which changes `class`→`struct` and `Get` from returning an instance to
returning a `ref`. So this is a breaking rewrite of `KhaozEngine.Ecs` (major version) plus a
migration of its one consumer, Hardpoint.

## Decisions

Settled during brainstorming:

1. **Full struct-based archetype ECS** (not the seam-preserving "archetype-of-references", which is
   dominated — it gives neither data locality nor a head start on the real thing).
2. **Component constraint is `struct`** (any value type), not `unmanaged`, so future components may
   hold managed fields (texture handles, strings). Columns are managed `T[]`.
3. **`ForEach` arity cap = 8** (overloads `ForEach<T1>` … `ForEach<T1..T8>`). Filters are unlimited.
4. **Include `EntityCommandBuffer` and `Resources` in v1.**
5. **Independent version 1.0.0** for the `KhaozEngine.Ecs` package; stays in the KhaozEngine repo.
6. **Two-phase rollout:** build the ECS green and game-independent first; migrate Hardpoint second.

## Architecture

### Entity (versioned handle)

```csharp
public readonly record struct Entity(int Id, uint Version);
```
Ids are recycled through a free-list; recycling bumps the slot's version. A stale `Entity` whose
version no longer matches the live slot is detected — `IsAlive` returns false — preventing the
recycled-id aliasing hazard the bare-int `World` has.

### Components

`public interface IComponent { }` stays as a marker; component types are `struct X : IComponent`.
Generic APIs constrain `where T : struct, IComponent`. **Tag components** (zero-size structs, e.g. a
`FlowFollower` marker) are recognised and stored as archetype membership only — no column is
allocated for them.

### Archetype storage

- An **archetype** is a unique set of component types, identified by a signature (a sorted set /
  bitset of dense `ComponentType` ids assigned by a registry).
- Each archetype holds one **column** (`T[]`, grown as needed) per non-tag component type, plus a
  row→`Entity` array and a count. Component data for an entity lives at `(archetype, row)`.
- The `World` keeps an `EntityRecord` per id — `(Archetype archetype, int row, uint version, bool
  alive)` — and a free-list of recycled ids.
- **Structural changes** (`Add<T>`, `Remove<T>`, `Set<T>` when it adds a new type, `Spawn`,
  `Despawn`) move the entity to the archetype matching its new signature: allocate a row there, copy
  the shared columns over, and **swap-remove** from the old archetype (the row that backfills has its
  `EntityRecord.row` fixed up). New archetypes are created lazily and cached by signature.

### Access

```csharp
Entity Spawn();                                   // empty archetype
void   Despawn(Entity e);
bool   IsAlive(Entity e);                         // version + alive check

void   Set<T>(Entity e, T value);                 // add (structural) or overwrite
void   Add<T>(Entity e, T value);                 // structural; throws if already present
void   Remove<T>(Entity e);                       // structural
ref T  Get<T>(Entity e);                          // live ref into the column (mutation sticks)
bool   Has<T>(Entity e);
bool   TryGet<T>(Entity e, out T value);          // by value (copy)
```

`Get<T>` returning `ref T` is what preserves the existing mutation ergonomics
(`world.Get<Transform>(e).Position = ...` becomes `ref var t = ref world.Get<Transform>(e); t.Position = ...`).

### Queries + iteration

- A **`Query`** is described with `With<T>()` / `Without<T>()` (any number of each) and caches the
  set of matching archetypes, refreshed when new archetypes appear.
- Primary iteration is **`ForEach`** with ref-passing delegates, arities 1–8:
  ```csharp
  world.ForEach((Entity e, ref Transform t, ref Movement m) => { t.Position += ...; });
  world.Query().Without<Frozen>().ForEach((Entity e, ref Transform t) => { ... });
  ```
  Implemented via generated delegate types `RefAction<T1..Tn>(Entity, ref T1, …, ref Tn)` and a
  `ForEach` overload per arity that walks matching archetypes, fetches the relevant columns once per
  archetype, and calls the delegate per row with refs into those columns. The ref-arity (≤8) is
  independent of the filter count.
- A plain **entity enumeration** (`Query(...).Entities()`) is available for cases that only need the
  entities, not component refs.

### Structural-change safety: EntityCommandBuffer

Structural changes during a `ForEach` would invalidate the archetype/columns being iterated. An
**`EntityCommandBuffer`** records `CreateEntity` / `Destroy` / `Add<T>` / `Remove<T>` / `Set<T>`
commands and applies them on `Playback(world)`. `World.Update` plays back the buffer after each
system. Systems that restructure during iteration record into the buffer rather than mutating
directly; direct structural calls outside iteration remain immediate.

### Systems

`public interface ISystem { void Update(World world, float dt); }` — unchanged. `World.AddSystem`
registers; `World.Update(dt)` runs systems in registration order, then plays back the frame's command
buffer. (System groups, ordering constraints, and parallel scheduling are deferred.)

### Resources (typed singletons)

`SetResource<T>(T)` / `T GetResource<T>()` / `bool HasResource<T>()` — a typed store for world-global
state (a match context, a clock, RNG). Optional for consumers; Hardpoint may keep constructor
injection or move shared state here.

## Deferred (explicitly out of scope for 1.0.0)

Parallel/job scheduling, change detection (`Changed<T>`), entity relationships/hierarchies,
serialization / save-load, shared or chunk-level components, and native/unmanaged-memory storage.
Each is a clean future addition; none is needed for the current games.

## Packaging & versioning

`KhaozEngine.Ecs` sets its own `<Version>1.0.0</Version>` in its csproj, overriding the repo-shared
version, so the ECS versions independently of Input/Screens/UI. It keeps its own `CHANGELOG.md`
section (or a dedicated changelog) and remains dependency-free (MonoGame only, for `Vector2` et al.
used by consumers' components — the ECS core itself needs no MonoGame type). Released by the normal
ritual (pack to local-feed cumulatively, tag, CI publish).

## Testing

Headless xUnit, exhaustive (this is a foundation):

- Entity version recycling: despawn → spawn reuses the id with a bumped version; the old handle
  reports `!IsAlive`; `Get`/`Has` on a stale handle do not return live data.
- Archetype transitions: `Add`/`Remove`/`Set` move the entity and preserve the other components'
  values; tag components change the signature with no column.
- `Get` returns a live ref — mutation through it persists.
- `Despawn` swap-remove: the backfilled entity's row is corrected; its components remain correct.
- `ForEach` correctness across arities 1, 2, 3 (and a spot-check at a higher arity), with `With` /
  `Without` filters, including matching-many-take-few.
- `EntityCommandBuffer`: create/destroy/add/remove/set recorded during a `ForEach` apply on playback;
  iteration during recording is not corrupted.
- `Resources`: set/get/has, overwrite, missing-resource behavior.
- `ISystem` ordering and per-frame command-buffer playback via `World.Update`.

## Hardpoint migration (phase 2, separate plan)

- The 12 components in `Components/GameplayComponents.cs` change `sealed class`→`struct` (keep
  `: IComponent`). `Entity?`/`Entity` fields are fine in structs.
- Each system's `Query<...>()` + per-entity `Get` loop is rewritten to
  `world.ForEach((e, ref ...) => { ... })`; `Despawn`-during-iteration moves to the command buffer
  where needed (e.g. `DamageSystem`, `ProjectileSystem`).
- `EcsWorldTests` is replaced by the new ECS test suite (which lives with the ECS, in
  `KhaozEngine.Tests`); Hardpoint keeps its system/gameplay tests, adjusted for `ref`/struct access.
- Hardpoint bumps its `KhaozEngine.Ecs` reference to `1.0.0`. Its full suite must stay green.

## Out of scope

Any change to `KhaozEngine.Input` / `.Screens` / `.UI`. Hardpoint gameplay/feature work
(meta-progression etc.). The deferred ECS features listed above.
