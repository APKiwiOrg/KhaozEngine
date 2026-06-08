# KhaozEngine.Ecs World Serialization — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. TDD: failing test → see it fail → implement → see it pass → commit.

**Goal:** Add JSON save/load of a `World` (entities + components + id-allocator state) to `KhaozEngine.Ecs`, released as `1.1.0`.

**Architecture:** Additive. New type-erased access (`Column.GetBoxed/SetBoxed`, registry `id↔Type` + non-generic `RegisterType`), an internal World load/save surface (`CreateAt`, `SetByType`, allocator save/restore), and a public `WorldSerializer` using `System.Text.Json`. Entities are recreated at their exact id+version on load so `Entity`-typed component fields survive without remapping. Free (despawned) id slots save their version too, so a post-load recycle stays collision-safe.

**Tech Stack:** C#, .NET 10, `System.Text.Json` (built in), xUnit. The ECS stays dependency-free beyond MonoGame.

**Companion spec:** `docs/superpowers/specs/2026-06-08-khaozecs-serialization-design.md`.

**Paths:** Repo root `~/KhaozEngine`. Branch off `main` first (`git checkout -b ecs-serialization`).

---

## Task 1: Type-erased access — Column boxing + registry id↔Type / RegisterType

**Files:** Modify `KhaozEngine.Ecs/Column.cs`, `KhaozEngine.Ecs/ComponentRegistry.cs`; Test `KhaozEngine.Tests/RegistryReflectionTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/RegistryReflectionTests.cs`:
```csharp
using System;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct SerPos : IComponent { public int X; }
file struct SerTag : IComponent { }

public class RegistryReflectionTests
{
    [Fact]
    public void RegisterTypeMatchesGenericId()
    {
        var reg = new ComponentRegistry();
        int gen = reg.Id<SerPos>();
        int viaType = reg.RegisterType(typeof(SerPos));
        Assert.Equal(gen, viaType);                       // same type => same id
        Assert.Equal(typeof(SerPos), reg.TypeOf(gen));    // reverse lookup
        Assert.False(reg.IsTag(gen));
        Assert.True(reg.IsTag(reg.RegisterType(typeof(SerTag))));
    }

    [Fact]
    public void RegisterTypeRejectsNonComponentStructs()
    {
        var reg = new ComponentRegistry();
        Assert.Throws<ArgumentException>(() => reg.RegisterType(typeof(int)));
    }

    [Fact]
    public void ColumnBoxedRoundTrips()
    {
        var reg = new ComponentRegistry();
        var col = (Column<SerPos>)reg.CreateColumn(reg.RegisterType(typeof(SerPos)));
        col.EnsureCapacity(1);
        col.SetBoxed(0, new SerPos { X = 42 });
        Assert.Equal(42, ((SerPos)col.GetBoxed(0)).X);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` → FAIL (members missing).

- [ ] **Step 3: Add boxing to Column**

In `KhaozEngine.Ecs/Column.cs`, add two abstract members to `Column` and implement them in `Column<T>`:
```csharp
internal abstract class Column
{
    public abstract void EnsureCapacity(int capacity);
    public abstract void CopyRow(Column dest, int srcRow, int destRow);
    public abstract void SwapRemove(int row, int last);
    public abstract object GetBoxed(int row);
    public abstract void SetBoxed(int row, object value);
}
```
```csharp
    // in Column<T>:
    public override object GetBoxed(int row) => Data[row];
    public override void SetBoxed(int row, object value)
    {
        EnsureCapacity(row + 1);
        Data[row] = (T)value;
    }
```

- [ ] **Step 4: Add id↔Type + RegisterType to ComponentRegistry**

In `KhaozEngine.Ecs/ComponentRegistry.cs`: add a `_types` list, populate it in `Id<T>`, and add `TypeOf` + a non-generic `RegisterType`:
```csharp
using System;
using System.Collections.Generic;
using System.Reflection;

namespace KhaozEngine.Ecs;

internal sealed class ComponentRegistry
{
    private readonly Dictionary<Type, int> _ids = new();
    private readonly List<Type> _types = new();
    private readonly List<bool> _isTag = new();
    private readonly List<Func<Column>> _factories = new();

    public int Id<T>() where T : struct, IComponent
    {
        Type t = typeof(T);
        if (_ids.TryGetValue(t, out int id)) return id;
        id = _ids.Count;
        _ids[t] = id;
        _types.Add(t);
        _isTag.Add(IsTagType(t));
        _factories.Add(static () => new Column<T>());
        return id;
    }

    /// <summary>Non-generic registration (used by load/serialization). Returns the existing id if already registered.</summary>
    public int RegisterType(Type t)
    {
        if (_ids.TryGetValue(t, out int id)) return id;
        if (!t.IsValueType || !typeof(IComponent).IsAssignableFrom(t))
            throw new ArgumentException($"{t.FullName} is not a struct implementing IComponent.");
        id = _ids.Count;
        _ids[t] = id;
        _types.Add(t);
        _isTag.Add(IsTagType(t));
        Type columnType = typeof(Column<>).MakeGenericType(t);
        _factories.Add(() => (Column)Activator.CreateInstance(columnType)!);
        return id;
    }

    public Type TypeOf(int id) => _types[id];
    public bool IsTag(int id) => _isTag[id];
    public Column CreateColumn(int id) => _factories[id]();

    private static bool IsTagType(Type t) =>
        t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0;
}
```

- [ ] **Step 5: Run to verify pass; commit**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add KhaozEngine.Ecs/Column.cs KhaozEngine.Ecs/ComponentRegistry.cs KhaozEngine.Tests/RegistryReflectionTests.cs
git commit -m "ECS: type-erased Column boxing + registry id<->Type + RegisterType"
```

---

## Task 2: World load/save surface

**Files:** Create `KhaozEngine.Ecs/World.Serialization.cs` (partial); Test `KhaozEngine.Tests/WorldLoadSurfaceTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/WorldLoadSurfaceTests.cs`:
```csharp
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct LBPos : IComponent { public int X; }
file struct LBTag : IComponent { }

public class WorldLoadSurfaceTests
{
    [Fact]
    public void CreateAtPlacesEntityWithExactIdAndVersion()
    {
        var w = new World();
        var e = w.CreateAt(5, 3);
        Assert.Equal(5, e.Id);
        Assert.Equal(3u, e.Version);
        Assert.True(w.IsAlive(e));
        w.SetByType(e, typeof(LBPos), new LBPos { X = 9 });
        Assert.Equal(9, w.Get<LBPos>(e).X);
        w.SetByType(e, typeof(LBTag), new LBTag());
        Assert.True(w.Has<LBTag>(e));
    }

    [Fact]
    public void RestoreAllocatorControlsNextSpawnAndRecycling()
    {
        var w = new World();
        w.RestoreAllocator(10, new (int id, uint version)[] { (4, 7) });
        var reused = w.Spawn();
        Assert.Equal(4, reused.Id);                 // recycled id popped before fresh
        Assert.Equal(7u, reused.Version);           // its saved version kept (recycle does not reset)
        var fresh = w.Spawn();
        Assert.Equal(10, fresh.Id);                 // then continues from nextId
    }

    [Fact]
    public void SaveAccessorsReportAllocatorState()
    {
        var w = new World();
        var a = w.Spawn(); var b = w.Spawn(); w.Spawn();
        w.Despawn(b);                               // frees b.Id with a bumped version
        Assert.Equal(3, w.SaveNextId);
        var free = w.SaveFreeSlots.ToList();
        Assert.Single(free);
        Assert.Equal(b.Id, free[0].id);
        Assert.True(free[0].version > b.Version);   // version was bumped on despawn
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement the partial**

`KhaozEngine.Ecs/World.Serialization.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    // --- save accessors (read-only) ---
    internal int SaveNextId => _nextId;
    internal IEnumerable<(int id, uint version)> SaveFreeSlots =>
        _free.Select(id => (id, _records[id].Version));
    internal IEnumerable<Archetype> SaveArchetypes => Archetypes.Values;
    internal ComponentRegistry Registry => Reg;

    // --- load surface ---

    /// <summary>Places an entity at a specific id and version (bypasses the free-list). For load.</summary>
    internal Entity CreateAt(int id, uint version)
    {
        EnsureRecord(id);
        var e = new Entity(id, version);
        ref Record rec = ref _records[id];
        rec.Archetype = _empty;
        rec.Row = _empty.AddRow(e);
        rec.Version = version;
        rec.Alive = true;
        return e;
    }

    /// <summary>Adds (or overwrites) a component identified by runtime <see cref="Type"/>. For load.</summary>
    internal void SetByType(Entity e, Type type, object value)
    {
        int id = Reg.RegisterType(type);
        if (!_records[e.Id].Archetype.Has(id))
            MoveEntity(e.Id, id, add: true);
        if (!Reg.IsTag(id))
        {
            Record r = _records[e.Id];
            r.Archetype.Columns[id].SetBoxed(r.Row, value);
        }
    }

    /// <summary>Restores the id allocator: next fresh id, and the recycled free slots (id + version).</summary>
    internal void RestoreAllocator(int nextId, IEnumerable<(int id, uint version)> freeSlots)
    {
        _nextId = nextId;
        _free.Clear();
        // Push so the first-listed slot is popped first (Spawn pops the top).
        var ordered = freeSlots.ToArray();
        for (int i = ordered.Length - 1; i >= 0; i--)
        {
            var (id, version) = ordered[i];
            EnsureRecord(id);
            _records[id].Version = version;
            _records[id].Alive = false;
            _free.Push(id);
        }
    }

    private void EnsureRecord(int id)
    {
        if (id >= _records.Length)
            Array.Resize(ref _records, Math.Max(_records.Length * 2, id + 1));
    }
}
```

- [ ] **Step 4: Run to verify pass; commit**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add KhaozEngine.Ecs/World.Serialization.cs KhaozEngine.Tests/WorldLoadSurfaceTests.cs
git commit -m "ECS: World load/save surface (CreateAt, SetByType, allocator save/restore)"
```

---

## Task 3: WorldSerializer (JSON)

**Files:** Create `KhaozEngine.Ecs/WorldSerializer.cs`; Test `KhaozEngine.Tests/WorldSerializerTests.cs`

- [ ] **Step 1: Write the failing tests**

`KhaozEngine.Tests/WorldSerializerTests.cs`:
```csharp
using System;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public struct SrTransform : IComponent { public float X; public float Y; }
public struct SrHealth : IComponent { public int Hp; }
public struct SrTarget : IComponent { public Entity Of; }
public struct SrMarker : IComponent { }

public class WorldSerializerTests
{
    private static WorldSerializer Ser() =>
        new(typeof(SrTransform), typeof(SrHealth), typeof(SrTarget), typeof(SrMarker));

    [Fact]
    public void RoundTripsComponentsTagsAndEntityReferences()
    {
        var w = new World();
        var a = w.Spawn();
        w.Set(a, new SrTransform { X = 20, Y = 260 });
        w.Set(a, new SrHealth { Hp = 12 });
        w.Set(a, new SrMarker());
        var b = w.Spawn();
        w.Set(b, new SrTransform { X = 1, Y = 2 });
        w.Set(b, new SrTarget { Of = a });           // entity reference

        string json = Ser().Save(w);
        World loaded = Ser().Load(json);

        Assert.True(loaded.IsAlive(a));               // same id+version
        Assert.True(loaded.IsAlive(b));
        Assert.Equal(20, loaded.Get<SrTransform>(a).X);
        Assert.Equal(12, loaded.Get<SrHealth>(a).Hp);
        Assert.True(loaded.Has<SrMarker>(a));
        Assert.Equal(a, loaded.Get<SrTarget>(b).Of);  // reference resolves to the right entity
        Assert.True(loaded.IsAlive(loaded.Get<SrTarget>(b).Of));
    }

    [Fact]
    public void AllocatorStateSurvivesRoundTrip()
    {
        var w = new World();
        var a = w.Spawn(); var b = w.Spawn(); w.Spawn();
        w.Despawn(b);                                  // hole at b.Id

        World loaded = Ser().Load(Ser().Save(w));
        var reused = loaded.Spawn();
        Assert.Equal(b.Id, reused.Id);                 // recycled before fresh
        Assert.False(loaded.IsAlive(b));               // stale handle (old version) stays dead
        var fresh = loaded.Spawn();
        Assert.Equal(3, fresh.Id);
    }

    [Fact]
    public void EmptyWorldRoundTrips()
    {
        World loaded = Ser().Load(Ser().Save(new World()));
        Assert.Equal(0, loaded.Spawn().Id);
    }

    [Fact]
    public void UnknownComponentOnLoadThrowsClearly()
    {
        var w = new World();
        w.Set(w.Spawn(), new SrHealth { Hp = 1 });
        string json = new WorldSerializer(typeof(SrHealth)).Save(w);
        var ex = Assert.Throws<InvalidOperationException>(
            () => new WorldSerializer(typeof(SrTransform)).Load(json));   // SrHealth not registered
        Assert.Contains("SrHealth", ex.Message);
    }

    [Fact]
    public void ConstructorRejectsNonComponentTypes()
    {
        Assert.Throws<ArgumentException>(() => new WorldSerializer(typeof(string)));
    }

    [Fact]
    public void FromAssemblyOfDiscoversComponents()
    {
        var ser = WorldSerializer.FromAssemblyOf<SrTransform>();
        var w = new World();
        w.Set(w.Spawn(), new SrTransform { X = 5, Y = 6 });
        World loaded = ser.Load(ser.Save(w));          // discovered SrTransform without listing it
        Assert.Equal(5, loaded.Query().With<SrTransform>().Entities()
            .Select(e => loaded.Get<SrTransform>(e).X).Single());
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement WorldSerializer**

`KhaozEngine.Ecs/WorldSerializer.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.Ecs;

/// <summary>
/// Saves and loads a <see cref="World"/> (entities + components + id-allocator state) as JSON.
/// Construct it with the component types your game uses (or scan an assembly). Entities are restored
/// at their exact id and version so <see cref="Entity"/>-typed component fields survive the round-trip.
/// Resources and systems are not serialized.
/// </summary>
public sealed class WorldSerializer
{
    private readonly Dictionary<string, Type> _byName = new();
    private readonly JsonSerializerOptions _options;

    /// <param name="componentTypes">Each must be a <c>struct</c> implementing <see cref="IComponent"/>.</param>
    public WorldSerializer(params Type[] componentTypes) : this(componentTypes, null) { }

    /// <param name="componentTypes">Each must be a <c>struct</c> implementing <see cref="IComponent"/>.</param>
    /// <param name="options">Optional JSON options; defaults to <c>IncludeFields = true</c>. Add
    /// converters here for value types that don't round-trip by default (e.g. MonoGame Color).</param>
    public WorldSerializer(IEnumerable<Type> componentTypes, JsonSerializerOptions? options)
    {
        _options = options ?? new JsonSerializerOptions { IncludeFields = true };
        foreach (Type t in componentTypes)
        {
            if (!t.IsValueType || t.IsAbstract || !typeof(IComponent).IsAssignableFrom(t))
                throw new ArgumentException($"{t.FullName} is not a struct implementing IComponent.");
            _byName[t.FullName!] = t;
        }
    }

    /// <summary>Builds a serializer from every <c>struct : IComponent</c> in <typeparamref name="T"/>'s assembly.</summary>
    public static WorldSerializer FromAssemblyOf<T>(JsonSerializerOptions? options = null)
    {
        var types = typeof(T).Assembly.GetTypes()
            .Where(t => t.IsValueType && !t.IsAbstract && typeof(IComponent).IsAssignableFrom(t));
        return new WorldSerializer(types, options);
    }

    public string Save(World world)
    {
        var doc = new SaveDoc
        {
            NextId = world.SaveNextId,
            FreeIds = world.SaveFreeSlots.Select(s => new FreeSlot { Id = s.id, Version = s.version }).ToList(),
        };
        ComponentRegistry reg = world.Registry;
        foreach (Archetype arch in world.SaveArchetypes)
        {
            for (int row = 0; row < arch.Count; row++)
            {
                Entity e = arch.Entities[row];
                var ed = new EntityDoc { Id = e.Id, Version = e.Version };
                foreach (int tid in arch.TypeIds)
                {
                    Type t = reg.TypeOf(tid);
                    object value = reg.IsTag(tid)
                        ? Activator.CreateInstance(t)!
                        : arch.Columns[tid].GetBoxed(row);
                    ed.Components[t.FullName!] = JsonSerializer.SerializeToElement(value, t, _options);
                }
                doc.Entities.Add(ed);
            }
        }
        return JsonSerializer.Serialize(doc, _options);
    }

    public World Load(string json)
    {
        SaveDoc doc = JsonSerializer.Deserialize<SaveDoc>(json, _options)
            ?? throw new InvalidOperationException("Empty or invalid save document.");
        var world = new World();
        foreach (EntityDoc ed in doc.Entities)
        {
            Entity e = world.CreateAt(ed.Id, ed.Version);
            foreach (var (name, element) in ed.Components)
            {
                if (!_byName.TryGetValue(name, out Type? t))
                    throw new InvalidOperationException(
                        $"Unknown component type '{name}' on load. Register it with the WorldSerializer.");
                object value = element.Deserialize(t, _options)!;
                world.SetByType(e, t, value);
            }
        }
        world.RestoreAllocator(doc.NextId, doc.FreeIds.Select(f => (f.Id, f.Version)));
        return world;
    }

    public void Save(World world, Stream stream)
    {
        using var w = new StreamWriter(stream, leaveOpen: true);
        w.Write(Save(world));
    }

    public World Load(Stream stream)
    {
        using var r = new StreamReader(stream, leaveOpen: true);
        return Load(r.ReadToEnd());
    }

    private sealed class SaveDoc
    {
        public int FormatVersion { get; set; } = 1;
        public int NextId { get; set; }
        public List<FreeSlot> FreeIds { get; set; } = new();
        public List<EntityDoc> Entities { get; set; } = new();
    }

    private sealed class FreeSlot { public int Id { get; set; } public uint Version { get; set; } }

    private sealed class EntityDoc
    {
        public int Id { get; set; }
        public uint Version { get; set; }
        public Dictionary<string, JsonElement> Components { get; set; } = new();
    }
}
```

- [ ] **Step 4: Run to verify pass.**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all `WorldSerializerTests` pass. (If `SerializeToElement(value, t, _options)` does not honour `IncludeFields` for a component's fields, confirm the test components use public fields and `_options.IncludeFields == true`.)

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Ecs/WorldSerializer.cs KhaozEngine.Tests/WorldSerializerTests.cs
git commit -m "ECS: WorldSerializer (JSON save/load, type table, FromAssemblyOf)"
```

---

## Task 4: Release 1.1.0

**Files:** Modify `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`, `CHANGELOG.md`

- [ ] **Step 1: Bump the Ecs package version**

In `KhaozEngine.Ecs/KhaozEngine.Ecs.csproj`, change `<Version>1.0.0</Version>` to `<Version>1.1.0</Version>`.

- [ ] **Step 2: Changelog** — prepend under the title in `CHANGELOG.md`:
```markdown
## KhaozEngine.Ecs 1.1.0

- Add `WorldSerializer`: JSON save/load of a `World` (entities + components + id-allocator state).
  Entities restore at their exact id/version so `Entity`-typed fields survive; tags and free-slot
  versions are preserved. Construct with your component types or `FromAssemblyOf<T>()`. Resources and
  systems are not serialized. Additive; no breaking change.
```

- [ ] **Step 3: Test, pack, tag, push**

```bash
cd ~/KhaozEngine
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj          # full suite green
dotnet pack KhaozEngine.Ecs/KhaozEngine.Ecs.csproj -c Release -o ./local-feed   # cumulative
ls local-feed/KhaozEngine.Ecs.1.1.0.nupkg
git add -A
git commit -m "Release KhaozEngine.Ecs 1.1.0 (World serialization)"
```
> Tag `ecs-v1.1.0` and push from `main` after the branch merges (the finishing step), so the publish CI fires on the merged commit.

---

## Self-Review

**Spec coverage:**
- Save-doc shape (formatVersion, nextId, free slots, entities → name→component JSON) → Task 3 DTOs.
- Preserve exact ids/versions; `Entity` references survive → Tasks 2 (`CreateAt`/`RestoreAllocator`), 3 (round-trip test asserts `loaded.Get<SrTarget>(b).Of == a`).
- Free slots save their version (collision-safe recycle) → Tasks 2, 3 (allocator test asserts stale handle stays dead).
- Type-erased access (`Column.GetBoxed/SetBoxed`, registry `id↔Type`/`RegisterType`) → Task 1.
- Tags serialize as `{}` and re-add → Task 3 (`SrMarker` in round-trip).
- Explicit type resolution + `FromAssemblyOf` + unknown-type error → Task 3.
- `IncludeFields = true` default + caller-supplied converters → Task 3 (`WorldSerializer` options).
- Additive `1.1.0` release → Task 4.

**Placeholder scan:** none — every new member is shown in full.

**Type consistency:** `Column.GetBoxed/SetBoxed`; `ComponentRegistry.RegisterType/TypeOf/Id<T>`; `World.CreateAt/SetByType/RestoreAllocator/SaveNextId/SaveFreeSlots/SaveArchetypes/Registry`; `WorldSerializer(params Type[])` / `(IEnumerable<Type>, JsonSerializerOptions?)` / `FromAssemblyOf<T>` / `Save`/`Load`. Save/Load DTOs (`SaveDoc`/`FreeSlot`/`EntityDoc`) consistent between write and read.

---

## Execution Handoff

After all tasks green, finish the branch (merge `ecs-serialization` → `main`), then tag `ecs-v1.1.0` and push `main` + tag so CI publishes. This is the first of four deferred-feature cycles; change detection, relationships, and system ordering/groups follow.
