# Per-cell world-state persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist the authoritative non-player entities a `ShardHost` cell owns into an `IWorldStore`, keyed per cell, so a sharded world survives a server restart.

**Architecture:** Add snapshot/restore primitives to `KhaozEngine.Sharding` (reusing the Replication snapshot codec cells already use for ghosting/migrate). Add a `CellPersistence` driver to `KhaozEngine.NetWorld` that wires those primitives to an `IWorldStore` through a small `ICellPersistenceHost` seam (lazy load-on-cell-create, periodic dirty save, `FlushAsync`, NetId high-water record). `ShardedWorldServer` and the `MmoServerSample` reference server implement the seam.

**Tech Stack:** C# / net10.0, xUnit (headless, no GPU). `KhaozEngine.Replication` (`SnapshotWriter`, `ClientReplicationView`), `KhaozEngine.WorldStore` (`IWorldStore`, `InMemoryWorldStore`, `IEnumerableWorldStore`), `KhaozEngine.Serialization` (`JsonDefaults`), `System.Buffers.Binary.BinaryPrimitives`.

## Global Constraints

- net10.0, MonoGame-free. Every new behaviour ships with a headless test in `KhaozEngine.Tests`.
- `KhaozEngine.Sharding` must NOT depend on `KhaozEngine.WorldStore` — storage stays out of the sharding core. Persistence wiring lives in `KhaozEngine.NetWorld` (already deps both `Sharding` and `WorldStore`).
- No em-dashes / semicolons in shipped prose (docs, XML comments, CHANGELOG). Plain hyphens fine.
- Conventional-commit subjects `area(scope): summary`. On the release/version-bump commit the scope is the new version (e.g. `netcode(9.2.0):`).
- Additive change = minor version bump. One `<KhaozEngineVersion>` line in `Directory.Build.props` governs all packages; the release ritual (bump + CHANGELOG + 3 guard-checked declarations + `dotnet pack -c Release -o ./local-feed` + tag) is a single final task, batched (do NOT push/tag per item).
- `local-feed/` must exist before restore (`mkdir -p local-feed`).
- Players are excluded from cell persistence (already persisted player-keyed by `WorldPersistence`). Ghosts and migrating entities are excluded (mirrors / in-flight).
- Restored entities keep their `NetId`s; the allocator must resume above the highest persisted id so a freshly spawned player can never collide.

---

## File Structure

**`KhaozEngine.Sharding`** (primitives, no storage dep):
- Modify `KhaozEngine.Sharding/CellSim.cs` — add `SnapshotOwned`, `RestoreOwned`, `MaxOwnedNetId`.
- Modify `KhaozEngine.Sharding/ShardHost.cs` — add `event Action<CellSim>? CellCreated` (fired in `GetOrCreateCell`) and public `CellSim EnsureCell(CellCoord)`.

**`KhaozEngine.NetWorld`** (wiring):
- Create `KhaozEngine.NetWorld/ICellPersistenceHost.cs` — the seam `CellPersistence` drives.
- Create `KhaozEngine.NetWorld/WorldMetaRecord.cs` — the NetId high-water JSON DTO.
- Create `KhaozEngine.NetWorld/CellPersistence.cs` — `CellPersistenceConfig` + `CellPersistence`.
- Modify `KhaozEngine.NetWorld/ShardedWorldServer.cs` — implement `ICellPersistenceHost`.

**`MmoServerSample`** (demonstration):
- Modify `MmoServerSample/MmoProtocol.cs` — add a `ResourceNode` component + register it.
- Modify `MmoServerSample/MmoServer.cs` — implement `ICellPersistenceHost`, own a `CellPersistence`, spawn nodes, wire preload/update/flush.

**Tests:**
- Create `KhaozEngine.Tests/Sharding/CellSimPersistenceTests.cs`
- Create `KhaozEngine.Tests/Sharding/ShardHostCellCreatedTests.cs`
- Create `KhaozEngine.Tests/NetWorld/CellPersistenceTests.cs`
- Create `KhaozEngine.Tests/NetWorld/ShardedCellPersistenceTests.cs`

---

## Task 1: Sharding — CellSim snapshot/restore primitives

**Files:**
- Modify: `KhaozEngine.Sharding/CellSim.cs`
- Test: `KhaozEngine.Tests/Sharding/CellSimPersistenceTests.cs`

**Interfaces:**
- Consumes: `SnapshotWriter.WriteFiltered(World, ReplicationRegistry, IReadOnlySet<int>)`, `ClientReplicationView(registry)` + `.Apply(World, byte[])` + `.Entities`, existing `CellSim.World`/`registry`/`Ghost`/`Migrating`.
- Produces:
  - `byte[] CellSim.SnapshotOwned(IReadOnlySet<int> excludedNetIds)`
  - `IReadOnlyList<int> CellSim.RestoreOwned(byte[] snapshot)`
  - `int CellSim.MaxOwnedNetId()`

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Sharding/CellSimPersistenceTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class CellSimPersistenceTests
{
    private struct Blob : IComponent { public int V; }

    private static ReplicationRegistry Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<Blob>(
            typeId: 1,
            write: (b, bw) => bw.Write(b.V),
            read: br => new Blob { V = br.ReadInt32() });
        return r;
    }

    private static CellSim Cell(ReplicationRegistry r) => new(new CellCoord(0, 0), 1f / 30f, r, 10f);

    private static Entity Owned(CellSim c, int netId, int v)
    {
        Entity e = c.World.Spawn();
        c.World.Set(e, new NetId(netId));
        c.World.Set(e, new Blob { V = v });
        return e;
    }

    [Fact]
    public void SnapshotOwned_ExcludesPlayers_Ghosts_AndMigrating()
    {
        ReplicationRegistry r = Registry();
        CellSim c = Cell(r);
        Owned(c, 5, 50);                                  // persistable
        Owned(c, 6, 60);                                  // player (excluded by id)
        Entity ghost = Owned(c, 7, 70); c.World.Set(ghost, new Ghost { Source = new CellCoord(1, 0) });
        Entity mig = Owned(c, 8, 80); c.World.Set(mig, new Migrating { Destination = new CellCoord(1, 0) });

        byte[] snap = c.SnapshotOwned(new HashSet<int> { 6 });

        // Restore into a fresh cell and confirm only NetId 5 survived.
        CellSim restored = Cell(r);
        IReadOnlyList<int> ids = restored.RestoreOwned(snap);
        Assert.Equal(new[] { 5 }, ids);
        Assert.True(restored.TryGetOwned(5, out Entity e));
        Assert.True(restored.World.TryGet(e, out Blob b));
        Assert.Equal(50, b.V);
    }

    [Fact]
    public void MaxOwnedNetId_ReturnsHighestOwned_ZeroWhenEmpty()
    {
        ReplicationRegistry r = Registry();
        CellSim empty = Cell(r);
        Assert.Equal(0, empty.MaxOwnedNetId());

        CellSim c = Cell(r);
        Owned(c, 3, 30);
        Owned(c, 9, 90);
        Entity ghost = Owned(c, 99, 990); c.World.Set(ghost, new Ghost { Source = new CellCoord(1, 0) });
        Assert.Equal(9, c.MaxOwnedNetId());               // ghost 99 not counted
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CellSimPersistenceTests" -v q`
Expected: FAIL — `CellSim` has no `SnapshotOwned` / `RestoreOwned` / `MaxOwnedNetId` (compile error).

- [ ] **Step 3: Write minimal implementation**

In `KhaozEngine.Sharding/CellSim.cs`, add these three public methods to the `CellSim` class (after `TryGetOwned`). Note the file already `using System.Collections.Generic;` and has `SnapshotWriter`/`ClientReplicationView` available via `KhaozEngine.Replication`:

```csharp
    /// <summary>
    /// A durable Replication snapshot of this cell's <b>persistable</b> entities: those it owns (present, not a
    /// <see cref="Ghost"/>, not <see cref="Migrating"/>) whose <see cref="NetId"/> is not in
    /// <paramref name="excludedNetIds"/> (the caller passes the player NetIds, which persist separately). Reuses the
    /// same <see cref="SnapshotWriter"/> codec cells use for ghosting/migrate, so any registered component persists.
    /// </summary>
    public byte[] SnapshotOwned(IReadOnlySet<int> excludedNetIds)
    {
        ArgumentNullException.ThrowIfNull(excludedNetIds);
        var ids = new HashSet<int>();
        World.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (World.Has<Ghost>(e) || World.Has<Migrating>(e)) return;
            if (excludedNetIds.Contains(id.Value)) return;
            ids.Add(id.Value);
        });
        return SnapshotWriter.WriteFiltered(World, registry, ids);
    }

    /// <summary>
    /// Restores the entities in <paramref name="snapshot"/> into this cell's world as freshly owned entities
    /// (a throwaway <see cref="ClientReplicationView"/>, exactly like <see cref="AdoptFromMigrate"/>), keeping their
    /// <see cref="NetId"/>s. Returns the restored NetId values. Intended to run once on cell creation.
    /// </summary>
    public IReadOnlyList<int> RestoreOwned(byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var view = new ClientReplicationView(registry);
        view.Apply(World, snapshot);
        var netIds = new List<int>(view.Entities.Count);
        foreach (KeyValuePair<int, Entity> kv in view.Entities) netIds.Add(kv.Key);
        return netIds;
    }

    /// <summary>The largest owned (non-ghost, non-migrating) <see cref="NetId"/> in this cell, or 0 if none.</summary>
    public int MaxOwnedNetId()
    {
        int max = 0;
        World.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (World.Has<Ghost>(e) || World.Has<Migrating>(e)) return;
            if (id.Value > max) max = id.Value;
        });
        return max;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CellSimPersistenceTests" -v q`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Sharding/CellSim.cs KhaozEngine.Tests/Sharding/CellSimPersistenceTests.cs
git commit -m "sharding: CellSim SnapshotOwned/RestoreOwned/MaxOwnedNetId primitives"
```

---

## Task 2: Sharding — ShardHost CellCreated event + EnsureCell

**Files:**
- Modify: `KhaozEngine.Sharding/ShardHost.cs`
- Test: `KhaozEngine.Tests/Sharding/ShardHostCellCreatedTests.cs`

**Interfaces:**
- Consumes: existing private `ShardHost.GetOrCreateCell(CellCoord)`, `CellSim.Coord`.
- Produces:
  - `event Action<CellSim>? ShardHost.CellCreated` — raised once, the first time each coordinate's cell is instantiated (from any path: `CellFor`, `SpawnAt`, handoff destination, `EnsureCell`).
  - `CellSim ShardHost.EnsureCell(CellCoord coord)` — get-or-create by coordinate.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Sharding/ShardHostCellCreatedTests.cs`:

```csharp
using System.Collections.Generic;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class ShardHostCellCreatedTests
{
    private static ShardHost Host() => new(cellSize: 10f, tickSeconds: 1f / 30f, registry: new ReplicationRegistry());

    [Fact]
    public void CellCreated_FiresOncePerNewCoord_NotOnRepeat()
    {
        ShardHost host = Host();
        var fired = new List<CellCoord>();
        host.CellCreated += c => fired.Add(c.Coord);

        host.CellFor(5f, 5f);                 // creates cell (0,0)
        host.SpawnAt(5f, 5f, out _);          // same cell (0,0) - no new fire
        host.EnsureCell(new CellCoord(3, 0)); // creates cell (3,0)
        host.EnsureCell(new CellCoord(3, 0)); // existing - no new fire

        Assert.Equal(new[] { new CellCoord(0, 0), new CellCoord(3, 0) }, fired);
    }

    [Fact]
    public void EnsureCell_ReturnsSameInstanceForSameCoord()
    {
        ShardHost host = Host();
        CellSim a = host.EnsureCell(new CellCoord(2, 2));
        CellSim b = host.EnsureCell(new CellCoord(2, 2));
        Assert.Same(a, b);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ShardHostCellCreatedTests" -v q`
Expected: FAIL — no `CellCreated` event / `EnsureCell` method (compile error).

- [ ] **Step 3: Write minimal implementation**

In `KhaozEngine.Sharding/ShardHost.cs`:

Add the event near the other public members (e.g. just after the `Cells` property):

```csharp
    /// <summary>
    /// Raised once for each cell the first time its coordinate is instantiated (via <see cref="CellFor"/>,
    /// <see cref="SpawnAt"/>, a handoff destination, or <see cref="EnsureCell"/>). The load hook for per-cell
    /// persistence: a subscriber restores that cell's saved state. Fired synchronously on the creating thread.
    /// </summary>
    public event Action<CellSim>? CellCreated;
```

Add `EnsureCell` near `CellFor`:

```csharp
    /// <summary>Gets the cell at <paramref name="coord"/>, creating it (and raising <see cref="CellCreated"/>) if absent.</summary>
    public CellSim EnsureCell(CellCoord coord) => GetOrCreateCell(coord);
```

Fire the event inside the existing `GetOrCreateCell` (the single creation choke point), only on the create branch:

```csharp
    private CellSim GetOrCreateCell(CellCoord coord)
    {
        if (!cells.TryGetValue(coord, out CellSim? cell))
        {
            cell = new CellSim(coord, tickSeconds, registry, interestCellSize);
            cells[coord] = cell;
            ordered.Add(cell);
            CellCreated?.Invoke(cell);
        }
        return cell;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ShardHostCellCreatedTests" -v q`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full Sharding suite (no regression from firing the event mid-tick)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Sharding" -v q`
Expected: PASS (all sharding tests, including handoff/ghost tests unaffected).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Sharding/ShardHost.cs KhaozEngine.Tests/Sharding/ShardHostCellCreatedTests.cs
git commit -m "sharding: ShardHost.CellCreated event + EnsureCell(coord)"
```

---

## Task 3: NetWorld — ICellPersistenceHost seam + WorldMetaRecord

**Files:**
- Create: `KhaozEngine.NetWorld/ICellPersistenceHost.cs`
- Create: `KhaozEngine.NetWorld/WorldMetaRecord.cs`
- Test: `KhaozEngine.Tests/NetWorld/CellPersistenceTests.cs` (add the fake host + first meta test)

**Interfaces:**
- Consumes: `KhaozEngine.Sharding.CellCoord`, `KhaozEngine.Serialization.JsonDefaults`.
- Produces:
  - `interface ICellPersistenceHost` with: `event Action<CellCoord>? CellCreated`; `IReadOnlyCollection<CellCoord> LiveCellCoords { get; }`; `byte[]? SnapshotCell(CellCoord)`; `IReadOnlyList<int> RestoreCell(CellCoord, byte[])`; `void EnsureCell(CellCoord)`; `int NextNetId { get; }`; `void EnsureNextNetIdAtLeast(int atLeast)`.
  - `class WorldMetaRecord { int Version; int NextNetId; byte[] Encode(); static WorldMetaRecord Decode(byte[]); }`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/NetWorld/CellPersistenceTests.cs` with a reusable fake host and the first (round-trip of the meta DTO) test:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class CellPersistenceTests
{
    // A fake ICellPersistenceHost backed by plain dictionaries - no real ShardHost.
    private sealed class FakeHost : ICellPersistenceHost
    {
        public readonly Dictionary<CellCoord, byte[]> Snapshots = new();   // what SnapshotCell returns
        public readonly Dictionary<CellCoord, byte[]> Restored = new();    // what RestoreCell received
        public int NextNetId { get; private set; } = 1;
        public event Action<CellCoord>? CellCreated;

        public IReadOnlyCollection<CellCoord> LiveCellCoords => new List<CellCoord>(Snapshots.Keys);
        public byte[]? SnapshotCell(CellCoord coord) => Snapshots.TryGetValue(coord, out byte[]? b) ? b : null;
        public IReadOnlyList<int> RestoreCell(CellCoord coord, byte[] snapshot)
        {
            Restored[coord] = snapshot;
            return RestoreIds.TryGetValue(coord, out List<int>? ids) ? ids : new List<int>();
        }
        public readonly Dictionary<CellCoord, List<int>> RestoreIds = new();
        public void EnsureCell(CellCoord coord) => CellCreated?.Invoke(coord);
        public void EnsureNextNetIdAtLeast(int atLeast) { if (atLeast > NextNetId) NextNetId = atLeast; }
        public void RaiseCellCreated(CellCoord coord) => CellCreated?.Invoke(coord);
        public void SetNextNetId(int v) => NextNetId = v;
    }

    [Fact]
    public void WorldMetaRecord_RoundTrips()
    {
        byte[] bytes = new WorldMetaRecord { NextNetId = 42 }.Encode();
        WorldMetaRecord back = WorldMetaRecord.Decode(bytes);
        Assert.Equal(42, back.NextNetId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CellPersistenceTests" -v q`
Expected: FAIL — `ICellPersistenceHost` / `WorldMetaRecord` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.NetWorld/ICellPersistenceHost.cs`:

```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The server-side surface <see cref="CellPersistence"/> drives, so the same per-cell persistence wiring serves
/// any <see cref="ShardHost"/>-based server. Cell-keyed and player-agnostic: <see cref="SnapshotCell"/> returns a
/// cell's persistable (owned, non-player, non-ghost, non-migrating) entities, and <see cref="RestoreCell"/> puts
/// them back. The host owns the <see cref="NetId"/> allocator so restored entities can never collide with fresh
/// spawns after a restart (see <see cref="EnsureNextNetIdAtLeast"/>).
/// </summary>
public interface ICellPersistenceHost
{
    /// <summary>Raised when a cell is first instantiated: persistence loads that cell's saved state here.</summary>
    event Action<CellCoord>? CellCreated;

    /// <summary>The coordinates of all currently instantiated cells (for the periodic dirty pass + flush).</summary>
    IReadOnlyCollection<CellCoord> LiveCellCoords { get; }

    /// <summary>The durable snapshot of a cell's persistable entities, or null if the cell is not instantiated.</summary>
    byte[]? SnapshotCell(CellCoord coord);

    /// <summary>Restores entities into a cell (call on the server thread). Returns the restored NetId values.</summary>
    IReadOnlyList<int> RestoreCell(CellCoord coord, byte[] snapshot);

    /// <summary>Instantiates a cell by coordinate (firing <see cref="CellCreated"/> if new); used by preload.</summary>
    void EnsureCell(CellCoord coord);

    /// <summary>The next NetId the allocator will hand out.</summary>
    int NextNetId { get; }

    /// <summary>Raises the allocator so its next id is at least <paramref name="atLeast"/> (never lowers it).</summary>
    void EnsureNextNetIdAtLeast(int atLeast);
}
```

Create `KhaozEngine.NetWorld/WorldMetaRecord.cs`:

```csharp
using System.Text.Json;
using KhaozEngine.Serialization;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The small world-scope meta record persisted under <see cref="CellPersistenceConfig.MetaKey"/>. Carries the
/// <see cref="NetId"/> high-water mark so the allocator resumes above every persisted entity id on restart,
/// keeping restored cell entities from colliding with freshly spawned players. Versioned + tolerant like
/// <see cref="PlayerRecord"/>: extend by adding properties.
/// </summary>
public sealed class WorldMetaRecord
{
    /// <summary>Record schema version; bump when the shape changes meaningfully.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The next NetId the allocator will hand out (one past the highest ever allocated).</summary>
    public int NextNetId { get; set; }

    /// <summary>Serializes to UTF-8 JSON bytes for the world store.</summary>
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, JsonDefaults.IndentedWrite);

    /// <summary>Deserializes from world-store bytes; tolerant of unknown / missing fields.</summary>
    public static WorldMetaRecord Decode(byte[] data) =>
        JsonSerializer.Deserialize<WorldMetaRecord>(data, JsonDefaults.TolerantRead) ?? new WorldMetaRecord();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CellPersistenceTests" -v q`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/ICellPersistenceHost.cs KhaozEngine.NetWorld/WorldMetaRecord.cs KhaozEngine.Tests/NetWorld/CellPersistenceTests.cs
git commit -m "netcode: ICellPersistenceHost seam + WorldMetaRecord DTO"
```

---

## Task 4: NetWorld — CellPersistence (load-on-create, dirty save, flush)

**Files:**
- Create: `KhaozEngine.NetWorld/CellPersistence.cs`
- Test: `KhaozEngine.Tests/NetWorld/CellPersistenceTests.cs` (add lifecycle tests)

**Interfaces:**
- Consumes: `ICellPersistenceHost`, `IWorldStore` (`LoadAsync`/`SaveAsync`), `WorldMetaRecord`, `CellCoord`, `System.Buffers.Binary.BinaryPrimitives`.
- Produces:
  - `class CellPersistenceConfig { float SaveIntervalSeconds=30; string CellKeyPrefix="cell:"; string MetaKey="world:meta"; int SchemaVersion=1; }`
  - `class CellPersistence` ctor `(ICellPersistenceHost host, IWorldStore store, CellPersistenceConfig? config = null)`; methods `void Update(float dt)`, `void SaveDirtyPass()`, `Task FlushAsync()`, `Task LoadMetaAsync()`, `Task PreloadAsync()`.

- [ ] **Step 1: Write the failing test**

Append to `KhaozEngine.Tests/NetWorld/CellPersistenceTests.cs` (inside the class; the `FakeHost` from Task 3 is reused). Add `using System.Text;` and `using KhaozEngine.WorldStore;` to the file's usings:

```csharp
    private static readonly CellCoord C00 = new(0, 0);

    [Fact]
    public async Task LoadOnCellCreate_AppliesRestoreOnUpdate_NotBefore()
    {
        var store = new InMemoryWorldStore();
        var host = new FakeHost();
        // Seed a saved cell blob: header (magic + schemaVersion 1) + a 1-byte-count-0 body stand-in.
        // Use the persistence's own wrapping by saving through a throwaway instance's SaveDirtyPass instead:
        host.Snapshots[C00] = new byte[] { 0, 0, 0, 0 };   // empty replication snapshot (count 0)
        host.RestoreIds[C00] = new List<int> { 7 };
        var seeder = new CellPersistence(host, store);
        seeder.SaveDirtyPass();                            // writes cell:0:0 (wrapped) to the store
        await seeder.FlushAsync();

        // Fresh persistence over the same store: creating the cell enqueues a load, applied only on Update.
        var host2 = new FakeHost();
        host2.RestoreIds[C00] = new List<int> { 7 };
        var cp = new CellPersistence(host2, store);
        host2.RaiseCellCreated(C00);                      // fires CellCreated -> async load
        await cp.FlushAsync();                             // await the load; FlushAsync drains + applies restores
        Assert.True(host2.Restored.ContainsKey(C00));     // restore applied
        Assert.True(host2.NextNetId >= 8);                // high-water raised past restored id 7
    }

    [Fact]
    public async Task SaveDirtyPass_OnlyWritesChangedCells()
    {
        var store = new InMemoryWorldStore();
        var host = new FakeHost();
        host.Snapshots[C00] = new byte[] { 0, 0, 0, 0 };
        var cp = new CellPersistence(host, store);

        cp.SaveDirtyPass();
        await cp.FlushAsync();
        byte[]? first = await store.LoadAsync("cell:0:0");
        Assert.NotNull(first);

        // Unchanged -> second pass writes nothing new (same bytes present).
        cp.SaveDirtyPass();
        await cp.FlushAsync();
        byte[]? second = await store.LoadAsync("cell:0:0");
        Assert.Equal(first, second);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CellPersistenceTests" -v q`
Expected: FAIL — `CellPersistence` / `CellPersistenceConfig` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.NetWorld/CellPersistence.cs`:

```csharp
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="CellPersistence"/>.</summary>
public sealed class CellPersistenceConfig
{
    /// <summary>How often the periodic snapshot saves dirty cells, seconds. A crash loses at most this much.</summary>
    public float SaveIntervalSeconds { get; init; } = 30f;

    /// <summary>Key namespace for cell records. Stored key is <c>{CellKeyPrefix}{x}:{y}</c>.</summary>
    public string CellKeyPrefix { get; init; } = "cell:";

    /// <summary>Key of the world-scope meta record (the NetId high-water mark).</summary>
    public string MetaKey { get; init; } = "world:meta";

    /// <summary>Blob schema version; bump on a breaking component-layout change so old saves are skipped, not mis-read.</summary>
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>
/// Wires an <see cref="IWorldStore"/> into a <see cref="ShardHost"/>-based server (via
/// <see cref="ICellPersistenceHost"/>) so a cell's authoritative non-player entities survive a restart. Mirrors
/// <see cref="WorldPersistence"/> but keyed by cell coordinate: lazy load-on-cell-create, periodic dirty snapshot
/// of changed cells, and a NetId high-water record so restored entities never collide with fresh spawns. Async
/// loads are applied on the server thread inside <see cref="Update"/> (never from a background continuation).
/// </summary>
public sealed class CellPersistence
{
    // Header: [int32 magic][int32 schemaVersion] then the raw Replication snapshot.
    private const int Magic = 0x3150434B; // "KCP1"

    private readonly ICellPersistenceHost host;
    private readonly IWorldStore store;
    private readonly CellPersistenceConfig config;

    private readonly ConcurrentQueue<(CellCoord coord, byte[] snapshot)> restoreQueue = new();
    private readonly ConcurrentDictionary<CellCoord, byte[]> lastSaved = new();   // raw (unwrapped) snapshot per cell
    private readonly HashSet<CellCoord> loadRequested = new();                    // server-thread-only idempotency
    private readonly object pendingLock = new();
    private readonly List<Task> pending = new();
    private int lastSavedNextNetId;
    private float sinceSave;

    public CellPersistence(ICellPersistenceHost host, IWorldStore store, CellPersistenceConfig? config = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.config = config ?? new CellPersistenceConfig();
        host.CellCreated += OnCellCreated;
    }

    private string CellKey(CellCoord c) => $"{config.CellKeyPrefix}{c.X}:{c.Y}";

    private void Track(Task task) { lock (pendingLock) pending.Add(task); }

    private void OnCellCreated(CellCoord coord)
    {
        if (!loadRequested.Add(coord)) return;          // load a given cell at most once
        Track(LoadCellAsync(coord));
    }

    private async Task LoadCellAsync(CellCoord coord)
    {
        byte[]? blob = await store.LoadAsync(CellKey(coord)).ConfigureAwait(false);
        if (blob is null) return;                       // no save -> cell stays as spawned
        if (!TryUnwrap(blob, out byte[] snapshot)) return; // header/schema mismatch -> skip
        lastSaved[coord] = snapshot;                    // loaded == clean baseline
        restoreQueue.Enqueue((coord, snapshot));
    }

    /// <summary>Call once per server frame. Applies completed loads (this thread) + runs the periodic dirty pass.</summary>
    public void Update(float dt)
    {
        DrainRestores();
        lock (pendingLock) pending.RemoveAll(t => t.Status == TaskStatus.RanToCompletion);
        sinceSave += dt;
        if (sinceSave >= config.SaveIntervalSeconds) { sinceSave = 0f; SaveDirtyPass(); }
    }

    private void DrainRestores()
    {
        while (restoreQueue.TryDequeue(out (CellCoord coord, byte[] snapshot) r))
        {
            IReadOnlyList<int> ids = host.RestoreCell(r.coord, r.snapshot);
            int max = 0;
            foreach (int id in ids) if (id > max) max = id;
            if (max > 0) host.EnsureNextNetIdAtLeast(max + 1);
        }
    }

    /// <summary>Saves every live cell whose persistable snapshot changed since its last save, plus the meta record.</summary>
    public void SaveDirtyPass()
    {
        foreach (CellCoord coord in new List<CellCoord>(host.LiveCellCoords))
        {
            byte[]? snap = host.SnapshotCell(coord);
            if (snap is null) continue;
            if (lastSaved.TryGetValue(coord, out byte[]? prev) && prev.AsSpan().SequenceEqual(snap)) continue;
            lastSaved[coord] = snap;
            Track(store.SaveAsync(CellKey(coord), Wrap(snap)));
        }
        SaveMetaIfAdvanced();
    }

    private void SaveMetaIfAdvanced()
    {
        int next = host.NextNetId;
        if (next <= lastSavedNextNetId) return;
        lastSavedNextNetId = next;
        Track(store.SaveAsync(config.MetaKey, new WorldMetaRecord { NextNetId = next }.Encode()));
    }

    /// <summary>Loads the NetId high-water record and resumes the allocator above it. Call at boot (server thread).</summary>
    public async Task LoadMetaAsync()
    {
        byte[]? data = await store.LoadAsync(config.MetaKey).ConfigureAwait(false);
        if (data is null) return;
        WorldMetaRecord meta = WorldMetaRecord.Decode(data);
        lastSavedNextNetId = meta.NextNetId;
        host.EnsureNextNetIdAtLeast(meta.NextNetId);
    }

    /// <summary>
    /// Instantiates every saved cell (enumerating <c>{CellKeyPrefix}*</c> keys) so its normal load path runs. No-op
    /// on a store that cannot enumerate. Call at boot on the server thread; follow with <see cref="FlushAsync"/> to
    /// apply the restores before the first tick.
    /// </summary>
    public async Task PreloadAsync()
    {
        if (store is not IEnumerableWorldStore es) return;
        var coords = new List<CellCoord>();
        await foreach (WorldStoreEntry entry in es.EnumerateAsync(config.CellKeyPrefix).ConfigureAwait(false))
            if (TryParseCoord(entry.Key, out CellCoord c)) coords.Add(c);
        foreach (CellCoord c in coords) host.EnsureCell(c);
    }

    /// <summary>Awaits all in-flight loads/saves, applies pending restores, then a final dirty + meta save.</summary>
    public async Task FlushAsync()
    {
        DrainRestores();
        await AwaitPendingAsync().ConfigureAwait(false);
        DrainRestores();
        SaveDirtyPass();
        await AwaitPendingAsync().ConfigureAwait(false);
    }

    private async Task AwaitPendingAsync()
    {
        Task[] tasks;
        lock (pendingLock) { tasks = pending.ToArray(); pending.Clear(); }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private byte[] Wrap(byte[] snapshot)
    {
        var buf = new byte[8 + snapshot.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), config.SchemaVersion);
        snapshot.CopyTo(buf.AsSpan(8));
        return buf;
    }

    private bool TryUnwrap(byte[] blob, out byte[] snapshot)
    {
        snapshot = Array.Empty<byte>();
        if (blob.Length < 8) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(0, 4)) != Magic) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4, 4)) != config.SchemaVersion) return false;
        snapshot = blob[8..];
        return true;
    }

    private bool TryParseCoord(string key, out CellCoord coord)
    {
        coord = default;
        if (!key.StartsWith(config.CellKeyPrefix, StringComparison.Ordinal)) return false;
        string rest = key.Substring(config.CellKeyPrefix.Length);
        int sep = rest.IndexOf(':');
        if (sep <= 0) return false;
        if (int.TryParse(rest.AsSpan(0, sep), out int x) && int.TryParse(rest.AsSpan(sep + 1), out int y))
        { coord = new CellCoord(x, y); return true; }
        return false;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CellPersistenceTests" -v q`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/CellPersistence.cs KhaozEngine.Tests/NetWorld/CellPersistenceTests.cs
git commit -m "netcode: CellPersistence - load-on-create, dirty save, high-water, flush"
```

---

## Task 5: NetWorld — CellPersistence header guard + meta + preload tests

**Files:**
- Test: `KhaozEngine.Tests/NetWorld/CellPersistenceTests.cs` (add guard/meta/preload tests)

**Interfaces:**
- Consumes: `CellPersistence` (Task 4), `FakeHost` (Task 3), `InMemoryWorldStore` (implements `IEnumerableWorldStore`).
- Produces: no new production types — this task locks in the guard/meta/preload behaviour with tests. If a test fails, fix `CellPersistence` (not the test).

- [ ] **Step 1: Write the failing test**

Append to `KhaozEngine.Tests/NetWorld/CellPersistenceTests.cs`:

```csharp
    [Fact]
    public async Task Load_SkipsBlobWithWrongSchemaVersion()
    {
        var store = new InMemoryWorldStore();
        var hostV1 = new FakeHost();
        hostV1.Snapshots[C00] = new byte[] { 0, 0, 0, 0 };
        var v1 = new CellPersistence(hostV1, store, new CellPersistenceConfig { SchemaVersion = 1 });
        v1.SaveDirtyPass();
        await v1.FlushAsync();

        // A reader on schema 2 must treat the v1 blob as unusable: no restore enqueued.
        var hostV2 = new FakeHost();
        var v2 = new CellPersistence(hostV2, store, new CellPersistenceConfig { SchemaVersion = 2 });
        hostV2.RaiseCellCreated(C00);
        await v2.FlushAsync();
        Assert.False(hostV2.Restored.ContainsKey(C00));   // skipped, not mis-decoded
    }

    [Fact]
    public async Task LoadMetaAsync_ResumesAllocatorAboveHighWater()
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("world:meta", new WorldMetaRecord { NextNetId = 500 }.Encode());
        var host = new FakeHost();                         // starts NextNetId = 1
        var cp = new CellPersistence(host, store);
        await cp.LoadMetaAsync();
        Assert.Equal(500, host.NextNetId);
    }

    [Fact]
    public async Task PreloadAsync_InstantiatesEverySavedCell()
    {
        var store = new InMemoryWorldStore();
        var seedHost = new FakeHost();
        seedHost.Snapshots[new CellCoord(1, 2)] = new byte[] { 0, 0, 0, 0 };
        seedHost.Snapshots[new CellCoord(-3, 4)] = new byte[] { 0, 0, 0, 0 };
        var seeder = new CellPersistence(seedHost, store);
        seeder.SaveDirtyPass();
        await seeder.FlushAsync();

        var host = new FakeHost();
        var created = new List<CellCoord>();
        host.CellCreated += created.Add;
        var cp = new CellPersistence(host, store);
        await cp.PreloadAsync();
        Assert.Contains(new CellCoord(1, 2), created);
        Assert.Contains(new CellCoord(-3, 4), created);
    }
```

- [ ] **Step 2: Run tests to verify they pass (behaviour already implemented in Task 4)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CellPersistenceTests" -v q`
Expected: PASS (6 tests total). If any fail, fix `CellPersistence.cs`, not the tests.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/NetWorld/CellPersistenceTests.cs
git commit -m "netcode: CellPersistence header-guard/meta/preload tests"
```

---

## Task 6: NetWorld — ShardedWorldServer implements ICellPersistenceHost

**Files:**
- Modify: `KhaozEngine.NetWorld/ShardedWorldServer.cs`
- Test: `KhaozEngine.Tests/NetWorld/ShardedCellPersistenceTests.cs`

**Interfaces:**
- Consumes: `ICellPersistenceHost`, existing `ShardedWorldServer` fields `host` (`ShardHost`), `netIdBySlot`, `nextNetId`; `CellSim.SnapshotOwned`/`RestoreOwned` (Task 1); `ShardHost.CellCreated`/`EnsureCell` (Task 2).
- Produces: `ShardedWorldServer : ICellPersistenceHost` (in addition to its existing interfaces). New members: `event Action<CellCoord>? CellCreated`, `IReadOnlyCollection<CellCoord> LiveCellCoords`, `byte[]? SnapshotCell(CellCoord)`, `IReadOnlyList<int> RestoreCell(CellCoord, byte[])`, `void EnsureCell(CellCoord)`, `int NextNetId`, `void EnsureNextNetIdAtLeast(int)`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/NetWorld/ShardedCellPersistenceTests.cs`. Mirror the harness of `ShardedWorldPersistenceTests` (loopback transport, join a player), then assert a cell snapshot excludes that player:

```csharp
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedCellPersistenceTests
{
    private static ShardedWorldServerConfig Cfg() => new()
    {
        TickSeconds = 1f / 30f, CellSize = 10f, OverlapMargin = 4f, InterestRadius = 4f, MaxPlayers = 8,
        SpawnPosition = _ => new Vector3(5f, 0f, 5f),
    };

    [Fact]
    public async Task SnapshotCell_ExcludesPlayers()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new ShardedWorldServer(st, Cfg());
        ICellPersistenceHost host = server;

        var client = new WorldClient(ct, MoveProtocol.CreateRegistry(), Encoding.UTF8.GetBytes("acct-1"));
        client.Connect();
        for (int i = 0; i < 20; i++) { client.Poll(); server.Poll(); server.Tick(1f / 30f); client.Update(1f / 30f); await Task.Yield(); }

        // The player lives in cell (0,0). Its snapshot must be empty (players persist separately).
        byte[]? snap = host.SnapshotCell(new CellCoord(0, 0));
        Assert.NotNull(snap);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, snap);    // Replication snapshot with entity count 0
    }

    [Fact]
    public void EnsureNextNetIdAtLeast_RaisesButNeverLowers()
    {
        var (st, _) = LoopbackTransport.CreatePair();
        ICellPersistenceHost host = new ShardedWorldServer(st, Cfg());
        int start = host.NextNetId;
        host.EnsureNextNetIdAtLeast(start + 10);
        Assert.Equal(start + 10, host.NextNetId);
        host.EnsureNextNetIdAtLeast(start);               // lower -> ignored
        Assert.Equal(start + 10, host.NextNetId);
    }
}
```

Note: confirm the exact `WorldClient` constructor + connect/poll loop against `ShardedWorldPersistenceTests.cs` and copy that harness shape (transport pair helper name, token argument). Adjust the loop to whatever that file uses to drive a join to completion.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ShardedCellPersistenceTests" -v q`
Expected: FAIL — `ShardedWorldServer` does not implement `ICellPersistenceHost` (cast/compile error).

- [ ] **Step 3: Write minimal implementation**

In `KhaozEngine.NetWorld/ShardedWorldServer.cs`:

Add `ICellPersistenceHost` to the class declaration:

```csharp
public sealed class ShardedWorldServer : IWorldPersistenceHost, IAdminControllable, ICellPersistenceHost
```

In the constructor, after `host` is created, bridge the sharding event to the coord-typed one (find where `host` is assigned and add):

```csharp
        host.CellCreated += cell => CellCreated?.Invoke(cell.Coord);
```

Add the interface members (near the other public members, e.g. after `Registry`). `CellCoord` and `CellSim` are already in scope via `using KhaozEngine.Sharding;`:

```csharp
    // --- ICellPersistenceHost: per-cell world-state persistence (non-player entities). ---

    /// <inheritdoc />
    public event Action<CellCoord>? CellCreated;

    /// <inheritdoc />
    public IReadOnlyCollection<CellCoord> LiveCellCoords
    {
        get
        {
            var coords = new List<CellCoord>(host.CellCount);
            foreach (CellSim cell in host.Cells) coords.Add(cell.Coord);
            return coords;
        }
    }

    /// <inheritdoc />
    public byte[]? SnapshotCell(CellCoord coord) =>
        host.TryGetCell(coord, out CellSim cell) ? cell.SnapshotOwned(new HashSet<int>(netIdBySlot.Values)) : null;

    /// <inheritdoc />
    public IReadOnlyList<int> RestoreCell(CellCoord coord, byte[] snapshot) =>
        host.TryGetCell(coord, out CellSim cell) ? cell.RestoreOwned(snapshot) : System.Array.Empty<int>();

    /// <inheritdoc />
    public void EnsureCell(CellCoord coord) => host.EnsureCell(coord);

    /// <inheritdoc />
    public int NextNetId => nextNetId;

    /// <inheritdoc />
    public void EnsureNextNetIdAtLeast(int atLeast) { if (atLeast > nextNetId) nextNetId = atLeast; }
```

Confirm the file already has `using System;` and `using System.Collections.Generic;` (it uses `Action`/`Dictionary` already). Add them if the build complains.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ShardedCellPersistenceTests" -v q`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the whole NetWorld + Sharding suite (no regression)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NetWorld|FullyQualifiedName~Sharding" -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.NetWorld/ShardedWorldServer.cs KhaozEngine.Tests/NetWorld/ShardedCellPersistenceTests.cs
git commit -m "netcode: ShardedWorldServer implements ICellPersistenceHost"
```

---

## Task 7: Integration — full round-trip across a host rebuild

**Files:**
- Test: `KhaozEngine.Tests/NetWorld/ShardedCellPersistenceTests.cs` (add integration test + a tiny test host over a real `ShardHost`)

**Interfaces:**
- Consumes: real `ShardHost` + `CellSim` primitives (Tasks 1-2), `CellPersistence` (Task 4), `InMemoryWorldStore`.
- Produces: proves a non-player entity persisted from one `ShardHost` reappears (with its NetId) in a fresh `ShardHost` built from the same store, and the allocator resumes above it.

- [ ] **Step 1: Write the failing test**

Append to `KhaozEngine.Tests/NetWorld/ShardedCellPersistenceTests.cs` a minimal `ICellPersistenceHost` over a real `ShardHost` plus its own NetId counter (this is the "host glue" a game writes), and the round-trip test. Add `using KhaozEngine.Ecs;`, `using KhaozEngine.Replication;`, `using KhaozEngine.WorldStore;`:

```csharp
    // A minimal real-ShardHost host: no players, own NetId counter. Mirrors what a game server implements.
    private sealed class GridHost : ICellPersistenceHost
    {
        public readonly ShardHost Host;
        private int nextNetId = 1;
        public GridHost(ReplicationRegistry r) { Host = new ShardHost(10f, 1f / 30f, r); Host.CellCreated += c => CellCreated?.Invoke(c.Coord); }
        public event System.Action<CellCoord>? CellCreated;
        public IReadOnlyCollection<CellCoord> LiveCellCoords { var l = new List<CellCoord>(); foreach (CellSim c in Host.Cells) l.Add(c.Coord); return l; } }
        public byte[]? SnapshotCell(CellCoord coord) => Host.TryGetCell(coord, out CellSim cell) ? cell.SnapshotOwned(new HashSet<int>()) : null;
        public IReadOnlyList<int> RestoreCell(CellCoord coord, byte[] snapshot) => Host.TryGetCell(coord, out CellSim cell) ? cell.RestoreOwned(snapshot) : System.Array.Empty<int>();
        public void EnsureCell(CellCoord coord) => Host.EnsureCell(coord);
        public int NextNetId => nextNetId;
        public void EnsureNextNetIdAtLeast(int atLeast) { if (atLeast > nextNetId) nextNetId = atLeast; }
        public int SpawnNode(float x, float y, int amount)
        {
            int id = nextNetId++;
            Entity e = Host.SpawnAt(x, y, out CellSim cell);
            cell.World.Set(e, new NetId(id));
            cell.World.Set(e, new ResourceNodeC { Amount = amount });
            return id;
        }
    }

    private struct ResourceNodeC : IComponent { public int Amount; }

    private static ReplicationRegistry NodeRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<ResourceNodeC>(typeId: 1, write: (n, bw) => bw.Write(n.Amount), read: br => new ResourceNodeC { Amount = br.ReadInt32() });
        return r;
    }

    [Fact]
    public async Task NonPlayerEntity_SurvivesHostRebuild_WithNetIdAndNoCollision()
    {
        var store = new InMemoryWorldStore();
        ReplicationRegistry r = NodeRegistry();

        // First run: spawn a node at (25,25) -> cell (2,2), persist, shut down.
        var g1 = new GridHost(r);
        int nodeId = g1.SpawnNode(25f, 25f, 77);
        var cp1 = new CellPersistence(g1, store);
        cp1.SaveDirtyPass();
        await cp1.FlushAsync();

        // Second run: fresh host + store. Preload instantiates cell (2,2) -> restore.
        var g2 = new GridHost(r);
        var cp2 = new CellPersistence(g2, store);
        await cp2.LoadMetaAsync();
        await cp2.PreloadAsync();
        await cp2.FlushAsync();

        Assert.True(g2.Host.TryGetCell(new CellCoord(2, 2), out CellSim cell));
        Assert.True(cell.TryGetOwned(nodeId, out Entity e));
        Assert.True(cell.World.TryGet(e, out ResourceNodeC n));
        Assert.Equal(77, n.Amount);
        Assert.True(g2.NextNetId > nodeId);              // allocator resumed above the restored id
    }
```

Note: fix the malformed `LiveCellCoords` getter above when transcribing — it must be a normal property body:

```csharp
        public IReadOnlyCollection<CellCoord> LiveCellCoords
        {
            get { var l = new List<CellCoord>(); foreach (CellSim c in Host.Cells) l.Add(c.Coord); return l; }
        }
```

- [ ] **Step 2: Run test to verify it fails, then passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ShardedCellPersistenceTests" -v q`
Expected: PASS (3 tests). If the round-trip fails, the bug is in the Task 1/4 production code — fix there.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/NetWorld/ShardedCellPersistenceTests.cs
git commit -m "netcode: cell-persistence host-rebuild round-trip integration test"
```

---

## Task 8: Sample — MmoServerSample demonstrates restart survival

**Files:**
- Modify: `MmoServerSample/MmoProtocol.cs`
- Modify: `MmoServerSample/MmoServer.cs`

**Interfaces:**
- Consumes: `ICellPersistenceHost`, `CellPersistence` (NetWorld), `ShardHost`/`CellSim` (Sharding), existing `MmoServer` fields.
- Produces: `MmoServer : ICellPersistenceHost`, a `ResourceNode` replicated component, and `MmoServer` methods `Task PreloadAsync()`, `Task FlushAsync()`, with `Update` folded into `Tick`.

Note: this is the reference sample, not a unit-tested surface. Verify by build + a scripted run (Step 4). `MmoServerSample` does not reference `KhaozEngine.NetWorld` yet — add the reference in Step 1.

- [ ] **Step 1: Add the NetWorld reference + ResourceNode component**

Add to `MmoServerSample/MmoServerSample.csproj` (inside the existing `<ItemGroup>` of package refs):

```xml
    <PackageReference Include="KhaozEngine.NetWorld" Version="$(KhaozEngineVersion)" />
```

In `MmoServerSample/MmoProtocol.cs`, add a component and register it (typeId 2, since `Position` is typeId 1):

```csharp
/// <summary>A static server-owned world resource (e.g. an ore vein). Non-player cell state that must survive a restart.</summary>
public struct ResourceNode : IComponent
{
    public int Amount;
}
```

In `MmoProtocol.CreateRegistry()`, after the `Position` registration, add:

```csharp
        r.Register<ResourceNode>(
            typeId: 2,
            write: (n, bw) => bw.Write(n.Amount),
            read: br => new ResourceNode { Amount = br.ReadInt32() });
```

- [ ] **Step 2: Make MmoServer implement ICellPersistenceHost + own a CellPersistence**

In `MmoServerSample/MmoServer.cs`:

Add usings: `using System.Collections.Generic;` (present), `using System.Threading.Tasks;`, `using KhaozEngine.NetWorld;`.

Change the class declaration:

```csharp
public sealed class MmoServer : ICellPersistenceHost
```

Add a `CellPersistence` field + construct it in the ctor (after `net` is assigned):

```csharp
    private readonly CellPersistence cellPersistence;
```

```csharp
        cellPersistence = new CellPersistence(this, store);
        host.CellCreated += cell => CellCreated?.Invoke(cell.Coord);
```

Add the interface members:

```csharp
    /// <inheritdoc />
    public event Action<CellCoord>? CellCreated;

    /// <inheritdoc />
    public IReadOnlyCollection<CellCoord> LiveCellCoords
    {
        get { var l = new List<CellCoord>(host.CellCount); foreach (CellSim c in host.Cells) l.Add(c.Coord); return l; }
    }

    /// <inheritdoc />
    public byte[]? SnapshotCell(CellCoord coord) =>
        host.TryGetCell(coord, out CellSim cell) ? cell.SnapshotOwned(new HashSet<int>(playerNetIdBySlot.Values)) : null;

    /// <inheritdoc />
    public IReadOnlyList<int> RestoreCell(CellCoord coord, byte[] snapshot) =>
        host.TryGetCell(coord, out CellSim cell) ? cell.RestoreOwned(snapshot) : Array.Empty<int>();

    /// <inheritdoc />
    public void EnsureCell(CellCoord coord) => host.EnsureCell(coord);

    /// <inheritdoc />
    public int NextNetId => nextNetId;

    /// <inheritdoc />
    public void EnsureNextNetIdAtLeast(int atLeast) { if (atLeast > nextNetId) nextNetId = atLeast; }

    /// <summary>Boot: resume the NetId allocator + instantiate saved cells, then apply restores. Call once before ticking.</summary>
    public async Task PreloadAsync()
    {
        await cellPersistence.LoadMetaAsync();
        await cellPersistence.PreloadAsync();
        await cellPersistence.FlushAsync();
    }

    /// <summary>Shutdown: persist all dirty cells + the NetId high-water. Call once when stopping.</summary>
    public Task FlushAsync() => cellPersistence.FlushAsync();

    /// <summary>Spawns a persistable resource node at a world position. Returns its NetId.</summary>
    public int SpawnResourceNode(float x, float y, int amount)
    {
        int netId = nextNetId++;
        Entity e = host.SpawnAt(x, y, out CellSim cell);
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new Position { X = x, Y = y });
        cell.World.Set(e, new ResourceNode { Amount = amount });
        return netId;
    }
```

Drive `cellPersistence.Update` from the existing `Tick(float dt)` (add as the first line):

```csharp
        cellPersistence.Update(dt);
```

- [ ] **Step 3: Build the sample**

Run: `dotnet build MmoServerSample/MmoServerSample.csproj -c Debug`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Demonstrate restart survival with a scripted run**

Add a small self-contained demo path to `MmoServerSample/Program.cs` guarded by an arg (so the normal server path is unchanged). At the top of `Main`, before the normal server setup:

```csharp
        if (args.Length > 0 && args[0] == "--persistence-demo")
        {
            var (a, _) = KhaozEngine.Netcode.LoopbackTransport.CreatePair();
            var store = new KhaozEngine.WorldStore.InMemoryWorldStore();
            // Share one store across two server "runs" to simulate a restart.
            var run1 = new MmoServer(a, new MmoServerConfig()) { };
            // NOTE: MmoServer's store is internal; for the demo, expose a ctor overload taking an IWorldStore.
            Console.WriteLine("persistence demo: see integration test ShardedCellPersistenceTests for the asserted round-trip.");
            return;
        }
```

Because `MmoServer` currently news its own `InMemoryWorldStore`, add a ctor overload so the demo (and any host) can inject a shared store:

```csharp
    public MmoServer(INetTransport transport, MmoServerConfig config) : this(transport, config, new InMemoryWorldStore()) { }

    public MmoServer(INetTransport transport, MmoServerConfig config, IWorldStore store)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        registry = CreateRegistry();
        host = new ShardHost(
            cellSize: config.CellSize, tickSeconds: config.TickSeconds, registry: registry,
            interestCellSize: config.CellSize, overlapMargin: config.OverlapMargin,
            positionAccessor: MmoProtocol.PositionAccessor);
        net = new NetServer(transport, config.MaxPlayers, new AllowAllAuthenticator());
        cellPersistence = new CellPersistence(this, store);
        host.CellCreated += cell => CellCreated?.Invoke(cell.Coord);
    }
```

Change the `store` field to be assigned in the ctor (remove the inline `= new InMemoryWorldStore()`):

```csharp
    private readonly IWorldStore store;
```

Then run: `dotnet run --project MmoServerSample/MmoServerSample.csproj -- --persistence-demo`
Expected: prints the demo line, exits 0. (The asserted round-trip lives in Task 7's integration test; the sample proves it compiles + wires end to end.)

- [ ] **Step 5: Commit**

```bash
git add MmoServerSample/
git commit -m "sample(mmo): CellPersistence wiring + ResourceNode, restart-survival demo"
```

---

## Task 9: Release ritual — version bump, docs, pack (batched, no push)

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `docs/USING-KHAOZENGINE.md`, `docs/DEPENDENCY-SEAMS.md`, `KhaozEngine.Sharding/README.md`, `KhaozEngine.NetWorld/README.md`.

**Interfaces:** none (release + docs).

- [ ] **Step 1: Read the current version + pick the next FREE one**

Run: `git fetch && git tag | sort -V | tail -5 && grep KhaozEngineVersion Directory.Build.props`
Determine the next free minor (e.g. if current is `9.1.0`, next is `9.2.0`; if a concurrent chat already took `9.2.0`, use the next free). Use that value everywhere below as `X.Y.Z`.

- [ ] **Step 2: Bump the version line**

In `Directory.Build.props`, set `<KhaozEngineVersion>X.Y.Z</KhaozEngineVersion>`.

- [ ] **Step 3: Add the CHANGELOG entry (newest-first, tight first sentence)**

Prepend under the top of `CHANGELOG.md`:

```markdown
## X.Y.Z

**Per-cell world-state persistence: a cell's authoritative non-player entities now survive a server restart,
keyed per cell in an `IWorldStore`.** Additive minor, new public API.

- **`KhaozEngine.Sharding`**: `CellSim.SnapshotOwned(IReadOnlySet<int> excludedNetIds)` /
  `CellSim.RestoreOwned(byte[])` / `CellSim.MaxOwnedNetId()` - snapshot/restore a cell's owned (non-ghost,
  non-migrating) entities via the existing Replication codec. `ShardHost.CellCreated` event (raised once per cell
  on first instantiation) + `ShardHost.EnsureCell(CellCoord)`.
- **`KhaozEngine.NetWorld`**: `CellPersistence` + `CellPersistenceConfig` wire an `IWorldStore` to a
  `ShardHost`-based server through the new `ICellPersistenceHost` seam - lazy load-on-cell-create, periodic dirty
  save of changed cells, `PreloadAsync`/`LoadMetaAsync`/`FlushAsync`, and a `WorldMetaRecord` NetId high-water
  mark so restored entities never collide with fresh spawns. `ShardedWorldServer` implements
  `ICellPersistenceHost`.
- Players stay out of scope (already persisted player-keyed by `WorldPersistence`); ghosts and migrating
  entities are excluded. A versioned blob header (`CellPersistenceConfig.SchemaVersion`) skips a save it cannot
  safely decode rather than mis-reading it.
- `MmoServerSample` gains a `ResourceNode` component + `CellPersistence` wiring demonstrating restart survival.
```

- [ ] **Step 4: Update the three guard-checked declarations**

- `docs/CONSUMERS.md`: set the "Engine current version" line to `X.Y.Z`.
- `docs/ROADMAP.md`: set "Current released version" to `X.Y.Z`, and **delete** the "Per-cell world-state snapshot persistence" bullet under "Overworld / world content" (it shipped).
- `README.md`: bump the `<PackageReference ... Version="X.Y.Z" />` example.

Run: `bash scripts/check-doc-versions.sh`
Expected: passes (all three match `<KhaozEngineVersion>`).

- [ ] **Step 5: Full doc sweep (feature docs, not just versions)**

- `README.md` package table: in the `KhaozEngine.Sharding` and `KhaozEngine.NetWorld` rows, note the new
  persistence primitives / `CellPersistence`.
- `KhaozEngine.Sharding/README.md`: document `SnapshotOwned`/`RestoreOwned`/`MaxOwnedNetId` + `CellCreated`/`EnsureCell`.
- `KhaozEngine.NetWorld/README.md`: document `CellPersistence`, `ICellPersistenceHost`, `WorldMetaRecord`.
- `docs/USING-KHAOZENGINE.md`: add a "Per-cell world persistence" section next to the player-persistence one,
  with the boot (`LoadMetaAsync` + `PreloadAsync` + `FlushAsync`), per-tick (`Update`), and shutdown (`FlushAsync`)
  wiring.
- `docs/DEPENDENCY-SEAMS.md`: add the `CellPersistence -> IWorldStore` + `ICellPersistenceHost` seam (and note
  `Sharding` gains no storage dep).

Mechanical check: `grep -rn "CellPersistence\|ICellPersistenceHost\|SnapshotOwned\|CellCreated" --include=*.md .` and confirm every doc that should mention them does.

- [ ] **Step 6: Build, test, pack**

```bash
mkdir -p local-feed
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -v q
dotnet pack -c Release -o ./local-feed
```
Expected: all tests pass; pack succeeds, `KhaozEngine.Sharding.X.Y.Z.nupkg` + `KhaozEngine.NetWorld.X.Y.Z.nupkg` (and the rest) land in `local-feed`.

- [ ] **Step 7: Commit (do NOT tag/push - the release is batched and confirmed with the user first)**

```bash
git add -A
git commit -m "netcode(X.Y.Z): per-cell world-state persistence"
```

Report to the user that the branch is ready to merge to `main`; the tag + push (CI publish) is held for the batch per the engine release policy.

---

## Self-Review

**Spec coverage:**
- Reuse Replication codec → Task 1 (`SnapshotWriter`/`ClientReplicationView`). ✓
- Cell-keyed store, load-on-create, dirty save, flush → Task 4. ✓
- Sharding gains primitives / NetWorld gains wiring split → Tasks 1-2 (Sharding), 3-6 (NetWorld). ✓
- Exclude players/ghosts/migrating → Task 1 + Task 6 (`SnapshotCell` passes player NetIds) + tests. ✓
- NetId high-water persistence → Task 4 (`WorldMetaRecord`, `LoadMetaAsync`, `SaveMetaIfAdvanced`) + Tasks 5/7 tests. ✓
- Versioned blob header guard → Task 4 (`Wrap`/`TryUnwrap`) + Task 5 test. ✓
- Optional startup preload over `IEnumerableWorldStore` → Task 4 (`PreloadAsync`) + Task 5/7 tests. ✓
- Server-thread application of async loads → Task 4 (`restoreQueue` + `Update`/`DrainRestores`). ✓
- Fixture entity + sample wiring → Task 7 (`ResourceNodeC` integration) + Task 8 (`ResourceNode` in sample). ✓
- Docs full-sweep + roadmap delete + release → Task 9. ✓

**Placeholder scan:** No TBD/TODO. The two "Note:" callouts (Task 6 harness shape, Task 7 malformed-getter fix) point the implementer at a concrete existing file / corrected code, not vague direction.

**Type consistency:** `ICellPersistenceHost` members are identical across Tasks 3, 6, 7, 8. `SnapshotOwned(IReadOnlySet<int>)`, `RestoreOwned(byte[]) -> IReadOnlyList<int>`, `MaxOwnedNetId()` consistent Task 1 ↔ 6/7. `CellCreated` is `Action<CellSim>` on `ShardHost` (Task 2) and `Action<CellCoord>` on `ICellPersistenceHost` (Task 3), bridged explicitly in Tasks 6/8 - intentional, called out at each bridge. `WorldMetaRecord.NextNetId` consistent Tasks 3/4/5/7.
</content>
