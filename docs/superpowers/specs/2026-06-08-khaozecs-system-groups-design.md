# KhaozEngine.Ecs — system ordering / groups (design)

**Date:** 2026-06-08
**Status:** Approved (pending written-spec review)
**Package:** `KhaozEngine.Ecs`, additive → version **1.4.0** (independent of the shared engine version).

## Goal

Organise systems into named groups with a definable run order, and let a game run a single group on
demand (e.g. a fixed-timestep simulation group several times per frame, presentation once). Fourth and
last of the deferred ECS features.

## Scope

**Named groups only** — not a before/after constraint graph with topological sort. Within a group,
systems run in **registration order**; groups run in a **definable order**. Registration-order-within-a-group
already gives deterministic ordering for these games; constraint graphs (and their cycle-detection
failure modes) are more than they need.

## Current behavior (baseline)

`AddSystem(ISystem)` appends to one flat list; `Update(dt)` runs it in registration order, flushing the
`Commands` buffer after each system. This becomes the single `"default"` group.

## Storage

```csharp
private const string DefaultGroup = "default";
private readonly Dictionary<string, List<ISystem>> _groups = new();   // group name -> systems (registration order)
private readonly List<string> _groupOrder = new();                    // group run order
```

## API (on `World`)

```csharp
void AddSystem(ISystem system, string group = "default");   // existing AddSystem(sys) calls keep working
void SetGroupOrder(params string[] groups);                 // define the run order
void Update(float dt);                                       // run all groups in order
void UpdateGroup(string group, float dt);                   // run one group; throws on unknown name
IReadOnlyList<string> SystemGroups { get; }                 // current run order (introspection)
```

## Behavior

- **`AddSystem(sys, group)`** creates the group on first use (appending it to `_groupOrder`) and adds
  the system to it. `AddSystem(sys)` targets `"default"`.
- **`SetGroupOrder(a, b, c)`** ensures each listed group exists, then sets `_groupOrder` to: the listed
  groups in the given order (de-duplicated), followed by **any existing group not listed**, preserving
  its current relative order. A group is never silently dropped from the run.
- **`Update(dt)`** runs each group in `_groupOrder`; **`UpdateGroup(name, dt)`** runs just that group's
  systems. Both run a group's systems in registration order and **flush `Commands` after each system**
  (unchanged from today). `UpdateGroup` on an unknown group throws `ArgumentException`.
- **Backward-compatible:** with no group ever named, everything is in `"default"` and `Update` behaves
  exactly as before. `Commands`, `Resources`, and `ISystem` are unchanged.

## Implementation surface

Rewrite the system storage + `Update` in `World.Systems.cs` to the grouped model, and add
`SetGroupOrder`, `UpdateGroup`, `SystemGroups`, plus private `RunGroup` and `GetOrCreateGroup` helpers.
No other files change. The resource methods stay as-is.

## Errors

- `UpdateGroup` with a name that was never created → `ArgumentException` (catches typos).
- Everything else is total (no other failure modes; empty groups/orders just run nothing).

## Testing (headless)

- `AddSystem` into two groups + `SetGroupOrder(b, a)` → systems run in group order `b` then `a`, and in
  registration order within each (assert via an output list).
- `SetGroupOrder` preserves an unlisted group (it still runs, after the listed ones).
- `UpdateGroup(name, dt)` runs only that group; calling it twice runs it twice (fixed-timestep shape).
- `UpdateGroup("nope", dt)` throws `ArgumentException`.
- Backward compat: `AddSystem(sys)` (no group) + `Update` runs in registration order, and a system that
  records into `Commands` has it flushed after it runs (existing `WorldSystemsTests` behavior holds).
- `SystemGroups` reflects the run order.

## Packaging

Additive (the new `AddSystem` optional parameter is source-compatible; consumers recompile against the
vendored package) → bump `KhaozEngine.Ecs` to `1.4.0`, changelog entry, pack to the local feed
cumulatively; tag `ecs-v1.4.0` and push from `main` after the branch merges (CI publishes).

## Out of scope / deferred

Before/after ordering constraints and topological sort; group enable/disable toggles; per-group
fixed-timestep accumulators (the game owns its loop and calls `UpdateGroup` as needed). This is the last
of the four deferred ECS features; the SpaceGame lockstep-determinism model remains separately parked.
