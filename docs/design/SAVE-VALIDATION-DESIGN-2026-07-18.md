# Save Validation and Save Management Design (2026-07-18)

Status: in flight. Program issue: [#224](https://github.com/APKiwiOrg/KhaozEngine/issues/224).
Closes [#148](https://github.com/APKiwiOrg/KhaozEngine/issues/148),
[#151](https://github.com/APKiwiOrg/KhaozEngine/issues/151),
[#152](https://github.com/APKiwiOrg/KhaozEngine/issues/152),
[#155](https://github.com/APKiwiOrg/KhaozEngine/issues/155),
[#193](https://github.com/APKiwiOrg/KhaozEngine/issues/193),
[#127](https://github.com/APKiwiOrg/KhaozEngine/issues/127).

## Problem

The persistence stack (`KhaozEngine.Persistence`) has real machinery: `GameStorage`, the coalesced
atomic `PersistenceQueue`, `SettingsManager<T>` with `sanitizeOnLoad`, `MigrationChain<T>`, and a
`SaveEncoder` (Base64 + HMAC-SHA256). But the tamper-resistance story is cosmetic and the
corruption-resilience story has holes:

1. `SaveEncoder.Decode` is deliberately lenient. A save whose HMAC does not match still loads, with
   only a log warning. Tampering is detected and then ignored.
2. Encoding is opt-in per `Save(..., encode: true)` call. A forgotten flag silently ships an
   unprotected file, and a save directory ends up a plaintext/encoded mix.
3. `GameStorage.Load<T>` throws `JsonException` on a corrupt save (#148), on a documented code path.
4. `AtomicJsonWriter` never flushes to disk before its rename, so the "atomic" write can still land
   torn after a power cut (#151).
5. Nothing keeps a previous generation of any file. One corruption means the save is gone, either a
   crash or a silent reset to defaults depending on the caller (#152).
6. A brand-new value (no file on disk) enters `MigrationChain` at version 0 and is treated as
   pre-dating the oldest step, logging a spurious corruption-looking warning on every first boot and
   never getting stamped with `CurrentVersion` (#155).
7. Server side, `WorldPersistence` applies whatever `PlayerRecord` the store returns. Position and
   the opaque game blob are applied with no plausibility check, so a corrupt or hand-edited store row
   flows straight into the live world. `MmoServerSample` additionally keys records by connection slot
   and never loads them back (#127).

Goals: tampered or corrupt local saves are rejected and recovered from, not silently accepted, and
never brick a player. Server-side record application gets a validation seam for the games that want
one. The whole pass ships as one engine release the four games adopt by repinning.

Non-goals: real cryptographic protection against a determined attacker (the HMAC key ships in the
game binary, so this is deterrence and corruption detection, stated honestly in the API docs), and
any cloud-save upload service (its own future program).

## Decisions taken with the user (2026-07-18)

1. **Tamper posture: strict with recovery.** An HMAC mismatch rejects the candidate file. Recovery
   ladders through backup generations, then defaults. The game receives a load outcome it can act on
   (UI, disabling achievements). Rationale: lenient-with-a-log means the HMAC buys nothing, and
   strict-without-recovery would brick a player over one flipped bit.
2. **Server-side scope: defensive store validation.** The MMO stack is already server-authoritative
   (the client never submits a save), so the gap is not trust of the client but trust of the store
   row and of writer bugs. A validation + quarantine seam on record apply covers it. The cloud-save
   upload service option was explicitly deferred.
3. **Encoding defaults on once an encoder is configured.** Configuring `GameStorageOptions.Encoder`
   is the signal of intent. Per-call opt-out remains for deliberately hand-editable files. Legacy
   plaintext still loads (shipped games must upgrade transparently) and re-encodes on next save.
4. **Two extensibility seams folded in** (from the A-vs-B comparison below): the envelope format is
   itself versioned so a metadata segment exists from day one, and backup generations are enumerable
   API rather than internal detail.

## Alternatives weighed

- **A. Extend the existing stack in place** (chosen). Strict verify in `SaveEncoder`, rotation and
  fsync in the write path, an outcome-reporting load pipeline in `GameStorage`/`FileSettingsStorage`,
  validation + quarantine hooks in `WorldPersistence`. Weighted 249 vs 179 for B on consumer-fit,
  cohesion, and risk. Every consumer already goes through this stack, so adoption is a repin.
- **B. A new `SaveVault` slot abstraction** layered over `GameStorage`. Scored higher only on raw
  extensibility (slots and metadata as entities, multi-file atomic generations, backend abstraction).
  Declined: no current game has slots, and the two genuinely valuable seams (metadata, enumerable
  generations) fold into A cheaply. A vault can layer on top of A's pipeline later if a game grows
  the need. Multi-file transactional saves and the slot entity are the only capabilities genuinely
  given up today, both speculative.
- **C. Minimal strictness patch** (strict decode plus bug fixes, no recovery). Eliminated by
  decision 1: strict rejection without backup rotation bricks saves.

## Design

### 1. Versioned save envelope (v2)

Current format: `{prefix}:{hmac-hex}:{base64-payload}`. New writes use:

```
{prefix}:v2:{hmac-hex}:{meta-base64}:{payload-base64}
```

The HMAC covers `{meta}:{payload}`, so metadata is tamper-protected too. Discrimination is
unambiguous: the segment after the prefix is a literal version tag (`v2`, future `v3`) versus 64 hex
chars in the legacy format. v1 files decode forever.

`SaveMetadata` carries `SavedAtUtc`, `GameVersion` (game-supplied via `GameStorageOptions`), and an
opaque game-supplied `Summary` string (character name, level, playtime, whatever a save browser
wants). `SaveEncoder` gains:

- `TryDecode(content)` returning a `SaveDecodeResult`: verdict (`Ok` / `TamperMismatch` /
  `Malformed` / `NotEncoded`), the JSON, and the metadata.
- `TryReadMetadata(content)` reading the meta segment without base64-decoding or parsing the
  payload, and still verifying the HMAC (it hashes the already-read string segments), so it returns
  the metadata plus the same verdict. A save browser can therefore mark a tampered save without
  paying for payload deserialization. This is what makes a cheap "Continue" menu possible.

The lenient `Decode` stays public for compatibility but the engine pipeline stops calling it.

### 2. Durable, rotated write path

`AtomicJsonWriter` writes the temp file through a `FileStream` and flushes to disk
(`Flush(flushToDisk: true)`) before the atomic rename, closing #151's real hole. Directory-entry
fsync is not reachable from .NET and is accepted as a residual risk, noted in the doc comment.

`PersistenceQueue`'s write step gains generation rotation: before the rename, the current target
rotates to `.bak1`, `.bak1` to `.bak2`, the oldest drops. `GameStorageOptions.BackupGenerations`
defaults to 2, zero disables. Settings ride the same queue, so settings files get rotation for free.
Rotation is unconditional per committed write. Skipping rotation when content is unchanged was
considered and dropped: it costs a read of the target on every write to save nothing that matters,
and upstream dirty-tracking (`SettingsManager`, coalescing) already suppresses most no-op writes.

### 3. Load pipeline: outcomes and the recovery ladder

New result type in `KhaozEngine.Persistence`:

- `SaveLoadResult<T>`: `Value`, `Outcome`, `Detail` (human-readable reason), `RecoveredGeneration`.
- `SaveLoadOutcome`: `Loaded`, `FreshDefault`, `LoadedLegacyPlaintext`, `RecoveredFromBackup`,
  `RejectedAndDefaulted`.

`GameStorage` gains `LoadWithOutcome<T>(fileName, migrations)` implementing the ladder: candidates
are the primary file then backups newest-first. Per candidate: read, strict-decode when an encoder is
configured and the content is enveloped (tamper mismatch rejects the candidate under `Strict`),
parse JSON (corrupt rejects the candidate), first valid candidate wins. All candidates invalid means
defaults plus `RejectedAndDefaulted`. The existing `Load<T>` becomes a thin wrapper discarding the
outcome, which also fixes #148 (corrupt JSON no longer throws anywhere in the pipeline).

Policy knobs on `GameStorageOptions`, each with a real justification:

- `TamperPolicy` (`Strict` default, `Lenient`): `Lenient` is the dev escape hatch for hand-editing
  saves during balancing. It restores today's accept-and-log behavior: a tampered primary loads with
  outcome `Loaded` and a `Detail` naming the HMAC mismatch, so the acceptance is still visible to
  the caller rather than only to the log.
- `AcceptLegacyPlaintext` (default true): plaintext acceptance is inherently a tamper bypass (strip
  the envelope and the HMAC never runs). Shipped games need it true to upgrade their install base
  transparently. A game can later set it false to close the door.
- `BackupGenerations` (default 2), `GameVersion` (stamped into envelope metadata).

Encode is default-on when `Encoder` is configured: `Save<T>(fileName, value)` encodes, a per-call
opt-out (`SaveWriteOptions.Encode = false`, which also carries the per-save `Summary`) keeps
deliberately hand-editable files plaintext. The old `Save(fileName, value, bool encode)` overload
stays and maps onto the new semantics. A legacy plaintext file loads as `LoadedLegacyPlaintext` and
the next save writes it enveloped, so the upgrade is one load-save cycle.

`FileSettingsStorage` rides the same ladder internally (via a default-interface
`LoadSettingsDetailed<T>` on `ISettingsStorage` that falls back to `LoadSettings<T>` for other
implementations), so `SettingsManager` recovers from backups instead of silently defaulting, and
exposes `LastLoadOutcome`.

Fresh values (no file on disk) are stamped with `MigrationChain.CurrentVersion` via a new
`StampCurrent(value)` instead of being run through the chain from version 0, closing #155. The
absent-file case is known at the `GameStorage`/`FileSettingsStorage` layer, which is where the stamp
decision is made.

### 4. Generation API

`GameStorage.ListGenerations(fileName)` probes primary plus backups and returns per generation:
index (0 = primary), path, last-write time UTC, and validity (`Valid` / `Tampered` / `Corrupt` /
`Missing`), reusing the ladder's candidate check. `RestoreGeneration(fileName, n)` promotes a backup
to primary, rotating the current primary out rather than destroying it. This is the exact surface a
player-facing "restore backup" UI needs later. Nothing else about backups is public.

### 5. Server-side defensive validation (`KhaozEngine.NetWorld`)

`WorldPersistenceConfig` gains two optional hooks, both evaluated on the server thread in
`DrainApplyQueue`, which is already the record-apply contract point:

- `Bounds` (`WorldBounds`, existing type): a loaded position outside bounds fails the record.
- `ValidateGameState(PlayerPersistenceContext ctx, byte[] blob)` returning a verdict with a reason:
  the game's plausibility check (schema parse, stat clamps, inventory sanity) before
  `ApplyGameState` runs.

A failing record is quarantined whole rather than partially applied: its original bytes are copied
to `quarantine:player:{accountId}` in the same `IWorldStore` (forensics preserved, latest wins), the
player spawns fresh as if no record existed, and `OnRecordQuarantined(accountId, reason)` fires next
to the existing `OnStoreError` eventing, raised on the server thread from `Update` like every other
`WorldPersistence` callback. The `lastSaved` baseline is deliberately not set for a quarantined
record, so the fresh state is dirty against the store and the next dirty pass overwrites the bad
primary while the quarantine copy survives. The `loadsInFlight` guard clears on quarantine exactly
as it does on a successful apply.

An **undecodable** record (JSON parse failure in `PlayerRecord.Decode`) takes the same quarantine
path instead of faulting the load task. Today that fault leaves the `loadsInFlight` guard set
forever by design (protecting the stored record for a retry), but a genuinely corrupt record never
decodes on any retry, so the guarded player also never dirty-saves again for the rest of the
session: progress silently stops persisting. Quarantining clears the guard, the player starts
fresh, and persistence resumes. The guard-forever behavior remains correct for the case it was
built for, a faulted store read (outage), where retrying can succeed.

`CellPersistence` needs nothing new: a throwing cell migration already quarantines the blob, which
is the same pattern, and cell blobs are server-written engine format with no game-facing shape to
validate. A game-supplied cell validator was considered and declined for lack of any consumer need.

`MmoServerSample` is corrected to key persistence by the verified `ServerSessionEvent.Subject`
instead of the ephemeral connection slot, to wire `WorldPersistence` load-on-join properly, and to
demonstrate `CaptureGameState` / `ValidateGameState` / `ApplyGameState`, closing #127.

### 6. Error handling posture

Local loads never throw: every corruption, tamper, and legacy path resolves through the ladder to a
reported outcome. Writes keep the existing retry and `PersistenceWriteFailed` eventing. Server-side
validation failures never crash and never silently apply: they quarantine and report.

### 7. Testing

All headless, in the existing per-area homes.

`KhaozEngine.Foundation.Tests` (existing home of `GameStorageTests`, `AtomicJsonWriterTests`,
`FileSettingsStorageTests`): v2 round-trip including metadata, v1 back-compat decode, flip-a-byte
tamper rejection recovering from `.bak1`, corrupt-JSON recovery, plaintext acceptance plus
re-encode-on-next-save, `AcceptLegacyPlaintext = false` rejection, rotation contents across a write
sequence, fresh-value `CurrentVersion` stamping (no spurious migration warning), `ListGenerations`
validity matrix, `RestoreGeneration` promotion, `Lenient` policy behavior.

`KhaozEngine.Server.Tests` (existing NetWorld home): invalid blob quarantined into
`InMemoryWorldStore` under the quarantine key, fresh spawn applied, `OnRecordQuarantined` raised,
next dirty pass overwrites the primary but not the quarantine copy, out-of-bounds position
quarantined, undecodable record quarantined with the guard cleared (a subsequent dirty pass saves
the fresh player), store-outage fault still leaves the guard set, valid record untouched by the
hooks.

### 8. Release and docs

One minor `<KhaozEngineVersion>` bump for the whole batch (additive API plus resilience behavior).
Full doc sweep: `KhaozEngine.Persistence/README.md` (new API, and the missing `MigrationChain`
section, #193), `KhaozEngine.NetWorld/README.md` (validation + quarantine hooks),
`docs/USING-KHAOZENGINE.md` (save/settings and world-persistence sections), `CHANGELOG.md`.
Consumer adoption is per-game repin: games opt into an encoder (or already have one) and otherwise
see only improved resilience.

## Declined, with reasons

- **HMAC on `IWorldStore` rows.** The server database is trusted infrastructure. An operator who can
  edit rows can also read the key from the server binary. Cost without a threat it answers.
- **Machine-bound save keys** (DPAPI-style per-user entropy). Breaks legitimate save copying, manual
  backups, and cloud-synced save folders. Wrong trade for games.
- **Game-supplied cell-blob validators.** No consumer need, and the throw-to-quarantine seam in
  `CellPersistence` migrations already covers corrupt bodies.
- **Cloud-save upload service** (server verifies and stores client saves for non-MMO games). A real
  future program with its own auth, storage, and abuse surface. Deferred, not designed here.
- **`SaveVault` slot abstraction and multi-file atomic generations.** See alternative B. Layers on
  top of this pipeline later if a game grows the need.
