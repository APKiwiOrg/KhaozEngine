# Persistent World Store Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the authoritative world survive a server restart by adding two durable `IWorldStore` backends (SQLite, SQL Server) and a `WorldPersistence` orchestrator that load-on-joins, save-on-leaves, and periodically snapshots player records.

**Architecture:** The dependency-free `KhaozEngine.WorldStore` core (`IWorldStore` + `InMemoryWorldStore`) stays untouched. Two new opt-in packages each pull their own ADO.NET provider (`KhaozEngine.WorldStore.Sqlite` → `Microsoft.Data.Sqlite`; `KhaozEngine.WorldStore.SqlServer` → `Microsoft.Data.SqlClient`), mirroring how `KhaozEngine.Netcode.LiteNetLib` adds its UDP dep without touching the netcode core. A backend-agnostic `WorldPersistence` (in `KhaozEngine.NetWorld`, beside `WorldServer`) wires `IWorldStore` + `KhaozEngine.Serialization` into the `WorldServer` connect/disconnect/tick lifecycle. SQLite carries the always-on test coverage; SQL Server runs the same conformance suite gated behind a connection-string env var.

**Tech Stack:** .NET 10, raw parameterized async ADO.NET (no EF/ORM), `Microsoft.Data.Sqlite` 10.0.9, `Microsoft.Data.SqlClient` 6.1.6, `System.Text.Json` (via `KhaozEngine.Serialization`), xUnit 2.9.2.

## Global Constraints

- **No em-dashes** anywhere (code, comments, commits, docs). Use periods, commas, parentheses.
- **TDD.** Every new behaviour ships with a headless test in `KhaozEngine.Tests`. Write the failing test first.
- **No new netcode wire protocol.** The connect token is *already* transmitted in the Hello; this plan only surfaces it. No new opcodes/messages.
- **No EF/ORM.** Raw parameterized ADO.NET only. Async. Connection string injected via a small config record.
- **The dep-free `KhaozEngine.WorldStore` core stays unchanged** (no DB dependency leaks into it).
- **Forward-tolerant player record:** unknown fields in a stored record must be ignored on load (so a later field add never breaks old saves).
- **Stay in scope.** Do NOT build: per-cell/world-snapshot persistence (that is 6b sharding), record-schema migrations, accounts/auth (the key is an opaque `accountId`), connection pooling beyond provider defaults, EF/ORM.
- **One shared version line.** `<KhaozEngine5xVersion>` in `Directory.Build.props` governs every package; one minor bump (`7.48.0` → `7.49.0`) releases all. Each packable csproj sets `<Version>$(KhaozEngine5xVersion)</Version>`.
- **Two packages are ADDED**, so the FULL added-package doc sweep is required (see Task 9): `Directory.Build.props`, README catalog + repo-layout, `CLAUDE.md` package map + umbrellas, `docs/CONSUMERS.md`, `docs/USING-KHAOZENGINE.md`, the 3 guard declarations, `CHANGELOG.md` + `CHANGENOTES.md`, plus the `KhaozEngine.Server` umbrella csproj and `KhaozEngine.slnx`.
- **Commit subjects:** conventional `area(scope): summary`; on the version-bump commit the scope is the new version (e.g. `worldstore(7.49.0): ...`).
- Work happens in the worktree `feature/persistent-worldstore` (already created). `local-feed/` must exist before restore (`mkdir -p local-feed` — already done).

---

## File Structure

**New library code**
- `KhaozEngine.WorldStore.Sqlite/KhaozEngine.WorldStore.Sqlite.csproj` — new package, refs WorldStore + Microsoft.Data.Sqlite.
- `KhaozEngine.WorldStore.Sqlite/SqliteWorldStore.cs` — `SqliteWorldStore : IWorldStore, IDisposable` + `SqliteWorldStoreOptions`.
- `KhaozEngine.WorldStore.Sqlite/README.md` — package readme (packed).
- `KhaozEngine.WorldStore.SqlServer/KhaozEngine.WorldStore.SqlServer.csproj` — new package, refs WorldStore + Microsoft.Data.SqlClient.
- `KhaozEngine.WorldStore.SqlServer/SqlServerWorldStore.cs` — `SqlServerWorldStore : IWorldStore` + `SqlServerWorldStoreOptions`.
- `KhaozEngine.WorldStore.SqlServer/README.md` — package readme (packed).
- `KhaozEngine.NetWorld/PlayerRecord.cs` — `PlayerRecord` DTO + JSON encode/decode (forward-tolerant).
- `KhaozEngine.NetWorld/WorldPersistence.cs` — `WorldPersistence` + `WorldPersistenceConfig`.

**Modified library code**
- `KhaozEngine.Netcode/ServerSessionEvent.cs` — add `Joined(int slot, byte[] token)` overload (token rides in `Data`); doc the `Data` field's role for Joined events.
- `KhaozEngine.Netcode/NetServer.cs:85` — pass the Hello `token` into `ServerSessionEvent.Joined`.
- `KhaozEngine.NetWorld/WorldServer.cs` — accountId-from-token; `PlayerJoined`/`PlayerLeaving` events; `TryGetAccountId`, `TryGetPlayerState`, `JoinedSlots`, `SetPlayerState`.
- `KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj` — add `KhaozEngine.Serialization` ProjectReference.
- `KhaozEngine.Server/KhaozEngine.Server.csproj` — add the two new WorldStore backend ProjectReferences.
- `KhaozEngine.slnx` — register the two new projects.
- `NetworkedWalkServer/Program.cs` + `.csproj` — wire `WorldPersistence` + `SqliteWorldStore`.
- `NetworkedWalkSample/Program.cs` — send a stable account token so reconnect restores position.

**Tests**
- `KhaozEngine.Tests/WorldStore/WorldStoreConformance.cs` — shared conformance logic (no `[Fact]`s; methods take `(IWorldStore, string ns)`).
- `KhaozEngine.Tests/WorldStore/WorldStoreTests.cs` — REPLACE existing: `InMemoryWorldStoreConformanceTests` + `SqliteWorldStoreConformanceTests` (always-on) + `SqlServerWorldStoreConformanceTests` (gated).
- `KhaozEngine.Tests/WorldStore/SqlServerFactAttribute.cs` — env-gated `[Fact]` (mirrors `GpuFactAttribute`).
- `KhaozEngine.Tests/NetWorld/PlayerRecordTests.cs` — encode/decode round-trip + forward-tolerance.
- `KhaozEngine.Tests/NetWorld/WorldPersistenceTests.cs` — load-on-join, save-on-leave, periodic snapshot, restart-survival.
- `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — add the two new backend ProjectReferences.

---

## Task 1: Shared conformance harness, run against `InMemoryWorldStore`

Establishes the one shared `IWorldStore` conformance suite and proves it against the existing in-memory backend before any DB code exists. Replaces the ad-hoc `WorldStoreTests.cs`.

**Files:**
- Create: `KhaozEngine.Tests/WorldStore/WorldStoreConformance.cs`
- Modify (replace contents): `KhaozEngine.Tests/WorldStore/WorldStoreTests.cs`

**Interfaces:**
- Consumes: `KhaozEngine.WorldStore.IWorldStore`, `KhaozEngine.WorldStore.InMemoryWorldStore`.
- Produces: `static class WorldStoreConformance` with these `public static async Task` methods, each taking `(IWorldStore s, string ns)` and prefixing every key with `ns` (so a shared DB backend can isolate tests by passing a fresh `ns`): `SaveLoad_RoundTrips`, `Save_Overwrites`, `Load_Absent_ReturnsNull`, `Delete_PresentThenAbsent`, `Exists_TracksPresence`, `Keys_AreIsolated`, `Bytes_AreExact`, `Concurrent_DistinctKeys`. Later tasks add per-backend `[Fact]`/`[SqlServerFact]` classes that call these.

- [ ] **Step 1: Write the shared conformance helper**

Create `KhaozEngine.Tests/WorldStore/WorldStoreConformance.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>
/// The single shared <see cref="IWorldStore"/> contract suite. Run against every backend (InMemory, SQLite
/// always; SQL Server gated). Every key is prefixed with <c>ns</c> so a shared-database backend can isolate a
/// run by passing a fresh namespace; the in-memory/file backends get a fresh store per test and ignore it.
/// </summary>
internal static class WorldStoreConformance
{
    public static async Task SaveLoad_RoundTrips(IWorldStore s, string ns)
    {
        await s.SaveAsync(ns + "k", new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, await s.LoadAsync(ns + "k"));
    }

    public static async Task Save_Overwrites(IWorldStore s, string ns)
    {
        await s.SaveAsync(ns + "k", new byte[] { 1 });
        await s.SaveAsync(ns + "k", new byte[] { 9, 9 });
        Assert.Equal(new byte[] { 9, 9 }, await s.LoadAsync(ns + "k"));
    }

    public static async Task Load_Absent_ReturnsNull(IWorldStore s, string ns)
        => Assert.Null(await s.LoadAsync(ns + "missing"));

    public static async Task Delete_PresentThenAbsent(IWorldStore s, string ns)
    {
        await s.SaveAsync(ns + "k", new byte[] { 1 });
        Assert.True(await s.DeleteAsync(ns + "k"));     // present -> removed
        Assert.False(await s.DeleteAsync(ns + "k"));    // already gone
    }

    public static async Task Exists_TracksPresence(IWorldStore s, string ns)
    {
        Assert.False(await s.ExistsAsync(ns + "k"));
        await s.SaveAsync(ns + "k", new byte[] { 1 });
        Assert.True(await s.ExistsAsync(ns + "k"));
        await s.DeleteAsync(ns + "k");
        Assert.False(await s.ExistsAsync(ns + "k"));
    }

    public static async Task Keys_AreIsolated(IWorldStore s, string ns)
    {
        await s.SaveAsync(ns + "a", new byte[] { 1 });
        await s.SaveAsync(ns + "b", new byte[] { 2 });
        await s.DeleteAsync(ns + "a");
        Assert.Null(await s.LoadAsync(ns + "a"));
        Assert.Equal(new byte[] { 2 }, await s.LoadAsync(ns + "b"));   // b untouched
    }

    public static async Task Bytes_AreExact(IWorldStore s, string ns)
    {
        var data = new byte[256];
        for (int i = 0; i < 256; i++) data[i] = (byte)i;   // every byte value incl 0x00
        await s.SaveAsync(ns + "blob", data);
        Assert.Equal(data, await s.LoadAsync(ns + "blob"));
    }

    public static async Task Concurrent_DistinctKeys(IWorldStore s, string ns)
    {
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            int n = i;
            tasks.Add(s.SaveAsync(ns + "k" + n, new byte[] { (byte)n }));
        }
        await Task.WhenAll(tasks);
        for (int i = 0; i < 50; i++)
            Assert.Equal(new byte[] { (byte)i }, await s.LoadAsync(ns + "k" + i));
    }
}
```

- [ ] **Step 2: Replace `WorldStoreTests.cs` with the in-memory conformance class**

Replace the entire contents of `KhaozEngine.Tests/WorldStore/WorldStoreTests.cs`:

```csharp
using System;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>The shared conformance suite against the dependency-free in-memory backend.</summary>
public class InMemoryWorldStoreConformanceTests
{
    private static IWorldStore New() => new InMemoryWorldStore();
    private static string Ns() => Guid.NewGuid().ToString("N");

    [Fact] public Task SaveLoad_RoundTrips() => WorldStoreConformance.SaveLoad_RoundTrips(New(), Ns());
    [Fact] public Task Save_Overwrites() => WorldStoreConformance.Save_Overwrites(New(), Ns());
    [Fact] public Task Load_Absent_ReturnsNull() => WorldStoreConformance.Load_Absent_ReturnsNull(New(), Ns());
    [Fact] public Task Delete_PresentThenAbsent() => WorldStoreConformance.Delete_PresentThenAbsent(New(), Ns());
    [Fact] public Task Exists_TracksPresence() => WorldStoreConformance.Exists_TracksPresence(New(), Ns());
    [Fact] public Task Keys_AreIsolated() => WorldStoreConformance.Keys_AreIsolated(New(), Ns());
    [Fact] public Task Bytes_AreExact() => WorldStoreConformance.Bytes_AreExact(New(), Ns());
    [Fact] public Task Concurrent_DistinctKeys() => WorldStoreConformance.Concurrent_DistinctKeys(New(), Ns());

    [Fact]
    public async Task Load_ReturnsIndependentCopy()
    {
        IWorldStore store = new InMemoryWorldStore();
        await store.SaveAsync("k", new byte[] { 1, 2, 3 });
        byte[] first = (await store.LoadAsync("k"))!;
        first[0] = 99;                                  // mutate the returned array
        byte[] second = (await store.LoadAsync("k"))!;
        Assert.Equal(new byte[] { 1, 2, 3 }, second);   // stored state unaffected
    }
}
```

- [ ] **Step 3: Run the in-memory conformance tests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~InMemoryWorldStoreConformanceTests"`
Expected: PASS (9 tests). If `dotnet test` rebuilds the whole solution and that is slow, that is fine.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Tests/WorldStore/WorldStoreConformance.cs KhaozEngine.Tests/WorldStore/WorldStoreTests.cs
git commit -m "test(worldstore): shared IWorldStore conformance suite over InMemoryWorldStore"
```

---

## Task 2: `KhaozEngine.WorldStore.Sqlite` package + `SqliteWorldStore`

The embedded, zero-infra, always-tested durable backend.

**Files:**
- Create: `KhaozEngine.WorldStore.Sqlite/KhaozEngine.WorldStore.Sqlite.csproj`
- Create: `KhaozEngine.WorldStore.Sqlite/SqliteWorldStore.cs`
- Create: `KhaozEngine.WorldStore.Sqlite/README.md`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj` (add ProjectReference)
- Modify: `KhaozEngine.Tests/WorldStore/WorldStoreTests.cs` (add `SqliteWorldStoreConformanceTests`)
- Modify: `KhaozEngine.slnx` (register project)

**Interfaces:**
- Consumes: `IWorldStore`.
- Produces: `sealed record SqliteWorldStoreOptions(string ConnectionString)`; `sealed class SqliteWorldStore : IWorldStore, IDisposable` with ctors `SqliteWorldStore(SqliteWorldStoreOptions options)` and `SqliteWorldStore(string connectionString)`.

- [ ] **Step 1: Create the package csproj**

Create `KhaozEngine.WorldStore.Sqlite/KhaozEngine.WorldStore.Sqlite.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.WorldStore.Sqlite</PackageId>
    <Version>$(KhaozEngine5xVersion)</Version>
    <Description>SQLite IWorldStore backend over Microsoft.Data.Sqlite: SqliteWorldStore persists the authoritative keyed byte[] world store to an embedded SQLite database (one world_store table, schema bootstrapped on construction, upsert via INSERT ... ON CONFLICT, raw parameterized async ADO.NET, no EF/ORM). The zero-infra dev/test and single-node backend; what keeps server persistence headless-testable. Opt-in: pulls Microsoft.Data.Sqlite without touching the dependency-free KhaozEngine.WorldStore core.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.9" />
    <ProjectReference Include="../KhaozEngine.WorldStore/KhaozEngine.WorldStore.csproj" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Register in the solution**

In `KhaozEngine.slnx`, add this line in the alphabetical block near the other `KhaozEngine.WorldStore` entry (after the `KhaozEngine.WorldStore/KhaozEngine.WorldStore.csproj` line):

```xml
  <Project Path="KhaozEngine.WorldStore.Sqlite/KhaozEngine.WorldStore.Sqlite.csproj" />
```

- [ ] **Step 3: Write the failing Sqlite conformance tests**

Add to `KhaozEngine.Tests/WorldStore/WorldStoreTests.cs` (append a new class; add `using System.IO;` and `using KhaozEngine.WorldStore.Sqlite;` at the top of the file):

```csharp
/// <summary>The shared conformance suite against the on-disk SQLite backend (a fresh temp DB per test).</summary>
public sealed class SqliteWorldStoreConformanceTests : IDisposable
{
    private readonly string path;
    private readonly SqliteWorldStore store;

    public SqliteWorldStoreConformanceTests()
    {
        path = Path.Combine(Path.GetTempPath(), "ke-ws-" + Guid.NewGuid().ToString("N") + ".db");
        store = new SqliteWorldStore($"Data Source={path}");
    }

    public void Dispose()
    {
        store.Dispose();
        foreach (string p in new[] { path, path + "-wal", path + "-shm" })
            try { File.Delete(p); } catch { /* best effort */ }
    }

    [Fact] public Task SaveLoad_RoundTrips() => WorldStoreConformance.SaveLoad_RoundTrips(store, "");
    [Fact] public Task Save_Overwrites() => WorldStoreConformance.Save_Overwrites(store, "");
    [Fact] public Task Load_Absent_ReturnsNull() => WorldStoreConformance.Load_Absent_ReturnsNull(store, "");
    [Fact] public Task Delete_PresentThenAbsent() => WorldStoreConformance.Delete_PresentThenAbsent(store, "");
    [Fact] public Task Exists_TracksPresence() => WorldStoreConformance.Exists_TracksPresence(store, "");
    [Fact] public Task Keys_AreIsolated() => WorldStoreConformance.Keys_AreIsolated(store, "");
    [Fact] public Task Bytes_AreExact() => WorldStoreConformance.Bytes_AreExact(store, "");
    [Fact] public Task Concurrent_DistinctKeys() => WorldStoreConformance.Concurrent_DistinctKeys(store, "");

    [Fact]
    public async Task SurvivesReopen_OnSameFile()
    {
        await store.SaveAsync("durable", new byte[] { 7, 8, 9 });
        using var reopened = new SqliteWorldStore($"Data Source={path}");   // fresh store, same file
        Assert.Equal(new byte[] { 7, 8, 9 }, await reopened.LoadAsync("durable"));
    }
}
```

Add the ProjectReference in `KhaozEngine.Tests/KhaozEngine.Tests.csproj` (in the `<ItemGroup>` with the other engine project references, alphabetically after the `KhaozEngine.WorldStore` line):

```xml
    <ProjectReference Include="../KhaozEngine.WorldStore.Sqlite/KhaozEngine.WorldStore.Sqlite.csproj" />
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet build KhaozEngine.WorldStore.Sqlite/KhaozEngine.WorldStore.Sqlite.csproj`
Expected: FAIL to compile (`SqliteWorldStore` does not exist).

- [ ] **Step 5: Implement `SqliteWorldStore`**

Create `KhaozEngine.WorldStore.Sqlite/SqliteWorldStore.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

/// <summary>Connection config for <see cref="SqliteWorldStore"/>. Inject the ADO.NET connection string
/// (for example <c>Data Source=world.db</c>); no other knobs, pooling stays at the provider default.</summary>
public sealed record SqliteWorldStoreOptions(string ConnectionString);

/// <summary>
/// SQLite-backed <see cref="IWorldStore"/> over Microsoft.Data.Sqlite. One <c>world_store(key, data, updated_at)</c>
/// table, bootstrapped on construction; upsert via <c>INSERT ... ON CONFLICT(key) DO UPDATE</c>; raw parameterized
/// async ADO.NET, no EF/ORM. Holds one open connection (so an in-memory <c>Data Source=:memory:</c> string keeps its
/// data) and serializes operations with a semaphore, so SQLite never sees concurrent commands on the shared
/// connection. The embedded dev/test and single-node backend.
/// </summary>
public sealed class SqliteWorldStore : IWorldStore, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SqliteWorldStore(SqliteWorldStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connection = new SqliteConnection(options.ConnectionString);
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE IF NOT EXISTS world_store (" +
            "key TEXT PRIMARY KEY, data BLOB NOT NULL, updated_at INTEGER NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Convenience ctor taking the raw connection string.</summary>
    public SqliteWorldStore(string connectionString) : this(new SqliteWorldStoreOptions(connectionString)) { }

    public async Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT data FROM world_store WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result as byte[];   // absent row or DBNull -> null
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO world_store (key, data, updated_at) VALUES ($k, $d, $t) " +
                "ON CONFLICT(key) DO UPDATE SET data = excluded.data, updated_at = excluded.updated_at;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$d", data);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM world_store WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM world_store WHERE key = $k LIMIT 1;";
            cmd.Parameters.AddWithValue("$k", key);
            object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is not null;
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        connection.Dispose();
        gate.Dispose();
    }
}
```

- [ ] **Step 6: Write the package README**

Create `KhaozEngine.WorldStore.Sqlite/README.md`:

```markdown
# KhaozEngine.WorldStore.Sqlite

SQLite `IWorldStore` backend over `Microsoft.Data.Sqlite`. The embedded, zero-infra dev/test and single-node
durable store for an authoritative world.

```csharp
using KhaozEngine.WorldStore.Sqlite;

IWorldStore store = new SqliteWorldStore("Data Source=world.db");
await store.SaveAsync("player:42", bytes);
byte[]? loaded = await store.LoadAsync("player:42");
```

One `world_store(key, data, updated_at)` table, bootstrapped on construction; upsert via
`INSERT ... ON CONFLICT(key) DO UPDATE`; raw parameterized async ADO.NET (no EF/ORM). Dispose the store to
close the connection. For production / Azure SQL use `KhaozEngine.WorldStore.SqlServer` against the same
`IWorldStore` contract.
```

- [ ] **Step 7: Run the Sqlite conformance + survives-reopen tests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SqliteWorldStoreConformanceTests"`
Expected: PASS (9 tests).

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.WorldStore.Sqlite KhaozEngine.slnx KhaozEngine.Tests
git commit -m "feat(worldstore): KhaozEngine.WorldStore.Sqlite (SqliteWorldStore over Microsoft.Data.Sqlite)"
```

---

## Task 3: `KhaozEngine.WorldStore.SqlServer` package + `SqlServerWorldStore`

The production (Azure SQL) backend. Same `IWorldStore` contract; conformance gated behind an env var so CI (no DB) skips it.

**Files:**
- Create: `KhaozEngine.WorldStore.SqlServer/KhaozEngine.WorldStore.SqlServer.csproj`
- Create: `KhaozEngine.WorldStore.SqlServer/SqlServerWorldStore.cs`
- Create: `KhaozEngine.WorldStore.SqlServer/README.md`
- Create: `KhaozEngine.Tests/WorldStore/SqlServerFactAttribute.cs`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj` (add ProjectReference)
- Modify: `KhaozEngine.Tests/WorldStore/WorldStoreTests.cs` (add `SqlServerWorldStoreConformanceTests`)
- Modify: `KhaozEngine.slnx` (register project)

**Interfaces:**
- Consumes: `IWorldStore`.
- Produces: `sealed record SqlServerWorldStoreOptions(string ConnectionString)`; `sealed class SqlServerWorldStore : IWorldStore` with ctors `SqlServerWorldStore(SqlServerWorldStoreOptions options)` and `SqlServerWorldStore(string connectionString)`; `sealed class SqlServerFactAttribute : FactAttribute` (skips unless `KE_SQLSERVER_TEST_CONNSTRING` is set).

- [ ] **Step 1: Create the package csproj**

Create `KhaozEngine.WorldStore.SqlServer/KhaozEngine.WorldStore.SqlServer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.WorldStore.SqlServer</PackageId>
    <Version>$(KhaozEngine5xVersion)</Version>
    <Description>SQL Server / Azure SQL IWorldStore backend over Microsoft.Data.SqlClient: SqlServerWorldStore persists the authoritative keyed byte[] world store to a SQL Server database (one world_store table, schema bootstrapped on construction, upsert via MERGE WITH (HOLDLOCK), raw parameterized async ADO.NET, no EF/ORM). The production backend (Azure SQL); same IWorldStore contract as the SQLite dev/test backend. Opt-in: pulls Microsoft.Data.SqlClient without touching the dependency-free KhaozEngine.WorldStore core.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" Version="6.1.6" />
    <ProjectReference Include="../KhaozEngine.WorldStore/KhaozEngine.WorldStore.csproj" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Register in the solution**

In `KhaozEngine.slnx`, add after the `KhaozEngine.WorldStore.Sqlite` line:

```xml
  <Project Path="KhaozEngine.WorldStore.SqlServer/KhaozEngine.WorldStore.SqlServer.csproj" />
```

- [ ] **Step 3: Implement `SqlServerWorldStore`**

Create `KhaozEngine.WorldStore.SqlServer/SqlServerWorldStore.cs`:

```csharp
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

/// <summary>Connection config for <see cref="SqlServerWorldStore"/>. Inject the ADO.NET connection string
/// (for example an Azure SQL connection string); pooling stays at the provider default.</summary>
public sealed record SqlServerWorldStoreOptions(string ConnectionString);

/// <summary>
/// SQL Server / Azure SQL <see cref="IWorldStore"/> over Microsoft.Data.SqlClient. One
/// <c>world_store([key], data, updated_at)</c> table, bootstrapped on construction; upsert via
/// <c>MERGE ... WITH (HOLDLOCK)</c> (race-safe single-row upsert); raw parameterized async ADO.NET, no EF/ORM.
/// Opens a short-lived pooled connection per operation (SqlClient pools by connection string). The production
/// backend; identical contract to the SQLite dev/test backend.
/// </summary>
public sealed class SqlServerWorldStore : IWorldStore
{
    private readonly string connectionString;

    public SqlServerWorldStore(SqlServerWorldStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connectionString = options.ConnectionString
            ?? throw new ArgumentException("ConnectionString is required.", nameof(options));
        EnsureSchema();
    }

    /// <summary>Convenience ctor taking the raw connection string.</summary>
    public SqlServerWorldStore(string connectionString) : this(new SqlServerWorldStoreOptions(connectionString)) { }

    private void EnsureSchema()
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "IF OBJECT_ID(N'dbo.world_store', N'U') IS NULL " +
            "CREATE TABLE dbo.world_store (" +
            "[key] NVARCHAR(450) NOT NULL PRIMARY KEY, " +
            "data VARBINARY(MAX) NOT NULL, " +
            "updated_at DATETIME2 NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    public async Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM dbo.world_store WHERE [key] = @k;";
        cmd.Parameters.AddWithValue("@k", key);
        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is byte[] b ? b : null;   // absent or DBNull -> null
    }

    public async Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "MERGE dbo.world_store WITH (HOLDLOCK) AS t " +
            "USING (SELECT @k AS [key]) AS s ON t.[key] = s.[key] " +
            "WHEN MATCHED THEN UPDATE SET data = @d, updated_at = SYSUTCDATETIME() " +
            "WHEN NOT MATCHED THEN INSERT ([key], data, updated_at) VALUES (@k, @d, SYSUTCDATETIME());";
        cmd.Parameters.AddWithValue("@k", key);
        SqlParameter d = cmd.Parameters.Add("@d", SqlDbType.VarBinary, -1);   // -1 = MAX
        d.Value = data;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.world_store WHERE [key] = @k;";
        cmd.Parameters.AddWithValue("@k", key);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.world_store WHERE [key] = @k;";
        cmd.Parameters.AddWithValue("@k", key);
        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }
}
```

- [ ] **Step 4: Write the env-gated fact attribute**

Create `KhaozEngine.Tests/WorldStore/SqlServerFactAttribute.cs`:

```csharp
using System;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>
/// A <see cref="FactAttribute"/> SKIPPED unless <c>KE_SQLSERVER_TEST_CONNSTRING</c> is set to a reachable SQL
/// Server / Azure SQL connection string. The SQLite backend carries the always-on coverage; CI has no SQL
/// Server, so these run only locally / against a test DB on demand. (Mirrors <c>GpuFactAttribute</c>.)
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING")))
            Skip = "set KE_SQLSERVER_TEST_CONNSTRING to run SQL Server world-store conformance";
    }
}
```

- [ ] **Step 5: Write the gated SqlServer conformance class**

Add to `KhaozEngine.Tests/WorldStore/WorldStoreTests.cs` (add `using KhaozEngine.WorldStore.SqlServer;` to the top of the file):

```csharp
/// <summary>The shared conformance suite against SQL Server / Azure SQL, gated behind KE_SQLSERVER_TEST_CONNSTRING
/// (skipped in CI where no SQL Server exists). Each test runs under a fresh key namespace to isolate the shared table.</summary>
public sealed class SqlServerWorldStoreConformanceTests
{
    private static IWorldStore New()
        => new SqlServerWorldStore(Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING")!);
    private static string Ns() => Guid.NewGuid().ToString("N") + ":";

    [SqlServerFact] public Task SaveLoad_RoundTrips() => WorldStoreConformance.SaveLoad_RoundTrips(New(), Ns());
    [SqlServerFact] public Task Save_Overwrites() => WorldStoreConformance.Save_Overwrites(New(), Ns());
    [SqlServerFact] public Task Load_Absent_ReturnsNull() => WorldStoreConformance.Load_Absent_ReturnsNull(New(), Ns());
    [SqlServerFact] public Task Delete_PresentThenAbsent() => WorldStoreConformance.Delete_PresentThenAbsent(New(), Ns());
    [SqlServerFact] public Task Exists_TracksPresence() => WorldStoreConformance.Exists_TracksPresence(New(), Ns());
    [SqlServerFact] public Task Keys_AreIsolated() => WorldStoreConformance.Keys_AreIsolated(New(), Ns());
    [SqlServerFact] public Task Bytes_AreExact() => WorldStoreConformance.Bytes_AreExact(New(), Ns());
    [SqlServerFact] public Task Concurrent_DistinctKeys() => WorldStoreConformance.Concurrent_DistinctKeys(New(), Ns());
}
```

Add the ProjectReference in `KhaozEngine.Tests/KhaozEngine.Tests.csproj` (after the `KhaozEngine.WorldStore.Sqlite` line):

```xml
    <ProjectReference Include="../KhaozEngine.WorldStore.SqlServer/KhaozEngine.WorldStore.SqlServer.csproj" />
```

- [ ] **Step 6: Write the package README**

Create `KhaozEngine.WorldStore.SqlServer/README.md`:

```markdown
# KhaozEngine.WorldStore.SqlServer

SQL Server / Azure SQL `IWorldStore` backend over `Microsoft.Data.SqlClient`. The production durable store for an
authoritative world; identical `IWorldStore` contract to the SQLite dev/test backend.

```csharp
using KhaozEngine.WorldStore.SqlServer;

IWorldStore store = new SqlServerWorldStore(
    "Server=tcp:my.database.windows.net,1433;Database=ruinborne;Authentication=Active Directory Default;Encrypt=True;");
await store.SaveAsync("player:42", bytes);
byte[]? loaded = await store.LoadAsync("player:42");
```

One `world_store([key], data, updated_at)` table, bootstrapped on construction; upsert via
`MERGE ... WITH (HOLDLOCK)`; raw parameterized async ADO.NET (no EF/ORM); a short-lived pooled connection per
operation. For dev/test use `KhaozEngine.WorldStore.Sqlite` against the same contract.
```

- [ ] **Step 7: Build the package and verify gated tests skip**

Run: `dotnet build KhaozEngine.WorldStore.SqlServer/KhaozEngine.WorldStore.SqlServer.csproj`
Expected: build succeeds.
Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SqlServerWorldStoreConformanceTests"`
Expected: 8 tests, all SKIPPED (env var not set) — reported as skipped, not failed.

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.WorldStore.SqlServer KhaozEngine.slnx KhaozEngine.Tests
git commit -m "feat(worldstore): KhaozEngine.WorldStore.SqlServer (SqlServerWorldStore, gated conformance)"
```

---

## Task 4: `PlayerRecord` — forward-tolerant serialized player record

The serialized shape behind `player:{accountId}`. JSON via `KhaozEngine.Serialization`, forward-tolerant (unknown fields ignored).

**Files:**
- Create: `KhaozEngine.NetWorld/PlayerRecord.cs`
- Modify: `KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj` (add Serialization ref)
- Create: `KhaozEngine.Tests/NetWorld/PlayerRecordTests.cs`

**Interfaces:**
- Consumes: `KhaozEngine.NetWorld.PlayerMoveState`, `KhaozEngine.Serialization.JsonDefaults`.
- Produces: `sealed class PlayerRecord` with `int Version`, `float X/Y/Z`; `static PlayerRecord From(in PlayerMoveState)`; `PlayerMoveState ToState()`; `byte[] Encode()`; `static PlayerRecord Decode(byte[])`.

- [ ] **Step 1: Add the Serialization project reference**

In `KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj`, add to the ProjectReference `<ItemGroup>`:

```xml
    <ProjectReference Include="../KhaozEngine.Serialization/KhaozEngine.Serialization.csproj" />
```

- [ ] **Step 2: Write the failing tests**

Create `KhaozEngine.Tests/NetWorld/PlayerRecordTests.cs`:

```csharp
using System.Numerics;
using System.Text;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerRecordTests
{
    [Fact]
    public void EncodeDecode_RoundTripsPosition()
    {
        var state = new PlayerMoveState { Position = new Vector3(12.5f, 3.25f, -7f) };
        byte[] bytes = PlayerRecord.From(state).Encode();
        PlayerMoveState back = PlayerRecord.Decode(bytes).ToState();
        Assert.Equal(state.Position, back.Position);
    }

    [Fact]
    public void Decode_IgnoresUnknownFields()
    {
        // A record written by a FUTURE version with extra fields must still load (forward tolerance).
        byte[] forward = Encoding.UTF8.GetBytes(
            "{\"Version\":2,\"X\":1.0,\"Y\":2.0,\"Z\":3.0,\"Facing\":90.0,\"Health\":100}");
        PlayerRecord rec = PlayerRecord.Decode(forward);
        Assert.Equal(new Vector3(1f, 2f, 3f), rec.ToState().Position);
    }

    [Fact]
    public void Decode_MissingFieldsDefaultToZero()
    {
        // An OLD record missing newer fields still loads; absent numerics default to 0.
        byte[] old = Encoding.UTF8.GetBytes("{\"X\":4.0,\"Z\":6.0}");
        PlayerRecord rec = PlayerRecord.Decode(old);
        Assert.Equal(new Vector3(4f, 0f, 6f), rec.ToState().Position);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet build KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj`
Expected: FAIL to compile (`PlayerRecord` does not exist).

- [ ] **Step 4: Implement `PlayerRecord`**

Create `KhaozEngine.NetWorld/PlayerRecord.cs`:

```csharp
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Serialization;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The serialized player record stored under <c>player:{accountId}</c>. Flattens <see cref="PlayerMoveState"/>
/// to a versioned JSON DTO (via <see cref="KhaozEngine.Serialization.JsonDefaults"/>). Forward-tolerant: the
/// tolerant reader ignores unknown JSON members, so adding fields later (facing, health, inventory) never
/// breaks an old save, and an old save missing a newer field just gets the default. Extend by adding properties.
/// </summary>
public sealed class PlayerRecord
{
    /// <summary>Record schema version; bump when the shape changes meaningfully.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Capsule-centre X.</summary>
    public float X { get; set; }
    /// <summary>Capsule-centre Y (ground-clamped).</summary>
    public float Y { get; set; }
    /// <summary>Capsule-centre Z.</summary>
    public float Z { get; set; }

    /// <summary>Builds a record from the live movement state.</summary>
    public static PlayerRecord From(in PlayerMoveState state) =>
        new() { X = state.Position.X, Y = state.Position.Y, Z = state.Position.Z };

    /// <summary>Reconstructs the movement state from this record.</summary>
    public PlayerMoveState ToState() => new() { Position = new Vector3(X, Y, Z) };

    /// <summary>Serializes to UTF-8 JSON bytes for the world store.</summary>
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, JsonDefaults.IndentedWrite);

    /// <summary>Deserializes from world-store bytes; tolerant of unknown / missing fields.</summary>
    public static PlayerRecord Decode(byte[] data) =>
        JsonSerializer.Deserialize<PlayerRecord>(data, JsonDefaults.TolerantRead) ?? new PlayerRecord();
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PlayerRecordTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.NetWorld/PlayerRecord.cs KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj KhaozEngine.Tests/NetWorld/PlayerRecordTests.cs
git commit -m "feat(networld): PlayerRecord (forward-tolerant JSON player record via Serialization)"
```

---

## Task 5: `WorldServer` persistence lifecycle seam (+ surface the connect token)

Give `WorldServer` the hooks `WorldPersistence` needs: an account id derived from the connect token, join/leave events, and accessors to read/override player state. No new wire protocol — the token is already in the Hello; we just stop dropping it.

**Files:**
- Modify: `KhaozEngine.Netcode/ServerSessionEvent.cs`
- Modify: `KhaozEngine.Netcode/NetServer.cs:85`
- Modify: `KhaozEngine.NetWorld/WorldServer.cs`
- Create: `KhaozEngine.Tests/NetWorld/WorldServerPersistenceHooksTests.cs`

**Interfaces:**
- Consumes: existing `WorldServer`, `LoopbackTransport`, `NetServer`, `NetClient` from `KhaozEngine.Netcode`.
- Produces on `WorldServer`: `event Action<int, string>? PlayerJoined` (slot, accountId — raised after spawn); `event Action<int, string, PlayerMoveState>? PlayerLeaving` (slot, accountId, final state — raised before despawn); `bool TryGetAccountId(int slot, out string accountId)`; `bool TryGetPlayerState(int slot, out PlayerMoveState state)`; `IReadOnlyCollection<int> JoinedSlots`; `void SetPlayerState(int slot, in PlayerMoveState state)`. On `ServerSessionEvent`: `static ServerSessionEvent Joined(int slot, byte[] token)`.

- [ ] **Step 1: Surface the connect token on the Joined event**

In `KhaozEngine.Netcode/ServerSessionEvent.cs`, update the `Data` doc comment and add a token overload. Replace the existing `Joined` factory block:

```csharp
    /// <summary>Game payload for a <see cref="ServerSessionEventKind.Data"/> event; for a
    /// <see cref="ServerSessionEventKind.Joined"/> event it carries the connect token the client presented in
    /// its Hello (empty if none). Empty for <see cref="ServerSessionEventKind.Left"/>.</summary>
    public byte[] Data { get; }
```

(the `Data` property doc above replaces the current one), and replace the `Joined(int slot)` factory with:

```csharp
    /// <summary>A player joined; <paramref name="token"/> is the connect token from their Hello (carried in Data).</summary>
    public static ServerSessionEvent Joined(int slot, byte[] token) =>
        new(ServerSessionEventKind.Joined, slot, token ?? Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);

    /// <summary>A player joined with no connect token.</summary>
    public static ServerSessionEvent Joined(int slot) => Joined(slot, Array.Empty<byte>());
```

In `KhaozEngine.Netcode/NetServer.cs`, change line 85 from `inbox.Enqueue(ServerSessionEvent.Joined(newSlot));` to:

```csharp
        inbox.Enqueue(ServerSessionEvent.Joined(newSlot, token));
```

- [ ] **Step 2: Write the failing WorldServer-hooks test**

Create `KhaozEngine.Tests/NetWorld/WorldServerPersistenceHooksTests.cs`:

```csharp
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldServerPersistenceHooksTests
{
    private static float FlatGround(float x, float z) => 0f;

    // Drives a loopback client into a WorldServer until it has joined, returning the server.
    private static WorldServer JoinOneClient(byte[] token, out int joinedSlot, out List<(int slot, string acct)> joins)
    {
        var pair = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(pair.Server, config, FlatGround, MoveTuning.Default);
        var captured = new List<(int slot, string acct)>();
        server.PlayerJoined += (slot, acct) => captured.Add((slot, acct));

        var client = new NetClient(pair.Client, token);
        int settledSlot = -1;
        for (int i = 0; i < 200 && captured.Count == 0; i++)
        {
            client.Poll();
            server.Poll();
            server.Tick(config.TickSeconds);
        }
        joins = captured;
        joinedSlot = captured.Count > 0 ? captured[0].slot : -1;
        settledSlot = joinedSlot;
        return server;
    }

    [Fact]
    public void PlayerJoined_DerivesAccountIdFromConnectToken()
    {
        WorldServer server = JoinOneClient(Encoding.UTF8.GetBytes("acct-123"), out int slot, out var joins);
        Assert.Single(joins);
        Assert.Equal("acct-123", joins[0].acct);
        Assert.True(server.TryGetAccountId(slot, out string acct));
        Assert.Equal("acct-123", acct);
    }

    [Fact]
    public void SetPlayerState_OverridesPositionAndState()
    {
        WorldServer server = JoinOneClient(Encoding.UTF8.GetBytes("acct-x"), out int slot, out _);
        var target = new PlayerMoveState { Position = new Vector3(50f, 0f, -25f) };
        server.SetPlayerState(slot, target);
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState got));
        Assert.Equal(target.Position, got.Position);
        Assert.Contains(slot, server.JoinedSlots);
    }
}
```

> Note: confirm the loopback pair API. If `LoopbackTransport.CreatePair()` is not the exact factory, read `KhaozEngine.Netcode/LoopbackTransport.cs` and `KhaozEngine.Tests/Netcode/NetSessionTests.cs` (which already drive a loopback client+server) and mirror their setup.

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet build KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj`
Expected: FAIL to compile (`PlayerJoined`, `TryGetAccountId`, `SetPlayerState`, `JoinedSlots`, `TryGetPlayerState` do not exist).

- [ ] **Step 4: Implement the WorldServer seam**

In `KhaozEngine.NetWorld/WorldServer.cs`:

(a) add `using System.Text;` at the top (after the existing usings).

(b) add the account-id map field beside the other per-slot dictionaries (after `lastAckBySlot`):

```csharp
    private readonly Dictionary<int, string> accountIdBySlot = new();
```

(c) add the public seam members (place after the existing `TryGetPlayerNetId` method):

```csharp
    /// <summary>Raised after a player entity has spawned: (slot, accountId). The accountId is the connect token
    /// (UTF-8) or <c>guest:{slot}</c> when none was presented. A persistence layer loads the saved record here.</summary>
    public event Action<int, string>? PlayerJoined;

    /// <summary>Raised just before a player despawns: (slot, accountId, final state). A persistence layer
    /// serializes and saves the final state here (the entity is gone after this returns).</summary>
    public event Action<int, string, PlayerMoveState>? PlayerLeaving;

    /// <summary>The account id for a joined slot (connect token or <c>guest:{slot}</c> fallback).</summary>
    public bool TryGetAccountId(int slot, out string accountId) => accountIdBySlot.TryGetValue(slot, out accountId!);

    /// <summary>The current authoritative movement state for a joined slot.</summary>
    public bool TryGetPlayerState(int slot, out PlayerMoveState state) => stateBySlot.TryGetValue(slot, out state);

    /// <summary>The slots of all currently joined players.</summary>
    public IReadOnlyCollection<int> JoinedSlots => netIdBySlot.Keys;

    /// <summary>Overrides a joined player's authoritative state (and its replicated position). Used by
    /// load-on-join to place the player at the saved position; no-op for an unknown slot.</summary>
    public void SetPlayerState(int slot, in PlayerMoveState state)
    {
        if (!entityBySlot.TryGetValue(slot, out Entity e)) return;
        stateBySlot[slot] = state;
        world.Set(e, new ReplicatedPosition { Value = state.Position });
    }
```

(d) change the Poll join dispatch to pass the token. Replace:

```csharp
                case ServerSessionEventKind.Joined:
                    OnJoin(ev.Slot);
                    break;
```

with:

```csharp
                case ServerSessionEventKind.Joined:
                    OnJoin(ev.Slot, ev.Data);
                    break;
```

(e) replace the `OnJoin` method signature and body to record the accountId and raise the event after spawn:

```csharp
    private void OnJoin(int slot, byte[] token)
    {
        Vector3 spawn = config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);
        // Ground-clamp the spawn (an idle step settles Y onto the terrain + half-height).
        PlayerMoveState state = simulator.Step(new PlayerMoveState { Position = spawn }, MoveCommand.Idle, config.TickSeconds);

        int netId = nextNetId++;
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, new ReplicatedPosition { Value = state.Position });

        string accountId = token is { Length: > 0 } ? Encoding.UTF8.GetString(token) : $"guest:{slot}";
        netIdBySlot[slot] = netId;
        entityBySlot[slot] = e;
        stateBySlot[slot] = state;
        lastAckBySlot[slot] = -1;
        accountIdBySlot[slot] = accountId;

        PlayerJoined?.Invoke(slot, accountId);
    }
```

(f) update `OnLeave` to raise `PlayerLeaving` before despawn and clean up the new map:

```csharp
    private void OnLeave(int slot)
    {
        if (accountIdBySlot.TryGetValue(slot, out string? acct) && stateBySlot.TryGetValue(slot, out PlayerMoveState final))
            PlayerLeaving?.Invoke(slot, acct, final);

        if (entityBySlot.TryGetValue(slot, out Entity e) && world.IsAlive(e)) world.Despawn(e);
        netIdBySlot.Remove(slot);
        entityBySlot.Remove(slot);
        stateBySlot.Remove(slot);
        lastAckBySlot.Remove(slot);
        accountIdBySlot.Remove(slot);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldServerPersistenceHooksTests"`
Expected: PASS (2 tests). Also run the existing netcode session tests to confirm the token change is non-breaking:
Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NetSessionTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Netcode/ServerSessionEvent.cs KhaozEngine.Netcode/NetServer.cs KhaozEngine.NetWorld/WorldServer.cs KhaozEngine.Tests/NetWorld/WorldServerPersistenceHooksTests.cs
git commit -m "feat(networld): WorldServer persistence lifecycle seam (accountId from connect token, join/leave hooks, state accessors)"
```

---

## Task 6: `WorldPersistence` orchestration

The piece that makes the world actually persist: load-on-join, save-on-leave, periodic dirty snapshot, all backend-agnostic. Includes the restart-survival test (the proof).

**Files:**
- Create: `KhaozEngine.NetWorld/WorldPersistence.cs`
- Create: `KhaozEngine.Tests/NetWorld/WorldPersistenceTests.cs`

**Interfaces:**
- Consumes: `WorldServer` (the Task 5 seam), `IWorldStore`, `PlayerRecord`, `PlayerMoveState`.
- Produces: `sealed class WorldPersistenceConfig { float SaveIntervalSeconds {get;init;} = 30f; string KeyPrefix {get;init;} = "player:"; }`; `sealed class WorldPersistence` with ctor `WorldPersistence(WorldServer server, IWorldStore store, WorldPersistenceConfig? config = null)`, `void Update(float dt)`, `void SaveDirtyPass()`, `Task FlushAsync()`.

- [ ] **Step 1: Write the failing orchestration tests**

Create `KhaozEngine.Tests/NetWorld/WorldPersistenceTests.cs`:

```csharp
using System;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldPersistenceTests
{
    private static float FlatGround(float x, float z) => 0f;

    private sealed class Harness : IDisposable
    {
        public readonly WorldServer Server;
        public readonly WorldPersistence Persistence;
        private readonly NetClient client;
        private readonly WorldServerConfig config;

        public Harness(IWorldStore store, byte[] token, WorldPersistenceConfig? pcfg = null)
        {
            var pair = LoopbackTransport.CreatePair();
            config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
            Server = new WorldServer(pair.Server, config, FlatGround, MoveTuning.Default);
            Persistence = new WorldPersistence(Server, store, pcfg);
            client = new NetClient(pair.Client, token);
        }

        // Pump until predicate true (or budget exhausted), running persistence.Update each frame.
        public void PumpUntil(Func<bool> done, int frames = 300)
        {
            for (int i = 0; i < frames && !done(); i++)
            {
                client.Poll();
                Server.Poll();
                Server.Tick(config.TickSeconds);
                Persistence.Update(config.TickSeconds);
            }
        }

        public void Disconnect() => client.Dispose();
        public void Dispose() => client.Dispose();
    }

    [Fact]
    public async Task LoadOnJoin_RestoresSavedPosition()
    {
        IWorldStore store = new InMemoryWorldStore();
        var saved = new PlayerMoveState { Position = new Vector3(33f, 0f, 44f) };
        await store.SaveAsync("player:hero", PlayerRecord.From(saved).Encode());

        using var h = new Harness(store, Encoding.UTF8.GetBytes("hero"));
        int slot = -1;
        h.Server.PlayerJoined += (s, _) => slot = s;
        h.PumpUntil(() => slot >= 0);
        await h.Persistence.FlushAsync();           // settle the async load
        h.Persistence.Update(0f);                   // apply the loaded state on the server thread

        Assert.True(h.Server.TryGetPlayerState(slot, out PlayerMoveState got));
        Assert.Equal(new Vector3(33f, 0f, 44f), got.Position);
    }

    [Fact]
    public async Task SaveOnLeave_PersistsFinalPosition()
    {
        IWorldStore store = new InMemoryWorldStore();
        using (var h = new Harness(store, Encoding.UTF8.GetBytes("hero")))
        {
            int slot = -1;
            h.Server.PlayerJoined += (s, _) => slot = s;
            h.PumpUntil(() => slot >= 0);
            h.Server.SetPlayerState(slot, new PlayerMoveState { Position = new Vector3(5f, 0f, 9f) });
            h.Disconnect();
            h.PumpUntil(() => h.Server.PlayerCount == 0);   // server observes the leave -> save fires
            await h.Persistence.FlushAsync();
        }
        byte[]? data = await store.LoadAsync("player:hero");
        Assert.NotNull(data);
        Assert.Equal(new Vector3(5f, 0f, 9f), PlayerRecord.Decode(data!).ToState().Position);
    }

    [Fact]
    public async Task PeriodicSnapshot_SavesDirtyPlayers()
    {
        IWorldStore store = new InMemoryWorldStore();
        using var h = new Harness(store, Encoding.UTF8.GetBytes("hero"),
            new WorldPersistenceConfig { SaveIntervalSeconds = 0.0001f });   // fire almost immediately
        int slot = -1;
        h.Server.PlayerJoined += (s, _) => slot = s;
        h.PumpUntil(() => slot >= 0);
        h.Server.SetPlayerState(slot, new PlayerMoveState { Position = new Vector3(1f, 0f, 2f) });
        h.Persistence.Update(1f);                   // dt past the interval -> dirty pass
        await h.Persistence.FlushAsync();

        byte[]? data = await store.LoadAsync("player:hero");
        Assert.NotNull(data);
        Assert.Equal(new Vector3(1f, 0f, 2f), PlayerRecord.Decode(data!).ToState().Position);
    }

    [Fact]
    public async Task SurvivesServerRestart_OnSqliteFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "ke-persist-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // First server: join, move, leave -> save.
            using (var store = new SqliteWorldStore($"Data Source={path}"))
            using (var h = new Harness(store, Encoding.UTF8.GetBytes("hero")))
            {
                int slot = -1;
                h.Server.PlayerJoined += (s, _) => slot = s;
                h.PumpUntil(() => slot >= 0);
                h.Server.SetPlayerState(slot, new PlayerMoveState { Position = new Vector3(100f, 0f, 200f) });
                h.Disconnect();
                h.PumpUntil(() => h.Server.PlayerCount == 0);
                await h.Persistence.FlushAsync();
            }

            // Brand-new server + store on the SAME file (a "restart"): the player is restored on join.
            using (var store = new SqliteWorldStore($"Data Source={path}"))
            using (var h = new Harness(store, Encoding.UTF8.GetBytes("hero")))
            {
                int slot = -1;
                h.Server.PlayerJoined += (s, _) => slot = s;
                h.PumpUntil(() => slot >= 0);
                await h.Persistence.FlushAsync();
                h.Persistence.Update(0f);

                Assert.True(h.Server.TryGetPlayerState(slot, out PlayerMoveState got));
                Assert.Equal(new Vector3(100f, 0f, 200f), got.Position);
            }
        }
        finally
        {
            foreach (string p in new[] { path, path + "-wal", path + "-shm" })
                try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj`
Expected: FAIL to compile (`WorldPersistence`, `WorldPersistenceConfig` do not exist).

- [ ] **Step 3: Implement `WorldPersistence`**

Create `KhaozEngine.NetWorld/WorldPersistence.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="WorldPersistence"/>.</summary>
public sealed class WorldPersistenceConfig
{
    /// <summary>How often the periodic snapshot saves dirty players, seconds. A crash loses at most this much.</summary>
    public float SaveIntervalSeconds { get; init; } = 30f;

    /// <summary>Key namespace for player records. Stored key is <c>{KeyPrefix}{accountId}</c>.</summary>
    public string KeyPrefix { get; init; } = "player:";
}

/// <summary>
/// Wires an <see cref="IWorldStore"/> into the <see cref="WorldServer"/> lifecycle so the world survives a
/// restart. Backend-agnostic (only <see cref="IWorldStore"/> + <see cref="PlayerRecord"/>):
/// load-on-join (place the player at the saved position, or leave the default spawn if absent),
/// save-on-leave (persist the final state), and a periodic snapshot of players whose state changed since their
/// last save. Async loads are applied to the server on the server thread inside <see cref="Update"/> (never from
/// a background continuation), so a genuinely-async backend can't race the tick loop.
/// </summary>
public sealed class WorldPersistence
{
    private readonly WorldServer server;
    private readonly IWorldStore store;
    private readonly WorldPersistenceConfig config;

    // Loaded states waiting to be applied on the server thread (Update drains these).
    private readonly ConcurrentQueue<(int slot, PlayerMoveState state)> applyQueue = new();
    // accountId -> last persisted bytes, for dirty comparison.
    private readonly ConcurrentDictionary<string, byte[]> lastSaved = new();
    // In-flight loads/saves, so FlushAsync can await them (tests + shutdown).
    private readonly object pendingLock = new();
    private readonly List<Task> pending = new();
    private float sinceSave;

    public WorldPersistence(WorldServer server, IWorldStore store, WorldPersistenceConfig? config = null)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.config = config ?? new WorldPersistenceConfig();
        server.PlayerJoined += OnPlayerJoined;
        server.PlayerLeaving += OnPlayerLeaving;
    }

    private string Key(string accountId) => config.KeyPrefix + accountId;

    private void Track(Task task)
    {
        lock (pendingLock) pending.Add(task);
    }

    private void OnPlayerJoined(int slot, string accountId) => Track(LoadOnJoinAsync(slot, accountId));

    private async Task LoadOnJoinAsync(int slot, string accountId)
    {
        byte[]? data = await store.LoadAsync(Key(accountId)).ConfigureAwait(false);
        if (data is null) return;                          // no save -> keep the default spawn
        lastSaved[accountId] = data;                       // loaded == clean baseline
        applyQueue.Enqueue((slot, PlayerRecord.Decode(data).ToState()));
    }

    private void OnPlayerLeaving(int slot, string accountId, PlayerMoveState finalState)
        => Track(SaveIfDirtyAsync(accountId, finalState));

    private async Task SaveIfDirtyAsync(string accountId, PlayerMoveState state)
    {
        byte[] data = PlayerRecord.From(state).Encode();
        if (lastSaved.TryGetValue(accountId, out byte[]? prev) && prev.AsSpan().SequenceEqual(data))
            return;                                        // unchanged since last save
        await store.SaveAsync(Key(accountId), data).ConfigureAwait(false);
        lastSaved[accountId] = data;
    }

    /// <summary>Call once per server frame. Applies any completed load-on-join state (on this thread) and runs
    /// the periodic dirty snapshot when <see cref="WorldPersistenceConfig.SaveIntervalSeconds"/> has elapsed.</summary>
    public void Update(float dt)
    {
        while (applyQueue.TryDequeue(out (int slot, PlayerMoveState state) a))
            server.SetPlayerState(a.slot, a.state);

        lock (pendingLock) pending.RemoveAll(t => t.Status == TaskStatus.RanToCompletion);

        sinceSave += dt;
        if (sinceSave >= config.SaveIntervalSeconds)
        {
            sinceSave = 0f;
            SaveDirtyPass();
        }
    }

    /// <summary>Saves every joined player whose state changed since its last save.</summary>
    public void SaveDirtyPass()
    {
        foreach (int slot in new List<int>(server.JoinedSlots))
            if (server.TryGetAccountId(slot, out string accountId) &&
                server.TryGetPlayerState(slot, out PlayerMoveState state))
                Track(SaveIfDirtyAsync(accountId, state));
    }

    /// <summary>Awaits all in-flight loads/saves, then applies any pending loaded state. Call on shutdown (or in
    /// tests) to reach a quiescent, fully-persisted point. Invoke from the server thread / when the loop is idle.</summary>
    public async Task FlushAsync()
    {
        Task[] tasks;
        lock (pendingLock) { tasks = pending.ToArray(); pending.Clear(); }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        while (applyQueue.TryDequeue(out (int slot, PlayerMoveState state) a))
            server.SetPlayerState(a.slot, a.state);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldPersistenceTests"`
Expected: PASS (4 tests, including `SurvivesServerRestart_OnSqliteFile` — the restart-survival proof).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/WorldPersistence.cs KhaozEngine.Tests/NetWorld/WorldPersistenceTests.cs
git commit -m "feat(networld): WorldPersistence (load-on-join, save-on-leave, periodic dirty snapshot)"
```

---

## Task 7: Demo wiring (`NetworkedWalkServer` + stable client token)

Make the reference server persist via SQLite, and have the windowed client send a stable account token so reconnect/restart restores position.

**Files:**
- Modify: `NetworkedWalkServer/NetworkedWalkServer.csproj` (add WorldStore + Sqlite refs)
- Modify: `NetworkedWalkServer/Program.cs`
- Modify: `NetworkedWalkSample/Program.cs`

**Interfaces:**
- Consumes: `WorldServer`, `WorldPersistence`, `WorldPersistenceConfig`, `SqliteWorldStore`, `WorldClient`.
- Produces: a running persistent demo (no new public API).

- [ ] **Step 1: Add references to the server demo csproj**

In `NetworkedWalkServer/NetworkedWalkServer.csproj`, add to the `<ItemGroup>`:

```xml
    <ProjectReference Include="../KhaozEngine.WorldStore/KhaozEngine.WorldStore.csproj" />
    <ProjectReference Include="../KhaozEngine.WorldStore.Sqlite/KhaozEngine.WorldStore.Sqlite.csproj" />
```

- [ ] **Step 2: Wire `WorldPersistence` into the server loop**

In `NetworkedWalkServer/Program.cs`, add these usings near the top (after the existing `using KhaozEngine.NetWorld;`):

```csharp
using KhaozEngine.WorldStore;
using KhaozEngine.WorldStore.Sqlite;
```

Replace the server-construction + loop section (from `using var transport = ...` to the end) with:

```csharp
using var transport = new LiteNetLibServerTransport(port);
var server = new WorldServer(transport, config, terrain.GroundHeight, MoveTuning.Default);

// Persist players to an embedded SQLite DB beside the server, so walking somewhere, disconnecting, and
// reconnecting (or restarting this process) restores position. Swap SqliteWorldStore for SqlServerWorldStore
// (KhaozEngine.WorldStore.SqlServer) to persist to Azure SQL instead - same IWorldStore contract.
string dbPath = args.Length > 1 ? args[1] : "networked-walk-world.db";
using var store = new SqliteWorldStore($"Data Source={dbPath}");
var persistence = new WorldPersistence(server, store,
    new WorldPersistenceConfig { SaveIntervalSeconds = 10f });

var clock = new FixedTickHost(config.TickSeconds);
var sw = Stopwatch.StartNew();
double last = 0;
Console.WriteLine($"Networked walk server on UDP {port} (tick {1f / config.TickSeconds:0} Hz), persisting to {dbPath}. Ctrl+C to stop.");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    persistence.FlushAsync().GetAwaiter().GetResult();   // save everyone before exit
    Console.WriteLine("Saved world. Bye.");
    Environment.Exit(0);
};

while (true)
{
    server.Poll();
    double now = sw.Elapsed.TotalSeconds;
    float elapsed = (float)(now - last);
    last = now;
    clock.Advance(elapsed, _ => server.Tick(config.TickSeconds));
    persistence.Update(elapsed);
    Thread.Sleep(5);
}
```

> Note: the existing file parses `port` from `args[0]`; this change adds an optional `args[1]` for the DB path. Keep the existing `int port = ...` line.

- [ ] **Step 3: Send a stable account token from the windowed client**

In `NetworkedWalkSample/Program.cs`, after the `int port = ...` line (line ~20), add:

```csharp
// A stable account id so reconnecting (or after a server restart) restores this player's saved position.
// Pass a third arg to use distinct accounts for two clients on one box, e.g. "player1" and "player2".
string account = args.Length > 2 ? args[2] : "player1";
```

Then update the `WorldClient` construction (line ~94) to pass the token. Change:

```csharp
        _client = new WorldClient(_transport, _terrain.GroundHeight, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = TickSeconds });
```

to:

```csharp
        _client = new WorldClient(_transport, _terrain.GroundHeight, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = TickSeconds },
            token: System.Text.Encoding.UTF8.GetBytes(account));
```

> Note: `account` is a top-level local; the windowed app object reads it via closure. If the sample's structure makes `account` out of scope at the construction site, capture it into the app object's field where the other config is set, mirroring how `host`/`port` are threaded in. Read the surrounding code and adapt.

- [ ] **Step 4: Build the demos**

Run: `dotnet build NetworkedWalkServer/NetworkedWalkServer.csproj && dotnet build NetworkedWalkSample/NetworkedWalkSample.csproj`
Expected: both build succeed.

- [ ] **Step 5: Commit**

```bash
git add NetworkedWalkServer NetworkedWalkSample
git commit -m "sample(networked-walk): persist players via WorldPersistence + SqliteWorldStore; client sends stable account token"
```

---

## Task 8: Umbrella, full test run

Add the two backends to the Server umbrella and confirm the whole suite is green before the doc sweep + release.

**Files:**
- Modify: `KhaozEngine.Server/KhaozEngine.Server.csproj`

**Interfaces:** none (packaging only).

- [ ] **Step 1: Add the backends to the Server umbrella**

In `KhaozEngine.Server/KhaozEngine.Server.csproj`, add under the `<!-- world store -->` comment (after the existing `KhaozEngine.WorldStore` ProjectReference):

```xml
    <ProjectReference Include="../KhaozEngine.WorldStore.Sqlite/KhaozEngine.WorldStore.Sqlite.csproj" />
    <ProjectReference Include="../KhaozEngine.WorldStore.SqlServer/KhaozEngine.WorldStore.SqlServer.csproj" />
```

- [ ] **Step 2: Run the entire test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all PASS; the 8 `SqlServerWorldStoreConformanceTests` SKIPPED; no failures. (GPU goldens skip without `KE_GPU_TESTS=1` as usual.)

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Server/KhaozEngine.Server.csproj
git commit -m "build(server): add WorldStore.Sqlite + WorldStore.SqlServer to the Server umbrella"
```

---

## Task 9: Doc sweep + version bump (release prep)

Two packages added → the FULL added-package doc sweep, plus the version bump and changelog/changenotes. Everything in this task lands in one bump.

**Files:**
- Modify: `Directory.Build.props` (version + the package-enumeration comment)
- Modify: `README.md` (package catalog table + repo-layout block + the `<PackageReference>` example version)
- Modify: `CLAUDE.md` (package map + Server umbrella description)
- Modify: `docs/CONSUMERS.md` (umbrella/package table + Engine current version line)
- Modify: `docs/ROADMAP.md` (Current released version line)
- Modify: `docs/USING-KHAOZENGINE.md` (persistence usage section + Azure SQL note)
- Modify: `CHANGELOG.md`, `CHANGENOTES.md`

**Interfaces:** none.

- [ ] **Step 1: Bump the version + update the enumeration comment**

In `Directory.Build.props`, change `<KhaozEngine5xVersion>7.48.0</KhaozEngine5xVersion>` to `7.49.0`. The descriptive comment above it enumerates foundation packages; append `WorldStore.Sqlite/WorldStore.SqlServer` to that list if it names WorldStore-family packages (read it and keep the list accurate).

- [ ] **Step 2: README package catalog + repo-layout + PackageReference example**

In `README.md`:
- Add two rows to the package-catalog table for `KhaozEngine.WorldStore.Sqlite` and `KhaozEngine.WorldStore.SqlServer` (next to the existing `KhaozEngine.WorldStore` row), one-line descriptions matching the csproj `<Description>`s, noting the Server umbrella.
- Add both to the repo-layout block (next to the `KhaozEngine.WorldStore` entry), same style as siblings.
- Update the copy-paste `<PackageReference Include="KhaozEngine..." Version="7.48.0" />` example(s) to `7.49.0` (the guard checks this).

Read the existing `KhaozEngine.WorldStore` row + layout entry first and mirror their exact formatting.

- [ ] **Step 3: CLAUDE.md package map + umbrella description**

In `CLAUDE.md`:
- In the package enumeration, add `WorldStore.Sqlite` + `WorldStore.SqlServer` where the WorldStore family is described. Update the `WorldStore` line in the "Server / parallel-job core types" paragraph to note the two opt-in backends (SQLite dev/test + SQL Server/Azure SQL prod) implementing the seam, and that `WorldPersistence` (in `NetWorld`) is the orchestration wiring `IWorldStore` into `WorldServer` (load-on-join / save-on-leave / periodic dirty snapshot).
- Update the `Foundation`/`Server` umbrella descriptions if they enumerate members: the two new packages join the **Server** umbrella.

- [ ] **Step 4: docs/CONSUMERS.md**

In `docs/CONSUMERS.md`:
- Update `**Engine current version:** \`7.48.0\`` to `7.49.0` (guard-checked).
- Add the two packages to the umbrella/package table (Server umbrella, version `7.49.0`), mirroring the `KhaozEngine.WorldStore` row.

- [ ] **Step 5: docs/ROADMAP.md**

In `docs/ROADMAP.md`, update `Current released version: **7.48.0**` to `**7.49.0**` (guard-checked). Add a one-line note under the overworld/MMO program that persistence (WorldStore.Sqlite/.SqlServer + WorldPersistence) shipped at 7.49.0, matching the surrounding style.

- [ ] **Step 6: docs/USING-KHAOZENGINE.md persistence section**

In `docs/USING-KHAOZENGINE.md`, add a "Persisting the world (`IWorldStore` + `WorldPersistence`)" section. Include:
- The `IWorldStore` contract recap and the two backends (SQLite dev/test, SQL Server prod).
- A `WorldPersistence` wiring snippet:

````markdown
```csharp
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore.Sqlite;

var server = new WorldServer(transport, config, groundHeight, MoveTuning.Default);
using var store = new SqliteWorldStore("Data Source=world.db");          // dev/test + single-node
var persistence = new WorldPersistence(server, store,
    new WorldPersistenceConfig { SaveIntervalSeconds = 10f });

// per frame:
server.Poll();
server.Tick(dt);
persistence.Update(dt);     // applies load-on-join state + periodic dirty snapshot
// on shutdown:
await persistence.FlushAsync();
```
````

- An Azure SQL note for Ruinborne:

````markdown
For production (Ruinborne uses Azure SQL), swap the backend - same `IWorldStore` contract:

```csharp
using KhaozEngine.WorldStore.SqlServer;
using var store = new SqlServerWorldStore(
    "Server=tcp:<srv>.database.windows.net,1433;Database=<db>;Authentication=Active Directory Default;Encrypt=True;");
```
````

- [ ] **Step 7: CHANGELOG.md + CHANGENOTES.md**

Prepend a newest-first `## 7.49.0` entry to `CHANGELOG.md` (detailed: the two new packages, `WorldPersistence`, the WorldServer seam + token surfacing, the conformance suite). Prepend a one-line digest to `CHANGENOTES.md`. No em-dashes. Match the existing entries' exact heading style.

Example CHANGELOG entry body:

```markdown
## 7.49.0

Persistent world store: the authoritative world now survives a server restart.

- NEW `KhaozEngine.WorldStore.Sqlite` (Server umbrella): `SqliteWorldStore : IWorldStore` over Microsoft.Data.Sqlite. The embedded zero-infra dev/test + single-node backend; one `world_store` table, `INSERT ... ON CONFLICT` upsert, raw parameterized async ADO.NET.
- NEW `KhaozEngine.WorldStore.SqlServer` (Server umbrella): `SqlServerWorldStore : IWorldStore` over Microsoft.Data.SqlClient (prod = Azure SQL); `MERGE WITH (HOLDLOCK)` upsert.
- NEW `KhaozEngine.NetWorld.WorldPersistence` (+ `WorldPersistenceConfig`, `PlayerRecord`): wires `IWorldStore` into the `WorldServer` lifecycle - load-on-join (spawn at the saved position, default if absent), save-on-leave, and a periodic snapshot of dirty players. Backend-agnostic (`IWorldStore` + Serialization). Forward-tolerant player record (unknown fields ignored).
- `WorldServer` gains a persistence seam: `PlayerJoined`/`PlayerLeaving` events, `TryGetAccountId`/`TryGetPlayerState`/`JoinedSlots`/`SetPlayerState`. The account id derives from the connect token (now surfaced on `ServerSessionEvent.Joined`; no new wire protocol).
- `NetworkedWalkServer` persists via `SqliteWorldStore`; the windowed client sends a stable account token so reconnect/restart restores position.
- Tests: one shared `IWorldStore` conformance suite run against InMemory + SQLite (always) and SQL Server (gated behind `KE_SQLSERVER_TEST_CONNSTRING`); `WorldPersistence` restart-survival test over a SQLite file.
```

CHANGENOTES line:

```markdown
- 7.49.0: Persistent world store - SqliteWorldStore + SqlServerWorldStore (IWorldStore backends) and WorldPersistence (load-on-join / save-on-leave / periodic snapshot) so the authoritative world survives a server restart.
```

- [ ] **Step 8: Run the doc-version guard**

Run: `./scripts/check-doc-versions.sh`
Expected: `all engine-version declarations match 7.49.0`. Fix any FAIL by correcting that declaration to `7.49.0`.

- [ ] **Step 9: Full build + test once more (post-doc-sweep)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all green (SQL Server tests skipped).

- [ ] **Step 10: Grep for stale references**

Run: `grep -rn "WorldStore.Sqlite\|WorldStore.SqlServer\|WorldPersistence" README.md CLAUDE.md docs/*.md | head -40`
Expected: every doc that should mention the new packages does; no doc still describes WorldStore as having no durable backend. Fix omissions.

- [ ] **Step 11: Commit**

```bash
git add Directory.Build.props README.md CLAUDE.md docs CHANGELOG.md CHANGENOTES.md
git commit -m "worldstore(7.49.0): persistent world store (SqliteWorldStore + SqlServerWorldStore + WorldPersistence); docs + version bump"
```

---

## Task 10: Release (merge, pack, tag, push)

Per the engine release ritual: this is the autonomous full publish.

**Files:** none (git + pack).

- [ ] **Step 1: Re-check for concurrent release races**

```bash
git fetch origin --tags
git tag | sort -V | tail -3
```
Expected: highest tag is still `v7.48.0`. If a `v7.49.0` (or higher) appeared, STOP and re-plan the version (bump past it). If `origin/main` advanced, merge it into this branch first.

- [ ] **Step 2: Merge the feature branch into `main`**

```bash
cd /Users/antonio/KhaozEngine            # the main checkout
git merge --no-ff worktree-feature+persistent-worldstore -m "Merge persistent world store (7.49.0): SqliteWorldStore + SqlServerWorldStore + WorldPersistence"
```

- [ ] **Step 3: Build + test the merged result on main**

```bash
mkdir -p local-feed
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
```
Expected: all green (SQL Server tests skipped). If anything fails, STOP and report.

- [ ] **Step 4: Pack to local-feed from the main root**

```bash
dotnet pack -c Release -o ./local-feed
```
Expected: packs all packable projects incl. `KhaozEngine.WorldStore.Sqlite.7.49.0.nupkg` + `KhaozEngine.WorldStore.SqlServer.7.49.0.nupkg`. Verify:
```bash
ls local-feed | grep -E "WorldStore.(Sqlite|SqlServer)\.7\.49\.0"
```

- [ ] **Step 5: Tag + push**

```bash
git tag v7.49.0
git push origin main
git push origin v7.49.0
```
(CI publishes every package to GitHub Packages on the `v*` tag.)

- [ ] **Step 6: Clean up the worktree + merged branch**

```bash
git worktree remove .claude/worktrees/feature+persistent-worldstore
git branch -d worktree-feature+persistent-worldstore
```
(The feature branch was never pushed, so there is no `origin/<branch>` to delete.)

- [ ] **Step 7: Verify final state**

```bash
git log --oneline -3
git status
```
Expected: `main` has the merge + is pushed; tree clean; worktree gone.

---

## Self-Review notes (verification of this plan against the spec)

- **Two backends, same contract** → Tasks 2 (SQLite) + 3 (SQL Server), both run the shared suite (Task 1). ✅
- **Engine-first opt-in packages, core unchanged** → new csprojs each pull their own provider; `KhaozEngine.WorldStore` untouched. ✅
- **`WorldPersistence` in NetWorld, backend-agnostic** → Task 6 (only `IWorldStore` + Serialization). ✅
- **Keys `player:{accountId}` → serialized PlayerMoveState** → `WorldPersistenceConfig.KeyPrefix="player:"`, `PlayerRecord` (Task 4). ✅
- **Load-on-join / save-on-leave / periodic dirty** → Task 6 (`Update`, events, `SaveDirtyPass`). ✅
- **Forward-tolerant record (ignore unknown)** → Task 4 `Decode_IgnoresUnknownFields`. ✅
- **Shared conformance over InMemory + SQLite; SQL Server gated** → Tasks 1/2/3 (`SqlServerFactAttribute`). ✅
- **Restart-survival proof** → Task 6 `SurvivesServerRestart_OnSqliteFile` + Task 2 `SurvivesReopen_OnSameFile`. ✅
- **Demo persists** → Task 7. ✅
- **No EF/ORM, raw parameterized async ADO.NET, schema bootstrap, dialect upsert** → Tasks 2/3. ✅
- **No new netcode** → only surfaces the already-sent Hello token (Task 5). ✅
- **Out of scope** (per-cell snapshots, migrations, auth) → not built. ✅
- **Minor bump + full added-package doc sweep + guard** → Task 9. ✅
- **Autonomous release** → Task 10. ✅
