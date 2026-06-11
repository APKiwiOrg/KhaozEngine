# Promote SpaceGame settings system into KhaozEngine.Persistence

Batch 2, item 10. Promote SpaceGame's generic settings stack (`ISettingsStorage`,
`SettingsManager<T>`, the file-based JSON storage) into the existing
`KhaozEngine.Persistence` package, parameterizing everything SpaceGame-specific.

Coordinator-owned release: NO version bump, NO CHANGELOG, NO `dotnet pack`. Work lands on
branch `batch2-item10-settings` for the coordinator to fold into the batched 3.3.0 release.

## Re-verification summary

Source files (still present, re-read):

- `SpaceGame.Core/Settings/ISettingsStorage.cs` — generic, no SpaceGame deps.
- `SpaceGame.Core/Settings/SettingsManager.cs` — generic, but `internal` and logs via `Debug.WriteLine`.
- `SpaceGame.Core/Settings/BaseSettingsStorage.cs` — file-based JSON storage with an inline
  coalescing atomic writer.
- `SpaceGame.Core/Settings/{Desktop,Mobile,Console}SettingsStorage.cs` — vestigial.
- `SpaceGame.Core/Settings/SpaceGameSettings.cs` + `SpaceGameServer` — game DTOs (the `T`).

Findings that shaped the design:

1. **Not already in the engine.** `KhaozEngine.Persistence` holds only `SaveEncoder`. No settings
   manager anywhere. Safe to add.
2. **Atomic-writer overlap with item 8.** `BaseSettingsStorage.QueueSettingsWrite` /
   `FlushPendingSettingsWrites` is a near line-for-line duplicate of `SaveSystem.QueueSaveWrite` /
   `FlushPendingSaves`, which is item 8's "atomic JSON writer." We run before item 8, so we write
   through a shared seam (below) instead of duplicating the writer.
3. **`SpecialFolderPath` is dead code.** The three platform storages only set it; nothing reads it.
   The path comes entirely from `AppDataPaths`, which already resolves per-OS. The subclasses behave
   identically and are dropped.
4. **`SettingsFileName` is a real variable.** SpaceGame runs two managers off one storage type with
   different files: `settings.json` and `leaderboard.json`
   (`serverStorage.SettingsFileName = "leaderboard.json"`). The storage keeps a settable filename;
   the shared queue's per-path coalescing lets two files share one queue.

## Coordinator decisions (locked)

- **Atomic write seam:** define `IPersistenceQueue` now (exact text below); storage writes through it;
  ship a throwaway impl so this branch builds/tests; item 8 supplies the real queue at merge.
- **App dependency:** take `KhaozEngine.Persistence -> KhaozEngine.App`; storage resolves its path via
  `AppDataPaths` internally.
- **Platform storages:** drop `Desktop`/`Mobile`/`Console`; one `FileSettingsStorage`.
- **Error logging:** inject `ILogger` (KhaozEngine.Diagnostics), optional/nullable so headless tests
  stay quiet.

## The seam (verbatim, shared with item 8)

```csharp
namespace KhaozEngine.Persistence;

public interface IPersistenceQueue
{
    void Enqueue(string path, string json);   // per-path coalescing, last-writer-wins
    void Flush();                              // flush pending writes (e.g. on shutdown)
}
```

Item 8 adds the identical text in its own work; the two copies merge cleanly. `FileSettingsStorage`
only *writes* through this. Reads are direct (`File.ReadAllText` -> deserialize), no seam needed.

## Components

All in namespace `KhaozEngine.Persistence`.

### `IPersistenceQueue.cs`
The seam above, verbatim.

### `TempDirectPersistenceQueue.cs` (TEMP - drop at merge)
Throwaway synchronous `IPersistenceQueue` so this branch builds and tests in isolation. `Enqueue`
does a direct temp-file-then-`File.Move(overwrite: true)` write (creating the directory first),
swallowing IO errors to preserve legacy behavior. `Flush` is a no-op (writes are already synchronous).
File-level header comment marks it `TEMP - drop at merge`; the coordinator deletes this file and wires
item 8's real `PersistenceQueue` in.

### `ISettingsStorage.cs`
Promoted as-is:

```csharp
public interface ISettingsStorage
{
    string SettingsFileName { get; set; }
    void SaveSettings<T>(T settings) where T : new();
    T LoadSettings<T>() where T : new();
    bool SettingsExist();
}
```

### `FileSettingsStorage.cs`
Single concrete `ISettingsStorage`, replacing `BaseSettingsStorage` and the three dropped platform
subclasses. No coalescing logic of its own (that is the queue's job now).

```csharp
public sealed class FileSettingsStorage : ISettingsStorage
{
    public FileSettingsStorage(AppDataPaths appDataPaths, IPersistenceQueue writeQueue);
    public string SettingsFileName { get; set; }   // default "settings.json"
    // SaveSettings<T>: writeQueue.Enqueue(appDataPaths.GetFilePath(SettingsFileName), serialize(settings))
    // LoadSettings<T>: if !SettingsExist -> new T(); else read + deserialize ?? new T()
    // SettingsExist:   File.Exists(appDataPaths.GetFilePath(SettingsFileName))
}
```

- Ctor null-checks both args (`ArgumentNullException`).
- `JsonSerializerOptions { WriteIndented = true }` (matches SpaceGame's on-disk format).

### `SettingsManager<T>.cs`
Promoted, made `public sealed`. `Debug.WriteLine` -> `logger?.Error(...)`.

```csharp
public sealed class SettingsManager<T> where T : new()
{
    public SettingsManager(ISettingsStorage storage, ILogger? logger = null); // null storage -> ArgumentNullException; Load() in ctor
    public ISettingsStorage Storage { get; }
    public T Settings { get; }                  // never null
    public event Action<T> SettingsLoaded;
    public event Action<T> SettingsSaved;
    public void Save();                          // storage.SaveSettings + SettingsSaved; swallow + logger?.Error on failure
    public void Load();                          // storage.LoadSettings ?? new T(); swallow + logger?.Error -> defaults; always fires SettingsLoaded
}
```

## Data flow (save)

`SettingsManager.Save()` -> `FileSettingsStorage.SaveSettings(T)` -> serialize ->
`IPersistenceQueue.Enqueue(fullPath, json)` -> temp impl now / item 8's coalescing async writer later
-> atomic temp+move to disk.

## Project wiring

- `KhaozEngine.Persistence.csproj`: add `<ProjectReference Include="../KhaozEngine.App/KhaozEngine.App.csproj" />`
  (Diagnostics ref already present). **Shared file with item 8** — coordinator sequences the commit/rebase.
- `KhaozEngine.slnx` and `KhaozEngine.Tests.csproj` already reference Persistence and App; no change.

## Testing (KhaozEngine.Tests, headless xUnit)

`SettingsManagerTests` (in-memory fake `ISettingsStorage`, fake `ILogger`):
- load-on-construct exposes stored settings; `SettingsLoaded` fires.
- `Save()` calls `storage.SaveSettings` and fires `SettingsSaved`.
- storage throws on load -> defaults used, no throw, `SettingsLoaded` fires with default.
- storage throws on save -> swallowed, no throw, `logger.Error` recorded.
- null storage -> `ArgumentNullException`.

`FileSettingsStorageTests` (real `AppDataPaths` over a temp dir via the existing
`FakeAppDataEnvironment`; round-trip exercises `TempDirectPersistenceQueue`):
- save then load returns an equal object.
- `SettingsExist` false before save, true after.
- load with no file -> `new T()` defaults.
- custom `SettingsFileName` writes/reads the right file (e.g. `leaderboard.json`).
- `SaveSettings` calls `Enqueue` with the expected full path + JSON (fake `IPersistenceQueue`).
- ctor null args -> `ArgumentNullException`.

## Out of scope / untouched

Version, CHANGELOG, `docs/CONSUMERS.md`, `dotnet pack`/local-feed publishing, and SpaceGame-side
rewiring. A short "Settings" section is added to `KhaozEngine.Persistence/README.md` as a separate
section to avoid friction with item 8's README additions.

## Reported to coordinator at completion

- Shared `KhaozEngine.Persistence.csproj` App-ref edit (sequence with item 8).
- `IPersistenceQueue.cs` dual-add (identical text both sides).
- Public API shape of `FileSettingsStorage` / `SettingsManager<T>` in case a game adoption depends on it.
