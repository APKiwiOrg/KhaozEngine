# Server administration surface (engine 8.4.0)

A generic, opt-in server-administration surface so any KhaozEngine game-server consumer (Ruinborne
first; Hardpoint / Nullwake / SpaceGame / future games next) can manage live players and persisted
accounts from an out-of-process admin backend (a web console). Four parts, all generic, none
game-specific. Additive minor release: nothing breaks for a consumer that does not opt in, and a
headless server with no admin config behaves exactly as it does on 8.3.0.

## Goals / non-goals

Goals:
- Inspect and control live players on the authoritative server (list / teleport / kick / broadcast).
- List persisted accounts in an `IWorldStore` (the current key/value seam has no enumeration).
- Ban accounts generically: consulted at connect, kicks a currently-online banned account.
- Expose all of the above over a minimal, TLS + bearer-token REST endpoint, opt-in and fully
  consumer-configured, without forcing ASP.NET Core onto server consumers that do not want it.

Non-goals (YAGNI, explicitly out of scope this release):
- Editing or deleting offline account records (Part 2 is read / enumerate only).
- A built-in admin web UI (the consumer's console is its own project; the engine ships the API).
- Per-route roles / scopes beyond the single config bearer token.
- Wire-protocol changes to the game client stream (admin broadcast reuses the existing
  `ServerNotice` channel; kick reason reuses a single-slot `ServerNotice`).
- Demo changes: the `NetworkedWalkServer` / `NetworkedWalkSample` demos stay as they are, so ASP.NET
  Core never lands on them. Wiring is documented instead.

## Packaging

- **Parts 1 and 3** land in `KhaozEngine.NetWorld` (where `WorldServer` / `ShardedWorldServer` live).
  No new dependency; fully headless-testable.
- **Part 2** lands in `KhaozEngine.WorldStore` (the interface) plus the three store implementations.
- **Part 4** is a NEW leaf package `KhaozEngine.Server.Admin`, deliberately **NOT** in the `Server`
  umbrella. Consumers add it explicitly, exactly like `WorldStore.Sqlite` / `WorldStore.SqlServer` /
  `Physics.Bepu`. It is the only thing that references ASP.NET Core, via
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (no NuGet package refs for the web
  stack; the shared-framework reference flows transitively only to consumers that reference this
  package). The engine stays MonoGame-free; the shared framework is a server-side-only dependency.

Rejected packaging alternative: putting the admin endpoint into the `Server` umbrella. That would
drag the ASP.NET Core shared framework onto every umbrella consumer (including those that want only
the sim server), which is the same mistake the umbrella already avoids for the SQL backends.

## Part 1 — Admin command surface

A shared seam both servers implement. Both already expose nearly the entire lower-level contract
(`JoinedSlots`, `TryGetAccountId`, `TryGetPlayerState`, `TryGetPlayerNetId`, `SetPlayerState`,
`SetPlayerDisplayName`, `Disconnect(slot)`, `BroadcastNotice`, `PlayerJoined` / `PlayerLeaving`), so
the seam adds only the four high-level admin operations on top.

```csharp
namespace KhaozEngine.NetWorld;

public interface IAdminControllable
{
    // Lock-free read of the most recently published online snapshot (<= 1 tick stale).
    IReadOnlyList<OnlinePlayer> ListOnline();

    // Queued; applied on the host thread between ticks. Account-or-slot target.
    void Teleport(PlayerRef target, Vector3 position);

    // Queued; generalizes Disconnect(slot) with account-or-slot + a reason delivered to the client.
    void Kick(PlayerRef target, string reason);

    // Queued; delivered to every client as a Custom ServerNotice (surfaced on WorldClient.NoticeReceived).
    void Broadcast(string text);
}

public readonly record struct OnlinePlayer(
    int Slot, string AccountId, string DisplayName,
    Vector3 Position, bool Grounded, float VerticalVelocity, int NetId);

// Either a connection slot or a verified account id. Built via PlayerRef.Slot(int) / PlayerRef.Account(string).
public readonly struct PlayerRef
{
    public static PlayerRef Slot(int slot);
    public static PlayerRef Account(string accountId);
    public bool IsSlot { get; }
    public int SlotValue { get; }
    public string AccountValue { get; }
}
```

### Thread-safety model (the crux)

`WorldServer` / `ShardedWorldServer` are single-threaded by contract: `Poll()` then `Tick(dt)` on one
host-loop thread. `ShardedWorldServer` fans only the per-cell sim across the job scheduler; its
cross-cell passes and all join/leave handling stay on the host thread. The existing
`RemoteCommandQueue` is documented single-thread-only, so admin commands (which arrive on Kestrel
threadpool threads) need their own genuinely thread-safe handoff:

- A thread-safe `ConcurrentQueue<AdminCommand>` (an internal value-type command record:
  `{ AdminCommandKind Kind; PlayerRef Target; Vector3 Position; string Text; }`). `Teleport` / `Kick`
  / `Broadcast` enqueue from any thread.
- The host loop **drains the queue at the top of `Tick`** on the host thread and applies each
  command through the server's existing host-thread-safe methods:
  - Teleport -> resolve target to slot, `SetPlayerState(slot, stateAtPosition)` (on the sharded
    server this writes the owning cell's entity; a subsequent handoff migrates the entity if the
    teleport crossed a cell boundary, `NetId` stable).
  - Kick -> resolve target to slot, send a single-slot `ServerNotice(Custom, reason)`, then
    `Disconnect(slot)`.
  - Broadcast -> `BroadcastNotice(new ServerNotice(Custom, text))`.
- `ListOnline()` returns a `volatile`-published immutable `OnlinePlayer[]` that the host loop
  **rebuilds at the end of `Tick`** (after positions settle / handoffs run). The read is lock-free
  and never touches sim state directly, so it cannot race a worker-threaded cell tick.
- Account -> slot resolution happens on the host thread during drain (reads `accountIdBySlot`).

`WorldServer.Disconnect(slot)` is retained unchanged; `Kick` is the generalized form. The
ConcurrentQueue + published-snapshot machinery is factored into one small internal helper both
servers own, so the two implementations do not duplicate the thread-safety-critical code. Each
server's per-server snapshot population (single world vs. per-cell `host.TryGetOwner`) stays in that
server.

Both servers gain `: IAdminControllable` and the four methods. No constructor change for Part 1.

## Part 2 — IWorldStore enumeration

The current `IWorldStore` is pure key/value (`LoadAsync` / `SaveAsync` / `DeleteAsync` /
`ExistsAsync`) with no way to list keys. Add an **optional** capability interface; stores that cannot
enumerate simply do not implement it, and consumers feature-detect with `store is
IEnumerableWorldStore`.

```csharp
namespace KhaozEngine.WorldStore;

public interface IEnumerableWorldStore
{
    // Streams every stored entry, optionally filtered to keys beginning with keyPrefix
    // (null/empty = all). Order is unspecified.
    IAsyncEnumerable<WorldStoreEntry> EnumerateAsync(string? keyPrefix = null, CancellationToken cancellationToken = default);
}

public readonly record struct WorldStoreEntry(string Key, DateTimeOffset UpdatedAt, long? Size);
```

Implemented on:
- **InMemoryWorldStore** — gains internal `updatedAt` tracking. The backing store changes from
  `ConcurrentDictionary<string, byte[]>` to `ConcurrentDictionary<string, Entry>` where
  `Entry = (byte[] Data, DateTimeOffset UpdatedAt)`. `SaveAsync` stamps `UpdatedAt` from an
  **optional injected clock** (new ctor arg `Func<DateTimeOffset>? clock = null`, default
  `() => DateTimeOffset.UtcNow`), so existing zero-arg construction is unchanged. `Load/Save/Delete/
  Exists` external behavior is identical (still defensive-clones the bytes). `EnumerateAsync` yields
  `Size = Data.Length`.
- **SqliteWorldStore** (`KhaozEngine.WorldStore.Sqlite`) — `SELECT key, updated_at, LENGTH(data)
  FROM world_store [WHERE key LIKE $p ESCAPE '\\']` over the open connection (behind the existing
  semaphore gate), streaming rows via `ExecuteReaderAsync`; `updated_at` (stored Unix ms INTEGER) ->
  `DateTimeOffset.FromUnixTimeMilliseconds`.
- **SqlServerWorldStore** (`KhaozEngine.WorldStore.SqlServer`) — `SELECT [key], updated_at,
  DATALENGTH(data) FROM dbo.world_store [WHERE [key] LIKE @p ESCAPE '\\']` on a per-call connection,
  streaming via `ExecuteReaderAsync`; `updated_at` (DATETIME2, UTC) -> `new DateTimeOffset(dt,
  TimeSpan.Zero)`.

Sqlite is added beyond the two the brief named (InMemory + SqlServer) because it is the
always-tested backend, so enumeration gets a real-DB unit test and the SQL impl is validated by
parity. `keyPrefix` is matched with a parameterized `LIKE prefix% ESCAPE '\\'`, escaping the LIKE
metacharacters `% _ \` in the supplied prefix so a literal prefix cannot be turned into a wildcard.

## Part 3 — Ban seam

A generic ban store consulted at connect alongside the existing `IConnectionAuthenticator`.

```csharp
namespace KhaozEngine.NetWorld;

public interface IBanStore
{
    // Synchronous: consulted on the host thread at connect. Honors expiry. Fast (in-memory cache).
    bool IsBanned(string accountId);

    ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null, CancellationToken cancellationToken = default);
    ValueTask UnbanAsync(string accountId, CancellationToken cancellationToken = default);

    // Current bans (expired entries pruned). For an admin "list bans" view.
    IReadOnlyCollection<BanRecord> ListBans();
}

public readonly record struct BanRecord(string AccountId, string Reason, DateTimeOffset? Until);
```

`IsBanned` is synchronous on purpose: the connect check runs on the host tick thread, where awaiting
DB I/O would stall the sim. Mutators are async so a DB-backed store can persist honestly.

Implementations:
- **InMemoryBanStore** (default): `ConcurrentDictionary<string, BanRecord>`. `IsBanned` checks
  presence and `Until` against an injected clock (`Func<DateTimeOffset>? clock = null`, default
  `UtcNow`); an expired ban returns false (and is pruned lazily). `Ban/UnbanAsync` complete
  synchronously. `ListBans` returns a pruned snapshot.
- **WorldStoreBanStore** (persistent, backend-agnostic): layers over the `IWorldStore` keyspace,
  keys `ban:{accountId}` (a `BanRecord` serialized as tolerant JSON, matching `PlayerRecord`). Holds
  an in-memory cache so `IsBanned` stays sync; `BanAsync` writes through to the store and updates the
  cache; `UnbanAsync` deletes and evicts. `LoadAsync()` hydrates the cache at startup by enumerating
  the `ban:` prefix (requires the store to implement `IEnumerableWorldStore`; documented). Chosen
  over a dedicated SQL ban table because one implementation then works on Sqlite *and* SqlServer (and
  InMemory), and it reuses Part 2.

Wiring into the servers:
- Both `WorldServer` and `ShardedWorldServer` gain an **additive trailing** ctor param
  `IBanStore? banStore = null` (mirrors the existing `IConnectionAuthenticator? authenticator = null`
  placement, so existing call sites compile unchanged).
- The connect check runs at the top of `OnJoin` (host thread), after the account id is resolved
  (`subject`, or `guest:{slot}` when empty): if `banStore?.IsBanned(accountId) == true`, send a
  single-slot `ServerNotice(Custom, reason)`, `Disconnect(slot)`, and return before spawning the
  entity / raising `PlayerJoined` (no persistence load, no `NetId`, no dictionary entries).
- Bans key on the **verified subject**. Guests (`guest:{slot}`) have no stable account, so they are
  not meaningfully bannable; documented.
- Ban-while-online is orchestrated by the facade (persist, then `Kick`), not by the server itself,
  keeping the server's queued commands purely synchronous.

## Facade — `ServerAdmin` (ties Parts 1-3 together)

A transport-agnostic, headless-testable in-proc admin API. It IS Parts 1-3 composed; Part 4 is a
thin HTTP shell over it. Living in `KhaozEngine.NetWorld` (which already references
`KhaozEngine.WorldStore`).

```csharp
namespace KhaozEngine.NetWorld;

public sealed class ServerAdmin
{
    public ServerAdmin(IAdminControllable server, IBanStore? bans = null, IEnumerableWorldStore? accounts = null);

    public IReadOnlyList<OnlinePlayer> ListOnline();
    public void Teleport(PlayerRef target, Vector3 position);
    public void Kick(PlayerRef target, string reason);
    public void Broadcast(string text);

    // Persist the ban, then kick the account if it is currently online (no-op if offline).
    public ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null, CancellationToken ct = default);
    public ValueTask UnbanAsync(string accountId, CancellationToken ct = default);
    public IReadOnlyCollection<BanRecord> ListBans();

    // Materializes the enumeration (admin "list accounts").
    public Task<IReadOnlyList<WorldStoreEntry>> ListAccountsAsync(string? keyPrefix = null, CancellationToken ct = default);

    public bool BansSupported { get; }      // bans != null
    public bool AccountsSupported { get; }  // accounts != null
}
```

When `bans` is null the ban operations throw `NotSupportedException`; when `accounts` is null
`ListAccountsAsync` throws `NotSupportedException`. The REST layer maps those to `501 Not
Implemented`, so a consumer that wires only some capabilities gets honest feature detection.

## Part 4 — HTTPS admin transport (`KhaozEngine.Server.Admin`)

A minimal Kestrel listener on a separate port, off by default, fully consumer-configured.

```csharp
namespace KhaozEngine.Server.Admin;

public sealed class AdminEndpointOptions
{
    public int Port { get; init; }                       // required; the admin port (separate from the game port)
    public required string BearerToken { get; init; }    // required; presented on every request
    public required AdminTlsCertificate Certificate { get; init; }
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;  // default: localhost only
    public string PathBase { get; init; } = "/admin";    // route prefix
}

// TLS material. A pinned self-signed cert is the expected default (the console pins its thumbprint).
public sealed class AdminTlsCertificate
{
    public static AdminTlsCertificate FromPfx(string path, string? password = null);
    public static AdminTlsCertificate FromPfxBytes(byte[] pfx, string? password = null);
    public static AdminTlsCertificate FromPem(string certPath, string keyPath);
    public static AdminTlsCertificate FromPemBytes(byte[] certPem, byte[] keyPem);
    public static AdminTlsCertificate FromCertificate(X509Certificate2 certificate);
    // RSA + CertificateRequest, stdlib only. Returns an exportable cert; the consumer pins its thumbprint.
    public static AdminTlsCertificate CreateSelfSigned(string subjectName, TimeSpan? lifetime = null);
    public X509Certificate2 Certificate { get; }
}

public sealed class AdminHttpServer : IAsyncDisposable
{
    public AdminHttpServer(ServerAdmin admin, AdminEndpointOptions options);
    public Task StartAsync(CancellationToken ct = default);
    public Task StopAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

- Built with `WebApplication.CreateSlimBuilder` (minimal hosting from the shared framework). Kestrel
  is configured to `Listen(BindAddress, Port, o => o.UseHttps(options.Certificate.Certificate))`.
- A bearer-auth middleware runs first on every request: the `Authorization: Bearer <token>` value is
  compared to the configured token with `CryptographicOperations.FixedTimeEquals` (constant time);
  any mismatch or missing header returns `401`.
- Routes (JSON in / out), all under `PathBase`:
  - `GET  /online` -> `OnlinePlayer[]`
  - `POST /teleport` `{ "slot": int? | "account": string?, "x": float, "y": float, "z": float }` -> `202`
  - `POST /kick` `{ "slot": int? | "account": string?, "reason": string }` -> `202`
  - `POST /broadcast` `{ "text": string }` -> `202`
  - `GET  /accounts?prefix=player:` -> `WorldStoreEntry[]` (`501` if accounts unsupported)
  - `POST /ban` `{ "accountId": string, "reason": string, "until": iso8601? }` -> `202` (`501` if bans unsupported)
  - `POST /unban` `{ "accountId": string }` -> `202` (`501` if bans unsupported)
  - `GET  /bans` -> `BanRecord[]` (`501` if bans unsupported)
- Mutating routes return `202 Accepted`: the command is enqueued onto the server's thread-safe admin
  queue (Parts 1) or awaited on the ban store (Part 3); the caller does not block on the next tick.
- No changes to the game client wire protocol.

## Testing (headless, no GPU, no Kestrel for Parts 1-3)

- **Parts 1-3 + ServerAdmin**, on BOTH `WorldServer` and `ShardedWorldServer`, via `LoopbackTransport`
  + `Poll`/`Tick`:
  - Teleport moves the authoritative state (visible after a tick); on the sharded server a
    cross-cell teleport migrates the entity with a stable `NetId`.
  - Kick disconnects the target (by slot and by account); the slot frees.
  - Broadcast reaches a connected `WorldClient` as a Custom notice.
  - `ListOnline` reflects joined players' account / display name / position / grounded / vVel / netId
    after a tick.
  - Cross-thread safety: enqueue admin commands from multiple threads while ticking; assert all
    apply with no corruption.
  - Ban-at-connect: a banned account's join is rejected (no spawn, `PlayerCount` unchanged).
  - Ban-while-online: `ServerAdmin.BanAsync` on an online account persists the ban and kicks the slot.
  - Ban expiry: an expired ban (injected clock) does not reject.
- **Enumeration**: `InMemoryWorldStore` and a real `SqliteWorldStore` (temp DB): full list, prefix
  filter, `UpdatedAt` stamped, `Size` populated, escaping of LIKE metacharacters. `SqlServerWorldStore`
  enumeration has no CI database (consistent with the existing SqlServer store), so its correctness
  rides on parity with the Sqlite test plus code review of the parameterized query.
- **Part 4**: one lightweight integration test. Boot `AdminHttpServer` on an ephemeral loopback port
  with `AdminTlsCertificate.CreateSelfSigned`, drive it with an `HttpClient` whose handler accepts the
  pinned cert: assert `401` without the bearer token, `200` for `GET /online` with it, and one
  mutating round-trip (e.g. `POST /broadcast` reaches a connected loopback client). The test project
  adds `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to reference the admin package.

## Documentation sweep (every place that should mention the new surface)

- `docs/USING-KHAOZENGINE.md`: a new "Server administration" section (the `ServerAdmin` facade, the
  `IBanStore` wiring, `IEnumerableWorldStore` feature detection, and the `KhaozEngine.Server.Admin`
  Kestrel endpoint with a self-signed-cert wiring snippet), slotted near the netcode sections.
- `README.md`: package-catalog table + repo-layout block gain `KhaozEngine.Server.Admin`.
- `CLAUDE.md`: package enumeration gains `Server.Admin` (noting it is opt-in and NOT in any umbrella,
  like `WorldStore.Sqlite`); the `NetWorld` entry notes the admin command surface + ban seam; the
  `WorldStore` entry notes `IEnumerableWorldStore`.
- `docs/CONSUMERS.md`: engine-version line -> 8.4.0; a note that `Server.Admin` is an opt-in sibling
  (not bundled in the `Server` umbrella).
- `docs/DEPENDENCY-SEAMS.md`: new edges (`Server.Admin -> NetWorld + WorldStore + AspNetCore shared
  framework`); the new `IEnumerableWorldStore` and `IBanStore` seams.
- Per-package READMEs (the `PackageReadmeFile` shipped inside each nupkg): new
  `KhaozEngine.Server.Admin/README.md`; `NetWorld/README.md` (admin surface + ban seam);
  `WorldStore/README.md` (enumeration); `WorldStore.Sqlite/README.md` + `WorldStore.SqlServer/README.md`
  (enumeration impl).
- `CHANGELOG.md`: a newest-first 8.4.0 entry whose first sentence is the one-line digest.
- The three guard declarations (`docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md`
  "Current released version", the `README.md` `<PackageReference>` example) -> 8.4.0.

## Release (8.4.0, additive minor)

In this worktree: TDD build (Part 1 + 2 + 3 + facade, then Part 4), full doc sweep, register the new
project in `KhaozEngine.slnx` + `KhaozEngine.Tests.csproj`, bump `<KhaozEngineVersion>` to 8.4.0 with
the `CHANGELOG.md` entry in the same commit, `dotnet pack -c Release -o ./local-feed`, merge to `main`
locally with the test suite green on the merged result. Per the engine batch-push rule the `v8.4.0`
tag + push to `origin` are **held and confirmed with the user** before publishing (CI publishes to
GitHub Packages on a `v*` tag). Ruinborne can vendor the packages from `local-feed` immediately.

The package set Ruinborne vendors to adopt: `KhaozEngine.NetWorld` (admin command surface + ban
seam + `ServerAdmin`), `KhaozEngine.WorldStore` (+ `.SqlServer` for enumeration), and the new opt-in
`KhaozEngine.Server.Admin` (the Kestrel endpoint).
