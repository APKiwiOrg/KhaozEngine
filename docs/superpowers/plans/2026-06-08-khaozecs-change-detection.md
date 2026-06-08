# KhaozEngine.Ecs Change Detection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. TDD: failing test → see it fail → implement → see it pass → commit.

**Goal:** Add per-tick change detection (`Added<T>`/`Removed<T>` automatic, `Changed<T>` via explicit `MarkChanged`) to `KhaozEngine.Ecs`, released as `1.2.0`.

**Architecture:** Additive. A new `World.ChangeTracking.cs` partial holds three per-tick event sets, `Tick`/`AdvanceTick`, `MarkChanged`, the `Added`/`Changed`/`Removed` queries, and internal `TrackAddedOrChanged`/`TrackRemoved` helpers. `Set`/`Remove` (in `World.Components.cs`) and `Despawn` (in `World.cs`) call those helpers. No column or archetype changes; the load path (`SetByType`) is left un-hooked so loading doesn't generate events.

**Tech Stack:** C#, .NET 10, xUnit.

**Companion spec:** `docs/superpowers/specs/2026-06-08-khaozecs-change-detection-design.md`.

**Paths:** Repo root `~/KhaozEngine`. Branch off `main` first (`git checkout -b ecs-change-detection`).

---

## Task 1: Change tracking — sets, API, and the Set/Remove/Despawn hooks

**Files:** Create `KhaozEngine.Ecs/World.ChangeTracking.cs`; Modify `KhaozEngine.Ecs/World.Components.cs`, `KhaozEngine.Ecs/World.cs`; Test `KhaozEngine.Tests/ChangeDetectionTests.cs`

- [ ] **Step 1: Write the failing tests**

`KhaozEngine.Tests/ChangeDetectionTests.cs`:
```csharp
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct CdA : IComponent { public int V; }
file struct CdB : IComponent { public int V; }
file struct CdTag : IComponent { }
public struct CdSer : IComponent { public int V; }   // public, for the serializer-exempt test

public class ChangeDetectionTests
{
    [Fact]
    public void AddAndFirstSetReportAsAdded()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdA { V = 1 });                     // first set = add
        Assert.Equal(new[] { e }, w.Added<CdA>().ToArray());
        Assert.Empty(w.Changed<CdA>());
    }

    [Fact]
    public void OverwriteAndMarkChangedReportAsChanged()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdA { V = 1 });
        w.AdvanceTick();                                 // clear the add
        w.Set(e, new CdA { V = 2 });                     // overwrite => changed
        Assert.Equal(new[] { e }, w.Changed<CdA>().ToArray());
        Assert.Empty(w.Added<CdA>());

        w.AdvanceTick();
        ref var a = ref w.Get<CdA>(e); a.V = 3;          // ref mutation (invisible to the ECS)
        w.MarkChanged<CdA>(e);                            // ... so the caller marks it
        Assert.Equal(new[] { e }, w.Changed<CdA>().ToArray());
    }

    [Fact]
    public void MarkChangedOnMissingComponentIsNoOp()
    {
        var w = new World();
        w.MarkChanged<CdA>(w.Spawn());                   // entity has no CdA
        Assert.Empty(w.Changed<CdA>());
    }

    [Fact]
    public void RemoveReportsRemovedAndEntityStaysAlive()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdA { V = 1 });
        w.AdvanceTick();
        w.Remove<CdA>(e);
        Assert.Equal(new[] { e }, w.Removed<CdA>().ToArray());
        Assert.True(w.IsAlive(e));
    }

    [Fact]
    public void DespawnReportsRemovedForEachComponentAndMayBeDead()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdA { V = 1 });
        w.Set(e, new CdTag());
        w.AdvanceTick();
        w.Despawn(e);
        Assert.Contains(e, w.Removed<CdA>());
        Assert.Contains(e, w.Removed<CdTag>());
        Assert.Empty(w.Removed<CdA>().Where(w.IsAlive)); // e is dead now
    }

    [Fact]
    public void AdvanceTickClearsSetsAndBumpsTick()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdA { V = 1 });
        ulong t0 = w.Tick;
        w.AdvanceTick();
        Assert.Equal(t0 + 1, w.Tick);
        Assert.Empty(w.Added<CdA>());                    // last tick's add cleared
    }

    [Fact]
    public void TypeIsolation()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdB { V = 1 });
        Assert.Empty(w.Added<CdA>());
        Assert.Equal(new[] { e }, w.Added<CdB>().ToArray());
    }

    [Fact]
    public void LoadDoesNotPopulateEventSets()
    {
        var src = new World();
        src.Set(src.Spawn(), new CdSer { V = 5 });
        var ser = new WorldSerializer(typeof(CdSer));
        World loaded = ser.Load(ser.Save(src));
        Assert.Empty(loaded.Added<CdSer>());             // SetByType (load) is un-hooked
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` → FAIL (members missing).

- [ ] **Step 3: Create the change-tracking partial**

`KhaozEngine.Ecs/World.ChangeTracking.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    private readonly HashSet<(Entity entity, int typeId)> _added = new();
    private readonly HashSet<(Entity entity, int typeId)> _changed = new();
    private readonly Dictionary<int, List<Entity>> _removed = new();

    /// <summary>Monotonic frame counter advanced by <see cref="AdvanceTick"/>.</summary>
    public ulong Tick { get; private set; }

    /// <summary>Advances the frame tick and clears the per-tick change sets. Call once per frame.</summary>
    public void AdvanceTick()
    {
        Tick++;
        _added.Clear();
        _changed.Clear();
        _removed.Clear();
    }

    /// <summary>Records a value-mutation of component <typeparamref name="T"/> on <paramref name="e"/> (for `ref` writes the ECS can't see). No-op if the entity lacks the component.</summary>
    public void MarkChanged<T>(Entity e) where T : struct, IComponent
    {
        if (Has<T>(e)) _changed.Add((e, Reg.Id<T>()));
    }

    /// <summary>Entities that gained component <typeparamref name="T"/> this tick (live only).</summary>
    public IEnumerable<Entity> Added<T>() where T : struct, IComponent => ByType(_added, Reg.Id<T>());

    /// <summary>Entities whose component <typeparamref name="T"/> value changed this tick (live only).</summary>
    public IEnumerable<Entity> Changed<T>() where T : struct, IComponent => ByType(_changed, Reg.Id<T>());

    /// <summary>Entities that lost component <typeparamref name="T"/> this tick. May include dead (despawned) entities; filter with <c>.Where(world.IsAlive)</c> for survivors.</summary>
    public IEnumerable<Entity> Removed<T>() where T : struct, IComponent =>
        _removed.TryGetValue(Reg.Id<T>(), out List<Entity>? list) ? list : Enumerable.Empty<Entity>();

    private IEnumerable<Entity> ByType(HashSet<(Entity entity, int typeId)> set, int id)
    {
        foreach (var (entity, typeId) in set)
            if (typeId == id && IsAlive(entity))
                yield return entity;
    }

    private void TrackAddedOrChanged(Entity e, int id, bool adding) =>
        (adding ? _added : _changed).Add((e, id));

    private void TrackRemoved(Entity e, int id)
    {
        if (!_removed.TryGetValue(id, out List<Entity>? list))
        {
            list = new List<Entity>();
            _removed[id] = list;
        }
        list.Add(e);
    }
}
```

- [ ] **Step 4: Hook `Set<T>` and `Remove<T>` in `World.Components.cs`**

Replace the existing `Set<T>` with (compute `adding` before the move, track after the write):
```csharp
    public void Set<T>(Entity e, T value) where T : struct, IComponent
    {
        if (!IsAlive(e)) throw new InvalidOperationException("Stale entity handle.");
        int id = Reg.Id<T>();
        bool adding = !_records[e.Id].Archetype.Has(id);
        if (adding)
            MoveEntity(e.Id, id, add: true);
        Record r = _records[e.Id];
        if (!Reg.IsTag(id))
            ((Column<T>)r.Archetype.Columns[id]).Set(r.Row, value);
        TrackAddedOrChanged(e, id, adding);
    }
```
Replace the existing `Remove<T>` with (track when it actually removes):
```csharp
    public void Remove<T>(Entity e) where T : struct, IComponent
    {
        if (!IsAlive(e)) throw new InvalidOperationException("Stale entity handle.");
        int id = Reg.Id<T>();
        if (_records[e.Id].Archetype.Has(id))
        {
            MoveEntity(e.Id, id, add: false);
            TrackRemoved(e, id);
        }
    }
```
> Note: `Add<T>` already delegates to `Set<T>` (after its already-present guard), so it reports as `Added` automatically. `SetByType` (the load path) is **not** modified, so loading generates no events.

- [ ] **Step 5: Hook `Despawn` in `World.cs`**

Replace the existing `Despawn` with (record each component as removed before the swap-remove):
```csharp
    /// <summary>Removes an entity (no-op on a stale/dead handle). Bumps the slot version and recycles the id.</summary>
    public void Despawn(Entity e)
    {
        if (!IsAlive(e)) return;
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

- [ ] **Step 6: Run to verify pass** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. All `ChangeDetectionTests` pass plus the existing suite (76).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Ecs/World.ChangeTracking.cs KhaozEngine.Ecs/World.Components.cs KhaozEngine.Ecs/World.cs KhaozEngine.Tests/ChangeDetectionTests.cs
git commit -m "ECS: per-tick change detection (Added/Changed/Removed, AdvanceTick, MarkChanged)"
```

---

## Task 2: Release 1.2.0

**Files:** Modify `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`, `CHANGELOG.md`

- [ ] **Step 1: Bump the Ecs package version** — in `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`, change `<Version>1.1.0</Version>` to `<Version>1.2.0</Version>`.

- [ ] **Step 2: Changelog** — prepend under the title in `CHANGELOG.md`:
```markdown
## KhaozEngine.Ecs 1.2.0

- Add per-tick change detection: `World.AdvanceTick()` (call once per frame), `Added<T>()` /
  `Removed<T>()` (automatic from structural changes), `Changed<T>()` with explicit `MarkChanged<T>(e)`
  (since `ref` writes are invisible to the ECS). `Removed<T>` may include despawned entities. Additive;
  no breaking change. The load path does not generate events.
```

- [ ] **Step 3: Test, pack, commit**

```bash
cd ~/KhaozEngine
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj          # full suite green
dotnet pack KhaozEngine.Ecs/KhaozEngine.Ecs.csproj -c Release -o ./local-feed   # cumulative
ls local-feed/KhaozEngine.Ecs.1.2.0.nupkg
git add -A
git commit -m "Release KhaozEngine.Ecs 1.2.0 (change detection)"
```
> Tag `ecs-v1.2.0` and push from `main` after the branch merges (the finishing step).

---

## Self-Review

**Spec coverage:**
- `Added<T>`/`Removed<T>` automatic; `Changed<T>` via `MarkChanged` → Task 1 (each tested).
- Per-tick event sets on the World; `AdvanceTick` clears + bumps `Tick` → Task 1 (`World.ChangeTracking.cs`, tested).
- Hooks: `Set` add-vs-overwrite, `Remove`, `Despawn`-per-component; `Add` via `Set`; load un-hooked → Task 1 Steps 4-5 (tested incl. `LoadDoesNotPopulateEventSets`).
- `Removed` may be dead; `.Where(IsAlive)` filters → Task 1 (`DespawnReportsRemovedForEachComponentAndMayBeDead`).
- Additive `1.2.0` release → Task 2.

**Placeholder scan:** none — every new/changed member shown in full.

**Type consistency:** `World.Tick`/`AdvanceTick`/`MarkChanged<T>`/`Added<T>`/`Changed<T>`/`Removed<T>` and internal `TrackAddedOrChanged`/`TrackRemoved`/`ByType` consistent across the partial and the hooks. `_added`/`_changed` are `HashSet<(Entity, int)>`; `_removed` is `Dictionary<int, List<Entity>>`. `Set`/`Remove`/`Despawn` edits preserve their existing guards and behavior, adding only tracking calls.

---

## Execution Handoff

After both tasks green, finish the branch (merge `ecs-change-detection` → `main`), then tag `ecs-v1.2.0` and push so CI publishes. Two deferred features remain after this: relationships/hierarchies and system ordering/groups.
