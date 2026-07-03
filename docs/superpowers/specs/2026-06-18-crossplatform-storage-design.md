# Cross-platform storage: publisher root + mobile + GameStorage facade

Date: 2026-06-18
Target release: **5.59.0** (5.x line, breaking-as-minor)

## Problem

`AppDataPaths` today resolves a desktop-only single-folder layout `<os-base>/<appFolderName>/`
(Windows `%APPDATA%`, macOS `Application Support`, Linux XDG). Two gaps:

1. No publisher root, so two games from the same publisher scatter at the OS root instead of
   nesting under one `APKiwi/` folder.
2. No Android/iOS branches, so there is no mobile app-sandbox story.

Separately, every game hand-assembles the same wiring: build `AppDataPaths`, build a
`PersistenceQueue`, build a `FileSettingsStorage` over it, optionally build a `SaveEncoder`, and
glue typed save/load by hand. That boilerplate should live in the engine once.

## Decisions (locked in brainstorming)

- **Publisher root is the only layout.** The old single-folder layout is removed, not kept
  alongside. Breaking change to `KhaozEngine.App`.
- **No data migration.** Old on-disk data is orphaned (these are dev/test saves). The old
  single-folder dirs on the dev box are removed manually as a post-ship cleanup step.
- **Full-featured facade.** The engine generically handles save/load + settings for every game,
  including encoded saves. Games keep their own settings/save *types*; the engine reads/writes and
  passes those files generically. Designed to be extended later.
- **Version: minor `5.59.0`**, noted as breaking-as-minor in CHANGELOG/CONSUMERS, consistent with
  prior 5.x practice (the 5.x line absorbs breaking changes as minors rather than majors).

## Part A - `AppDataPaths` becomes publisher-rooted (`KhaozEngine.App`)

Canonical layout: `<os-base>/<publisher>/<appName>/`. Single layout, no legacy fallback shape.

### Public API change (breaking)

- New constructor: `AppDataPaths(string publisher, string appName)`. Both must be non-null,
  non-empty, non-whitespace, else `ArgumentException` (naming the offending argument).
- The old `AppDataPaths(string appFolderName)` single-arg constructor is **removed**.
- Internal test constructor: `AppDataPaths(string publisher, string appName, IAppDataEnvironment environment)`.
- File-path helpers (`BaseDirectory`, `SaveFilePath`, `SettingsFilePath`, `LogFilePath`,
  `PreviousLogFilePath`, `GetFilePath`) are unchanged in shape; they compose off the new base.

### Resolution

`ResolveBaseDirectory()` resolves an OS base, then `Path.Combine(base, publisher, appName)`.
Mobile branches are checked **before** desktop branches so a platform that also reports a desktop
flag cannot shadow them.

| Platform | Base resolved via | Example result |
|----------|-------------------|----------------|
| Android  | `SpecialFolder.LocalApplicationData` (app sandbox files dir) | `<sandbox>/APKiwi/Hardpoint/` |
| iOS      | `SpecialFolder.LocalApplicationData` (Library/Application Support in sandbox) | `<sandbox>/APKiwi/Hardpoint/` |
| Windows  | `SpecialFolder.ApplicationData` | `%APPDATA%\APKiwi\Hardpoint\` |
| macOS    | `SpecialFolder.ApplicationData` | `~/Library/Application Support/APKiwi/Hardpoint/` |
| Linux    | `$XDG_DATA_HOME` else `~/.local/share` | `$XDG_DATA_HOME/APKiwi/Hardpoint/` |

Fallback chain (unchanged in spirit, with publisher/appName appended):
1. If the matched OS branch yields a blank path, fall through.
2. `SpecialFolder.LocalApplicationData` + publisher/appName.
3. Last resort: `SpecialFolder.UserProfile` + `.<publisher>` lowercased + appName, i.e.
   `Path.Combine(home, "." + publisher.ToLowerInvariant(), appName)` - keeps the dotfile-style
   hidden root but still nests the game under the publisher.

`BaseDirectory` stays `Lazy<string>` (resolve + `Directory.CreateDirectory` exactly once, thread-safe).

### `IAppDataEnvironment` / `SystemAppDataEnvironment`

- Add `bool IsAndroid { get; }` and `bool IsIOS { get; }`.
- `SystemAppDataEnvironment` maps them to `OperatingSystem.IsAndroid()` / `OperatingSystem.IsIOS()`.
- BCL-only; no MonoGame, no mobile SDK references.

## Part B - `GameStorage` facade (`KhaozEngine.Persistence`)

Lives in `KhaozEngine.Persistence` (which already references `KhaozEngine.App`). One object that
assembles paths + queue + storages + optional encoder, and exposes generic typed save/load.

### Construction

```csharp
var storage = new GameStorage("APKiwi", "Hardpoint");                    // plaintext, defaults
var storage = new GameStorage("APKiwi", "Hardpoint", new GameStorageOptions
{
    Encoder = mySaveEncoder,   // SaveEncoder? - enables encoded save/load
    Logger  = log,             // ILogger? - passed to the internal PersistenceQueue
    MaxWriteAttempts = 3,       // queue tuning (default 3)
    RetryDelay = TimeSpan.FromMilliseconds(50), // queue tuning (default 50ms)
});
```

- `publisher` / `appName` validated the same way `AppDataPaths` validates (delegated to it).
- `GameStorageOptions` is a plain options object; every field optional. `null` options ==
  all defaults.

### Surface

```csharp
public AppDataPaths Paths { get; }          // publisher-rooted
public PersistenceQueue WriteQueue { get; } // the one shared coalesced async writer
public ISettingsStorage Settings { get; }   // FileSettingsStorage over WriteQueue
public SaveEncoder? Encoder { get; }        // null when no encoder configured

public SettingsManager<T> CreateSettingsManager<T>(Func<T,T>? sanitizeOnLoad = null) where T : new();

public void Save<T>(string fileName, T value, bool encode = false);  // serialize -> (encode?) -> enqueue
public T Load<T>(string fileName) where T : new();                   // read; auto-decode; new T() if absent
public bool Exists(string fileName);
public void Delete(string fileName);

public void Flush();    // drain WriteQueue (shutdown)
public void Dispose();  // Flush + dispose WriteQueue
```

### Behaviors

- **One shared write queue.** All writes (settings + save data) go through the single
  `WriteQueue` (atomic + coalesced). Reads are direct file reads.
- **`Save<T>`**: serialize `value` with `JsonDefaults.IndentedWrite`. If `encode: true`, require a
  configured `Encoder` (else `InvalidOperationException`) and wrap the JSON via `Encoder.Encode`.
  Enqueue the resulting text to `Paths.GetFilePath(fileName)`.
- **`Load<T>`**: if the file is absent, return `new T()`. Otherwise read the text. If an `Encoder`
  is configured **and** the content `IsEncoded`, decode it first (lenient decode per `SaveEncoder`,
  which recovers JSON even on HMAC mismatch); a decode that returns null falls back to treating the
  raw text as JSON. Deserialize to `T`; on null deserialize, return `new T()`. Encoding is therefore
  transparent on load: a file written encoded is read back without the caller specifying anything.
- **`Exists`**: `File.Exists(Paths.GetFilePath(fileName))`.
- **`Delete`**: delete the file if present (best-effort; absent file is a no-op, not an error).
- **`Settings`**: a `FileSettingsStorage` constructed over `Paths` + `WriteQueue`. Its
  `SettingsFileName` defaults to `settings.json` and remains caller-settable.
- **`CreateSettingsManager<T>`**: convenience that returns `new SettingsManager<T>(Settings, logger, sanitizeOnLoad)`,
  using the same logger the facade was given.
- **Lifetime**: `GameStorage` owns the `WriteQueue` it creates and disposes it on `Dispose`
  (which flushes first). `Flush` is exposed for explicit shutdown drains.

## Part C - Tests, packaging, cleanup

### Tests (`KhaozEngine.Tests`, headless)

Extend `AppDataPathsTests` (same fake-env style):
- Publisher layout `Path.Combine(base, publisher, appName)` for Windows, macOS, Linux-XDG,
  Linux-HOME-fallback, Android, iOS.
- Mobile branches resolve via `LocalApplicationData` and are checked before desktop branches.
- Blank-OS-path fall-through, `LocalApplicationData` fallback, `UserProfile` last-resort
  (`.<publisher>/<appName>`).
- Invalid publisher and invalid appName each throw `ArgumentException` (null/empty/whitespace).
- Resolve-once caching still holds.
- `FakeAppDataEnvironment` gains settable `IsAndroid` / `IsIOS`.

New `GameStorageTests` (real temp dirs, like the existing helper pattern):
- Plaintext `Save<T>`/`Load<T>` round-trip (after `Flush`).
- Encoded `Save<T>(encode:true)`/`Load<T>` round-trip with a configured `SaveEncoder`.
- Auto-decode on load: a file written encoded loads back via `Load<T>` with no flag.
- `Save(encode:true)` with no encoder throws `InvalidOperationException`.
- `Load<T>` of an absent file returns `new T()`.
- `Exists` / `Delete` behavior (present, absent-no-op).
- `Settings` save/load and `CreateSettingsManager<T>` load-on-construct.
- `Dispose` flushes pending writes (data present on disk afterward).

### Packaging / release ritual

- Bump `<KhaozEngineVersion>` 5.58.0 → **5.59.0** in `Directory.Build.props`.
- `CHANGELOG.md`: newest-first 5.59.0 entry describing the publisher root, mobile branches,
  `AppDataPaths` ctor change (breaking-as-minor), and `GameStorage`/`GameStorageOptions`.
- Update the three guarded declarations to 5.59.0: `docs/CONSUMERS.md` "Engine current version",
  `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` examples.
- `dotnet pack -c Release -o ./local-feed` (cumulative).
- Commit, `git tag v5.59.0`, push main + tag.
- No consumer needs to adopt in this work; consumers bump on their own schedule. Note in CONSUMERS
  that adopting requires changing `new AppDataPaths(name)` call sites to the publisher form (or
  switching to `GameStorage`).

### Old-dir cleanup (dev box)

After ship: grep the consumer repos (`~/Hardpoint`, `~/Nullwake`, `~/SpaceGame`) for the folder
names they pass to `AppDataPaths`, then remove the corresponding old single-folder dirs under
`~/Library/Application Support/` on this box (per user request, no migration).

## Out of scope

- Cloud/remote sync, encryption beyond the existing HMAC-obfuscation `SaveEncoder`.
- Consumer adoption PRs (separate follow-up per game).
- Backup/restore, versioned save slots (could be a later `GameStorage` extension).
