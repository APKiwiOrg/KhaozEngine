# KhaozEngine.WorldStore

Server-side durable persistence seam for an authoritative world.

- **`IWorldStore`** - an async, keyed `byte[]` store (`LoadAsync`/`SaveAsync`/`DeleteAsync`/`ExistsAsync`),
  shaped for a **database** backend rather than the per-user local JSON save files of `KhaozEngine.Persistence`.
  The game serializes a character/zone/account record to bytes (via its own serializer) and persists it by key.
- **`InMemoryWorldStore`** - a thread-safe, dependency-free reference implementation for tests and local dev.

Real backends (SQLite, Postgres, cloud KV) implement `IWorldStore` as **infrastructure** - the engine ships
the seam + the in-memory reference, not a DB driver. Zero dependencies.

Part of the MMO netcode stack (Phase 2). See `docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md`.
