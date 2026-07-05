# Cell-blob schema evolution + restore hardening (MMO arch gap 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:executing-plans (inline) or subagent-driven-development. Steps use checkbox (`- [ ]`) tracking.

**Goal:** Give the cell persistence blob a migration path, a non-throwing (quarantining) restore, unknown-extension retention, and a surfaced diagnostics event, so a component-layout change, a corrupt key, or a registry regression can no longer wipe or corrupt the world at rest.

**Architecture:** The persist snapshot wire format (`[count][per entity: netId + component frames][0]`, extension frames id>=16 length-prefixed, built-ins unframed) is walked by two new Replication helpers (`SnapshotBlobReader`/`SnapshotBlobWriter`). Migrations are `byte[]->byte[]` transforms on the snapshot body, registered on `CellPersistenceConfig` and validated (contiguous, no gaps) at `CellPersistence` construction, mirroring `MigrationChain` semantics without adding a package edge. The async load enqueues the RAW blob; header-parse + migration + quarantine + restore all happen on the server thread in `DrainRestores`, so every diagnostics event is raised on the server thread (mirrors `WorldPersistence.OnStoreError`). Restore uses new TryApply-with-retention semantics: unknown extension frames are captured per-netId in `CellSim` bookkeeping and re-emitted verbatim at `SnapshotOwned` (retain-and-rewrite).

**Tech Stack:** net10.0, C#, xUnit headless tests. Packages: `KhaozEngine.Replication`, `KhaozEngine.Sharding`, `KhaozEngine.NetWorld`.

## Global Constraints

- Engine version bump target: **9.33.0** (next free above tip v9.32.1; additive minor). One `<KhaozEngineVersion>` bump + one `CHANGELOG.md` entry at the end.
- No em-dashes / semicolons in shipped prose (comments, changelog, docs).
- Additive only: no breaking public API. `ICellPersistenceHost` gains a **default interface method**; `CellSim.RestoreOwned(byte[])` keeps its signature (now non-throwing, delegates to the new richer method).
- Snapshot format is owned by `KhaozEngine.Replication`; migration config lives with `CellPersistenceConfig` in `KhaozEngine.NetWorld`. NetWorld already references Replication directly, so no new package edge. Do NOT add a `KhaozEngine.Persistence` edge (inline the ~15-line chain validation instead).
- Byte-identity: a current-`SchemaVersion` blob must restore byte-identically (it bypasses reader/writer/migration entirely). A `SnapshotBlobReader` to `SnapshotBlobWriter` round trip of a well-formed blob must be byte-identical.
- Every new behaviour ships a headless test.

---

## Design decisions (resolve the spec open notes)

1. **Migration delegate shape.** `public delegate byte[] CellSnapshotMigration(byte[] snapshotBody);` operating on the raw snapshot BODY (post 8-byte header). The author uses `SnapshotBlobReader`/`SnapshotBlobWriter` inside. `RegisterMigration(fromVersion, migrate)` returns the config (fluent). Duplicate `fromVersion` throws immediately; contiguity + "no step >= SchemaVersion" validated at `CellPersistence` ctor (registration-freeze point, like `MigrationChainBuilder.Build`).

2. **Built-in vs extension walking.** `SnapshotBlobReader` walks extension frames (id>=16) with no extra knowledge (length-prefixed). Built-in frames (id<16, unframed) need a caller-supplied `builtinPayloadLength(typeId)` resolver giving the OLD-version byte length; absent/unknown throws (cannot walk). The common consumer case (only extension components persisted) needs no resolver.

3. **Retention shape.** Chosen: retain raw unknown-extension frames per-netId in `CellSim` bookkeeping (`Dictionary<int, List<RetainedComponent>>`), re-emitted at `SnapshotOwned`. Justification: the reduced registry has no ECS type for the unknown component to live on, so an ECS-component home is impossible; the spec explicitly blesses the per-entity-bookkeeping fallback. Pruned in `UnregisterOwned` to bound growth. **Documented limitation:** a retained frame does not follow a cell handoff during a registry regression (there is no live component to migrate); retention protects the restart load->save cycle, which is the stated goal.

4. **Quarantine + preservation.** Every undecodable load (corrupt header, corrupt frame, migration threw, too-old, too-new) copies the ORIGINAL raw blob to `quarantine:{cellKey}` before the fresh cell can overwrite the main key, then the cell starts fresh. No suspend-overwrite machinery. Guarantees nothing is destroyed (always copied first). Partial-apply rollback: on decode failure the throwaway view spawned entities are despawned so the cell is genuinely empty.

5. **Baseline after restore.** Clean current-version restore seeds `lastSaved[coord] = body` (no churn). A migrated restore leaves `lastSaved` unset so the upgraded form (current header + migrated body) is rewritten once, advancing the on-disk schema version.

6. **Event kinds:** `SkippedTooOld`, `SkippedTooNew`, `Migrated` (from, to), `QuarantinedCorrupt` (message), `RetainedUnknownExtensions` (count). Raised on the server thread in `DrainRestores`, outside any lock, mirroring `WorldPersistence.OnStoreError`.

## File map

**KhaozEngine.Replication**
- Create `SnapshotBlobReader.cs`: `SnapshotBlobReader(byte[] snapshot, Func<ushort,int>? builtinPayloadLength=null)`, `IReadOnlyList<SnapshotBlobEntity> Entities`. Plus `SnapshotBlobEntity`, `SnapshotBlobComponent` (readonly structs) here.
- Create `SnapshotBlobWriter.cs`: `AddEntity(int netId, IEnumerable<SnapshotBlobComponent>)`, `byte[] ToArray()`.
- Create `RetainedComponent.cs`: public readonly struct `(int NetId, ushort TypeId, byte[] Payload)`.
- Modify `ClientReplicationView.cs`: thread optional `Action<int,ushort,byte[]>? unknownExtensionSink` into `ReadEntityComponents`; add `bool TryApplyRetainingUnknown(World, byte[], out IReadOnlyList<RetainedComponent>, out string?)`.
- Modify `SnapshotWriter.cs`: new `WriteFiltered(..., Func<int, IReadOnlyList<RetainedComponent>?>? retainedExtensionFrames)` overload appending retained frames before each entity terminator.

**KhaozEngine.Sharding**
- Modify `CellSim.cs`: `retainedUnknown` dict; `CellRestoreResult TryRestoreOwned(byte[])` (rollback on failure, stash retained, register owned); `RestoreOwned` delegates (non-throwing); `SnapshotOwned` re-emits retained; prune in `UnregisterOwned`. Add `CellRestoreResult` readonly struct.

**KhaozEngine.NetWorld**
- Modify `CellPersistence.cs`: `CellPersistenceConfig.RegisterMigration` + migration store; `CellPersistence.Issue` event; validate+freeze migration chain in ctor; load path enqueues raw blob; `DrainRestores`/`ProcessLoadedBlob` do header-parse + migrate + quarantine + restore + events on server thread.
- Create `CellPersistenceIssue.cs`: `CellPersistenceIssue` readonly struct + `CellPersistenceIssueKind` enum + `CellSnapshotMigration` delegate.
- Modify `ICellPersistenceHost.cs`: default interface method `CellRestoreReport TryRestoreCell(CellCoord, byte[])` wrapping `RestoreCell`; add `CellRestoreReport` readonly struct.
- Modify `ShardedWorldServer.cs`: override `TryRestoreCell` to call `CellSim.TryRestoreOwned` and translate.

**Tests**
- `KhaozEngine.Tests/Replication/SnapshotBlobTests.cs`: reader/writer round trip byte-identity; extension-only walk with no resolver; built-in walk with resolver; corrupt frame throws.
- `KhaozEngine.Tests/Sharding/CellSimRetentionTests.cs`: unknown-extension retained through reduced-registry save/load and reappears intact under full registry; corrupt snapshot gives TryRestoreOwned Ok=false + cell empty (rollback).
- `KhaozEngine.Tests/NetWorld/CellPersistenceMigrationTests.cs`: v(N-1) migrates+restores; two-step chain composes; gap throws at ctor; corrupt blob quarantines (event fired, original bytes preserved, host keeps ticking); too-old skipped+event; current-version restores byte-identically (no regression); Migrated/RetainedUnknownExtensions events surfaced.

## Task order (TDD, commit per task)

- [ ] **T1** Replication: `RetainedComponent`, `SnapshotBlobReader`/`Writer` (+ structs) with tests. Commit.
- [ ] **T2** Replication: `ClientReplicationView.TryApplyRetainingUnknown` + sink; `SnapshotWriter` retained overload, with tests. Commit.
- [ ] **T3** Sharding: `CellSim.TryRestoreOwned` + retention + `SnapshotOwned` re-emit + `RestoreOwned` delegate + prune, with tests. Commit.
- [ ] **T4** NetWorld: `CellPersistenceIssue`/kind/delegate; `CellPersistenceConfig.RegisterMigration`; `ICellPersistenceHost.TryRestoreCell` default + `CellRestoreReport`; `ShardedWorldServer` override. Commit.
- [ ] **T5** NetWorld: `CellPersistence` load-path refactor (raw enqueue, `ProcessLoadedBlob`, migrate, quarantine, events, baseline rule) + chain validation in ctor, with the full migration test matrix. Commit.
- [ ] **T6** Docs sweep + version bump 9.33.0 + CHANGELOG + per-package READMEs + USING + pack + tag + merge + push.

## Self-review notes
- Spec deliverables 1-6 all mapped: migration hooks (T4/T5), non-throwing quarantine (T3/T5), retention (T2/T3), event (T4/T5), tests (T1-T5), docs (T6).
- Type-name consistency: `CellSnapshotMigration`, `SnapshotBlobReader/Writer`, `SnapshotBlobEntity/Component`, `RetainedComponent`, `CellRestoreResult` (Sharding), `CellRestoreReport` (NetWorld), `CellPersistenceIssue`/`CellPersistenceIssueKind`, `TryRestoreOwned`, `TryRestoreCell`, `TryApplyRetainingUnknown`.
