# Persistent world store design (`WorldStore.Sqlite` + `WorldStore.SqlServer` + `WorldPersistence`)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Program: MMO engine architecture — persistence (the world survives a restart)

## Context

The engine is at `7.48.0` (overworld track: terrain → walkable → forest → networked → streaming). The
server stack is authoritative and complete, but **everything is in-memory** — restart the server and
the world and every player vanish. That is the one property a *persistent-world* MMO cannot lack.

`KhaozEngine.WorldStore` defines `IWorldStore` — a deliberately simple async **keyed blob store**
(`LoadAsync`/`SaveAsync`/`DeleteAsync`/`ExistsAsync`, `string key → byte[]`; the caller serializes its
own records). Only `InMemoryWorldStore` ships, and the interface doc keeps DB backends as
*infrastructure*. Two gaps: there is **no durable backend**, and **nothing calls the seam** — no
save/load orchestration exists. This sub-project closes both, engine-first.

Timing: Ruinborne is provisioning **Azure SQL** for exactly this. Reference repo for the program:
`https://github.com/levy-street/world-of-claudecraft` (persists accounts/characters server-side).

### Locked decisions (from brainstorming)

1. **Two backends** (the same `IWorldStore` contract, validated identically): **SQLite** (embedded,
   zero-infra, the dev/test + single-node backend) and **SQL Server** (`Microsoft.Data.SqlClient`,
   prod = Azure SQL). SQLite is what keeps persistence headless-testable.
2. **Engine-first, opt-in per-backend packages** — the dependency-free `WorldStore` core stays clean;
   each backend is its own package that pulls its own DB dependency. Same pattern as
   `Netcode.LiteNetLib` adding the UDP dep without touching the netcode core. Every game benefits.
3. **`WorldPersistence` orchestration** in `NetWorld` (beside `WorldServer`), backend-agnostic — it
   only touches `IWorldStore` + `KhaozEngine.Serialization`. This is what makes the world actually
   persist.

## Packages

- `KhaozEngine.WorldStore` (existing, dep-free): `IWorldStore` + `InMemoryWorldStore`. Unchanged.
- `KhaozEngine.WorldStore.Sqlite` (NEW, → Server umbrella): `SqliteWorldStore` over
  `Microsoft.Data.Sqlite`.
- `KhaozEngine.WorldStore.SqlServer` (NEW, → Server umbrella): `SqlServerWorldStore` over
  `Microsoft.Data.SqlClient`.

Both back onto one table:

```
world_store ( key       <text/nvarchar(450)>  PRIMARY KEY,
              data      <blob/varbinary(max)>  NOT NULL,
              updated_at <timestamp>           NOT NULL )
```

- Schema bootstrap on construction (`CREATE TABLE IF NOT EXISTS` / SQL Server existence check).
- Upsert via dialect-specific SQL: SQLite `INSERT ... ON CONFLICT(key) DO UPDATE`; SQL Server
  `MERGE` (or `UPDATE ...; IF @@ROWCOUNT = 0 INSERT`).
- Raw ADO.NET, parameterized, async, connection string injected via a small config record. **No
  EF/ORM** (minimal deps).

## `WorldPersistence` — `KhaozEngine.NetWorld`

Wires `IWorldStore` into the `WorldServer` lifecycle. Backend-agnostic.

- **Keys + records**: `player:{accountId}` → a serialized player record (`PlayerMoveState`:
  position, etc.) via `KhaozEngine.Serialization`. The record type is extensible (more fields/record
  kinds later) and versioned-tolerant (unknown-trailing-bytes ignored — see open items).
- **Load-on-join**: on connect, `LoadAsync("player:{id}")` → spawn at the saved position; absent ⇒
  new player at a default spawn.
- **Save-on-leave**: on disconnect, serialize + `SaveAsync`.
- **Periodic snapshot**: every `SaveIntervalSeconds`, save players marked dirty since their last
  save, so a crash loses at most one interval.
- Hooks onto `WorldServer`'s connect/disconnect/tick; no new netcode.

## Data flow

```
connect    → WorldPersistence.LoadAsync(player:{id}) → spawn at saved PlayerMoveState (or default)
tick (Nx)  → save dirty players (periodic snapshot)
disconnect → serialize PlayerMoveState → SaveAsync(player:{id})
restart    → next connect LoadAsync restores the player          [the whole point]
```

## Testing (headless; SQLite carries the always-on coverage)

- **Shared `IWorldStore` conformance suite** (one parameterized test class) run against
  `InMemoryWorldStore` AND `SqliteWorldStore`: save→load round-trip, overwrite, load-absent → null,
  delete (present/absent), exists, key isolation, byte-exactness, basic concurrency.
- **`SqlServerWorldStore`** runs the *same* conformance suite, **gated behind a connection-string env
  var** (skipped in CI when absent; run locally / against an Azure SQL test DB on demand). SQLite is
  the durable backend CI always exercises.
- **`WorldPersistence`** (via SQLite temp file): load-on-join restores a saved position; save-on-leave
  persists; periodic snapshot saves dirty players; **reopen a fresh store on the same SQLite file →
  the player is restored** (the restart-survival test).

## Scope

### In scope

- `KhaozEngine.WorldStore.Sqlite` + `KhaozEngine.WorldStore.SqlServer` (shared conformance).
- `WorldPersistence` orchestration in `NetWorld`, wired into `WorldServer`.
- The demo server (`NetworkedWalkServer`) uses `WorldPersistence` + a `SqliteWorldStore`, so walking
  somewhere, disconnecting, and reconnecting (or restarting the server) restores position.
- Headless tests (conformance + orchestration via SQLite; SQL Server gated).
- Release: **minor** bump; FULL added-package doc sweep (two packages added): `Directory.Build.props`,
  README package catalog + repo-layout, `CLAUDE.md` package map + umbrellas, `docs/CONSUMERS.md`,
  `docs/USING-KHAOZENGINE.md` (a persistence usage section + an Azure SQL connection-string note for
  Ruinborne), the 3 guard declarations, `CHANGELOG.md` + `CHANGENOTES.md`.

### Out of scope (named so they are not forgotten)

- **Per-cell / world-snapshot persistence** — that pairs with **6b multi-cell sharding** (each
  `CellSim` persisting its slice). Player records only here.
- **Record-schema migrations / versioning** — keep the player record forward-tolerant; a real
  migration story is future.
- **Accounts / auth** — the key is just an opaque `accountId`; real auth is later.
- **EF/ORM**, connection pooling beyond the providers' defaults, sharded/partitioned DB.

## Engine-first placement

- Backends → new opt-in packages `KhaozEngine.WorldStore.Sqlite` / `.SqlServer` (Server umbrella),
  core `WorldStore` stays dep-free.
- `WorldPersistence` → `KhaozEngine.NetWorld` (beside `WorldServer`).
- Ruinborne consumes `.SqlServer` (Azure SQL) in prod and `.Sqlite` in dev/test — same `IWorldStore`.

## Open items to confirm during implementation

- The player record's serialized shape + forward-tolerance (ignore unknown trailing bytes so a later
  field add doesn't break old saves).
- `SaveIntervalSeconds` default + the dirty-tracking granularity (per-player flag on movement).
- SQL Server upsert form (`MERGE` vs `UPDATE`/`INSERT`) and whether to add a `created_at`.
- Key namespacing convention (`player:`, room for `world:`, `cell:` later).
- Whether the SQL Server conformance run uses LocalDB / Testcontainers locally or just an injected
  connection string (prefer injected string, gated).

## The MMO architecture program (for orientation)

Overworld track 1–5 ✅ + 6a streaming ✅ (`7.43`–`7.48`). **Persistence — this spec.**
Still ahead: 6b multi-cell server sharding (+ per-cell snapshot persistence, which builds on this),
PBR splat textures + water, the procedural dungeon generator, and glTF animation-clip playback →
animated characters.
