# KhaozEngine.Ecs — World serialization (design)

**Date:** 2026-06-08
**Status:** Approved (pending written-spec review)
**Package:** `KhaozEngine.Ecs`, additive → version **1.1.0** (independent of the shared engine version).

## Goal

Save and restore a `World` (its live entities and their components) to/from JSON, so games can
persist and resume state. This is the first of the deferred ECS features being folded in (the others
— change detection, relationships, system ordering/groups — follow in their own cycles). It directly
unblocks Hardpoint sub-project #2 (roguelike meta-progression: save/resume an in-progress run).

## Scope

In scope: serializing the World's **entities + components** and the **id-allocator state**. Out of
scope: `Resources` (the game manages its own globals) and `ISystem`s (not data; the game re-adds
systems after a load). Format is **JSON via `System.Text.Json`** (built in — the ECS stays
dependency-free beyond MonoGame; human-readable saves; reflection handles arbitrary component structs).

## Save document shape

```json
{
  "formatVersion": 1,
  "nextId": 12,
  "freeIds": [3, 7],
  "entities": [
    {
      "id": 0,
      "version": 1,
      "components": {
        "Hardpoint.Core.Components.Transform": { "Position": { "X": 20, "Y": 260 } },
        "Hardpoint.Core.Components.Targeting": { "Range": 120, "Target": { "Id": 4, "Version": 1 } },
        "Hardpoint.Core.Components.FlowFollower": {}
      }
    }
  ]
}
```

- `nextId` and `freeIds` capture the World's id allocator so post-load `Spawn`s neither collide with
  loaded ids nor mis-recycle.
- Each entity records its `id`, `version`, and a map of **component full type name → component JSON**.
- **Tag components** (zero-field structs) serialize as `{}` and are re-added on load.
- An `Entity`-typed component field (e.g. `Targeting.Target`, `Projectile.Target`) serializes as
  `{ "Id", "Version" }` via the `Entity` record struct.

## Entity references survive by preserving ids

On load, each entity is recreated **at its exact id and version**, and the allocator state is
restored. Therefore an `Entity` reference stored inside a component still resolves to the correct
entity after the round-trip — **no reference-remapping pass is needed**. (The alternative, re-spawning
with fresh ids and rewriting every `Entity`-typed field, is avoided.)

## ECS additions (the serializer lives in `KhaozEngine.Ecs` and uses internals)

- **`ComponentRegistry`**: add a reverse `id → Type` lookup and a non-generic `int RegisterType(Type)`
  that assigns the dense id, detects tag-ness (zero instance fields), and builds the column factory
  via `typeof(Column<>).MakeGenericType(type)`. This lets a component known only as a `Type` (resolved
  from a name on load) be stored, and lets save map a column's id back to its `Type`.
- **`Column`**: type-erased `object GetBoxed(int row)` and `void SetBoxed(int row, object value)`;
  `Column<T>` implements them with boxing.
- **`World`** (internal load/save surface):
  - Save: enumerate all live entities, and for each, its `(Type, boxed value)` components (from its
    archetype's columns + the registry's `id → Type`). Tags contribute their `Type` with no column.
  - Load: `Entity CreateAt(int id, uint version)` (place an entity at a specific slot, growing
    `_records`, bypassing the free-list), a non-generic `SetByType(Entity, Type, object boxedValue)`
    (structural add + boxed column write, or tag membership), and restore `nextId` + `freeIds`.

## Type resolution

Full type names do not reflect across assemblies (game components live in the game assembly, not the
ECS), so the serializer is given the component types explicitly:

```csharp
public sealed class WorldSerializer
{
    public WorldSerializer(params Type[] componentTypes);   // explicit set (each must be struct : IComponent)
    public static WorldSerializer FromAssemblyOf<T>();      // scan T's assembly for struct : IComponent

    public string Save(World world);
    public World  Load(string json);
    public void   Save(World world, Stream stream);
    public World  Load(Stream stream);
}
```

The serializer builds a `fullName → Type` table from the provided types. Save writes full names; load
resolves them against the table (an unknown name on load throws a clear error naming the missing
component type). Component values are (de)serialized with `JsonSerializer.Serialize/Deserialize(value, type)`.

## Errors

- Unknown component type name on load → `InvalidOperationException` naming the type and suggesting it
  be registered with the serializer.
- A provided type that is not `struct : IComponent` → `ArgumentException` at construction.
- Malformed JSON → the `System.Text.Json` exception propagates.

## Testing (headless)

- **Round-trip fidelity:** a World with several entities across different archetypes (single- and
  multi-component), including a tag component and an `Entity`-reference component, saves and loads into
  a fresh `WorldSerializer.Load`; every entity's components and field values match, and the
  `Entity`-reference still resolves to the correct loaded entity.
- **Allocator state:** after load, `IsAlive` is correct for all loaded handles; the next `Spawn`
  returns the expected id (continues from `nextId`); a despawned-then-recycled id in `freeIds` is
  reused before fresh ids.
- **Versions:** a loaded handle with the saved version is alive; a stale handle (wrong version) is not.
- **Edge cases:** empty world round-trips to an empty world; a world with despawned entities (holes in
  the id space) round-trips with the holes preserved.
- **Errors:** unknown-type-on-load and not-a-component-construction throw as specified.

## Packaging

Additive (new `WorldSerializer` + internal registry/column/world load-save surface; no breaking
change to existing APIs) → bump `KhaozEngine.Ecs` to `1.1.0`, changelog entry, pack to the local feed
cumulatively, tag `ecs-v1.1.0`, CI publishes.

## Out of scope / deferred

`Resources`/`ISystem` serialization; binary or pluggable formats; save-format migration tooling
(beyond the `formatVersion` field); the other deferred ECS features (change detection, relationships,
system ordering/groups), each its own cycle.
