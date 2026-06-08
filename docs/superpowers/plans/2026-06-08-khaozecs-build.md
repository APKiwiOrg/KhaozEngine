# KhaozEngine.Ecs Archetype ECS — Build Plan (Plan 1 of 2)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. TDD: failing test → see it fail → implement → see it pass → commit.

**Goal:** Replace the placeholder `World` in `KhaozEngine.Ecs` with a real struct-based archetype ECS (versioned entities, archetype/column storage, `ref` access, `With`/`Without` queries, `ForEach` arities 1–8, an `EntityCommandBuffer`, and typed `Resources`), released at its own version `1.0.0`.

**Architecture:** Entities are versioned handles into a record table. Components are `struct`s; entities with the same component set share an **archetype** that stores each component type in a contiguous **column** (`T[]`). Structural changes move an entity between archetypes (copy shared columns, swap-remove from the old). Queries cache matching archetypes; `ForEach` passes `ref`s into columns. Structural changes during iteration go through an `EntityCommandBuffer`.

**Tech Stack:** C#, .NET 10, xUnit. The ECS core needs no MonoGame type (consumers' component structs may use MonoGame types, compiled in their assembly).

**Companion spec:** `docs/superpowers/specs/2026-06-08-khaozecs-archetype-design.md`.

**Paths:** Repo root `~/KhaozEngine`. All new/changed files under `KhaozEngine.Ecs/` and tests under `KhaozEngine.Tests/`.

**Scope:** This plan builds the ECS, green and game-independent. Hardpoint's migration onto it is **Plan 2** (separate). Hardpoint stays on `KhaozEngine.Ecs 0.1.1` (from the cumulative local feed) and is unaffected by this plan.

---

## File Structure (KhaozEngine.Ecs/)
- `Entity.cs` — versioned handle (rewrite).
- `IComponent.cs` — marker (unchanged).
- `ComponentRegistry.cs` — `Type`→dense id, tag detection, column factory.
- `Column.cs` — `Column` abstract + `Column<T>` typed storage.
- `Archetype.cs` — signature, columns, rows, add/swap-remove.
- `ArchetypeSignature.cs` — sorted-int-set key with equality/hash.
- `World.cs` — entities, structural ops, access, queries, systems, resources (rewrite).
- `Query.cs` — `With`/`Without` + `ForEach` arities 1–8 + `Entities()`.
- `RefActions.cs` — `RefAction<T1..Tn>` delegate types.
- `EntityCommandBuffer.cs` — deferred structural changes.
- **Delete:** `World.cs` old contents (rewritten), `KhaozEngine.Tests/EcsWorldTests.cs` (replaced).

---

## Task 1: Reset the package — versioned Entity, marker, remove old World + test

**Files:**
- Modify: `KhaozEngine.Ecs/Entity.cs`
- Keep: `KhaozEngine.Ecs/IComponent.cs`
- Delete: `KhaozEngine.Ecs/World.cs` (old), `KhaozEngine.Tests/EcsWorldTests.cs`

- [ ] **Step 1: Rewrite Entity with a version**

`KhaozEngine.Ecs/Entity.cs`:
```csharp
namespace KhaozEngine.Ecs;

/// <summary>
/// A versioned handle to an entity. <see cref="Id"/> indexes the world's record table;
/// <see cref="Version"/> distinguishes a live entity from a stale handle to a recycled id.
/// </summary>
public readonly record struct Entity(int Id, uint Version);
```

- [ ] **Step 2: Remove the old World and its test (they describe the old API)**

```bash
cd ~/KhaozEngine
git rm KhaozEngine.Ecs/World.cs KhaozEngine.Tests/EcsWorldTests.cs
```
> `IComponent.cs` stays as-is (the marker interface). The new `World.cs` is created in Task 4; the package will not build again until then — that's expected for this rewrite.

---

## Task 2: ComponentRegistry + Column storage

**Files:**
- Create: `KhaozEngine.Ecs/ComponentRegistry.cs`, `KhaozEngine.Ecs/Column.cs`
- Test: `KhaozEngine.Tests/ColumnTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/ColumnTests.cs`:
```csharp
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct Pos : IComponent { public int X; }
file struct Tag : IComponent { }

public class ColumnTests
{
    [Fact]
    public void RegistryAssignsDenseIdsAndDetectsTags()
    {
        var reg = new ComponentRegistry();
        int pos = reg.Id<Pos>();
        int tag = reg.Id<Tag>();
        Assert.Equal(pos, reg.Id<Pos>());      // stable
        Assert.NotEqual(pos, tag);
        Assert.False(reg.IsTag(pos));
        Assert.True(reg.IsTag(tag));           // no fields => tag
    }

    [Fact]
    public void ColumnStoresGetsAndSwapRemoves()
    {
        var reg = new ComponentRegistry();
        var col = (Column<Pos>)reg.CreateColumn(reg.Id<Pos>());
        col.EnsureCapacity(3);
        col.Set(0, new Pos { X = 10 });
        col.Set(1, new Pos { X = 20 });
        col.Set(2, new Pos { X = 30 });
        col.Get(1).X = 99;                     // ref mutation
        Assert.Equal(99, col.Get(1).X);
        col.SwapRemove(0, 2);                  // move last (row2) into row0
        Assert.Equal(30, col.Get(0).X);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` → FAIL (types missing).

- [ ] **Step 3: Implement Column**

`KhaozEngine.Ecs/Column.cs`:
```csharp
using System;

namespace KhaozEngine.Ecs;

/// <summary>Type-erased component storage for one component type within one archetype.</summary>
internal abstract class Column
{
    public abstract void EnsureCapacity(int capacity);
    public abstract void CopyRow(Column dest, int srcRow, int destRow);
    public abstract void SwapRemove(int row, int last);
}

/// <summary>Contiguous storage for component type <typeparamref name="T"/> (a column in an archetype).</summary>
internal sealed class Column<T> : Column where T : struct
{
    public T[] Data = new T[8];

    public ref T Get(int row) => ref Data[row];
    public void Set(int row, T value) => Data[row] = value;

    public override void EnsureCapacity(int capacity)
    {
        if (Data.Length >= capacity) return;
        int n = Data.Length;
        while (n < capacity) n *= 2;
        Array.Resize(ref Data, n);
    }

    public override void CopyRow(Column dest, int srcRow, int destRow)
    {
        var d = (Column<T>)dest;
        d.EnsureCapacity(destRow + 1);
        d.Data[destRow] = Data[srcRow];
    }

    public override void SwapRemove(int row, int last) => Data[row] = Data[last];
}
```

- [ ] **Step 4: Implement ComponentRegistry**

`KhaozEngine.Ecs/ComponentRegistry.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Reflection;

namespace KhaozEngine.Ecs;

/// <summary>Per-world registry: assigns each component type a dense id, records whether it is a
/// zero-field "tag", and can build the right <see cref="Column"/> for an id.</summary>
internal sealed class ComponentRegistry
{
    private readonly Dictionary<Type, int> _ids = new();
    private readonly List<bool> _isTag = new();
    private readonly List<Func<Column>> _factories = new();

    public int Id<T>() where T : struct, IComponent
    {
        Type t = typeof(T);
        if (_ids.TryGetValue(t, out int id)) return id;
        id = _ids.Count;
        _ids[t] = id;
        _isTag.Add(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0);
        _factories.Add(static () => new Column<T>());
        return id;
    }

    public bool IsTag(int id) => _isTag[id];
    public Column CreateColumn(int id) => _factories[id]();
}
```

- [ ] **Step 5: Run to verify pass; commit**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj   # ColumnTests pass
git add KhaozEngine.Ecs/ComponentRegistry.cs KhaozEngine.Ecs/Column.cs KhaozEngine.Tests/ColumnTests.cs KhaozEngine.Ecs/Entity.cs
git commit -m "ECS: versioned Entity, ComponentRegistry, Column storage"
```

---

## Task 3: Archetype + ArchetypeSignature

**Files:**
- Create: `KhaozEngine.Ecs/ArchetypeSignature.cs`, `KhaozEngine.Ecs/Archetype.cs`
- Test: `KhaozEngine.Tests/ArchetypeTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/ArchetypeTests.cs`:
```csharp
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct A : IComponent { public int V; }
file struct B : IComponent { public int V; }

public class ArchetypeTests
{
    [Fact]
    public void SignatureEqualityAndHash()
    {
        var s1 = new ArchetypeSignature(new[] { 1, 3, 5 });
        var s2 = new ArchetypeSignature(new[] { 1, 3, 5 });
        var s3 = new ArchetypeSignature(new[] { 1, 3 });
        Assert.Equal(s1, s2);
        Assert.Equal(s1.GetHashCode(), s2.GetHashCode());
        Assert.NotEqual(s1, s3);
    }

    [Fact]
    public void AddRowAndSwapRemoveFixesEntities()
    {
        var reg = new ComponentRegistry();
        int a = reg.Id<A>();
        var arch = new Archetype(new[] { a }, reg);
        var e0 = new Entity(0, 1);
        var e1 = new Entity(1, 1);
        int r0 = arch.AddRow(e0);
        int r1 = arch.AddRow(e1);
        ((Column<A>)arch.Columns[a]).Set(r0, new A { V = 100 });
        ((Column<A>)arch.Columns[a]).Set(r1, new A { V = 200 });

        bool moved = arch.SwapRemove(r0, out Entity backfilled);
        Assert.True(moved);
        Assert.Equal(e1, backfilled);                          // e1 moved into row 0
        Assert.Equal(1, arch.Count);
        Assert.Equal(200, ((Column<A>)arch.Columns[a]).Get(0).V);
    }

    [Fact]
    public void TagComponentHasNoColumn()
    {
        var reg = new ComponentRegistry();
        int a = reg.Id<A>();
        int marker = reg.Id<MarkerC>();
        var arch = new Archetype(new[] { a, marker }, reg);
        Assert.True(arch.Columns.ContainsKey(a));
        Assert.False(arch.Columns.ContainsKey(marker));        // tag => no column
    }
}

file struct MarkerC : IComponent { }
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement ArchetypeSignature**

`KhaozEngine.Ecs/ArchetypeSignature.cs`:
```csharp
using System;

namespace KhaozEngine.Ecs;

/// <summary>A sorted set of component-type ids identifying an archetype; value-equal by contents.</summary>
internal readonly struct ArchetypeSignature : IEquatable<ArchetypeSignature>
{
    public readonly int[] Ids;   // sorted ascending

    public ArchetypeSignature(int[] sortedIds) => Ids = sortedIds;

    public bool Equals(ArchetypeSignature other)
    {
        if (Ids.Length != other.Ids.Length) return false;
        for (int i = 0; i < Ids.Length; i++)
            if (Ids[i] != other.Ids[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is ArchetypeSignature s && Equals(s);

    public override int GetHashCode()
    {
        var h = new HashCode();
        for (int i = 0; i < Ids.Length; i++) h.Add(Ids[i]);
        return h.ToHashCode();
    }
}
```

- [ ] **Step 4: Implement Archetype**

`KhaozEngine.Ecs/Archetype.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>Stores all entities sharing one component-type set. Each non-tag type has a column;
/// entities/components are addressed by row.</summary>
internal sealed class Archetype
{
    public readonly int[] TypeIds;                          // sorted signature
    public readonly Dictionary<int, Column> Columns = new();
    public Entity[] Entities = new Entity[8];
    public int Count;

    public Archetype(int[] sortedTypeIds, ComponentRegistry reg)
    {
        TypeIds = sortedTypeIds;
        foreach (int id in sortedTypeIds)
            if (!reg.IsTag(id))
                Columns[id] = reg.CreateColumn(id);
    }

    public bool Has(int typeId) => Array.BinarySearch(TypeIds, typeId) >= 0;

    public int AddRow(Entity e)
    {
        EnsureCapacity(Count + 1);
        int row = Count++;
        Entities[row] = e;
        return row;
    }

    /// <summary>Removes <paramref name="row"/> by moving the last row into it. Returns true and the
    /// backfilled entity when a move happened (its record's row must be updated to <paramref name="row"/>).</summary>
    public bool SwapRemove(int row, out Entity moved)
    {
        int last = --Count;
        if (row != last)
        {
            moved = Entities[last];
            Entities[row] = moved;
            foreach (Column col in Columns.Values) col.SwapRemove(row, last);
            return true;
        }
        moved = default;
        return false;
    }

    private void EnsureCapacity(int cap)
    {
        if (Entities.Length < cap)
        {
            int n = Entities.Length;
            while (n < cap) n *= 2;
            Array.Resize(ref Entities, n);
        }
        foreach (Column col in Columns.Values) col.EnsureCapacity(cap);
    }
}
```

- [ ] **Step 5: Run to verify pass; commit**

```bash
git add KhaozEngine.Ecs/ArchetypeSignature.cs KhaozEngine.Ecs/Archetype.cs KhaozEngine.Tests/ArchetypeTests.cs
git commit -m "ECS: ArchetypeSignature + Archetype storage"
```

---

## Task 4: World — spawn / despawn / versioning

**Files:**
- Create: `KhaozEngine.Ecs/World.cs`
- Test: `KhaozEngine.Tests/WorldEntityTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/WorldEntityTests.cs`:
```csharp
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public class WorldEntityTests
{
    [Fact]
    public void SpawnGivesDistinctLiveEntities()
    {
        var w = new World();
        var a = w.Spawn();
        var b = w.Spawn();
        Assert.NotEqual(a.Id, b.Id);
        Assert.True(w.IsAlive(a));
        Assert.True(w.IsAlive(b));
    }

    [Fact]
    public void DespawnInvalidatesHandleAndRecyclesIdWithNewVersion()
    {
        var w = new World();
        var a = w.Spawn();
        w.Despawn(a);
        Assert.False(w.IsAlive(a));
        var b = w.Spawn();                 // reuses a.Id
        Assert.Equal(a.Id, b.Id);
        Assert.NotEqual(a.Version, b.Version);
        Assert.True(w.IsAlive(b));
        Assert.False(w.IsAlive(a));        // stale handle stays dead
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement World (entity core; structural/query/system members added in later tasks)**

`KhaozEngine.Ecs/World.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>
/// An archetype entity-component-system world. Entities are versioned handles; components are
/// structs stored in archetype columns. See docs/USING for the contract.
/// </summary>
public sealed partial class World
{
    private struct Record { public Archetype Archetype; public int Row; public uint Version; public bool Alive; }

    private Record[] _records = new Record[64];
    private int _nextId;
    private readonly Stack<int> _free = new();

    internal readonly ComponentRegistry Reg = new();
    internal readonly Dictionary<ArchetypeSignature, Archetype> Archetypes = new();
    internal int ArchetypeGen;
    private readonly Archetype _empty;

    public World()
    {
        _empty = new Archetype(Array.Empty<int>(), Reg);
        Archetypes[new ArchetypeSignature(Array.Empty<int>())] = _empty;
        ArchetypeGen++;
    }

    /// <summary>Creates a new entity with no components.</summary>
    public Entity Spawn()
    {
        int id = _free.Count > 0 ? _free.Pop() : _nextId++;
        if (id >= _records.Length) Array.Resize(ref _records, Math.Max(_records.Length * 2, id + 1));
        ref Record rec = ref _records[id];
        if (rec.Version == 0) rec.Version = 1;     // first use
        var e = new Entity(id, rec.Version);
        rec.Archetype = _empty;
        rec.Row = _empty.AddRow(e);
        rec.Alive = true;
        return e;
    }

    /// <summary>Removes an entity (no-op on a stale/dead handle). Bumps the slot version and recycles the id.</summary>
    public void Despawn(Entity e)
    {
        if (!IsAlive(e)) return;
        ref Record rec = ref _records[e.Id];
        if (rec.Archetype.SwapRemove(rec.Row, out Entity moved))
            _records[moved.Id].Row = rec.Row;
        rec.Alive = false;
        rec.Version++;
        _free.Push(e.Id);
    }

    /// <summary>True if the handle refers to a live entity (version still matches).</summary>
    public bool IsAlive(Entity e) =>
        (uint)e.Id < (uint)_records.Length && _records[e.Id].Alive && _records[e.Id].Version == e.Version;
}
```

- [ ] **Step 4: Run to verify pass; commit**

```bash
git add KhaozEngine.Ecs/World.cs KhaozEngine.Tests/WorldEntityTests.cs
git commit -m "ECS: World entity lifecycle with versioned recycling"
```

---

## Task 5: Structural ops + access (Set/Add/Remove/Get/Has/TryGet)

**Files:**
- Create: `KhaozEngine.Ecs/World.Components.cs` (partial)
- Test: `KhaozEngine.Tests/WorldComponentTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/WorldComponentTests.cs`:
```csharp
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct Position : IComponent { public int X, Y; }
file struct Velocity : IComponent { public int Dx; }
file struct Frozen : IComponent { }   // tag

public class WorldComponentTests
{
    [Fact]
    public void SetGetHasAndRefMutation()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Position { X = 1, Y = 2 });
        Assert.True(w.Has<Position>(e));
        Assert.False(w.Has<Velocity>(e));
        w.Get<Position>(e).X = 42;          // live ref
        Assert.Equal(42, w.Get<Position>(e).X);
        Assert.Equal(2, w.Get<Position>(e).Y);
    }

    [Fact]
    public void AddingASecondComponentPreservesTheFirst()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Position { X = 7, Y = 8 });
        w.Set(e, new Velocity { Dx = 5 });          // structural move to {Position,Velocity}
        Assert.Equal(7, w.Get<Position>(e).X);       // preserved across the move
        Assert.Equal(5, w.Get<Velocity>(e).Dx);
    }

    [Fact]
    public void RemoveMovesArchetypeAndDropsComponent()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Position { X = 1, Y = 1 });
        w.Set(e, new Velocity { Dx = 9 });
        w.Remove<Velocity>(e);
        Assert.False(w.Has<Velocity>(e));
        Assert.True(w.Has<Position>(e));
        Assert.Equal(1, w.Get<Position>(e).X);
    }

    [Fact]
    public void TagComponentTracksMembershipWithNoData()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Position());
        w.Set(e, new Frozen());
        Assert.True(w.Has<Frozen>(e));
        w.Remove<Frozen>(e);
        Assert.False(w.Has<Frozen>(e));
    }

    [Fact]
    public void TryGetReturnsValueOrFalse()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Position { X = 3, Y = 4 });
        Assert.True(w.TryGet<Position>(e, out var p));
        Assert.Equal(3, p.X);
        Assert.False(w.TryGet<Velocity>(e, out _));
    }

    [Fact]
    public void DespawnFromMultiEntityArchetypeKeepsOthersIntact()
    {
        var w = new World();
        var a = w.Spawn(); w.Set(a, new Position { X = 1 });
        var b = w.Spawn(); w.Set(b, new Position { X = 2 });
        var c = w.Spawn(); w.Set(c, new Position { X = 3 });
        w.Despawn(b);                                  // swap-remove inside the {Position} archetype
        Assert.True(w.IsAlive(a)); Assert.True(w.IsAlive(c));
        Assert.Equal(1, w.Get<Position>(a).X);
        Assert.Equal(3, w.Get<Position>(c).X);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement the structural + access partial**

`KhaozEngine.Ecs/World.Components.cs`:
```csharp
using System;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    /// <summary>True if the entity currently has component <typeparamref name="T"/>.</summary>
    public bool Has<T>(Entity e) where T : struct, IComponent =>
        IsAlive(e) && _records[e.Id].Archetype.Has(Reg.Id<T>());

    /// <summary>Adds or overwrites component <typeparamref name="T"/> (adding triggers an archetype move).</summary>
    public void Set<T>(Entity e, T value) where T : struct, IComponent
    {
        int id = Reg.Id<T>();
        if (!_records[e.Id].Archetype.Has(id))
            MoveEntity(e.Id, id, add: true);
        Record r = _records[e.Id];
        if (!Reg.IsTag(id))
            ((Column<T>)r.Archetype.Columns[id]).Set(r.Row, value);
    }

    /// <summary>Adds component <typeparamref name="T"/>; throws if already present.</summary>
    public void Add<T>(Entity e, T value) where T : struct, IComponent
    {
        if (_records[e.Id].Archetype.Has(Reg.Id<T>()))
            throw new InvalidOperationException($"Entity already has {typeof(T).Name}.");
        Set(e, value);
    }

    /// <summary>Removes component <typeparamref name="T"/> (no-op if absent).</summary>
    public void Remove<T>(Entity e) where T : struct, IComponent
    {
        int id = Reg.Id<T>();
        if (_records[e.Id].Archetype.Has(id))
            MoveEntity(e.Id, id, add: false);
    }

    /// <summary>Returns a live ref to component <typeparamref name="T"/>. Throws if absent or a tag.</summary>
    public ref T Get<T>(Entity e) where T : struct, IComponent
    {
        int id = Reg.Id<T>();
        Record r = _records[e.Id];
        return ref ((Column<T>)r.Archetype.Columns[id]).Get(r.Row);
    }

    /// <summary>Copies out component <typeparamref name="T"/> if present.</summary>
    public bool TryGet<T>(Entity e, out T value) where T : struct, IComponent
    {
        if (Has<T>(e)) { value = Get<T>(e); return true; }
        value = default;
        return false;
    }

    // Moves an entity to the archetype with componentTypeId added/removed: allocate a row there,
    // copy shared columns, swap-remove from the old archetype, fix the backfilled record.
    private void MoveEntity(int id, int componentTypeId, bool add)
    {
        ref Record rec = ref _records[id];
        Archetype from = rec.Archetype;
        int[] newSig = add ? AddToSignature(from.TypeIds, componentTypeId)
                           : RemoveFromSignature(from.TypeIds, componentTypeId);
        Archetype to = GetOrCreateArchetype(newSig);

        int destRow = to.AddRow(new Entity(id, rec.Version));
        foreach (var kv in from.Columns)
            if (to.Columns.TryGetValue(kv.Key, out Column destCol))
                kv.Value.CopyRow(destCol, rec.Row, destRow);

        int oldRow = rec.Row;
        if (from.SwapRemove(oldRow, out Entity moved))
            _records[moved.Id].Row = oldRow;

        rec.Archetype = to;
        rec.Row = destRow;
    }

    private Archetype GetOrCreateArchetype(int[] sortedSig)
    {
        var key = new ArchetypeSignature(sortedSig);
        if (!Archetypes.TryGetValue(key, out Archetype a))
        {
            a = new Archetype(sortedSig, Reg);
            Archetypes[key] = a;
            ArchetypeGen++;
        }
        return a;
    }

    private static int[] AddToSignature(int[] sig, int id)
    {
        var r = new int[sig.Length + 1];
        int i = 0;
        while (i < sig.Length && sig[i] < id) { r[i] = sig[i]; i++; }
        r[i] = id;
        for (int j = i; j < sig.Length; j++) r[j + 1] = sig[j];
        return r;
    }

    private static int[] RemoveFromSignature(int[] sig, int id)
    {
        var r = new int[sig.Length - 1];
        int k = 0;
        foreach (int x in sig) if (x != id) r[k++] = x;
        return r;
    }
}
```

- [ ] **Step 4: Run to verify pass; commit**

```bash
git add KhaozEngine.Ecs/World.Components.cs KhaozEngine.Tests/WorldComponentTests.cs
git commit -m "ECS: structural ops + ref access with archetype moves"
```

---

## Task 6: Queries + ForEach (arities 1–8)

**Files:**
- Create: `KhaozEngine.Ecs/RefActions.cs`, `KhaozEngine.Ecs/Query.cs`, `KhaozEngine.Ecs/World.Query.cs`
- Test: `KhaozEngine.Tests/QueryTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/QueryTests.cs`:
```csharp
using System.Collections.Generic;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct P : IComponent { public int X; }
file struct V : IComponent { public int Dx; }
file struct Stunned : IComponent { }   // tag filter

public class QueryTests
{
    [Fact]
    public void ForEachSingleComponentMutatesInPlace()
    {
        var w = new World();
        for (int i = 0; i < 3; i++) { var e = w.Spawn(); w.Set(e, new P { X = i }); }
        w.ForEach((Entity e, ref P p) => p.X += 10);
        var xs = new List<int>();
        w.ForEach((Entity e, ref P p) => xs.Add(p.X));
        xs.Sort();
        Assert.Equal(new[] { 10, 11, 12 }, xs);
    }

    [Fact]
    public void ForEachTwoComponentsOnlyVisitsEntitiesWithBoth()
    {
        var w = new World();
        var a = w.Spawn(); w.Set(a, new P { X = 1 }); w.Set(a, new V { Dx = 5 });
        var b = w.Spawn(); w.Set(b, new P { X = 2 });                    // no V
        int visited = 0;
        w.ForEach((Entity e, ref P p, ref V v) => { p.X += v.Dx; visited++; });
        Assert.Equal(1, visited);
        Assert.Equal(6, w.Get<P>(a).X);
        Assert.Equal(2, w.Get<P>(b).X);
    }

    [Fact]
    public void WithoutFilterExcludesTaggedEntities()
    {
        var w = new World();
        var a = w.Spawn(); w.Set(a, new P { X = 1 });
        var b = w.Spawn(); w.Set(b, new P { X = 2 }); w.Set(b, new Stunned());
        int seen = 0;
        w.Query().Without<Stunned>().ForEach((Entity e, ref P p) => seen++);
        Assert.Equal(1, seen);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement RefActions, Query, and World.Query**

`KhaozEngine.Ecs/RefActions.cs` — delegate types, arities 1–8:
```csharp
namespace KhaozEngine.Ecs;

public delegate void RefAction<T1>(Entity e, ref T1 c1);
public delegate void RefAction<T1, T2>(Entity e, ref T1 c1, ref T2 c2);
public delegate void RefAction<T1, T2, T3>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3);
public delegate void RefAction<T1, T2, T3, T4>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4);
public delegate void RefAction<T1, T2, T3, T4, T5>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5);
public delegate void RefAction<T1, T2, T3, T4, T5, T6>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6);
public delegate void RefAction<T1, T2, T3, T4, T5, T6, T7>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7);
public delegate void RefAction<T1, T2, T3, T4, T5, T6, T7, T8>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6, ref T7 c7, ref T8 c8);
```

`KhaozEngine.Ecs/Query.cs` — filters + matching-archetype cache + `ForEach` arities 1–8 + `Entities()`:
```csharp
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>A reusable query: With/Without filters over archetypes, iterated by ForEach (ref) or Entities().</summary>
public sealed class Query
{
    private readonly World _world;
    private readonly List<int> _with = new();
    private readonly List<int> _without = new();
    private readonly List<Archetype> _matched = new();
    private int _gen = -1;

    internal Query(World world) => _world = world;

    public Query With<T>() where T : struct, IComponent { _with.Add(_world.Reg.Id<T>()); return this; }
    public Query Without<T>() where T : struct, IComponent { _without.Add(_world.Reg.Id<T>()); return this; }

    private void Refresh()
    {
        if (_gen == _world.ArchetypeGen) return;
        _gen = _world.ArchetypeGen;
        _matched.Clear();
        foreach (Archetype a in _world.Archetypes.Values)
        {
            bool ok = true;
            foreach (int w in _with) if (!a.Has(w)) { ok = false; break; }
            if (ok) foreach (int wo in _without) if (a.Has(wo)) { ok = false; break; }
            if (ok) _matched.Add(a);
        }
    }

    /// <summary>Yields each matching entity (no component refs).</summary>
    public IEnumerable<Entity> Entities()
    {
        Refresh();
        foreach (Archetype a in _matched)
            for (int r = 0; r < a.Count; r++)
                yield return a.Entities[r];
    }

    public void ForEach<T1>(RefAction<T1> action) where T1 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            int n = a.Count;
            for (int r = 0; r < n; r++) action(a.Entities[r], ref c1.Data[r]);
        }
    }

    public void ForEach<T1, T2>(RefAction<T1, T2> action)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        int id1 = _world.Reg.Id<T1>(), id2 = _world.Reg.Id<T2>();
        Refresh();
        foreach (Archetype a in _matched)
        {
            if (!a.Has(id1) || !a.Has(id2)) continue;
            var c1 = (Column<T1>)a.Columns[id1];
            var c2 = (Column<T2>)a.Columns[id2];
            int n = a.Count;
            for (int r = 0; r < n; r++) action(a.Entities[r], ref c1.Data[r], ref c2.Data[r]);
        }
    }

    // Arities 3-8 follow the identical pattern: one extra `int idK = _world.Reg.Id<TK>();`, one extra
    // `if (!a.Has(idK)) continue;` guard, one extra `var cK = (Column<TK>)a.Columns[idK];`, and one
    // extra `ref cK.Data[r]` argument. Generate ForEach<T1,T2,T3> ... ForEach<T1..T8> exactly so.
}
```

> **Implementer note:** write out all of `ForEach<T1,T2,T3>` through `ForEach<T1..T8>` in `Query.cs` by mechanically extending the two shown above (one more id/guard/column/ref-arg each). They are not optional or abbreviated in the final file.

`KhaozEngine.Ecs/World.Query.cs` — no-filter convenience that delegates to a fresh `Query` (filters add their own):
```csharp
namespace KhaozEngine.Ecs;

public sealed partial class World
{
    /// <summary>Starts a filtered query.</summary>
    public Query Query() => new(this);

    public void ForEach<T1>(RefAction<T1> a) where T1 : struct, IComponent => new Query(this).ForEach(a);
    public void ForEach<T1, T2>(RefAction<T1, T2> a)
        where T1 : struct, IComponent where T2 : struct, IComponent => new Query(this).ForEach(a);
    // Arities 3-8: same one-line delegation, one per arity. Write all of them out.
}
```

> **Implementer note:** likewise write all `World.ForEach<...>` arities 1–8 (each a one-line delegation to `new Query(this).ForEach(a)`).

- [ ] **Step 4: Run to verify pass; commit**

```bash
git add KhaozEngine.Ecs/RefActions.cs KhaozEngine.Ecs/Query.cs KhaozEngine.Ecs/World.Query.cs KhaozEngine.Tests/QueryTests.cs
git commit -m "ECS: queries (With/Without) + ForEach arities 1-8"
```

---

## Task 7: EntityCommandBuffer

**Files:**
- Create: `KhaozEngine.Ecs/EntityCommandBuffer.cs`
- Test: `KhaozEngine.Tests/CommandBufferTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/CommandBufferTests.cs`:
```csharp
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct Hp : IComponent { public int V; }
file struct Dead : IComponent { }

public class CommandBufferTests
{
    [Fact]
    public void StructuralChangesDuringIterationApplyOnPlayback()
    {
        var w = new World();
        for (int i = 0; i < 3; i++) { var e = w.Spawn(); w.Set(e, new Hp { V = i }); }   // 0,1,2

        var ecb = new EntityCommandBuffer();
        w.ForEach((Entity e, ref Hp h) => { if (h.V == 0) ecb.Despawn(e); else ecb.Set(e, new Dead()); });
        // nothing changed yet (deferred)
        int before = 0; w.ForEach((Entity e, ref Hp h) => before++);
        Assert.Equal(3, before);

        ecb.Playback(w);
        int hpCount = 0; w.ForEach((Entity e, ref Hp h) => hpCount++);
        Assert.Equal(2, hpCount);                        // the V==0 entity despawned
        int deadCount = 0; w.Query().With<Dead>().ForEach((Entity e, ref Hp h) => deadCount++);
        Assert.Equal(2, deadCount);
    }

    [Fact]
    public void CreatedEntitiesGetTheirComponentsOnPlayback()
    {
        var w = new World();
        var ecb = new EntityCommandBuffer();
        var tmp = ecb.Create();
        ecb.Set(tmp, new Hp { V = 7 });
        ecb.Playback(w);
        int seen = 0; int val = 0;
        w.ForEach((Entity e, ref Hp h) => { seen++; val = h.V; });
        Assert.Equal(1, seen);
        Assert.Equal(7, val);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement EntityCommandBuffer**

`KhaozEngine.Ecs/EntityCommandBuffer.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>Records structural changes (create/despawn/set/remove) made during iteration and applies
/// them at a safe point via <see cref="Playback"/>. Created entities use negative placeholder ids
/// that resolve to real entities on playback.</summary>
public sealed class EntityCommandBuffer
{
    private enum Op { Create, Despawn, Set, Remove }

    private readonly List<(Op op, Entity target, int placeholder, Action<World, Entity>? apply)> _cmds = new();
    private int _nextPlaceholder = -1;

    /// <summary>Records creation of a new entity; the returned handle is a placeholder usable in later Set calls.</summary>
    public Entity Create()
    {
        var ph = new Entity(_nextPlaceholder--, 0);
        _cmds.Add((Op.Create, ph, ph.Id, null));
        return ph;
    }

    public void Despawn(Entity e) => _cmds.Add((Op.Despawn, e, 0, null));

    public void Set<T>(Entity e, T value) where T : struct, IComponent =>
        _cmds.Add((Op.Set, e, 0, (w, target) => w.Set(target, value)));

    public void Remove<T>(Entity e) where T : struct, IComponent =>
        _cmds.Add((Op.Remove, e, 0, (w, target) => w.Remove<T>(target)));

    /// <summary>Applies all recorded commands in order, then clears the buffer.</summary>
    public void Playback(World world)
    {
        var resolved = new Dictionary<int, Entity>();   // placeholder id -> real entity
        foreach (var c in _cmds)
        {
            Entity target = Resolve(c.target, resolved);
            switch (c.op)
            {
                case Op.Create: resolved[c.placeholder] = world.Spawn(); break;
                case Op.Despawn: world.Despawn(target); break;
                case Op.Set: c.apply!(world, target); break;
                case Op.Remove: c.apply!(world, target); break;
            }
        }
        _cmds.Clear();
    }

    private static Entity Resolve(Entity e, Dictionary<int, Entity> resolved) =>
        e.Id < 0 && resolved.TryGetValue(e.Id, out Entity real) ? real : e;
}
```

- [ ] **Step 4: Run to verify pass; commit**

```bash
git add KhaozEngine.Ecs/EntityCommandBuffer.cs KhaozEngine.Tests/CommandBufferTests.cs
git commit -m "ECS: EntityCommandBuffer (deferred structural changes)"
```

---

## Task 8: Resources + Systems

**Files:**
- Create: `KhaozEngine.Ecs/World.Systems.cs` (partial)
- Test: `KhaozEngine.Tests/WorldSystemsTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/WorldSystemsTests.cs`:
```csharp
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct Counter : IComponent { public int N; }
file sealed class Clock { public float Total; }

file sealed class TickSystem : ISystem
{
    public void Update(World w, float dt) => w.ForEach((Entity e, ref Counter c) => c.N++);
}

public class WorldSystemsTests
{
    [Fact]
    public void ResourcesSetGetHas()
    {
        var w = new World();
        Assert.False(w.HasResource<Clock>());
        w.SetResource(new Clock { Total = 1.5f });
        Assert.True(w.HasResource<Clock>());
        Assert.Equal(1.5f, w.GetResource<Clock>().Total);
    }

    [Fact]
    public void SystemsRunInOrderOnUpdate()
    {
        var w = new World();
        var e = w.Spawn(); w.Set(e, new Counter { N = 0 });
        w.AddSystem(new TickSystem());
        w.Update(1f);
        w.Update(1f);
        Assert.Equal(2, w.Get<Counter>(e).N);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement systems + resources partial**

`KhaozEngine.Ecs/World.Systems.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>A unit of per-frame logic. Run in registration order by <see cref="World.Update"/>.</summary>
public interface ISystem
{
    void Update(World world, float dt);
}

public sealed partial class World
{
    private readonly List<ISystem> _systems = new();
    private readonly Dictionary<Type, object> _resources = new();

    /// <summary>Registers a system. Systems run in registration order each <see cref="Update"/>.</summary>
    public void AddSystem(ISystem system) => _systems.Add(system);

    /// <summary>Runs every system in order.</summary>
    public void Update(float dt)
    {
        for (int i = 0; i < _systems.Count; i++)
            _systems[i].Update(this, dt);
    }

    /// <summary>Stores a world-global singleton of type <typeparamref name="T"/>.</summary>
    public void SetResource<T>(T value) where T : class => _resources[typeof(T)] = value;

    /// <summary>Gets the world-global singleton of type <typeparamref name="T"/>. Throws if unset.</summary>
    public T GetResource<T>() where T : class => (T)_resources[typeof(T)];

    /// <summary>True if a resource of type <typeparamref name="T"/> has been set.</summary>
    public bool HasResource<T>() where T : class => _resources.ContainsKey(typeof(T));
}
```

- [ ] **Step 4: Run to verify pass; commit**

```bash
git add KhaozEngine.Ecs/World.Systems.cs KhaozEngine.Tests/WorldSystemsTests.cs
git commit -m "ECS: systems (ordered Update) + typed resources"
```

---

## Task 9: Independent version 1.0.0 + release

**Files:**
- Modify: `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj` (own version), `CHANGELOG.md`

- [ ] **Step 1: Give the Ecs package its own version**

In `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`, inside the `<PropertyGroup>`, add:
```xml
    <Version>1.0.0</Version>
```
This overrides the repo-shared version for this package only.

- [ ] **Step 2: Changelog**

Prepend a section to `CHANGELOG.md` (note the independent line):
```markdown
## KhaozEngine.Ecs 1.0.0

- Rewrite as a struct-based archetype ECS: versioned `Entity`, archetype/column storage, `ref`
  `Get<T>`, `With`/`Without` queries, `ForEach` arities 1-8, `EntityCommandBuffer`, typed `Resources`.
- Breaking vs 0.1.x: components are now `struct : IComponent`; `Get<T>` returns `ref T`; the
  `List<Entity> Query<T>()` overloads are replaced by `ForEach`. Versioned independently of the
  other KhaozEngine packages (which stay on 0.2.x).
```

- [ ] **Step 3: Full suite green, pack, tag, push**

```bash
cd ~/KhaozEngine
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj          # all ECS + input/screen/UI tests green
dotnet pack KhaozEngine.Ecs/KhaozEngine.Ecs.csproj -c Release -o ./local-feed   # cumulative
ls local-feed/KhaozEngine.Ecs.1.0.0.nupkg
git add -A
git commit -m "Release KhaozEngine.Ecs 1.0.0 (archetype ECS)"
git tag ecs-v1.0.0
git push origin main
git push origin ecs-v1.0.0
```
> Use a package-scoped tag (`ecs-v1.0.0`) so it does not collide with the engine's `v0.2.x` tags. (If the CI publish workflow only triggers on `v*`, add `ecs-v*` to its tag filter, or publish this package manually with `dotnet nuget push`.)

- [ ] **Step 4: Confirm the whole library still builds + tests green**

Run: `dotnet build -c Release && dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: green. Input/Screens/UI are untouched and still 0.2.0.

---

## Self-Review

**Spec coverage:**
- Versioned `Entity` + recycling/stale detection → Tasks 1, 4.
- `struct, IComponent`; tag components no-column → Tasks 2, 3, 5.
- Archetype/column storage, structural moves, swap-remove fixups → Tasks 3, 5.
- `ref Get<T>` / Set / Add / Remove / Has / TryGet → Task 5.
- `With`/`Without` queries + `ForEach` arities 1–8 + `Entities()` → Task 6.
- `EntityCommandBuffer` (create/despawn/set/remove + placeholder resolution) → Task 7.
- `Resources` + `ISystem`/`AddSystem`/`Update` → Task 8.
- Independent `1.0.0`, changelog (per the engine's release rule) → Task 9.
- Deferred features (scheduling, change detection, relationships, serialization, native memory) → absent by design.

**Placeholder scan:** the only deliberately-templated code is `ForEach` arities 3–8 (Task 6), specified by an exact mechanical rule and flagged "write them all out" — not vague. Everything else is complete.

**Type consistency:** `Entity(int Id, uint Version)`; `where T : struct, IComponent` on every component API; `Column<T>.Data`/`Get`/`Set` used by `World`, `Archetype`, `Query`; `ComponentRegistry.Id<T>()`/`IsTag`/`CreateColumn`; `Archetype.Has`/`AddRow`/`SwapRemove(out Entity)`; `World` partial across `World.cs`/`World.Components.cs`/`World.Query.cs`/`World.Systems.cs` sharing `_records`/`Reg`/`Archetypes`/`ArchetypeGen`. `RefAction<...>` matches `ForEach<...>` arities. `EntityCommandBuffer.Create/Despawn/Set/Remove/Playback`. `World.SetResource/GetResource/HasResource`, `AddSystem/Update`.

---

## Execution Handoff

This is Plan 1 of 2. After the ECS is green and `1.0.0` is packed, **Plan 2** migrates Hardpoint onto it (components `class`→`struct`, systems' `Query+Get` loops → `ForEach`/`ref`, despawns-during-iteration → `EntityCommandBuffer`, bump to `1.0.0`, keep its suite green).
```
