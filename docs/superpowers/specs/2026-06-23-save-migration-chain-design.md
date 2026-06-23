# Versioned save-migration chain for KhaozEngine.Persistence

Date: 2026-06-23
Status: approved, ready for implementation
Target version: 7.32.0 (additive -> minor)

## Problem

`KhaozEngine.Persistence/SettingsManager<T>` offers a single `sanitizeOnLoad: Func<T,T>` hook
applied after every load. Consumers cram all schema migration into that one callback. Hardpoint's
`CampaignSave.Sanitize` hand-rolls a v1->v2 migration with an inline `data.SchemaVersion = 2` bump.
This does not scale: each new schema version piles more branching into one function, there is no
per-version stepper, and no shared pattern across games or across settings vs save files. The raw
save-file path (`GameStorage.Load<T>`) has no migration or sanitize hook at all.

## Goal

A registered, versioned migration chain in the persistence layer. The consumer registers ordered
steppers (v1->v2, v2->v3, ...) keyed by a schema-version field. On load the manager runs the chain
from the file's stored version up to the current version, in order, then the normal sanitize/clamp
pass. Generic over `T`; the existing single-hook path keeps working unchanged; usable for both save
files and settings files.

## Design decisions (settled)

- **Version-field access:** delegate-based core (`getVersion: Func<T,int>`, `setVersion: Action<T,int>`)
  so any POCO works, plus an optional `ISchemaVersioned { int SchemaVersion { get; set; } }` interface
  with a zero-config factory overload for types that opt in.
- **Reuse surface:** a standalone `MigrationChain<T>` unit, wired into BOTH `SettingsManager<T>`
  (new optional ctor arg, runs before `sanitizeOnLoad`) and `GameStorage` (`CreateSettingsManager`
  and the raw `Load<T>` path).
- **Strictness:** fail-fast at build (gaps / duplicates / out-of-range steps throw when the chain is
  built = programmer error caught at startup); lenient at runtime (user data never crashes).

## Public API (KhaozEngine.Persistence)

```csharp
public interface ISchemaVersioned
{
    int SchemaVersion { get; set; }
}

// Static entry points.
public static class MigrationChain
{
    // Any POCO: caller supplies how to read/write the version field.
    public static MigrationChainBuilder<T> For<T>(Func<T,int> getVersion, Action<T,int> setVersion)
        where T : new();

    // Opt-in convenience: wires get/set to the interface property.
    public static MigrationChainBuilder<T> For<T>()
        where T : ISchemaVersioned, new();
}

public sealed class MigrationChainBuilder<T> where T : new()
{
    // Register a v -> v+1 data transform. `migrate` does ONLY the data change; the chain stamps
    // the version field via setVersion after the step succeeds. Returning null keeps the input value.
    public MigrationChainBuilder<T> Step(int fromVersion, Func<T,T> migrate);

    // Validate + freeze. Throws ArgumentException on: a gap in [startVersion .. currentVersion-1],
    // a duplicate fromVersion, or any step whose fromVersion >= currentVersion.
    public MigrationChain<T> Build(int currentVersion);
}

public sealed class MigrationChain<T> where T : new()
{
    public int CurrentVersion { get; }

    // Run stored-version -> CurrentVersion. Never throws on user data; logs and degrades.
    public T Migrate(T value, ILogger? logger = null);
}
```

### `Build(currentVersion)` validation (fail-fast)

Let the registered `fromVersion` keys be `S`. `Build` throws `ArgumentException` when:

- any `fromVersion >= currentVersion` (a step that targets at/beyond current makes no sense), or
- a duplicate `fromVersion` was registered, or
- with `startVersion = min(S)`, the keys are not exactly the contiguous run
  `{ startVersion, startVersion+1, ..., currentVersion-1 }` (a gap such as a missing v2->v3).

An empty chain (no steps) is allowed and acts as a version-aware no-op; `startVersion` is treated as
`currentVersion`.

### `Migrate(value, logger)` behaviour (lenient runtime)

```
v = getVersion(value)
v >= CurrentVersion        -> no-op (already current, or a save from a newer build). return value.
v <  startVersion          -> log Warn ("save version {v} predates oldest migration step {startVersion}");
                              return value unchanged so sanitize/defaults can cope.
startVersion <= v < current -> loop: apply step[v]; on success setVersion(value, v+1), v++;
                                       repeat until v == CurrentVersion.
                              a step that throws -> log Error, halt the loop, return the partially
                                       migrated value (version stamped only for the steps that completed).
```

The whole `Migrate` call is also wrapped defensively so a delegate (getVersion/setVersion) that
throws is logged and the original value is returned, consistent with the existing
"corrupt save never crashes" philosophy.

**Fresh-default convention (documented, not enforced):** a save/settings type should default its
version field to the current version (e.g. `public int SchemaVersion { get; set; } = 3;`). Then a
brand-new `new T()` reports `>= CurrentVersion` and the chain no-ops silently, with no warning noise
on first run. Steps are keyed to the real historical versions that on-disk files actually report.

## Integration

### `SettingsManager<T>`

New optional ctor parameter (appended, back-compat):

```csharp
public SettingsManager(
    ISettingsStorage storage,
    ILogger? logger = null,
    Func<T,T>? sanitizeOnLoad = null,
    MigrationChain<T>? migrations = null)
```

`Load()` order becomes: **load value -> (if migrations != null) value = migrations.Migrate(value, logger)
-> (if sanitizeOnLoad != null) sanitize -> assign Settings -> raise SettingsLoaded.** Chain and hook are
independent. With `migrations == null` the behaviour is identical to today.

### `GameStorage`

Both gain an appended optional `MigrationChain<T>? migrations = null`:

```csharp
public SettingsManager<T> CreateSettingsManager<T>(
    Func<T,T>? sanitizeOnLoad = null,
    MigrationChain<T>? migrations = null) where T : new();

public T Load<T>(string fileName, MigrationChain<T>? migrations = null) where T : new();
```

`CreateSettingsManager` forwards the chain to the `SettingsManager` ctor. `Load<T>` applies
`migrations?.Migrate(...)` after deserialize (and after the absent-file `new T()` path, which the
fresh-default convention makes a no-op). This brings migration to raw save files, which have none today.
All additions are optional parameters -> source-compatible -> minor bump.

## Files

- New `KhaozEngine.Persistence/MigrationChain.cs` — `MigrationChain<T>`, `MigrationChainBuilder<T>`,
  static `MigrationChain` factory.
- New `KhaozEngine.Persistence/ISchemaVersioned.cs` — the opt-in interface.
- Edit `KhaozEngine.Persistence/SettingsManager.cs` — optional `migrations` arg, run before sanitize.
- Edit `KhaozEngine.Persistence/GameStorage.cs` — optional `migrations` arg on `CreateSettingsManager`
  and `Load<T>`.

## Tests (KhaozEngine.Tests, mirroring SettingsManagerTests.cs style)

New `MigrationChainTests.cs`:

- Build: gap throws; duplicate fromVersion throws; step at/above currentVersion throws; valid
  contiguous chain builds; empty chain builds.
- Migrate ordered stepping v1->v2->v3 applies steps in order and auto-stamps the version each step.
- Stored == current -> no-op; stored > current (newer file) -> no-op.
- Stored below lowest step -> value unchanged + Warn logged.
- A step that throws -> Error logged, loop halts, version stamped only for completed steps,
  partial value returned.
- `For<T>()` interface overload reads/writes `SchemaVersion`; `For<T>(get,set)` delegate overload
  works on a plain POCO.
- A getVersion/setVersion delegate that throws is swallowed + logged, original value returned.

Add to `SettingsManagerTests.cs` (or a focused companion):

- Chain runs before sanitizeOnLoad (observable order), both applied.
- Chain supplied, no sanitize hook.
- Chain runs on the initial ctor load.
- No chain -> identical to today (regression guard).

`GameStorage` load-with-chain test: write an old-version file through real `FileSettingsStorage`/paths,
`Load<T>(file, chain)` returns the migrated, current-version value.

## Release ritual (in its own worktree)

Additive -> minor: `7.31.0 -> 7.32.0`.

1. Bump `<KhaozEngine5xVersion>` in `Directory.Build.props` to `7.32.0`.
2. `CHANGELOG.md` newest-first detailed entry.
3. `CHANGENOTES.md` one-line digest.
4. Update the three guard declarations: `docs/CONSUMERS.md` "Engine current version",
   `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` example.
5. Document the feature in `docs/USING-KHAOZENGINE.md` (persistence section).
6. `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` green.
7. `dotnet pack -c Release -o ./local-feed`.
8. Commit, `git tag v7.32.0`, push main + tag.

## Out of scope

Hardpoint adoption — rewriting `CampaignSave.Sanitize` as a chain and its v3 Foothold reset — is a
separate consumer-side follow-up, not part of this engine change.
