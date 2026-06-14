# KhaozEngine.Ecs - relationships / hierarchy (design)

**Date:** 2026-06-08
**Status:** Approved (pending written-spec review)
**Package:** `KhaozEngine.Ecs`, additive → version **1.3.0** (independent of the shared engine version).

## Goal

Parent-child entity hierarchy - one parent, any number of children - for attachments (a turret on a
moving platform, a multi-part enemy) and scene-graph transforms. Third of the deferred ECS features
(system ordering/groups remains).

## Scope

Parent-child **hierarchy only** - not general typed relationships ("A targets B", "A owns C"); that
is more than these games need. The ECS provides the **links and traversal**; computing world
transforms (`parent.world × child.local`) stays **game-side** - the ECS is game-agnostic and does not
know a game's `Transform`. A game writes a small system that walks the links and propagates its own
transform.

## Storage

- A built-in component `public struct Parent : IComponent { public Entity Value; }` on the child holds
  the upward link. Because it is an ordinary component, hierarchies **serialize for free** via the
  `1.1.0` serializer (entity references are preserved).
- The World keeps a derived `Dictionary<int parentId, List<Entity>> _children` index for fast
  downward traversal and cascade ops. It is rebuilt from `Parent` components on load.

## API (on `World`)

```csharp
void          SetParent(Entity child, Entity parent);   // attaches/re-parents; throws on cycle, dead parent, or self
void          Detach(Entity child);                     // child becomes a root (Removes its Parent); no-op if already root
Entity?       GetParent(Entity child);                  // null if a root
IReadOnlyList<Entity> Children(Entity parent);          // empty if none
void          DespawnTree(Entity e);                    // despawn e and all descendants (post-order)
```

`Parent` is also publicly usable directly (e.g. `world.Has<Parent>(e)`), but `SetParent`/`Detach`
are the supported mutators - they keep the `Parent` component and the `_children` index consistent.

## Despawn semantics

- **`Despawn(e)`** (existing) **detaches e's children to root** (each surviving child loses its
  `Parent`) and **unlinks e from its own parent** (removes e from the parent's children list), then
  proceeds with the normal despawn. Children survive their parent's plain despawn.
- **`DespawnTree(e)`** collects the subtree in **post-order** (deepest first) and despawns each, so
  the whole subtree is removed.

## Validity & cycles

`SetParent(child, parent)`:
- Rejects `parent == child` and a dead `parent` (`ArgumentException` / stale-handle throw).
- Walks up from `parent` via `GetParent`; if it reaches `child`, throws `InvalidOperationException`
  (would create a cycle).
- If `child` already has a parent, detaches it from the old parent first, then attaches to the new.

## Serialization interaction

- `Parent` serializes/deserializes as a normal component; entity references survive (ids preserved on
  load), so the upward links are restored exactly.
- The `_children` index is **derived** and not serialized; `WorldSerializer.Load` calls an internal
  `world.RebuildHierarchyIndex()` after loading to repopulate it from the loaded `Parent` components.
- `WorldSerializer` **auto-includes the built-in `Parent` type** in its type table (it lives in the
  engine assembly, not the game's, so `FromAssemblyOf`/explicit game-type lists would otherwise miss
  it).

## Implementation surface

- New `Parent.cs` - the component.
- New `World.Hierarchy.cs` partial - the `_children` index, `SetParent`/`Detach`/`GetParent`/
  `Children`/`DespawnTree`, and internal `RebuildHierarchyIndex` + the unlink/detach helpers used by
  `Despawn`.
- `World.cs` `Despawn` - detach children to root and unlink from parent before the existing removal.
- `WorldSerializer` - add `typeof(Parent)` to its type table; call `RebuildHierarchyIndex` at the end
  of `Load`.

The hierarchy mutators go through the existing `Set<Parent>` / `Remove<Parent>` paths, so change
detection (`Added`/`Removed`/`Changed`) reports `Parent` like any component.

## Testing (headless)

- `SetParent` then `GetParent`/`Children` reflect the link both ways.
- Re-parenting moves the child out of the old parent's `Children` and into the new one's.
- `SetParent(child, child)` and a parenting that would form a cycle both throw; a dead parent throws.
- `Detach` makes the child a root and removes it from the parent's `Children`; `Detach` on a root is a
  no-op.
- `Despawn(parent)` leaves children alive as roots (no `Parent`) and removes the parent from its own
  parent's `Children`.
- `DespawnTree(root)` despawns the root and every descendant; nothing of the subtree remains alive.
- A multi-level hierarchy round-trips through `WorldSerializer` save/load: `GetParent`/`Children`
  match, and the rebuilt index works (e.g. `DespawnTree` after load removes the right entities).

## Packaging

Additive → bump `KhaozEngine.Ecs` to `1.3.0`, changelog entry, pack to the local feed cumulatively;
tag `ecs-v1.3.0` and push from `main` after the branch merges (CI publishes).

## Out of scope / deferred

General typed relationships; transform propagation (game-side); multi-parent / DAG structures
(strict tree only); the remaining deferred feature (system ordering/groups).
