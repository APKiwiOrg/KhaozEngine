# KhaozEngine.Ecs Relationships / Hierarchy — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. TDD throughout.

**Goal:** Add a parent-child entity hierarchy (`Parent` component + World-maintained children index, `SetParent`/`Detach`/`GetParent`/`Children`/`DespawnTree`, plus save/load support) to `KhaozEngine.Ecs`, released as `1.3.0`.

**Architecture:** Additive. A built-in `Parent : IComponent` holds the upward link (serializes for free). A `World.Hierarchy.cs` partial keeps a derived `parent → children` index and the mutators. `Despawn` detaches children to root; `DespawnTree` cascades. The serializer auto-includes `Parent` and rebuilds the index on load. Transform propagation stays game-side.

**Tech Stack:** C#, .NET 10, xUnit.

**Companion spec:** `docs/superpowers/specs/2026-06-08-khaozecs-relationships-design.md`.

**Paths:** Repo root `~/KhaozEngine`. Branch off `main` first (`git checkout -b ecs-relationships`).

---

## Task 1: Parent component + hierarchy API + Despawn hook

**Files:** Create `KhaozEngine.Ecs/Parent.cs`, `KhaozEngine.Ecs/World.Hierarchy.cs`; Modify `KhaozEngine.Ecs/World.cs`; Test `KhaozEngine.Tests/HierarchyTests.cs`

- [ ] **Step 1: Write the failing tests**

`KhaozEngine.Tests/HierarchyTests.cs`:
```csharp
using System;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public class HierarchyTests
{
    [Fact]
    public void SetParentLinksBothDirections()
    {
        var w = new World();
        var p = w.Spawn(); var c = w.Spawn();
        w.SetParent(c, p);
        Assert.Equal(p, w.GetParent(c));
        Assert.Equal(new[] { c }, w.Children(p).ToArray());
        Assert.Null(w.GetParent(p));
        Assert.Empty(w.Children(c));
    }

    [Fact]
    public void ReParentMovesChildBetweenParents()
    {
        var w = new World();
        var p1 = w.Spawn(); var p2 = w.Spawn(); var c = w.Spawn();
        w.SetParent(c, p1);
        w.SetParent(c, p2);
        Assert.Equal(p2, w.GetParent(c));
        Assert.Empty(w.Children(p1));
        Assert.Equal(new[] { c }, w.Children(p2).ToArray());
    }

    [Fact]
    public void SelfParentAndCyclesThrow()
    {
        var w = new World();
        var a = w.Spawn(); var b = w.Spawn();
        Assert.Throws<ArgumentException>(() => w.SetParent(a, a));
        w.SetParent(b, a);                                   // a -> b
        Assert.Throws<InvalidOperationException>(() => w.SetParent(a, b)); // would cycle
    }

    [Fact]
    public void SetParentToDeadParentThrows()
    {
        var w = new World();
        var p = w.Spawn(); var c = w.Spawn();
        w.Despawn(p);
        Assert.Throws<ArgumentException>(() => w.SetParent(c, p));
    }

    [Fact]
    public void DetachOrphansChild()
    {
        var w = new World();
        var p = w.Spawn(); var c = w.Spawn();
        w.SetParent(c, p);
        w.Detach(c);
        Assert.Null(w.GetParent(c));
        Assert.Empty(w.Children(p));
        w.Detach(c);                                         // no-op on a root
    }

    [Fact]
    public void DespawnDetachesChildrenToRootAndUnlinksFromParent()
    {
        var w = new World();
        var grand = w.Spawn(); var p = w.Spawn(); var c = w.Spawn();
        w.SetParent(p, grand);
        w.SetParent(c, p);
        w.Despawn(p);
        Assert.True(w.IsAlive(c));                           // child survives
        Assert.Null(w.GetParent(c));                         // ... as a root
        Assert.Empty(w.Children(grand));                     // p unlinked from its parent
    }

    [Fact]
    public void DespawnTreeRemovesWholeSubtree()
    {
        var w = new World();
        var root = w.Spawn(); var a = w.Spawn(); var b = w.Spawn(); var leaf = w.Spawn();
        w.SetParent(a, root);
        w.SetParent(b, root);
        w.SetParent(leaf, a);
        w.DespawnTree(root);
        Assert.False(w.IsAlive(root));
        Assert.False(w.IsAlive(a));
        Assert.False(w.IsAlive(b));
        Assert.False(w.IsAlive(leaf));
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Create the Parent component**

`KhaozEngine.Ecs/Parent.cs`:
```csharp
namespace KhaozEngine.Ecs;

/// <summary>
/// Built-in component holding an entity's parent in the hierarchy. Set and cleared via
/// <see cref="World.SetParent"/> / <see cref="World.Detach"/>, which also keep the World's
/// children index consistent. Serializes like any component (the parent reference is preserved).
/// </summary>
public struct Parent : IComponent { public Entity Value; }
```

- [ ] **Step 4: Create the hierarchy partial**

`KhaozEngine.Ecs/World.Hierarchy.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    private readonly Dictionary<int, List<Entity>> _children = new();   // parentId -> children
    private static readonly List<Entity> _noChildren = new();

    /// <summary>Makes <paramref name="child"/> a child of <paramref name="parent"/> (re-parenting if needed). Throws on self-parent, a dead parent, or a cycle.</summary>
    public void SetParent(Entity child, Entity parent)
    {
        if (!IsAlive(child)) throw new InvalidOperationException("Stale entity handle.");
        if (!IsAlive(parent)) throw new ArgumentException("Parent is not alive.", nameof(parent));
        if (child.Equals(parent)) throw new ArgumentException("An entity cannot be its own parent.");

        for (Entity? a = parent; a is Entity cur; a = GetParent(cur))
            if (cur.Equals(child))
                throw new InvalidOperationException("SetParent would create a cycle.");

        DetachFromParentIndex(child);                       // leave the old parent's list (if any)
        Set(child, new Parent { Value = parent });          // overwrite the link (change-tracked)
        AddToParentIndex(parent, child);
    }

    /// <summary>Detaches <paramref name="child"/> from its parent, making it a root. No-op if already a root.</summary>
    public void Detach(Entity child)
    {
        if (!Has<Parent>(child)) return;
        DetachFromParentIndex(child);
        Remove<Parent>(child);
    }

    /// <summary>The entity's parent, or null if it is a root.</summary>
    public Entity? GetParent(Entity child) => TryGet<Parent>(child, out Parent p) ? p.Value : null;

    /// <summary>The entity's children (empty if none). The returned list is a live, read-only view.</summary>
    public IReadOnlyList<Entity> Children(Entity parent) =>
        _children.TryGetValue(parent.Id, out List<Entity>? list) ? list : _noChildren;

    /// <summary>Despawns <paramref name="e"/> and all of its descendants (post-order).</summary>
    public void DespawnTree(Entity e)
    {
        if (!IsAlive(e)) return;
        var order = new List<Entity>();
        CollectPostOrder(e, order);
        foreach (Entity node in order) Despawn(node);
    }

    private void CollectPostOrder(Entity e, List<Entity> order)
    {
        if (_children.TryGetValue(e.Id, out List<Entity>? kids))
            foreach (Entity c in kids.ToArray())            // copy: Despawn mutates the index
                CollectPostOrder(c, order);
        order.Add(e);
    }

    private void AddToParentIndex(Entity parent, Entity child)
    {
        if (!_children.TryGetValue(parent.Id, out List<Entity>? list))
        {
            list = new List<Entity>();
            _children[parent.Id] = list;
        }
        list.Add(child);
    }

    private void DetachFromParentIndex(Entity child)
    {
        if (TryGet<Parent>(child, out Parent p) && _children.TryGetValue(p.Value.Id, out List<Entity>? list))
            list.Remove(child);
    }

    /// <summary>Called by <see cref="Despawn"/>: orphan e's children (clear their Parent) and unlink e from its own parent.</summary>
    internal void DetachHierarchyOnDespawn(Entity e)
    {
        DetachFromParentIndex(e);                            // remove e from its parent's children list
        if (_children.TryGetValue(e.Id, out List<Entity>? kids))
        {
            foreach (Entity c in kids.ToArray())
                if (Has<Parent>(c)) Remove<Parent>(c);       // orphan to root
            _children.Remove(e.Id);
        }
    }

    /// <summary>Rebuilds the children index from Parent components (after a load).</summary>
    internal void RebuildHierarchyIndex()
    {
        _children.Clear();
        foreach (Entity child in Query().With<Parent>().Entities())
            AddToParentIndex(Get<Parent>(child).Value, child);
    }
}
```

- [ ] **Step 5: Hook `Despawn` in `World.cs`**

In `Despawn`, add the hierarchy cleanup as the first line of the body (before taking `ref Record rec`):
```csharp
    public void Despawn(Entity e)
    {
        if (!IsAlive(e)) return;
        DetachHierarchyOnDespawn(e);
        ref Record rec = ref _records[e.Id];
        foreach (int tid in rec.Archetype.TypeIds)
            TrackRemoved(e, tid);
        if (rec.Archetype.SwapRemove(rec.Row, out Entity moved))
            _records[moved.Id].Row = rec.Row;
        rec.Alive = false;
        rec.Version++;
        _free.Push(e.Id);
    }
```
> `DetachHierarchyOnDespawn` runs before `ref Record rec` is taken; it does not resize `_records` (only `Spawn`/`CreateAt` do), so the ref is valid.

- [ ] **Step 6: Run to verify pass; commit**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add KhaozEngine.Ecs/Parent.cs KhaozEngine.Ecs/World.Hierarchy.cs KhaozEngine.Ecs/World.cs KhaozEngine.Tests/HierarchyTests.cs
git commit -m "ECS: parent-child hierarchy (Parent, SetParent/Detach/Children, DespawnTree)"
```

---

## Task 2: Serialization integration

**Files:** Modify `KhaozEngine.Ecs/WorldSerializer.cs`; Test `KhaozEngine.Tests/HierarchySerializationTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/HierarchySerializationTests.cs`:
```csharp
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public struct HsTag : IComponent { public int V; }

public class HierarchySerializationTests
{
    [Fact]
    public void HierarchyRoundTripsAndIndexIsRebuilt()
    {
        var w = new World();
        var root = w.Spawn(); var a = w.Spawn(); var leaf = w.Spawn();
        w.Set(root, new HsTag { V = 1 });
        w.SetParent(a, root);
        w.SetParent(leaf, a);

        var ser = new WorldSerializer(typeof(HsTag));        // Parent is auto-included
        World loaded = ser.Load(ser.Save(w));

        Assert.Equal(root, loaded.GetParent(a));             // links restored
        Assert.Equal(a, loaded.GetParent(leaf));
        Assert.Equal(new[] { a }, loaded.Children(root).ToArray());   // index rebuilt
        Assert.Equal(new[] { leaf }, loaded.Children(a).ToArray());

        loaded.DespawnTree(root);                            // rebuilt index drives the cascade
        Assert.False(loaded.IsAlive(a));
        Assert.False(loaded.IsAlive(leaf));
    }
}
```

- [ ] **Step 2: Run to verify failure** (the rebuilt-index assertions fail — `Load` doesn't rebuild yet).

- [ ] **Step 3: Auto-include `Parent` and rebuild the index on load**

In `KhaozEngine.Ecs/WorldSerializer.cs`:

(a) In the `WorldSerializer(IEnumerable<Type>, JsonSerializerOptions?)` constructor, after the loop that fills `_byName`, always register the built-in `Parent`:
```csharp
        _byName[typeof(Parent).FullName!] = typeof(Parent);
```

(b) In `Load`, after `world.RestoreAllocator(...)`, rebuild the hierarchy index:
```csharp
        world.RestoreAllocator(doc.NextId, doc.FreeIds.Select(f => (f.Id, f.Version)));
        world.RebuildHierarchyIndex();
        return world;
```

- [ ] **Step 4: Run to verify pass; commit**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add KhaozEngine.Ecs/WorldSerializer.cs KhaozEngine.Tests/HierarchySerializationTests.cs
git commit -m "ECS: serializer auto-includes Parent and rebuilds the hierarchy index on load"
```

---

## Task 3: Release 1.3.0

**Files:** Modify `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`, `CHANGELOG.md`

- [ ] **Step 1: Bump the Ecs package version** — change `<Version>1.2.0</Version>` to `<Version>1.3.0</Version>` in `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`.

- [ ] **Step 2: Changelog** — prepend under the title in `CHANGELOG.md`:
```markdown
## KhaozEngine.Ecs 1.3.0

- Add a parent-child hierarchy: built-in `Parent` component, `World.SetParent` / `Detach` /
  `GetParent` / `Children`, and `DespawnTree` (cascade) vs plain `Despawn` (detaches children to
  root). Cycle-guarded. Hierarchies serialize (the children index rebuilds on load; `Parent` is
  auto-included by `WorldSerializer`). Transform propagation stays game-side. Additive.
```

- [ ] **Step 3: Test, pack, commit**

```bash
cd ~/KhaozEngine
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj          # full suite green
dotnet pack KhaozEngine.Ecs/KhaozEngine.Ecs.csproj -c Release -o ./local-feed   # cumulative
ls local-feed/KhaozEngine.Ecs.1.3.0.nupkg
git add -A
git commit -m "Release KhaozEngine.Ecs 1.3.0 (parent-child hierarchy)"
```
> Tag `ecs-v1.3.0` and push from `main` after the branch merges (the finishing step).

---

## Self-Review

**Spec coverage:**
- `Parent` component + children index → Task 1.
- `SetParent`/`Detach`/`GetParent`/`Children`/`DespawnTree` → Task 1 (each tested).
- Cycle/self/dead-parent guards → Task 1 (`SelfParentAndCyclesThrow`, `SetParentToDeadParentThrows`).
- Despawn detaches children to root + unlinks from parent → Task 1 (`DespawnDetachesChildrenToRootAndUnlinksFromParent`).
- `DespawnTree` post-order cascade → Task 1 (`DespawnTreeRemovesWholeSubtree`).
- Serialization: `Parent` auto-included, index rebuilt on load → Task 2 (`HierarchyRoundTripsAndIndexIsRebuilt`).
- Transform propagation game-side → not implemented (correctly out of scope).
- Additive `1.3.0` release → Task 3.

**Placeholder scan:** none — every new/changed member shown in full.

**Type consistency:** `Parent { Entity Value }`; `World.SetParent/Detach/GetParent/Children/DespawnTree` + internal `AddToParentIndex`/`DetachFromParentIndex`/`DetachHierarchyOnDespawn`/`CollectPostOrder`/`RebuildHierarchyIndex`; `_children` is `Dictionary<int, List<Entity>>`. `Despawn` edit keeps its existing change-tracking + removal, adding only `DetachHierarchyOnDespawn` first. `WorldSerializer` constructor adds `typeof(Parent)`; `Load` calls `RebuildHierarchyIndex`.

---

## Execution Handoff

After all tasks green, finish the branch (merge `ecs-relationships` → `main`), tag `ecs-v1.3.0`, push so CI publishes. One deferred feature remains: system ordering/groups.
