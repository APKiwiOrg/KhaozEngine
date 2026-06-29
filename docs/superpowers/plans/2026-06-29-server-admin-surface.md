# Server Administration Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a generic, opt-in server-administration surface (live-player commands, world-store enumeration, a ban seam, and an HTTPS admin endpoint) so any KhaozEngine game-server consumer can manage live players and persisted accounts from an out-of-process console.

**Architecture:** Parts 1 (admin commands) and 3 (ban seam) land in `KhaozEngine.NetWorld` on a shared `IAdminControllable` seam implemented by both `WorldServer` and `ShardedWorldServer`, using a thread-safe `ConcurrentQueue` drained on the host thread plus a `volatile`-published online snapshot. Part 2 (enumeration) adds an optional `IEnumerableWorldStore` to `KhaozEngine.WorldStore` and its three stores. Part 4 is a new opt-in `KhaozEngine.Server.Admin` package (the only ASP.NET Core dependency, via `FrameworkReference`) exposing a `ServerAdmin` facade over a minimal Kestrel REST endpoint.

**Tech Stack:** net10.0, C# latest, xUnit, Microsoft.Data.Sqlite / Microsoft.Data.SqlClient (existing), ASP.NET Core shared framework (Kestrel minimal hosting, Part 4 only). No MonoGame.

## Global Constraints

- **Engine version:** bump `<KhaozEngineVersion>` in `Directory.Build.props` from `8.3.0` to `8.4.0` (additive minor) once, at the end (Task 15). Every packable csproj already inherits `<Version>$(KhaozEngineVersion)</Version>`.
- **Additive / non-breaking:** every change is opt-in. No existing `WorldServer` / `ShardedWorldServer` / `IWorldStore` call site may break. New ctor params are trailing and optional (`IBanStore? banStore = null`).
- **MonoGame-free; ASP.NET Core is Part-4-only.** Only `KhaozEngine.Server.Admin` references ASP.NET Core, via `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. It is NOT added to the `KhaozEngine.Server` umbrella.
- **Headless-testable.** Every new behaviour ships a headless xUnit test in `KhaozEngine.Tests` (no GPU; no Kestrel for Parts 1-3). Tests construct servers over `LoopbackTransport` and drive `Poll()` then `Tick(dt)` on one thread.
- **Thread-safety contract (Part 1):** admin commands arrive on foreign threads (Kestrel). They enqueue onto a `ConcurrentQueue<AdminCommand>` drained at the **top of `Tick`** on the host thread; `ListOnline()` reads a `volatile`-published `OnlinePlayer[]` rebuilt at the **end of `Tick`**. Never lock or touch sim state from the calling thread.
- **No em-dashes** in any code comment, doc, commit message, or output. Use periods, commas, parentheses, or rewrites.
- **Commit subjects:** conventional-commit `area(scope): summary`. On the release commit use the version as scope, e.g. `netcode(8.4.0): ...`.
- **`.NET 10 cert API:** use `X509CertificateLoader` (the `X509Certificate2(byte[])` ctor is obsolete in net9+).
- **Spec:** `docs/superpowers/specs/2026-06-29-server-admin-surface-design.md` is the source of truth.

---

### Task 1: `IEnumerableWorldStore` + `WorldStoreEntry` + `InMemoryWorldStore` enumeration

**Files:**
- Create: `KhaozEngine.WorldStore/IEnumerableWorldStore.cs`
- Create: `KhaozEngine.WorldStore/WorldStoreEntry.cs`
- Modify: `KhaozEngine.WorldStore/InMemoryWorldStore.cs` (whole file rewritten)
- Test: `KhaozEngine.Tests/WorldStore/InMemoryWorldStoreEnumerationTests.cs`

**Interfaces:**
- Produces: `KhaozEngine.WorldStore.IEnumerableWorldStore.EnumerateAsync(string? keyPrefix = null, CancellationToken = default) -> IAsyncEnumerable<WorldStoreEntry>`; `readonly record struct WorldStoreEntry(string Key, DateTimeOffset UpdatedAt, long? Size)`; `InMemoryWorldStore(Func<DateTimeOffset>? clock = null)`.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/WorldStore/InMemoryWorldStoreEnumerationTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

public class InMemoryWorldStoreEnumerationTests
{
    private static async Task<List<WorldStoreEntry>> Drain(IEnumerableWorldStore s, string? prefix = null)
    {
        var list = new List<WorldStoreEntry>();
        await foreach (WorldStoreEntry e in s.EnumerateAsync(prefix)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Enumerate_ReturnsAllEntries_WithSizeAndTimestamp()
    {
        var when = new DateTimeOffset(2026, 6, 29, 0, 0, 0, TimeSpan.Zero);
        var store = new InMemoryWorldStore(() => when);
        await store.SaveAsync("player:1", Encoding.UTF8.GetBytes("abc"));
        await store.SaveAsync("player:2", Encoding.UTF8.GetBytes("de"));

        List<WorldStoreEntry> all = await Drain(store);

        Assert.Equal(2, all.Count);
        WorldStoreEntry p1 = all.Single(e => e.Key == "player:1");
        Assert.Equal(3L, p1.Size);
        Assert.Equal(when, p1.UpdatedAt);
    }

    [Fact]
    public async Task Enumerate_FiltersByPrefix()
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:1", new byte[] { 1 });
        await store.SaveAsync("ban:bob", new byte[] { 2 });

        List<WorldStoreEntry> bans = await Drain(store, "ban:");

        Assert.Single(bans);
        Assert.Equal("ban:bob", bans[0].Key);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter InMemoryWorldStoreEnumerationTests`
Expected: FAIL (compile error: `IEnumerableWorldStore` / `WorldStoreEntry` / clock ctor not found).

- [ ] **Step 3: Create the interface and entry type**

`KhaozEngine.WorldStore/WorldStoreEntry.cs`:
```csharp
using System;

namespace KhaozEngine.WorldStore;

/// <summary>One entry exposed by <see cref="IEnumerableWorldStore.EnumerateAsync"/>: the key, when it was last
/// written, and its stored size in bytes when the backend can report it cheaply (null otherwise).</summary>
public readonly record struct WorldStoreEntry(string Key, DateTimeOffset UpdatedAt, long? Size);
```

`KhaozEngine.WorldStore/IEnumerableWorldStore.cs`:
```csharp
using System.Collections.Generic;
using System.Threading;

namespace KhaozEngine.WorldStore;

/// <summary>Optional capability on an <see cref="IWorldStore"/>: list stored keys. A store that cannot enumerate
/// (some remote KV backends) simply does not implement it; consumers feature-detect with
/// <c>store is IEnumerableWorldStore</c>. Order is unspecified.</summary>
public interface IEnumerableWorldStore
{
    /// <summary>Streams every stored entry, optionally restricted to keys beginning with
    /// <paramref name="keyPrefix"/> (null/empty = all).</summary>
    IAsyncEnumerable<WorldStoreEntry> EnumerateAsync(string? keyPrefix = null, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Rewrite `InMemoryWorldStore` to track timestamps and enumerate**

Replace the whole file `KhaozEngine.WorldStore/InMemoryWorldStore.cs`:
```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore;

/// <summary>
/// Thread-safe, dependency-free in-memory <see cref="IWorldStore"/> for tests and local dev. Defensively
/// copies on save and load so a caller mutating its array can't corrupt stored state. Tracks a per-key
/// last-write timestamp (from an injectable clock) so it can also satisfy <see cref="IEnumerableWorldStore"/>.
/// Not durable across process restarts.
/// </summary>
public sealed class InMemoryWorldStore : IWorldStore, IEnumerableWorldStore
{
    private readonly record struct Entry(byte[] Data, DateTimeOffset UpdatedAt);
    private readonly ConcurrentDictionary<string, Entry> store = new();
    private readonly Func<DateTimeOffset> clock;

    /// <summary>The default clock is <see cref="DateTimeOffset.UtcNow"/>; inject a fixed clock in tests.</summary>
    public InMemoryWorldStore(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);

    public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[]? copy = store.TryGetValue(key, out Entry e) ? (byte[])e.Data.Clone() : null;
        return Task.FromResult(copy);
    }

    public Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        store[key] = new Entry((byte[])data.Clone(), clock());
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Task.FromResult(store.TryRemove(key, out _));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Task.FromResult(store.ContainsKey(key));
    }

    public async IAsyncEnumerable<WorldStoreEntry> EnumerateAsync(
        string? keyPrefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (KeyValuePair<string, Entry> kv in store)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(keyPrefix) && !kv.Key.StartsWith(keyPrefix, StringComparison.Ordinal)) continue;
            yield return new WorldStoreEntry(kv.Key, kv.Value.UpdatedAt, kv.Value.Data.Length);
        }
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter InMemoryWorldStoreEnumerationTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.WorldStore/ KhaozEngine.Tests/WorldStore/InMemoryWorldStoreEnumerationTests.cs
git commit -m "netcode: add IEnumerableWorldStore + InMemoryWorldStore enumeration"
```

---

### Task 2: `SqliteWorldStore` enumeration

**Files:**
- Modify: `KhaozEngine.WorldStore.Sqlite/SqliteWorldStore.cs`
- Test: `KhaozEngine.Tests/WorldStore/SqliteWorldStoreEnumerationTests.cs`

**Interfaces:**
- Consumes: `IEnumerableWorldStore`, `WorldStoreEntry` (Task 1).
- Produces: `SqliteWorldStore : IWorldStore, IEnumerableWorldStore, IDisposable`; `internal static string SqliteWorldStore.LikeEscape(string)`.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/WorldStore/SqliteWorldStoreEnumerationTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

public class SqliteWorldStoreEnumerationTests
{
    private static async Task<List<WorldStoreEntry>> Drain(IEnumerableWorldStore s, string? prefix = null)
    {
        var list = new List<WorldStoreEntry>();
        await foreach (WorldStoreEntry e in s.EnumerateAsync(prefix)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Enumerate_FiltersByPrefix_AndReportsSize()
    {
        using var store = new SqliteWorldStore("Data Source=:memory:");
        await store.SaveAsync("player:1", new byte[] { 1, 2, 3 });
        await store.SaveAsync("ban:bob", new byte[] { 9 });

        List<WorldStoreEntry> bans = await Drain(store, "ban:");

        Assert.Single(bans);
        Assert.Equal("ban:bob", bans[0].Key);
        Assert.Equal(1L, bans[0].Size);
        Assert.True(bans[0].UpdatedAt > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Enumerate_TreatsWildcardInPrefixAsLiteral()
    {
        using var store = new SqliteWorldStore("Data Source=:memory:");
        await store.SaveAsync("a%b", new byte[] { 1 });
        await store.SaveAsync("axb", new byte[] { 2 });

        List<WorldStoreEntry> hits = await Drain(store, "a%");

        Assert.Single(hits);
        Assert.Equal("a%b", hits[0].Key);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SqliteWorldStoreEnumerationTests`
Expected: FAIL (compile: `SqliteWorldStore` does not implement `IEnumerableWorldStore`).

- [ ] **Step 3: Add the interface + method**

In `KhaozEngine.WorldStore.Sqlite/SqliteWorldStore.cs`: add `using System.Collections.Generic;`, `using System.Runtime.CompilerServices;`, and `using KhaozEngine.WorldStore;` (for the entry/interface types — same root namespace so no using actually needed; keep as is). Change the class declaration line:
```csharp
public sealed class SqliteWorldStore : IWorldStore, IEnumerableWorldStore, IDisposable
```
Add these members (before `public void Dispose()`):
```csharp
    public async IAsyncEnumerable<WorldStoreEntry> EnumerateAsync(
        string? keyPrefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            if (string.IsNullOrEmpty(keyPrefix))
            {
                cmd.CommandText = "SELECT key, updated_at, LENGTH(data) FROM world_store;";
            }
            else
            {
                cmd.CommandText = "SELECT key, updated_at, LENGTH(data) FROM world_store WHERE key LIKE $p ESCAPE '\\';";
                cmd.Parameters.AddWithValue("$p", LikeEscape(keyPrefix) + "%");
            }
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string key = reader.GetString(0);
                var updated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
                long size = reader.GetInt64(2);
                yield return new WorldStoreEntry(key, updated, size);
            }
        }
        finally { gate.Release(); }
    }

    // Escapes SQLite LIKE metacharacters so a supplied prefix matches literally (ESCAPE '\').
    internal static string LikeEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SqliteWorldStoreEnumerationTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.WorldStore.Sqlite/ KhaozEngine.Tests/WorldStore/SqliteWorldStoreEnumerationTests.cs
git commit -m "netcode: SqliteWorldStore enumeration (streaming, prefix-filtered)"
```

---

### Task 3: `SqlServerWorldStore` enumeration

**Files:**
- Modify: `KhaozEngine.WorldStore.SqlServer/SqlServerWorldStore.cs`
- Modify (verify only): `KhaozEngine.WorldStore.SqlServer/KhaozEngine.WorldStore.SqlServer.csproj` (must contain `<InternalsVisibleTo Include="KhaozEngine.Tests" />`; add it if missing)
- Test: `KhaozEngine.Tests/WorldStore/SqlServerWorldStoreEscapeTests.cs`

**Interfaces:**
- Produces: `SqlServerWorldStore : IWorldStore, IEnumerableWorldStore`; `internal static string SqlServerWorldStore.LikeEscape(string)`.

Note: there is no SQL Server in CI, so enumeration correctness rides on parity with the Sqlite test (Task 2) plus a unit test of the LIKE-escaping helper. The query itself is verified by compile + review.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/WorldStore/SqlServerWorldStoreEscapeTests.cs`:
```csharp
using KhaozEngine.WorldStore.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

public class SqlServerWorldStoreEscapeTests
{
    [Fact]
    public void LikeEscape_EscapesMetacharacters()
    {
        Assert.Equal("ban\\_x", SqlServerWorldStore.LikeEscape("ban_x"));
        Assert.Equal("a\\%b", SqlServerWorldStore.LikeEscape("a%b"));
        Assert.Equal("x\\[y", SqlServerWorldStore.LikeEscape("x[y"));
        Assert.Equal("p\\\\q", SqlServerWorldStore.LikeEscape("p\\q"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SqlServerWorldStoreEscapeTests`
Expected: FAIL (compile: `LikeEscape` not found / not accessible).

- [ ] **Step 3: Ensure InternalsVisibleTo, then add the interface + method**

Confirm `KhaozEngine.WorldStore.SqlServer/KhaozEngine.WorldStore.SqlServer.csproj` has, inside an `<ItemGroup>`:
```xml
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
```
(Add that ItemGroup if absent.)

In `KhaozEngine.WorldStore.SqlServer/SqlServerWorldStore.cs`: add `using System.Collections.Generic;` and `using System.Runtime.CompilerServices;` and `using KhaozEngine.WorldStore;`. Change the class declaration:
```csharp
public sealed class SqlServerWorldStore : IWorldStore, IEnumerableWorldStore
```
Add these members at the end of the class:
```csharp
    public async IAsyncEnumerable<WorldStoreEntry> EnumerateAsync(
        string? keyPrefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        if (string.IsNullOrEmpty(keyPrefix))
        {
            cmd.CommandText = "SELECT [key], updated_at, DATALENGTH(data) FROM dbo.world_store;";
        }
        else
        {
            cmd.CommandText = "SELECT [key], updated_at, DATALENGTH(data) FROM dbo.world_store WHERE [key] LIKE @p ESCAPE '\\';";
            cmd.Parameters.AddWithValue("@p", LikeEscape(keyPrefix) + "%");
        }
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string key = reader.GetString(0);
            var updated = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), TimeSpan.Zero);
            long? size = reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2));
            yield return new WorldStoreEntry(key, updated, size);
        }
    }

    // Escapes SQL Server LIKE metacharacters (incl. the '[' set marker) so a supplied prefix matches literally.
    internal static string LikeEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SqlServerWorldStoreEscapeTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.WorldStore.SqlServer/ KhaozEngine.Tests/WorldStore/SqlServerWorldStoreEscapeTests.cs
git commit -m "netcode: SqlServerWorldStore enumeration (parity with Sqlite)"
```

---

### Task 4: `IBanStore` + `BanRecord` + `InMemoryBanStore`

**Files:**
- Create: `KhaozEngine.NetWorld/IBanStore.cs` (holds `BanRecord` + `IBanStore`)
- Create: `KhaozEngine.NetWorld/InMemoryBanStore.cs`
- Test: `KhaozEngine.Tests/NetWorld/InMemoryBanStoreTests.cs`

**Interfaces:**
- Produces: `readonly record struct BanRecord(string AccountId, string Reason, DateTimeOffset? Until)`; `IBanStore { bool IsBanned(string); ValueTask BanAsync(string, string, DateTimeOffset?, CancellationToken); ValueTask UnbanAsync(string, CancellationToken); IReadOnlyCollection<BanRecord> ListBans(); }`; `InMemoryBanStore(Func<DateTimeOffset>? clock = null)`.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/NetWorld/InMemoryBanStoreTests.cs`:
```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class InMemoryBanStoreTests
{
    [Fact]
    public async Task Ban_ThenIsBanned_ThenUnban()
    {
        var bans = new InMemoryBanStore();
        Assert.False(bans.IsBanned("evil"));
        await bans.BanAsync("evil", "cheating");
        Assert.True(bans.IsBanned("evil"));
        Assert.Equal("cheating", bans.ListBans().Single().Reason);
        await bans.UnbanAsync("evil");
        Assert.False(bans.IsBanned("evil"));
        Assert.Empty(bans.ListBans());
    }

    [Fact]
    public async Task ExpiredBan_IsNotBanned()
    {
        var now = new DateTimeOffset(2026, 6, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = now;
        var bans = new InMemoryBanStore(() => clock);
        await bans.BanAsync("temp", "timeout", now.AddMinutes(10));
        Assert.True(bans.IsBanned("temp"));
        clock = now.AddMinutes(11);
        Assert.False(bans.IsBanned("temp"));
        Assert.Empty(bans.ListBans());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter InMemoryBanStoreTests`
Expected: FAIL (compile: `IBanStore` / `BanRecord` / `InMemoryBanStore` not found).

- [ ] **Step 3: Create the seam**

`KhaozEngine.NetWorld/IBanStore.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.NetWorld;

/// <summary>One ban: the account, why, and an optional expiry (null = permanent).</summary>
public readonly record struct BanRecord(string AccountId, string Reason, DateTimeOffset? Until);

/// <summary>
/// Generic account ban seam. A server consults <see cref="IsBanned"/> on the host thread at connect (alongside the
/// <see cref="KhaozEngine.Netcode.IConnectionAuthenticator"/>), so it is synchronous and must be cheap. Mutators are
/// async so a database-backed store can persist honestly. Bans key on the verified account id (the authenticator's
/// subject); a guest (no stable subject) is not meaningfully bannable.
/// </summary>
public interface IBanStore
{
    /// <summary>True if <paramref name="accountId"/> is currently banned (honoring expiry). Synchronous and fast.</summary>
    bool IsBanned(string accountId);

    /// <summary>Records (or refreshes) a ban. <paramref name="until"/> null = permanent.</summary>
    ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null, CancellationToken cancellationToken = default);

    /// <summary>Removes any ban on <paramref name="accountId"/>.</summary>
    ValueTask UnbanAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>The current (non-expired) bans, for an admin list view.</summary>
    IReadOnlyCollection<BanRecord> ListBans();
}
```

`KhaozEngine.NetWorld/InMemoryBanStore.cs`:
```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.NetWorld;

/// <summary>Dependency-free in-memory <see cref="IBanStore"/>. Expiry is checked against an injectable clock
/// (default <see cref="DateTimeOffset.UtcNow"/>); expired entries are pruned lazily on read.</summary>
public sealed class InMemoryBanStore : IBanStore
{
    private readonly ConcurrentDictionary<string, BanRecord> bans = new();
    private readonly Func<DateTimeOffset> clock;

    public InMemoryBanStore(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);

    public bool IsBanned(string accountId)
    {
        if (string.IsNullOrEmpty(accountId)) return false;
        if (!bans.TryGetValue(accountId, out BanRecord r)) return false;
        if (r.Until is { } until && until <= clock()) { bans.TryRemove(accountId, out _); return false; }
        return true;
    }

    public ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        bans[accountId] = new BanRecord(accountId, reason ?? string.Empty, until);
        return ValueTask.CompletedTask;
    }

    public ValueTask UnbanAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        bans.TryRemove(accountId, out _);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyCollection<BanRecord> ListBans()
    {
        DateTimeOffset now = clock();
        var live = new List<BanRecord>();
        foreach (BanRecord r in bans.Values)
        {
            if (r.Until is { } until && until <= now) { bans.TryRemove(r.AccountId, out _); continue; }
            live.Add(r);
        }
        return live;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter InMemoryBanStoreTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/IBanStore.cs KhaozEngine.NetWorld/InMemoryBanStore.cs KhaozEngine.Tests/NetWorld/InMemoryBanStoreTests.cs
git commit -m "netcode: IBanStore seam + InMemoryBanStore default"
```

---

### Task 5: `WorldStoreBanStore` (persistent ban store over the IWorldStore keyspace)

**Files:**
- Create: `KhaozEngine.NetWorld/WorldStoreBanStore.cs`
- Test: `KhaozEngine.Tests/NetWorld/WorldStoreBanStoreTests.cs`

**Interfaces:**
- Consumes: `IBanStore`, `BanRecord` (Task 4); `IWorldStore`, `IEnumerableWorldStore` (Task 1); `KhaozEngine.Serialization.JsonDefaults` (existing: `IndentedWrite`, `TolerantRead`).
- Produces: `WorldStoreBanStore(IWorldStore store, Func<DateTimeOffset>? clock = null)` with `Task LoadAsync(CancellationToken = default)`.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/NetWorld/WorldStoreBanStoreTests.cs`:
```csharp
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldStoreBanStoreTests
{
    [Fact]
    public async Task Ban_Persists_AndHydratesIntoAFreshStore()
    {
        var backing = new InMemoryWorldStore();
        var bans = new WorldStoreBanStore(backing);
        await bans.BanAsync("evil", "cheating");
        Assert.True(bans.IsBanned("evil"));
        Assert.True(await backing.ExistsAsync("ban:evil"));

        var reloaded = new WorldStoreBanStore(backing);
        Assert.False(reloaded.IsBanned("evil"));   // cache not hydrated yet
        await reloaded.LoadAsync();
        Assert.True(reloaded.IsBanned("evil"));

        await reloaded.UnbanAsync("evil");
        Assert.False(reloaded.IsBanned("evil"));
        Assert.False(await backing.ExistsAsync("ban:evil"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter WorldStoreBanStoreTests`
Expected: FAIL (compile: `WorldStoreBanStore` not found).

- [ ] **Step 3: Implement**

`KhaozEngine.NetWorld/WorldStoreBanStore.cs`:
```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Serialization;
using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Persistent <see cref="IBanStore"/> layered over an <see cref="IWorldStore"/> keyspace (keys
/// <c>ban:{accountId}</c>, value a forward-tolerant JSON record). Backend-agnostic: works over any IWorldStore
/// (Sqlite, SqlServer, in-memory). Keeps an in-memory cache so <see cref="IsBanned"/> stays synchronous for the
/// host-thread connect check, and writes through to the store on every mutate. Call <see cref="LoadAsync"/> once at
/// startup to hydrate the cache from the store (requires the store to implement <see cref="IEnumerableWorldStore"/>;
/// without it, persisted bans are invisible to <see cref="IsBanned"/> until re-added this session).
/// </summary>
public sealed class WorldStoreBanStore : IBanStore
{
    private const string KeyPrefix = "ban:";
    private readonly IWorldStore store;
    private readonly Func<DateTimeOffset> clock;
    private readonly ConcurrentDictionary<string, BanRecord> cache = new();

    public WorldStoreBanStore(IWorldStore store, Func<DateTimeOffset>? clock = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Hydrates the in-memory cache from the store. No-op if the store cannot enumerate.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (store is not IEnumerableWorldStore en) return;
        await foreach (WorldStoreEntry entry in en.EnumerateAsync(KeyPrefix, cancellationToken).ConfigureAwait(false))
        {
            byte[]? data = await store.LoadAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (data is null) continue;
            BanRecord r = Decode(data);
            if (!string.IsNullOrEmpty(r.AccountId)) cache[r.AccountId] = r;
        }
    }

    public bool IsBanned(string accountId)
    {
        if (string.IsNullOrEmpty(accountId)) return false;
        if (!cache.TryGetValue(accountId, out BanRecord r)) return false;
        if (r.Until is { } until && until <= clock()) { cache.TryRemove(accountId, out _); return false; }
        return true;
    }

    public async ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        var record = new BanRecord(accountId, reason ?? string.Empty, until);
        cache[accountId] = record;
        await store.SaveAsync(KeyPrefix + accountId, Encode(record), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UnbanAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        cache.TryRemove(accountId, out _);
        await store.DeleteAsync(KeyPrefix + accountId, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyCollection<BanRecord> ListBans()
    {
        DateTimeOffset now = clock();
        var live = new List<BanRecord>();
        foreach (BanRecord r in cache.Values)
        {
            if (r.Until is { } until && until <= now) { cache.TryRemove(r.AccountId, out _); continue; }
            live.Add(r);
        }
        return live;
    }

    // A settable DTO (not the record struct) keeps System.Text.Json round-tripping simple and forward-tolerant.
    private sealed class BanDto
    {
        public string AccountId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTimeOffset? Until { get; set; }
    }

    private static byte[] Encode(in BanRecord r) =>
        JsonSerializer.SerializeToUtf8Bytes(new BanDto { AccountId = r.AccountId, Reason = r.Reason, Until = r.Until }, JsonDefaults.IndentedWrite);

    private static BanRecord Decode(byte[] data)
    {
        BanDto? d = JsonSerializer.Deserialize<BanDto>(data, JsonDefaults.TolerantRead);
        return d is null ? default : new BanRecord(d.AccountId, d.Reason, d.Until);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter WorldStoreBanStoreTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/WorldStoreBanStore.cs KhaozEngine.Tests/NetWorld/WorldStoreBanStoreTests.cs
git commit -m "netcode: WorldStoreBanStore (persistent ban store over IWorldStore keyspace)"
```

---

### Task 6: Admin primitives - `PlayerRef`, `OnlinePlayer`, `IAdminControllable`, `AdminCommand`, `AdminCommandBuffer`

**Files:**
- Create: `KhaozEngine.NetWorld/PlayerRef.cs`
- Create: `KhaozEngine.NetWorld/OnlinePlayer.cs`
- Create: `KhaozEngine.NetWorld/IAdminControllable.cs`
- Create: `KhaozEngine.NetWorld/AdminCommand.cs` (internal `AdminCommandKind` + `AdminCommand` + `AdminCommandBuffer`)
- Test: `KhaozEngine.Tests/NetWorld/AdminCommandBufferTests.cs`

**Interfaces:**
- Produces: `readonly struct PlayerRef` (`PlayerRef.Slot(int)`, `PlayerRef.Account(string)`, `bool IsSlot`, `int SlotValue`, `string AccountValue`); `readonly record struct OnlinePlayer(int Slot, string AccountId, string DisplayName, Vector3 Position, bool Grounded, float VerticalVelocity, int NetId)`; `interface IAdminControllable { IReadOnlyList<OnlinePlayer> ListOnline(); void Teleport(PlayerRef, Vector3); void Kick(PlayerRef, string); void Broadcast(string); }`; internal `enum AdminCommandKind { Teleport, Kick, Broadcast }`, internal `readonly struct AdminCommand { AdminCommandKind Kind; PlayerRef Target; Vector3 Position; string Text; }`, internal `sealed class AdminCommandBuffer { void Enqueue(in AdminCommand); void Drain(Action<AdminCommand>); void Publish(IReadOnlyList<OnlinePlayer>); IReadOnlyList<OnlinePlayer> Online; }`.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/NetWorld/AdminCommandBufferTests.cs`:
```csharp
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class AdminCommandBufferTests
{
    [Fact]
    public void Drain_ReturnsEveryEnqueuedCommand_EvenFromManyThreads()
    {
        var buf = new AdminCommandBuffer();
        Parallel.For(0, 200, i =>
            buf.Enqueue(new AdminCommand { Kind = AdminCommandKind.Teleport, Position = new Vector3(i, 0, 0) }));

        var seen = new List<AdminCommand>();
        buf.Drain(seen.Add);

        Assert.Equal(200, seen.Count);
    }

    [Fact]
    public void Online_ReturnsLastPublishedSnapshot()
    {
        var buf = new AdminCommandBuffer();
        Assert.Empty(buf.Online);
        var snap = new[] { new OnlinePlayer(0, "a", "A", Vector3.Zero, true, 0f, 1) };
        buf.Publish(snap);
        Assert.Single(buf.Online);
        Assert.Equal("a", buf.Online[0].AccountId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter AdminCommandBufferTests`
Expected: FAIL (compile: types not found).

- [ ] **Step 3: Create the types**

`KhaozEngine.NetWorld/PlayerRef.cs`:
```csharp
namespace KhaozEngine.NetWorld;

/// <summary>An admin command target: either a connection slot or a verified account id. Build with
/// <see cref="Slot"/> / <see cref="Account"/>; the server resolves it to a slot on the host thread.</summary>
public readonly struct PlayerRef
{
    private PlayerRef(bool isSlot, int slot, string account) { IsSlot = isSlot; SlotValue = slot; AccountValue = account; }

    public static PlayerRef Slot(int slot) => new(true, slot, string.Empty);
    public static PlayerRef Account(string accountId) => new(false, 0, accountId ?? string.Empty);

    public bool IsSlot { get; }
    public int SlotValue { get; }
    public string AccountValue { get; }
}
```

`KhaozEngine.NetWorld/OnlinePlayer.cs`:
```csharp
using System.Numerics;

namespace KhaozEngine.NetWorld;

/// <summary>A point-in-time view of one connected player for an admin console.</summary>
public readonly record struct OnlinePlayer(
    int Slot, string AccountId, string DisplayName, Vector3 Position, bool Grounded, float VerticalVelocity, int NetId);
```

`KhaozEngine.NetWorld/IAdminControllable.cs`:
```csharp
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The generic live-admin surface implemented by both <see cref="WorldServer"/> and <see cref="ShardedWorldServer"/>.
/// Reads (<see cref="ListOnline"/>) return a snapshot published once per tick (lock-free, at most one tick stale).
/// Mutations are queued and applied on the host thread between ticks, so callers (e.g. an HTTP handler on a foreign
/// thread) never touch the simulation directly.
/// </summary>
public interface IAdminControllable
{
    /// <summary>The most recently published online snapshot (at most one tick stale).</summary>
    IReadOnlyList<OnlinePlayer> ListOnline();

    /// <summary>Queues a teleport of <paramref name="target"/> to <paramref name="position"/> (vertical velocity reset).</summary>
    void Teleport(PlayerRef target, Vector3 position);

    /// <summary>Queues a kick of <paramref name="target"/>; the reason is delivered to that client as a notice.</summary>
    void Kick(PlayerRef target, string reason);

    /// <summary>Queues a broadcast of <paramref name="text"/> to every client (a Custom server notice).</summary>
    void Broadcast(string text);
}
```

`KhaozEngine.NetWorld/AdminCommand.cs`:
```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.NetWorld;

internal enum AdminCommandKind : byte { Teleport, Kick, Broadcast }

/// <summary>A queued admin mutation, applied on the host thread during the next tick.</summary>
internal readonly struct AdminCommand
{
    public AdminCommandKind Kind { get; init; }
    public PlayerRef Target { get; init; }
    public Vector3 Position { get; init; }
    public string Text { get; init; }
}

/// <summary>
/// The thread-safety bridge for the admin surface, shared by both servers. <see cref="Enqueue"/> is called from any
/// thread; <see cref="Drain"/> runs on the host thread at the top of a tick; <see cref="Publish"/> stores the
/// online snapshot at the end of a tick and <see cref="Online"/> reads it lock-free.
/// </summary>
internal sealed class AdminCommandBuffer
{
    private readonly ConcurrentQueue<AdminCommand> queue = new();
    private volatile IReadOnlyList<OnlinePlayer> online = Array.Empty<OnlinePlayer>();

    public void Enqueue(in AdminCommand command) => queue.Enqueue(command);

    public void Drain(Action<AdminCommand> apply)
    {
        while (queue.TryDequeue(out AdminCommand cmd)) apply(cmd);
    }

    public void Publish(IReadOnlyList<OnlinePlayer> snapshot) => online = snapshot;

    public IReadOnlyList<OnlinePlayer> Online => online;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter AdminCommandBufferTests`
Expected: PASS (2 tests). (`KhaozEngine.NetWorld` already grants `InternalsVisibleTo KhaozEngine.Tests`.)

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/PlayerRef.cs KhaozEngine.NetWorld/OnlinePlayer.cs KhaozEngine.NetWorld/IAdminControllable.cs KhaozEngine.NetWorld/AdminCommand.cs KhaozEngine.Tests/NetWorld/AdminCommandBufferTests.cs
git commit -m "netcode: admin command primitives (PlayerRef, OnlinePlayer, IAdminControllable, AdminCommandBuffer)"
```

---

### Task 7: `WorldServer` admin surface (implement `IAdminControllable`)

**Files:**
- Modify: `KhaozEngine.NetWorld/WorldServer.cs`
- Test: `KhaozEngine.Tests/NetWorld/WorldServerAdminTests.cs`

**Interfaces:**
- Consumes: `IAdminControllable`, `OnlinePlayer`, `PlayerRef`, `AdminCommand`, `AdminCommandBuffer` (Task 6); existing `ServerNotice`, `ServerNoticeKind`, `MoveProtocol.EncodeServerFrame`, `MoveProtocol.EncodeNotice`, `PlayerIdentity`, `NetChannelReliability`.
- Produces: `WorldServer : IWorldPersistenceHost, IAdminControllable` with `ListOnline/Teleport/Kick/Broadcast`. Existing `Disconnect(slot)` unchanged.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/NetWorld/WorldServerAdminTests.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldServerAdminTests
{
    private static float Flat(float x, float z) => 0f;

    private static WorldServer JoinOne(string account, out NetClient client, out WorldServerConfig config, out int slot)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        client = new NetClient(ct, Encoding.UTF8.GetBytes(account));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        server.Tick(config.TickSeconds);   // publish the online snapshot
        slot = server.JoinedSlots.First();
        return server;
    }

    [Fact]
    public void ListOnline_ReflectsJoinedPlayer()
    {
        WorldServer server = JoinOne("alice", out _, out _, out _);
        IReadOnlyList<OnlinePlayer> online = server.ListOnline();
        Assert.Single(online);
        Assert.Equal("alice", online[0].AccountId);
    }

    [Fact]
    public void Teleport_ByAccount_MovesAuthoritativeState()
    {
        WorldServer server = JoinOne("alice", out NetClient client, out WorldServerConfig config, out int slot);
        server.Teleport(PlayerRef.Account("alice"), new Vector3(50f, 0f, 70f));
        server.Poll(); server.Tick(config.TickSeconds);
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState s));
        Assert.Equal(50f, s.Position.X, 3);
        Assert.Equal(70f, s.Position.Z, 3);
    }

    [Fact]
    public void Kick_BySlot_DisconnectsPlayer()
    {
        WorldServer server = JoinOne("alice", out NetClient client, out WorldServerConfig config, out int slot);
        server.Kick(PlayerRef.Slot(slot), "bye");
        for (int i = 0; i < 60 && server.PlayerCount > 0; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(0, server.PlayerCount);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter WorldServerAdminTests`
Expected: FAIL (compile: `WorldServer` does not implement `IAdminControllable`).

- [ ] **Step 3: Add the field and interface declaration**

In `KhaozEngine.NetWorld/WorldServer.cs`, change the class declaration line (47):
```csharp
public sealed class WorldServer : IWorldPersistenceHost, IAdminControllable
```
Add a field near the other `private readonly` fields (after line 56 `private readonly DrainController drain = new();`):
```csharp
    private readonly AdminCommandBuffer admin = new();
```

- [ ] **Step 4: Add the IAdminControllable members + helpers**

Insert this block just before `private void OnJoin(` (line 258):
```csharp
    /// <inheritdoc/>
    public IReadOnlyList<OnlinePlayer> ListOnline() => admin.Online;

    /// <inheritdoc/>
    public void Teleport(PlayerRef target, Vector3 position) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.Teleport, Target = target, Position = position });

    /// <inheritdoc/>
    public void Kick(PlayerRef target, string reason) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.Kick, Target = target, Text = reason ?? string.Empty });

    /// <inheritdoc/>
    public void Broadcast(string text) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.Broadcast, Text = text ?? string.Empty });

    private int ResolveSlot(in PlayerRef target)
    {
        if (target.IsSlot) return netIdBySlot.ContainsKey(target.SlotValue) ? target.SlotValue : -1;
        foreach (KeyValuePair<int, string> kv in accountIdBySlot)
            if (kv.Value == target.AccountValue) return kv.Key;
        return -1;
    }

    private void SendNoticeTo(int slot, in ServerNotice notice)
    {
        byte[] envelope = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Notice, MoveProtocol.EncodeNotice(notice));
        net.SendTo(slot, envelope, NetChannelReliability.ReliableOrdered);
    }

    private void ApplyAdminCommand(AdminCommand cmd)
    {
        switch (cmd.Kind)
        {
            case AdminCommandKind.Teleport:
            {
                int slot = ResolveSlot(cmd.Target);
                if (slot >= 0 && stateBySlot.TryGetValue(slot, out PlayerMoveState st))
                {
                    st.Position = cmd.Position;
                    st.VerticalVelocity = 0f;
                    SetPlayerState(slot, st);
                }
                break;
            }
            case AdminCommandKind.Kick:
            {
                int slot = ResolveSlot(cmd.Target);
                if (slot >= 0)
                {
                    SendNoticeTo(slot, new ServerNotice(ServerNoticeKind.Custom, cmd.Text));
                    Disconnect(slot);
                }
                break;
            }
            case AdminCommandKind.Broadcast:
                BroadcastNotice(new ServerNotice(ServerNoticeKind.Custom, cmd.Text));
                break;
        }
    }

    private OnlinePlayer[] BuildOnlineSnapshot()
    {
        var list = new List<OnlinePlayer>(netIdBySlot.Count);
        foreach (int slot in netIdBySlot.Keys)
        {
            string acct = accountIdBySlot.TryGetValue(slot, out string? a) ? a : string.Empty;
            PlayerMoveState st = stateBySlot.TryGetValue(slot, out PlayerMoveState s) ? s : default;
            int netId = netIdBySlot[slot];
            string name = string.Empty;
            if (entityBySlot.TryGetValue(slot, out Entity e) && world.TryGet(e, out PlayerIdentity pi))
                name = pi.DisplayName ?? string.Empty;
            list.Add(new OnlinePlayer(slot, acct, name, st.Position, st.Grounded, st.VerticalVelocity, netId));
        }
        return list.ToArray();
    }
```

- [ ] **Step 5: Wire the drain + publish into `Tick`**

In `Tick(float dt)` (line 216), add as the FIRST statement of the method body (before `var slots = ...`):
```csharp
        admin.Drain(ApplyAdminCommand);
```
And add as the LAST statement of `Tick` (after `drain.Advance(dt);`):
```csharp
        admin.Publish(BuildOnlineSnapshot());
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter WorldServerAdminTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.NetWorld/WorldServer.cs KhaozEngine.Tests/NetWorld/WorldServerAdminTests.cs
git commit -m "netcode: WorldServer admin surface (ListOnline/Teleport/Kick/Broadcast)"
```

---

### Task 8: `WorldServer` ban-at-connect

**Files:**
- Modify: `KhaozEngine.NetWorld/WorldServer.cs`
- Test: `KhaozEngine.Tests/NetWorld/WorldServerBanTests.cs`

**Interfaces:**
- Consumes: `IBanStore`, `InMemoryBanStore` (Task 4); `SendNoticeTo` (Task 7).
- Produces: `WorldServer(..., IConnectionAuthenticator? authenticator = null, IBanStore? banStore = null)`.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/NetWorld/WorldServerBanTests.cs`:
```csharp
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldServerBanTests
{
    private static float Flat(float x, float z) => 0f;

    [Fact]
    public async Task BannedAccount_IsRejectedAtConnect()
    {
        var bans = new InMemoryBanStore();
        await bans.BanAsync("evil", "cheating");

        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, banStore: bans);
        var client = new NetClient(ct, Encoding.UTF8.GetBytes("evil"));   // AllowAllAuthenticator: subject = token

        for (int i = 0; i < 200; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }

        Assert.Equal(0, server.PlayerCount);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter WorldServerBanTests`
Expected: FAIL (compile: no `banStore:` parameter).

- [ ] **Step 3: Add the ctor param + field**

In `WorldServer.cs`, extend the constructor signature (line 68-71) to add the trailing param:
```csharp
    public WorldServer(INetTransport transport, WorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null, IPhysicsWorld? physics = null,
        IConnectionAuthenticator? authenticator = null, IBanStore? banStore = null)
```
Add a field with the others (after `private readonly AdminCommandBuffer admin = new();`):
```csharp
    private readonly IBanStore? banStore;
```
Assign it in the ctor body (after the `net = new NetServer(...)` line, before `interest = ...`):
```csharp
        this.banStore = banStore;
```

- [ ] **Step 4: Add the connect check in `OnJoin`**

In `OnJoin` (line 258), move the account-id computation to the top and add the ban check. Replace the opening of the method (the `commands.Forget(slot);` line through the spawn) so it begins:
```csharp
    private void OnJoin(int slot, string subject, string displayName)
    {
        string accountId = string.IsNullOrEmpty(subject) ? $"guest:{slot}" : subject;
        if (banStore is not null && banStore.IsBanned(accountId))
        {
            SendNoticeTo(slot, new ServerNotice(ServerNoticeKind.Custom, "banned"));
            net.Disconnect(slot);
            return;
        }

        // Belt-and-suspenders: clear any stale command-queue state on the (recycled) slot before spawning, in case
        // a prior occupant's Left was ever missed. A fresh session's seqs restart at 0; a stale high-water mark
        // would reject every one and freeze the player (see OnLeave).
        commands.Forget(slot);
```
Then DELETE the later duplicate `string accountId = string.IsNullOrEmpty(subject) ? $"guest:{slot}" : subject;` line (was line 275), since it is now computed at the top.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter WorldServerBanTests`
Expected: PASS.

- [ ] **Step 6: Run the existing WorldServer auth tests to confirm no regression**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter WorldServerAuthTests`
Expected: PASS (unchanged).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.NetWorld/WorldServer.cs KhaozEngine.Tests/NetWorld/WorldServerBanTests.cs
git commit -m "netcode: WorldServer ban-at-connect (optional IBanStore)"
```

---

### Task 9: `ShardedWorldServer` admin surface (implement `IAdminControllable`)

**Files:**
- Modify: `KhaozEngine.NetWorld/ShardedWorldServer.cs`
- Test: `KhaozEngine.Tests/NetWorld/ShardedWorldServerAdminTests.cs`

**Interfaces:**
- Consumes: same admin primitives (Task 6); `host.TryGetOwner`, `CellSim`, `PlayerIdentity`, existing `SetPlayerState`/`TryGetPlayerState`.
- Produces: `ShardedWorldServer : IWorldPersistenceHost, IAdminControllable`.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/NetWorld/ShardedWorldServerAdminTests.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedWorldServerAdminTests
{
    private static float Flat(float x, float z) => 0f;

    private static ShardedWorldServer JoinOne(string account, out NetClient client, out ShardedWorldServerConfig config, out int slot)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new ShardedWorldServer(st, config, Flat, MoveTuning.Default);
        client = new NetClient(ct, Encoding.UTF8.GetBytes(account));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        server.Tick(config.TickSeconds);   // publish snapshot
        slot = server.JoinedSlots.First();
        return server;
    }

    [Fact]
    public void ListOnline_ReflectsJoinedPlayer()
    {
        ShardedWorldServer server = JoinOne("alice", out _, out _, out _);
        IReadOnlyList<OnlinePlayer> online = server.ListOnline();
        Assert.Single(online);
        Assert.Equal("alice", online[0].AccountId);
    }

    [Fact]
    public void Teleport_MovesAuthoritativeState_AcrossCells()
    {
        ShardedWorldServer server = JoinOne("alice", out _, out ShardedWorldServerConfig config, out int slot);
        // CellSize default 60: teleport well into a different cell.
        server.Teleport(PlayerRef.Account("alice"), new Vector3(150f, 0f, 150f));
        for (int i = 0; i < 4; i++) { server.Poll(); server.Tick(config.TickSeconds); }
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState s));
        Assert.Equal(150f, s.Position.X, 2);
        Assert.Equal(150f, s.Position.Z, 2);
    }

    [Fact]
    public void Kick_DisconnectsPlayer()
    {
        ShardedWorldServer server = JoinOne("alice", out NetClient client, out ShardedWorldServerConfig config, out int slot);
        server.Kick(PlayerRef.Slot(slot), "bye");
        for (int i = 0; i < 60 && server.PlayerCount > 0; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(0, server.PlayerCount);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ShardedWorldServerAdminTests`
Expected: FAIL (compile: `ShardedWorldServer` does not implement `IAdminControllable`).

- [ ] **Step 3: Add field + interface declaration**

In `KhaozEngine.NetWorld/ShardedWorldServer.cs`, change the class declaration (line 55):
```csharp
public sealed class ShardedWorldServer : IWorldPersistenceHost, IAdminControllable
```
Add a field after `private readonly DrainController drain = new();` (line 74):
```csharp
    private readonly AdminCommandBuffer admin = new();
```

- [ ] **Step 4: Add the IAdminControllable members + helpers**

Insert before `private void OnJoin(` (line 303):
```csharp
    /// <inheritdoc/>
    public IReadOnlyList<OnlinePlayer> ListOnline() => admin.Online;

    /// <inheritdoc/>
    public void Teleport(PlayerRef target, Vector3 position) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.Teleport, Target = target, Position = position });

    /// <inheritdoc/>
    public void Kick(PlayerRef target, string reason) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.Kick, Target = target, Text = reason ?? string.Empty });

    /// <inheritdoc/>
    public void Broadcast(string text) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.Broadcast, Text = text ?? string.Empty });

    private int ResolveSlot(in PlayerRef target)
    {
        if (target.IsSlot) return netIdBySlot.ContainsKey(target.SlotValue) ? target.SlotValue : -1;
        foreach (KeyValuePair<int, string> kv in accountIdBySlot)
            if (kv.Value == target.AccountValue) return kv.Key;
        return -1;
    }

    private void SendNoticeTo(int slot, in ServerNotice notice)
    {
        byte[] envelope = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Notice, MoveProtocol.EncodeNotice(notice));
        net.SendTo(slot, envelope, NetChannelReliability.ReliableOrdered);
    }

    private void ApplyAdminCommand(AdminCommand cmd)
    {
        switch (cmd.Kind)
        {
            case AdminCommandKind.Teleport:
            {
                int slot = ResolveSlot(cmd.Target);
                if (slot >= 0 && TryGetPlayerState(slot, out PlayerMoveState st))
                {
                    st.Position = cmd.Position;
                    st.VerticalVelocity = 0f;
                    SetPlayerState(slot, st);
                }
                break;
            }
            case AdminCommandKind.Kick:
            {
                int slot = ResolveSlot(cmd.Target);
                if (slot >= 0)
                {
                    SendNoticeTo(slot, new ServerNotice(ServerNoticeKind.Custom, cmd.Text));
                    Disconnect(slot);
                }
                break;
            }
            case AdminCommandKind.Broadcast:
                BroadcastNotice(new ServerNotice(ServerNoticeKind.Custom, cmd.Text));
                break;
        }
    }

    private OnlinePlayer[] BuildOnlineSnapshot()
    {
        var list = new List<OnlinePlayer>(netIdBySlot.Count);
        foreach (int slot in netIdBySlot.Keys)
        {
            if (!netIdBySlot.TryGetValue(slot, out int netId)) continue;
            string acct = accountIdBySlot.TryGetValue(slot, out string? a) ? a : string.Empty;
            TryGetPlayerState(slot, out PlayerMoveState st);
            string name = string.Empty;
            if (host.TryGetOwner(netId, out CellSim cell, out Entity e) && cell.World.TryGet(e, out PlayerIdentity pi))
                name = pi.DisplayName ?? string.Empty;
            list.Add(new OnlinePlayer(slot, acct, name, st.Position, st.Grounded, st.VerticalVelocity, netId));
        }
        return list.ToArray();
    }
```

- [ ] **Step 5: Wire drain + publish into `Tick`**

In `Tick(float dt)` (line 241), add as the FIRST statement (before `var slots = ...`):
```csharp
        admin.Drain(ApplyAdminCommand);
```
And as the LAST statement (after `drain.Advance(dt);`):
```csharp
        admin.Publish(BuildOnlineSnapshot());
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ShardedWorldServerAdminTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.NetWorld/ShardedWorldServer.cs KhaozEngine.Tests/NetWorld/ShardedWorldServerAdminTests.cs
git commit -m "netcode: ShardedWorldServer admin surface (shared IAdminControllable seam)"
```

---

### Task 10: `ShardedWorldServer` ban-at-connect

**Files:**
- Modify: `KhaozEngine.NetWorld/ShardedWorldServer.cs`
- Test: `KhaozEngine.Tests/NetWorld/ShardedWorldServerBanTests.cs`

**Interfaces:**
- Produces: `ShardedWorldServer(..., IConnectionAuthenticator? authenticator = null, IBanStore? banStore = null)`.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/NetWorld/ShardedWorldServerBanTests.cs`:
```csharp
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedWorldServerBanTests
{
    private static float Flat(float x, float z) => 0f;

    [Fact]
    public async Task BannedAccount_IsRejectedAtConnect()
    {
        var bans = new InMemoryBanStore();
        await bans.BanAsync("evil", "cheating");

        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new ShardedWorldServer(st, config, Flat, MoveTuning.Default, banStore: bans);
        var client = new NetClient(ct, Encoding.UTF8.GetBytes("evil"));

        for (int i = 0; i < 200; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }

        Assert.Equal(0, server.PlayerCount);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ShardedWorldServerBanTests`
Expected: FAIL (compile: no `banStore:` parameter).

- [ ] **Step 3: Add ctor param + field**

In `ShardedWorldServer.cs`, extend the ctor (line 78-81):
```csharp
    public ShardedWorldServer(INetTransport transport, ShardedWorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning, Func<float, float, Vector3>? groundNormal = null,
        WorldBounds? bounds = null, IPhysicsWorld? physics = null,
        IConnectionAuthenticator? authenticator = null, IBanStore? banStore = null)
```
Add a field after `private readonly AdminCommandBuffer admin = new();`:
```csharp
    private readonly IBanStore? banStore;
```
Assign in the ctor body (after `net = new NetServer(...)`, line 101):
```csharp
        this.banStore = banStore;
```

- [ ] **Step 4: Add the connect check in `OnJoin`**

In `OnJoin` (line 303), prepend the account-id computation + ban check, and remove the later duplicate `string accountId = ...` (was line 323). The method should begin:
```csharp
    private void OnJoin(int slot, string subject, string displayName)
    {
        string accountId = string.IsNullOrEmpty(subject) ? $"guest:{slot}" : subject;
        if (banStore is not null && banStore.IsBanned(accountId))
        {
            SendNoticeTo(slot, new ServerNotice(ServerNoticeKind.Custom, "banned"));
            net.Disconnect(slot);
            return;
        }

        // Belt-and-suspenders: clear any stale command-queue state on the (recycled) slot before spawning, in case
        // a prior occupant's Left was ever missed. A fresh session's seqs restart at 0; a stale high-water mark
        // would reject every one and freeze the player (see OnLeave).
        commands.Forget(slot);
```
Then DELETE the later line `string accountId = string.IsNullOrEmpty(subject) ? $"guest:{slot}" : subject;`.

- [ ] **Step 5: Run test (+ regression) to verify**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "ShardedWorldServerBanTests|ShardedWorldServerAdminTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.NetWorld/ShardedWorldServer.cs KhaozEngine.Tests/NetWorld/ShardedWorldServerBanTests.cs
git commit -m "netcode: ShardedWorldServer ban-at-connect (optional IBanStore)"
```

---

### Task 11: `ServerAdmin` facade

**Files:**
- Create: `KhaozEngine.NetWorld/ServerAdmin.cs`
- Test: `KhaozEngine.Tests/NetWorld/ServerAdminTests.cs`

**Interfaces:**
- Consumes: `IAdminControllable`, `IBanStore`, `OnlinePlayer`, `PlayerRef`, `BanRecord` (Tasks 4/6); `IEnumerableWorldStore`, `WorldStoreEntry` (Task 1).
- Produces: `ServerAdmin(IAdminControllable server, IBanStore? bans = null, IEnumerableWorldStore? accounts = null)` with `ListOnline/Teleport/Kick/Broadcast/BanAsync/UnbanAsync/ListBans/ListAccountsAsync`, `bool BansSupported`, `bool AccountsSupported`.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/NetWorld/ServerAdminTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerAdminTests
{
    private static float Flat(float x, float z) => 0f;

    [Fact]
    public async Task BanAsync_PersistsAndKicksOnlinePlayer()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var bans = new InMemoryBanStore();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, banStore: bans);
        var client = new NetClient(ct, Encoding.UTF8.GetBytes("evil"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }

        var admin = new ServerAdmin(server, bans);
        await admin.BanAsync("evil", "cheating");
        for (int i = 0; i < 60 && server.PlayerCount > 0; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.True(bans.IsBanned("evil"));
        Assert.Equal(0, server.PlayerCount);
        Assert.Equal("cheating", admin.ListBans().Single().Reason);
    }

    [Fact]
    public async Task ListAccounts_MaterializesEnumeration_AndFeatureDetects()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:1", new byte[] { 1 });

        var admin = new ServerAdmin(server, bans: null, accounts: store);
        IReadOnlyList<WorldStoreEntry> accounts = await admin.ListAccountsAsync("player:");

        Assert.Single(accounts);
        Assert.True(admin.AccountsSupported);
        Assert.False(admin.BansSupported);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await admin.UnbanAsync("x"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ServerAdminTests`
Expected: FAIL (compile: `ServerAdmin` not found).

- [ ] **Step 3: Implement**

`KhaozEngine.NetWorld/ServerAdmin.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Transport-agnostic admin facade composing the three admin capabilities: live commands over any
/// <see cref="IAdminControllable"/> (WorldServer or ShardedWorldServer), an optional <see cref="IBanStore"/>, and an
/// optional <see cref="IEnumerableWorldStore"/> for listing persisted accounts. This is the in-process embodiment of
/// the admin surface; <c>KhaozEngine.Server.Admin</c> is a thin HTTPS shell over it. Banning an online account here
/// persists the ban and then kicks the player. Capabilities not wired (null bans/accounts) throw
/// <see cref="NotSupportedException"/> so a caller can feature-detect via <see cref="BansSupported"/> /
/// <see cref="AccountsSupported"/>.
/// </summary>
public sealed class ServerAdmin
{
    private readonly IAdminControllable server;
    private readonly IBanStore? bans;
    private readonly IEnumerableWorldStore? accounts;

    public ServerAdmin(IAdminControllable server, IBanStore? bans = null, IEnumerableWorldStore? accounts = null)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.bans = bans;
        this.accounts = accounts;
    }

    /// <summary>True if a ban store was wired (ban/unban/list-bans are available).</summary>
    public bool BansSupported => bans is not null;
    /// <summary>True if an enumerable account store was wired (list-accounts is available).</summary>
    public bool AccountsSupported => accounts is not null;

    public IReadOnlyList<OnlinePlayer> ListOnline() => server.ListOnline();
    public void Teleport(PlayerRef target, Vector3 position) => server.Teleport(target, position);
    public void Kick(PlayerRef target, string reason) => server.Kick(target, reason);
    public void Broadcast(string text) => server.Broadcast(text);

    /// <summary>Persists a ban then kicks the account if it is currently online (no-op if offline).</summary>
    public async ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null, CancellationToken ct = default)
    {
        IBanStore store = bans ?? throw new NotSupportedException("No ban store configured.");
        await store.BanAsync(accountId, reason, until, ct).ConfigureAwait(false);
        server.Kick(PlayerRef.Account(accountId), reason);
    }

    public ValueTask UnbanAsync(string accountId, CancellationToken ct = default)
        => (bans ?? throw new NotSupportedException("No ban store configured.")).UnbanAsync(accountId, ct);

    public IReadOnlyCollection<BanRecord> ListBans()
        => (bans ?? throw new NotSupportedException("No ban store configured.")).ListBans();

    /// <summary>Materializes <see cref="IEnumerableWorldStore.EnumerateAsync"/> into a list (admin "list accounts").</summary>
    public async Task<IReadOnlyList<WorldStoreEntry>> ListAccountsAsync(string? keyPrefix = null, CancellationToken ct = default)
    {
        IEnumerableWorldStore store = accounts ?? throw new NotSupportedException("No enumerable account store configured.");
        var list = new List<WorldStoreEntry>();
        await foreach (WorldStoreEntry e in store.EnumerateAsync(keyPrefix, ct).ConfigureAwait(false)) list.Add(e);
        return list;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ServerAdminTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/ServerAdmin.cs KhaozEngine.Tests/NetWorld/ServerAdminTests.cs
git commit -m "netcode: ServerAdmin facade (live commands + bans + account enumeration)"
```

---

### Task 12: `KhaozEngine.Server.Admin` package scaffold + `AdminTlsCertificate` + `AdminEndpointOptions`

**Files:**
- Create: `KhaozEngine.Server.Admin/KhaozEngine.Server.Admin.csproj`
- Create: `KhaozEngine.Server.Admin/AdminTlsCertificate.cs`
- Create: `KhaozEngine.Server.Admin/AdminEndpointOptions.cs`
- Create: `KhaozEngine.Server.Admin/README.md` (placeholder header; full content in Task 14)
- Modify: `KhaozEngine.slnx` (register the project)
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj` (reference the project + add the AspNetCore framework ref)
- Test: `KhaozEngine.Tests/ServerAdmin/AdminTlsCertificateTests.cs`

**Interfaces:**
- Produces: `KhaozEngine.Server.Admin.AdminTlsCertificate` (factories `FromCertificate`/`FromPfx`/`FromPfxBytes`/`FromPem`/`FromPemBytes`/`CreateSelfSigned`, property `X509Certificate2 Certificate`); `AdminEndpointOptions { int Port; string BearerToken; AdminTlsCertificate Certificate; IPAddress BindAddress; string PathBase; }`.

- [ ] **Step 1: Create the package csproj**

`KhaozEngine.Server.Admin/KhaozEngine.Server.Admin.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Server.Admin</PackageId>
    <Version>$(KhaozEngineVersion)</Version>
    <Description>Opt-in HTTPS admin endpoint for a KhaozEngine game server: a minimal Kestrel listener (TLS + a single bearer token) exposing the generic ServerAdmin surface (list/teleport/kick/broadcast online players, enumerate persisted accounts, ban/unban) as a small REST API. The ONLY package that references ASP.NET Core (via a FrameworkReference); deliberately NOT bundled in the KhaozEngine.Server umbrella, so a sim server that does not want an admin endpoint never pulls the web stack. Off until the consumer wires a port, certificate, and token.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="../KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj" />
    <ProjectReference Include="../KhaozEngine.WorldStore/KhaozEngine.WorldStore.csproj" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create a minimal README placeholder (full content in Task 14)**

`KhaozEngine.Server.Admin/README.md`:
```markdown
# KhaozEngine.Server.Admin

Opt-in HTTPS admin endpoint (Kestrel + bearer token over TLS) over the generic `ServerAdmin` surface. See
`docs/USING-KHAOZENGINE.md` ("Server administration"). Not bundled in the `KhaozEngine.Server` umbrella; add it
explicitly when you want an admin endpoint.
```

- [ ] **Step 3: Register in the solution + test project**

In `KhaozEngine.slnx`, add (alphabetical, next to the other `KhaozEngine.Server` line):
```xml
  <Project Path="KhaozEngine.Server.Admin/KhaozEngine.Server.Admin.csproj" />
```
In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, add a `FrameworkReference` (so the test host can load the ASP.NET Core shared framework) inside the first `<ItemGroup>` (the one with the package references):
```xml
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
```
and add a project reference alongside the other `<ProjectReference>` entries:
```xml
    <ProjectReference Include="../KhaozEngine.Server.Admin/KhaozEngine.Server.Admin.csproj" />
```

- [ ] **Step 4: Write the failing test**

`KhaozEngine.Tests/ServerAdmin/AdminTlsCertificateTests.cs`:
```csharp
using KhaozEngine.Server.Admin;
using Xunit;

namespace KhaozEngine.Tests.ServerAdmin;

public class AdminTlsCertificateTests
{
    [Fact]
    public void CreateSelfSigned_ProducesCertWithPrivateKey()
    {
        var tls = AdminTlsCertificate.CreateSelfSigned("khaoz-admin");
        Assert.True(tls.Certificate.HasPrivateKey);
        Assert.Contains("khaoz-admin", tls.Certificate.Subject);
    }
}
```

- [ ] **Step 5: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter AdminTlsCertificateTests`
Expected: FAIL (compile: `AdminTlsCertificate` / package not found).

- [ ] **Step 6: Implement `AdminTlsCertificate`**

`KhaozEngine.Server.Admin/AdminTlsCertificate.cs`:
```csharp
using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KhaozEngine.Server.Admin;

/// <summary>
/// TLS material for the admin endpoint. A pinned self-signed certificate is the expected default (the consumer's
/// console pins its thumbprint); load a real one from PFX/PEM when you have it.
/// </summary>
public sealed class AdminTlsCertificate
{
    private AdminTlsCertificate(X509Certificate2 cert) => Certificate = cert;

    /// <summary>The certificate (with private key) Kestrel binds for TLS.</summary>
    public X509Certificate2 Certificate { get; }

    public static AdminTlsCertificate FromCertificate(X509Certificate2 certificate)
        => new(certificate ?? throw new ArgumentNullException(nameof(certificate)));

    public static AdminTlsCertificate FromPfx(string path, string? password = null)
        => new(X509CertificateLoader.LoadPkcs12FromFile(path, password));

    public static AdminTlsCertificate FromPfxBytes(byte[] pfx, string? password = null)
        => new(X509CertificateLoader.LoadPkcs12(pfx, password));

    public static AdminTlsCertificate FromPem(string certPath, string keyPath)
        => new(X509Certificate2.CreateFromPemFile(certPath, keyPath));

    public static AdminTlsCertificate FromPemBytes(byte[] certPem, byte[] keyPem)
        => new(X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(certPem), Encoding.UTF8.GetString(keyPem)));

    /// <summary>Generates a self-signed RSA-2048 certificate (default 10-year lifetime). Re-imports through a PFX so
    /// the private key is persisted and Kestrel can use it for TLS on every platform.</summary>
    public static AdminTlsCertificate CreateSelfSigned(string subjectName, TimeSpan? lifetime = null)
    {
        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={subjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 generated = req.CreateSelfSigned(now.AddDays(-1), now.Add(lifetime ?? TimeSpan.FromDays(3650)));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        return new AdminTlsCertificate(X509CertificateLoader.LoadPkcs12(pfx, null));
    }
}
```

- [ ] **Step 7: Implement `AdminEndpointOptions`**

`KhaozEngine.Server.Admin/AdminEndpointOptions.cs`:
```csharp
using System.Net;

namespace KhaozEngine.Server.Admin;

/// <summary>Consumer-supplied configuration for the admin endpoint. Off until you construct an
/// <see cref="AdminHttpServer"/> with these. Binds to loopback by default (the console runs on the same host or
/// reaches it through a tunnel).</summary>
public sealed class AdminEndpointOptions
{
    /// <summary>The admin listen port (separate from the game transport port).</summary>
    public required int Port { get; init; }

    /// <summary>The single bearer token required on every request (constant-time compared).</summary>
    public required string BearerToken { get; init; }

    /// <summary>The TLS certificate Kestrel binds.</summary>
    public required AdminTlsCertificate Certificate { get; init; }

    /// <summary>Bind address; defaults to loopback.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>Route prefix for every admin endpoint.</summary>
    public string PathBase { get; init; } = "/admin";
}
```

- [ ] **Step 8: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter AdminTlsCertificateTests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add KhaozEngine.Server.Admin/ KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Tests/ServerAdmin/AdminTlsCertificateTests.cs
git commit -m "server-admin: scaffold KhaozEngine.Server.Admin package + TLS cert helper + options"
```

---

### Task 13: `AdminHttpServer` (Kestrel REST endpoint over `ServerAdmin`)

**Files:**
- Create: `KhaozEngine.Server.Admin/AdminHttpServer.cs`
- Test: `KhaozEngine.Tests/ServerAdmin/AdminHttpServerTests.cs`

**Interfaces:**
- Consumes: `ServerAdmin`, `PlayerRef` (Tasks 11/6); `AdminEndpointOptions`, `AdminTlsCertificate` (Task 12).
- Produces: `AdminHttpServer(ServerAdmin admin, AdminEndpointOptions options) : IAsyncDisposable` with `Task StartAsync(CancellationToken)`, `Task StopAsync(CancellationToken)`.

- [ ] **Step 1: Write the failing integration test**

`KhaozEngine.Tests/ServerAdmin/AdminHttpServerTests.cs`:
```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin;
using Xunit;

namespace KhaozEngine.Tests.ServerAdmin;

public class AdminHttpServerTests
{
    private static float Flat(float x, float z) => 0f;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Requires_Bearer_And_Serves_Online()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new NetClient(ct, Encoding.UTF8.GetBytes("alice"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        server.Tick(config.TickSeconds);

        var admin = new ServerAdmin(server);
        int port = FreePort();
        var opts = new AdminEndpointOptions
        {
            Port = port,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
        };
        await using var http = new AdminHttpServer(admin, opts);
        await http.StartAsync();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var hc = new HttpClient(handler);
        string baseUrl = $"https://127.0.0.1:{port}/admin";

        HttpResponseMessage noAuth = await hc.GetAsync(baseUrl + "/online");
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

        hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        HttpResponseMessage ok = await hc.GetAsync(baseUrl + "/online");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        string body = await ok.Content.ReadAsStringAsync();
        Assert.Contains("alice", body);

        await http.StopAsync();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter AdminHttpServerTests`
Expected: FAIL (compile: `AdminHttpServer` not found).

- [ ] **Step 3: Implement `AdminHttpServer`**

`KhaozEngine.Server.Admin/AdminHttpServer.cs`:
```csharp
using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KhaozEngine.Server.Admin;

/// <summary>
/// A minimal Kestrel HTTPS listener exposing the <see cref="ServerAdmin"/> surface as a small REST API, guarded by a
/// single bearer token. Off until constructed; binds the supplied certificate and address from
/// <see cref="AdminEndpointOptions"/>. Mutating routes return 202 (the command is enqueued / awaited on the store);
/// capabilities not wired in the facade return 501.
/// </summary>
public sealed class AdminHttpServer : IAsyncDisposable
{
    private readonly WebApplication app;

    public AdminHttpServer(ServerAdmin admin, AdminEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(options);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(options.BindAddress, options.Port, listen => listen.UseHttps(options.Certificate.Certificate)));
        app = builder.Build();

        byte[] expected = Encoding.UTF8.GetBytes("Bearer " + options.BearerToken);
        app.Use(async (ctx, next) =>
        {
            byte[] got = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
            if (got.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(got, expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(ctx);
        });

        RouteGroupBuilder g = app.MapGroup(options.PathBase);

        g.MapGet("/online", () => Results.Json(admin.ListOnline()));

        g.MapPost("/teleport", (TeleportRequest r) =>
        {
            admin.Teleport(r.ToRef(), new Vector3(r.X, r.Y, r.Z));
            return Results.Accepted();
        });

        g.MapPost("/kick", (KickRequest r) =>
        {
            admin.Kick(r.ToRef(), r.Reason ?? string.Empty);
            return Results.Accepted();
        });

        g.MapPost("/broadcast", (BroadcastRequest r) =>
        {
            admin.Broadcast(r.Text ?? string.Empty);
            return Results.Accepted();
        });

        g.MapGet("/accounts", async (string? prefix) =>
            admin.AccountsSupported
                ? Results.Json(await admin.ListAccountsAsync(prefix))
                : Results.StatusCode(StatusCodes.Status501NotImplemented));

        g.MapGet("/bans", () =>
            admin.BansSupported
                ? Results.Json(admin.ListBans())
                : Results.StatusCode(StatusCodes.Status501NotImplemented));

        g.MapPost("/ban", async (BanRequest r) =>
        {
            if (!admin.BansSupported) return Results.StatusCode(StatusCodes.Status501NotImplemented);
            await admin.BanAsync(r.AccountId, r.Reason ?? string.Empty, r.Until);
            return Results.Accepted();
        });

        g.MapPost("/unban", async (UnbanRequest r) =>
        {
            if (!admin.BansSupported) return Results.StatusCode(StatusCodes.Status501NotImplemented);
            await admin.UnbanAsync(r.AccountId);
            return Results.Accepted();
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => app.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => app.StopAsync(cancellationToken);
    public ValueTask DisposeAsync() => app.DisposeAsync();
}

// Request DTOs (internal; minimal-API JSON binding reads their public properties).
internal sealed record TeleportRequest(int? Slot, string? Account, float X, float Y, float Z)
{
    public PlayerRef ToRef() => Slot is { } s ? PlayerRef.Slot(s) : PlayerRef.Account(Account ?? string.Empty);
}
internal sealed record KickRequest(int? Slot, string? Account, string? Reason)
{
    public PlayerRef ToRef() => Slot is { } s ? PlayerRef.Slot(s) : PlayerRef.Account(Account ?? string.Empty);
}
internal sealed record BroadcastRequest(string? Text);
internal sealed record BanRequest(string AccountId, string? Reason, DateTimeOffset? Until);
internal sealed record UnbanRequest(string AccountId);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter AdminHttpServerTests`
Expected: PASS. If minimal-API body binding rejects the `internal` DTOs at runtime, change `internal sealed record` to `public sealed record` for the five request types and re-run.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Server.Admin/AdminHttpServer.cs KhaozEngine.Tests/ServerAdmin/AdminHttpServerTests.cs
git commit -m "server-admin: AdminHttpServer (Kestrel REST endpoint, bearer auth, TLS)"
```

---

### Task 14: Documentation sweep

Doc-only task (no test). Update every doc that should mention the new surface, then grep to confirm nothing is missed. No version bump here (that is Task 15).

**Files:**
- Modify: `docs/USING-KHAOZENGINE.md` (new section)
- Modify: `README.md` (package catalog table + repo-layout block)
- Modify: `CLAUDE.md` (package map: `Server.Admin` entry, `NetWorld` admin/ban note, `WorldStore` enumeration note, umbrella note)
- Modify: `docs/CONSUMERS.md` (note that `Server.Admin` is an opt-in sibling)
- Modify: `docs/DEPENDENCY-SEAMS.md` (new edges + seams)
- Modify: `KhaozEngine.NetWorld/README.md` (admin surface + ban seam)
- Modify: `KhaozEngine.WorldStore/README.md` (enumeration)
- Modify: `KhaozEngine.WorldStore.Sqlite/README.md` + `KhaozEngine.WorldStore.SqlServer/README.md` (enumeration impl)
- Replace: `KhaozEngine.Server.Admin/README.md` (full content)

- [ ] **Step 1: Add the USING-KHAOZENGINE.md section**

In `docs/USING-KHAOZENGINE.md`, after the `### Reconnect + server notices (KhaozEngine.NetWorld 8.2.0)` section (around line 2300-2370) and before `### World cell grid`, insert:
```markdown
## Server administration (`ServerAdmin` / `IBanStore` / `IEnumerableWorldStore` / `KhaozEngine.Server.Admin`) (since 8.4.0)

A generic, opt-in admin surface for a live server. Nothing changes for a server that does not use it.

**Live commands.** Both `WorldServer` and `ShardedWorldServer` implement `IAdminControllable`:
`ListOnline()` returns the connected players (slot, account id, display name, position, grounded, vertical velocity,
net id) from a snapshot published once per tick; `Teleport(PlayerRef, Vector3)`, `Kick(PlayerRef, reason)`, and
`Broadcast(text)` are queued and applied on the host thread between ticks, so you can call them safely from another
thread (an HTTP handler). Target a player by `PlayerRef.Slot(n)` or `PlayerRef.Account("...")`.

**Bans.** `IBanStore` is consulted at connect (alongside the authenticator): a banned account is rejected before it
spawns. `InMemoryBanStore` is the default; `WorldStoreBanStore` persists over any `IWorldStore` keyspace
(`ban:{accountId}`) and caches in memory so the connect check stays synchronous (call `LoadAsync()` once at startup
to hydrate from the store). Pass it as the trailing `banStore:` ctor arg on either server. Bans key on the verified
account id; guests are not bannable.

**Account enumeration.** Stores opt into `IEnumerableWorldStore` (`InMemoryWorldStore`, `SqliteWorldStore`,
`SqlServerWorldStore` all do): `EnumerateAsync(keyPrefix?)` streams `WorldStoreEntry { Key, UpdatedAt, Size? }`.
Feature-detect with `store is IEnumerableWorldStore`.

**Facade.** `ServerAdmin(IAdminControllable server, IBanStore? bans = null, IEnumerableWorldStore? accounts = null)`
composes the three: `BanAsync` persists then kicks if the account is online; `ListAccountsAsync(prefix)` materializes
the enumeration; unwired capabilities throw `NotSupportedException` (feature-detect via `BansSupported` /
`AccountsSupported`).

**HTTPS endpoint (`KhaozEngine.Server.Admin`).** An opt-in package (the only one that pulls ASP.NET Core, via a
`FrameworkReference`; not in the `Server` umbrella - add it explicitly). It hosts a minimal Kestrel REST API over a
`ServerAdmin`, TLS + a single bearer token:

```csharp
var admin = new ServerAdmin(worldServer, new WorldStoreBanStore(store), store);
var endpoint = new AdminHttpServer(admin, new AdminEndpointOptions
{
    Port = 9443,
    BearerToken = "<long-random-secret>",
    Certificate = AdminTlsCertificate.CreateSelfSigned("my-game-admin"),   // pin its thumbprint in your console
});
await endpoint.StartAsync();
// ... run the server loop ...
await endpoint.StopAsync();
```

Routes (all under `/admin`, all require `Authorization: Bearer <token>`): `GET /online`, `POST /teleport`,
`POST /kick`, `POST /broadcast`, `GET /accounts?prefix=`, `GET /bans`, `POST /ban`, `POST /unban`. Mutations return
202; capabilities not wired return 501. Bind defaults to loopback. There are no changes to the game client wire
protocol.
```

- [ ] **Step 2: Update README.md catalog + layout**

In `README.md`, add a row to the package-catalog table (in the server section, next to `KhaozEngine.NetWorld`):
```markdown
| `KhaozEngine.Server.Admin` | Opt-in HTTPS admin endpoint (Kestrel + bearer token over TLS) over the `ServerAdmin` surface (list/teleport/kick/broadcast, account enumeration, ban/unban). The only package that references ASP.NET Core; not in the `Server` umbrella. |
```
And add to the repo-layout block (alphabetical):
```
KhaozEngine.Server.Admin/      Opt-in HTTPS admin endpoint (Kestrel) over ServerAdmin
```

- [ ] **Step 3: Update CLAUDE.md package map**

In `CLAUDE.md`, in the "Server / parallel-job core types" enumeration: append to the `NetWorld` description a note that it now carries the admin surface and ban seam, e.g. after the 8.2.0 additive paragraph add:
```
    8.4.0 additive: a generic server-admin surface - `IAdminControllable` (`ListOnline`/`Teleport`/`Kick`/`Broadcast`,
    queued + applied on the host thread, online snapshot published per tick) implemented by BOTH `WorldServer` and
    `ShardedWorldServer`; `PlayerRef`/`OnlinePlayer`; an `IBanStore` seam (`InMemoryBanStore` + `WorldStoreBanStore`
    over the `IWorldStore` keyspace `ban:{accountId}`, sync `IsBanned` consulted at connect via the trailing optional
    `banStore:` ctor arg, ban-while-online kicks); and the `ServerAdmin` facade composing them. `WorldStore` gains the
    opt-in `IEnumerableWorldStore` (`EnumerateAsync` + `WorldStoreEntry`) on `InMemoryWorldStore`/`SqliteWorldStore`/
    `SqlServerWorldStore`.
```
And add a new package bullet (near the umbrellas / opt-in backends note) recording the new package:
```
  - **Server admin endpoint (8.4.0):** `Server.Admin` = the opt-in HTTPS admin endpoint (`AdminHttpServer` over
    Kestrel minimal hosting, `AdminEndpointOptions`, `AdminTlsCertificate` incl. `CreateSelfSigned`) exposing
    `ServerAdmin` as a bearer-token REST API. The ONLY package referencing ASP.NET Core (via `FrameworkReference`);
    NOT in any umbrella - added explicitly like `WorldStore.Sqlite` / `Physics.Bepu`.
```

- [ ] **Step 4: Update CONSUMERS.md**

In `docs/CONSUMERS.md`, in the paragraph that lists opt-in siblings (`Physics.Bepu`, `WorldStore.Sqlite/.SqlServer`),
add `Server.Admin` as another opt-in sibling not bundled in the `Server` umbrella. (The engine-version line is bumped in Task 15.)

- [ ] **Step 5: Update DEPENDENCY-SEAMS.md**

In `docs/DEPENDENCY-SEAMS.md`, add: the `IBanStore` seam (NetWorld) and the `IEnumerableWorldStore` seam (WorldStore);
and the new package edge `KhaozEngine.Server.Admin -> NetWorld + WorldStore + Microsoft.AspNetCore.App (shared framework)`.

- [ ] **Step 6: Update the per-package READMEs**

- `KhaozEngine.NetWorld/README.md`: add a short "Server administration (8.4.0)" paragraph describing `IAdminControllable`/`ServerAdmin`/`IBanStore` (mirror the USING section, condensed).
- `KhaozEngine.WorldStore/README.md`: note the opt-in `IEnumerableWorldStore` + `WorldStoreEntry`.
- `KhaozEngine.WorldStore.Sqlite/README.md` and `KhaozEngine.WorldStore.SqlServer/README.md`: note that the store implements `IEnumerableWorldStore` (streaming, prefix-filtered).
- Replace `KhaozEngine.Server.Admin/README.md` with full content:
```markdown
# KhaozEngine.Server.Admin

Opt-in HTTPS admin endpoint for a KhaozEngine game server. A minimal Kestrel listener (TLS + a single bearer token)
exposing the generic `ServerAdmin` surface as a small REST API: list/teleport/kick/broadcast online players,
enumerate persisted accounts, ban/unban.

This is the only KhaozEngine package that references ASP.NET Core (via a `FrameworkReference`), and it is **not**
bundled in the `KhaozEngine.Server` umbrella - add it explicitly when you want an admin endpoint, so a sim server
that does not never pulls the web stack.

```csharp
var admin = new ServerAdmin(worldServer, new WorldStoreBanStore(store), store);
await using var endpoint = new AdminHttpServer(admin, new AdminEndpointOptions
{
    Port = 9443,
    BearerToken = "<long-random-secret>",
    Certificate = AdminTlsCertificate.CreateSelfSigned("my-game-admin"),
});
await endpoint.StartAsync();
```

Routes (under `/admin`, all require `Authorization: Bearer <token>`): `GET /online`, `POST /teleport`, `POST /kick`,
`POST /broadcast`, `GET /accounts?prefix=`, `GET /bans`, `POST /ban`, `POST /unban`. See
`docs/USING-KHAOZENGINE.md` ("Server administration").
```

- [ ] **Step 7: Grep to confirm coverage**

Run:
```bash
grep -rn "Server.Admin\|IEnumerableWorldStore\|IBanStore\|IAdminControllable\|ServerAdmin" --include=*.md . | grep -v docs/superpowers
```
Expected: hits in README.md, CLAUDE.md, docs/USING-KHAOZENGINE.md, docs/CONSUMERS.md, docs/DEPENDENCY-SEAMS.md, and the relevant per-package READMEs. Confirm no doc still describes the old behaviour as complete.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "docs(8.4.0): document the server administration surface across catalog/map/USING/seams/READMEs"
```

---

### Task 15: Release (version bump, CHANGELOG, guard declarations, pack)

**Files:**
- Modify: `Directory.Build.props`
- Modify: `CHANGELOG.md`
- Modify: `docs/CONSUMERS.md` (engine version line), `docs/ROADMAP.md` (current released version), `README.md` (the `<PackageReference>` example version)

- [ ] **Step 1: Bump the engine version**

In `Directory.Build.props`, change `<KhaozEngineVersion>8.3.0</KhaozEngineVersion>` to `<KhaozEngineVersion>8.4.0</KhaozEngineVersion>`.

- [ ] **Step 2: Add the CHANGELOG entry (newest-first, first sentence = digest)**

At the top of `CHANGELOG.md` (newest-first), add:
```markdown
## 8.4.0

Generic, opt-in server administration surface: live admin commands, world-store enumeration, an account-ban seam, and an opt-in HTTPS admin endpoint.

- **Admin command surface.** `WorldServer` and `ShardedWorldServer` now implement `IAdminControllable`: `ListOnline()` (a per-tick published snapshot of connected players: slot, account id, display name, position, grounded, vertical velocity, net id) plus `Teleport(PlayerRef, Vector3)`, `Kick(PlayerRef, reason)`, and `Broadcast(text)`, all queued and applied on the host thread between ticks (thread-safe to call from an HTTP handler). `PlayerRef.Slot(n)` / `PlayerRef.Account(id)` target a player. Additive; existing `WorldServer.Disconnect(slot)` is unchanged.
- **`IEnumerableWorldStore`.** Optional `IWorldStore` capability: `EnumerateAsync(keyPrefix?)` streams `WorldStoreEntry { Key, UpdatedAt, Size? }`. Implemented on `InMemoryWorldStore`, `SqliteWorldStore`, and `SqlServerWorldStore`. `InMemoryWorldStore` now tracks a per-key write timestamp (optional injectable clock; external behaviour unchanged).
- **Ban seam.** `IBanStore` (`InMemoryBanStore` default; `WorldStoreBanStore` persists over the `IWorldStore` keyspace `ban:{accountId}`, hydrated via enumeration). Consulted at connect via the new trailing optional `banStore:` ctor arg on both servers; a banned account is rejected before spawn. `ServerAdmin.BanAsync` persists then kicks an online account.
- **`ServerAdmin` facade** composing all three (transport-agnostic, headless-testable).
- **`KhaozEngine.Server.Admin`** (new, opt-in, NOT in the `Server` umbrella): a minimal Kestrel HTTPS admin endpoint (`AdminHttpServer`, `AdminEndpointOptions`, `AdminTlsCertificate` incl. `CreateSelfSigned`) over `ServerAdmin` with a single bearer token. The only package that references ASP.NET Core (via `FrameworkReference`); a server that does not reference it is unchanged and MonoGame-free.
```

- [ ] **Step 3: Update the three guard declarations**

- `docs/CONSUMERS.md`: change `**Engine current version:** \`8.3.0\`` to `8.4.0`.
- `docs/ROADMAP.md`: change the "Current released version" line to `8.4.0`.
- `README.md`: change the `<PackageReference ... Version="8.3.0" />` example to `8.4.0`.

- [ ] **Step 4: Run the doc-version guard**

Run: `bash scripts/check-doc-versions.sh`
Expected: passes (the three declarations match `8.4.0`).

- [ ] **Step 5: Build + run the FULL test suite on the worktree**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all tests pass (new admin/ban/enumeration/http tests + existing suite, no regressions).

- [ ] **Step 6: Pack to local-feed**

Run:
```bash
mkdir -p local-feed
dotnet pack -c Release -o ./local-feed
```
Expected: packs every package incl. the new `KhaozEngine.Server.Admin.8.4.0.nupkg`.

- [ ] **Step 7: Commit the release**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "netcode(8.4.0): server administration surface (admin commands, enumeration, bans, HTTPS endpoint)"
```

- [ ] **Step 8: Merge to main, then HOLD the tag + push for confirmation**

Per the engine batch-push rule, merge to `main` locally and run the suite on the merged result, but do NOT push or tag without explicit confirmation (CI publishes to GitHub Packages on a `v*` tag). Report that `v8.4.0` is ready to tag + push, and that the packed `local-feed` lets Ruinborne vendor immediately. The merge/cleanup follows the global finishing-a-development-branch options.

---

## Self-Review

**Spec coverage:**
- Part 1 admin commands -> Tasks 6, 7, 9 (both servers, shared seam). Thread-safety contract -> Task 6 (`AdminCommandBuffer`) + the Tick drain/publish wiring in Tasks 7/9. `Disconnect` generalized via `Kick` -> Tasks 7/9 (original retained).
- Part 2 enumeration -> Tasks 1, 2, 3 (InMemory + Sqlite + SqlServer). Feature-detect -> `ServerAdmin` (Task 11).
- Part 3 ban seam -> Tasks 4 (interface + in-memory), 5 (persistent), 8/10 (connect check on both servers), 11 (ban-while-online kick in the facade).
- Part 4 HTTPS transport -> Tasks 12 (package + TLS + options), 13 (Kestrel endpoint). Out-of-umbrella packaging -> Task 12 csproj. Bearer auth + TLS + self-signed default -> Tasks 12/13.
- Docs -> Task 14. Release/version/CHANGELOG/guard/pack -> Task 15.

**Placeholder scan:** No TBD/TODO. Every code step shows complete code; every test step shows the test body and the run command with expected result. Task 14 doc edits give concrete insert text for the new content and explicit locations for the catalog/map updates.

**Type consistency:** `IAdminControllable` (4 members) is consumed identically in Tasks 7/9/11. `PlayerRef.Slot/Account`, `OnlinePlayer` field order, `WorldStoreEntry(Key, UpdatedAt, Size)`, `BanRecord(AccountId, Reason, Until)`, `IBanStore` (sync `IsBanned` + async `BanAsync`/`UnbanAsync` + `ListBans`), and `ServerAdmin` ctor `(IAdminControllable, IBanStore?, IEnumerableWorldStore?)` match across all consuming tasks. The trailing ctor param is `IBanStore? banStore = null` on both servers (Tasks 8/10).

**Known runtime risk flagged in-plan:** minimal-API JSON binding of the `internal` request DTOs (Task 13 Step 4) - if it rejects them, switch the five records to `public`.

