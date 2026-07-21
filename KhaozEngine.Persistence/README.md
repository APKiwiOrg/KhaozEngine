# KhaozEngine.Persistence

Game-agnostic save/persistence helpers.

`SaveEncoder` wraps save JSON in a Base64 + HMAC-SHA256 envelope to deter casual tampering. It is a
deterrent, not real security: the HMAC key ships in the game binary. The v2 format
(`{prefix}:v2:{hmac}:{meta-base64}:{payload-base64}`) HMACs tamper-protected `SaveMetadata` alongside the
payload. Legacy v1 (`{prefix}:{hmac}:{base64}`) is still read. `TryDecode` returns a structured
`SaveDecodeResult` (Ok / TamperMismatch / Malformed / NotEncoded) and leaves the strict-versus-lenient
choice to the caller. `TryReadMetadata` verifies the HMAC and returns the metadata without decoding the
payload. The legacy `Decode` stays lenient (recovers the JSON even on an HMAC mismatch) and reports
outcomes through the engine logger.

**On a `TamperMismatch` result, the recovered `Metadata` is best-effort, not vouched for.** Both
`TryDecode` and `TryReadMetadata` still populate `Metadata` when the envelope decodes but its HMAC does
not verify, so a caller can inspect what a tampered save claims. That claim is exactly what an attacker
would control, so never display it as trusted (a save-browser summary, a level label, a playtime figure):
gate it behind the verdict, and either withhold it or clearly mark it unverified when the verdict is not
`Ok`.

```csharp
using System.Text;
using KhaozEngine.Diagnostics;   // ILogger / Log
using KhaozEngine.Persistence;

var encoder = new SaveEncoder(
    Encoding.UTF8.GetBytes("MyGame-SaveIntegrity-v1"),
    "MGSV1",
    Log.For<SaveEncoder>());

string onDisk = encoder.Encode(json);
string? loaded = encoder.Decode(onDisk);   // null only if not-our-format / malformed / corrupt
```

## GameStorage

`GameStorage` is a one-call facade over the whole stack: publisher-rooted `KhaozEngine.App.AppDataPaths`,
a shared `PersistenceQueue`, a `FileSettingsStorage`, and an optional `SaveEncoder`. It owns the write
queue and flushes/disposes it on `Dispose`.

```csharp
using KhaozEngine.Persistence;

var storage = new GameStorage("MyStudio", "MyGame", new GameStorageOptions
{
    Encoder = encoder,                       // configures encoding as the default for Save
    TamperPolicy = TamperPolicy.Strict,      // default: reject a save whose HMAC does not verify
    AcceptLegacyPlaintext = true,            // default: still load a plaintext save under a configured encoder
    BackupGenerations = 2,                   // default: keep .bak1 and .bak2 alongside the primary
    GameVersion = "1.4.2",                   // stamped into every encoded save's SaveMetadata.GameVersion
});

storage.Save("save.json", myGameState);      // encoded when Encoder is configured, plaintext otherwise
storage.Flush();                             // writes are queued: flush before Load reads the same file back
MyGameState loaded = storage.Load<MyGameState>("save.json");
```

`GameStorageOptions.TamperPolicy`, like the encoder itself, is about detecting and recovering from
tampering and corruption, not stopping a determined attacker: the HMAC key ships in the game binary
either way (see above).

### Encoding: default-on, per-call opt-out

Configuring `GameStorageOptions.Encoder` makes encoding the DEFAULT for every `Save` call, the reverse of
the old opt-in-per-call behavior, so a forgotten flag can no longer ship an unprotected file. Opt out (or
force it) per call with `SaveWriteOptions`, for example for a file meant to stay deliberately
hand-editable:

```csharp
storage.Save("debug-save.json", value, new SaveWriteOptions { Encode = false });
storage.Save("save.json", value, new SaveWriteOptions { Encode = true, Summary = "Chapter 3, Level 42" });
```

`SaveWriteOptions.Summary` is stamped into the envelope's `SaveMetadata.Summary` for that write (ignored
on a plaintext write). `GameStorageOptions.GameVersion` and the write time (`SaveMetadata.SavedAtUtc`) are
stamped automatically on every encoded write. `Save(fileName, value, bool encode)` still works and maps
onto the same `SaveWriteOptions` semantics as a two-argument shortcut. `Save` throws
`InvalidOperationException` when encoding is requested (implicitly or explicitly) but no `Encoder` was
configured.

### Loading: the recovery ladder and `LoadWithOutcome`

`Load<T>(fileName, migrations?)` never throws on a bad save. It probes the primary file, then each backup
generation in order, transparently decoding when an encoder is configured, and returns the first valid
candidate, or a fresh `new T()` if none are. `LoadWithOutcome<T>` runs the same ladder but reports HOW it
resolved via `SaveLoadResult<T>` (`Value`, `Outcome`, `Detail`, `RecoveredGeneration`, `Metadata`) and its
`SaveLoadOutcome`:

- **`Loaded`** - the primary file was valid.
- **`FreshDefault`** - nothing existed on disk. A fresh default was returned and stamped current via
  `migrations` rather than migrated (see `MigrationChain<T>.StampCurrent` below).
- **`LoadedLegacyPlaintext`** - the primary was a valid plaintext save read under a configured encoder (a
  pre-upgrade or hand-edited save). A subsequent default-on `Save` re-encodes it, but a file the game keeps
  writing with `Encode = false` (`SaveWriteOptions`) stays plaintext deliberately and is never re-encoded
  this way.
- **`RecoveredFromBackup`** - the primary was invalid but a backup generation loaded, and
  `RecoveredGeneration` names which one.
- **`RejectedAndDefaulted`** - at least one candidate existed but every generation was invalid, and a
  fresh default was returned (also stamped current, not migrated).

A candidate whose HMAC does not verify (`SaveDecodeVerdict.TamperMismatch`) is rejected under
`TamperPolicy.Strict` (the default) and accepted under `TamperPolicy.Lenient` - the dev escape hatch for
hand-editing saves during balancing - in which case the resulting `Loaded` result still carries a `Detail`
naming the mismatch rather than accepting it silently. `RecoveredFromBackup` and `RejectedAndDefaulted`,
plus a lenient tamper-accept, are logged (`Warn`/`Error`) through the facade's logger. `Loaded` (with no
detail), `FreshDefault`, and `LoadedLegacyPlaintext` stay quiet, since those are the expected path, not a
recovery. `AcceptLegacyPlaintext` (default true) lets a shipped game's existing install base upgrade
transparently from an unenveloped save. Set it false once you no longer need to accept one, but note it
rejects ALL plaintext, including a deliberate `Encode = false` file, so a game hardening its real saves
this way should keep hand-editable files out of that storage, or leave the flag true.

### Backup generations

`GameStorageOptions.BackupGenerations` (default 2) is how many numbered backups the internal write queue
keeps per target path: generation 0 is the primary path itself, generation *n* (n >= 1) is
`path + ".bak" + n` (`SaveBackups.GenerationPath`). Rotation runs once per committed write, before the
write attempt: the current primary is COPIED (not moved) into generation 1, older generations shift up one
slot via a move, and the oldest drops. Copying rather than moving the primary leaves it in place
throughout, so a write that then fails every retry attempt still finds an intact primary on disk - only a
successful write ever replaces it.

- **`ListGenerations(fileName)`** flushes pending writes, then probes the primary and every backup and
  returns exactly `BackupGenerations + 1` `SaveGenerationInfo` entries (generation 0 first): path,
  last-write time UTC (null when missing), `SaveGenerationValidity` (`Valid` / `Tampered` / `Corrupt` /
  `Missing`), and the decoded envelope metadata (null when missing, invalid, or unencoded). This is the
  surface a player-facing "restore backup" UI reads.
- **`RestoreGeneration(fileName, generation)`** flushes pending writes, then promotes a backup to primary:
  the current primary is rotated into generation 1 first (nothing on disk is destroyed), then the
  requested backup's content is written to the primary path. Returns false, leaving everything untouched,
  when the requested generation's file does not exist. Throws `ArgumentOutOfRangeException` outside
  `1..BackupGenerations`.

## AtomicJsonWriter

Static crash-safe writer: content goes to a sibling `.tmp` file which is then moved over the target,
so a crash mid-write never leaves a half-written destination. Synchronous and throws on IO failure
(the caller decides whether to catch). The temp file's content is flushed to the physical disk before
the rename. The directory entry itself is not fsynced (not reachable from .NET), an accepted residual
risk.

```csharp
AtomicJsonWriter.WriteText(path, json);
AtomicJsonWriter.Write(path, myValue);                  // serialize (indented) then write
AtomicJsonWriter.Write(appDataPaths, "save.json", myValue);
```

## PersistenceQueue

Coalesced asynchronous writer (`IPersistenceQueue`). Each `Enqueue` records the latest payload per
target path (rapid repeats to one path collapse to the last) and a background worker drains them via
`AtomicJsonWriter`. Writes never throw into the caller: they retry briefly, then log through the
optional `ILogger` and raise `WriteFailed`. `Flush()` blocks until drained and the queue is
`IDisposable` (disposing flushes), so a game can guarantee a clean write on shutdown.

`WriteFailed` is delivered on a background worker thread after the drain latch is released, one handler
at a time and in failure order, never concurrently. A handler may therefore safely call `Flush()` or
`Dispose()` on the queue without deadlocking. One consequence: `Flush()` returns once the writes are
drained and does not block until the `WriteFailed` handlers have run (the failure is still logged
synchronously before `Flush()` returns).

The optional `backupGenerations` constructor argument (default 0, off) keeps that many numbered backups
per target path, rotated once per committed payload before the write attempt - see "Backup generations"
above for the copy-not-move rotation semantics. `GameStorage` wires this from
`GameStorageOptions.BackupGenerations`. A bare `PersistenceQueue` used directly defaults to no rotation.

```csharp
using var queue = new PersistenceQueue(Log.For<PersistenceQueue>());   // logger, maxAttempts, retryDelay all optional
queue.WriteFailed += (_, e) => Notify(e.Path, e.Exception);

queue.Enqueue(appDataPaths, "save.json", saveData);    // or Enqueue(path, json) / Enqueue<T>(path, value)
// on shutdown:
queue.Flush();
```

## BatchedWriter\<T\>

Bounded async batch-write queue for a server-side append-only log (chat, an economy ledger, admin
actions, and similar record streams), not the file-per-path saves above. `Enqueue` only pushes onto an
in-memory queue and never does IO, so it is safe on a hot path like a sim tick. `Update(dt)`, called by
the host every tick, drains up to `maxBatch` records on a `flushIntervalSeconds` cadence and fires one
batched write off-thread through the injected sink - there is no internal timer, so nothing flushes
unless `Update` is called. Overflow drops the OLDEST queued record(s) (counted via `DroppedCount`), and
a whole-batch write failure is salvaged by retrying every record individually, so one poisoned row costs
one row instead of the whole batch. `FlushAsync()` ignores the interval, drains everything queued, and
awaits every in-flight write - call it once on shutdown. A `null` sink makes every member a no-op.

```csharp
using KhaozEngine.Diagnostics;
using KhaozEngine.Persistence;

var writer = new BatchedWriter<ChatMessage>(
    sink: (batch, ct) => store.AppendAsync(batch, ct),   // null disables the writer entirely
    label: "chatlog",
    logger: Log.For<BatchedWriter<ChatMessage>>());      // optional, maxQueue/maxBatch/flushIntervalSeconds too

writer.Enqueue(message);     // hot path, non-blocking
writer.Update(dt);           // once per tick, host-driven
await writer.FlushAsync();   // shutdown drain
```

## Settings

`SettingsManager<T>` holds a strongly-typed settings object and persists it through an
`ISettingsStorage`. `FileSettingsStorage` serializes to indented JSON under a
`KhaozEngine.App.AppDataPaths` directory and writes through an `IPersistenceQueue` (atomic,
per-path coalescing). Load/save failures are swallowed and reported via an optional
`KhaozEngine.Diagnostics.ILogger`.

**Settings ride the same recovery ladder as saves.** `FileSettingsStorage.LoadSettingsDetailed<T>()`
(what the plain `LoadSettings<T>` calls internally) probes the primary settings file, then each backup
generation in turn, for the first one that reads and deserializes cleanly, reporting how it resolved via
`SaveLoadResult<T>`/`SaveLoadOutcome` (`Loaded`, `RecoveredFromBackup`, `RejectedAndDefaulted`,
`FreshDefault` - there is no encoding at the settings layer, so `LoadedLegacyPlaintext` never applies
here). `FileSettingsStorage.BackupGenerations` (default 2) is a SEPARATE knob from
`GameStorageOptions.BackupGenerations` and is not auto-synced with it: set it to match the write queue's
own backup count (when going through `GameStorage`) so a corrupt primary can actually recover.
`SettingsManager<T>.LastLoadOutcome` records how the most recent `Load()` resolved, so a caller can
surface "recovered from backup" or "settings reset" instead of silently swallowing it.

```csharp
using KhaozEngine.App;
using KhaozEngine.Persistence;

var paths = new AppDataPaths("MyGame");
var storage = new FileSettingsStorage(paths, persistenceQueue);   // queue supplied by the host
var settings = new SettingsManager<MySettings>(storage, Log.For<SettingsManager<MySettings>>());

settings.Settings.MasterVolume = 0.8f;
settings.Save();
```

### Schema migration & downgrade safety

`SettingsManager<T>` also takes an optional `migrations: MigrationChain<T>?` constructor argument, run
on every load BEFORE `sanitizeOnLoad`: it steps the loaded value from its stored schema version up to the
chain's current version in ordered, registered steps instead of a version check inside `sanitizeOnLoad`.
See "MigrationChain<T>" below for the full API. The pattern below (a hand-rolled version check inside
`sanitizeOnLoad`) still works, and the two compose (`migrations` runs first).

`SettingsManager<T>` takes an optional `sanitizeOnLoad` hook that runs on **every** load, including
the initial load in the constructor (the `SettingsLoaded` event can't help there; it fires inside
the ctor before a caller can subscribe). Use it to clamp fields and migrate an embedded schema version:

```csharp
var mgr = new SettingsManager<SaveData>(
    storage,
    Log.For<SettingsManager<SaveData>>(),
    sanitizeOnLoad: Migrate);

static SaveData Migrate(SaveData s)
{
    if (s.Version < 2) { /* fill new fields, rename, etc. */ s.Version = 2; }
    s.MasterVolume = Math.Clamp(s.MasterVolume, 0f, 1f);
    return s;
}
```

For forward-compatibility (a newer build wrote fields this older build doesn't know), give the DTO a
`[JsonExtensionData]` bag. `FileSettingsStorage` round-trips the live object through
`System.Text.Json`, so unknown fields survive a load + save and are not dropped on downgrade. No
engine code required, just the DTO shape:

```csharp
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class SaveData
{
    public int Version { get; set; } = 2;
    public float MasterVolume { get; set; } = 1f;

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
```

## MigrationChain<T>

`MigrationChain<T>` steps a value from whatever schema version it was saved at up to the current version,
one version at a time, so a schema change is a small ordered set of pure data transforms instead of a
single sprawling `sanitizeOnLoad` branch. It plugs into `GameStorage.Load`/`LoadWithOutcome` and into
`SettingsManager<T>` (constructor or `GameStorage.CreateSettingsManager`) as an optional `migrations`
argument.

```csharp
using KhaozEngine.Persistence;

public sealed class CampaignSaveData : ISchemaVersioned
{
    public int SchemaVersion { get; set; } = 3;   // default = current, so a fresh save no-ops
    // ... fields ...
}

MigrationChain<CampaignSaveData> migrations = MigrationChain.For<CampaignSaveData>()   // ISchemaVersioned zero-config factory
    .Step(1, d => { /* v1 -> v2 data change */ return d; })
    .Step(2, d => { /* v2 -> v3 data change */ return d; })
    .Build(currentVersion: 3);

CampaignSaveData loaded = storage.Load("campaign.json", migrations);
```

- **`MigrationChain.For<T>()`** reads/writes the version through `ISchemaVersioned.SchemaVersion`
  (reference types only). **`MigrationChain.For<T>(getVersion, setVersion)`** takes explicit accessor
  delegates for a POCO that does not implement the interface, or for a value type.
- **`Step(fromVersion, migrate)`** registers the transform that takes a value from `fromVersion` to
  `fromVersion + 1`. The transform does ONLY the data change (mutate in place or return a replacement).
  The chain stamps the version field afterwards. Throws `ArgumentException` if a step from that version is
  already registered.
- **`Build(currentVersion)`** validates and freezes the chain: the registered steps must form the
  contiguous run `{ start .. currentVersion - 1 }` with no gaps, and no step may target at or beyond
  `currentVersion`. An empty chain (no steps registered) is allowed and acts as a no-op. Both violations
  throw `ArgumentException` at build time (startup), not at load time.
- **`Migrate(value, logger?)`** runs the chain from the value's stored version up to `CurrentVersion`.
  Never throws on the value it is handed, consistent with the rest of the persistence stack's "a bad save
  never crashes the game" stance: a value already at or above current is returned untouched (including a
  save from a newer build). A value older than the oldest registered step is logged (`Warn`) and returned
  unchanged. A step that throws is logged (`Error`) and halts the chain, returning the partially-migrated
  value (its version reflects only the completed steps). A null value is returned as-is.
- **`StampCurrent(value)`** marks a brand-new value (one that never came from disk) as already at
  `CurrentVersion`, without running any step. `GameStorage.LoadWithOutcome` and `SettingsManager.Load`
  both call it for a fresh default (`SaveLoadOutcome.FreshDefault` or `RejectedAndDefaulted`) instead of
  running `Migrate`, so a first boot is never mistaken for a pre-migration save. Before this, a brand-new
  value entered the chain at schema version 0, which read as older than every registered step: first boot
  logged a spurious corruption-looking "schema version predates the oldest migration step" warning on
  every launch, and the fresh value was never actually stamped to `CurrentVersion`. `StampCurrent` closes
  that gap. Null is returned unchanged, same as `Migrate`.

`SettingsManager<T>` runs the same chain through its constructor's `migrations` parameter, before the
optional `sanitizeOnLoad` hook, and surfaces which recovery path the most recent `Load()` took via
`LastLoadOutcome` - see "Schema migration & downgrade safety" above.
