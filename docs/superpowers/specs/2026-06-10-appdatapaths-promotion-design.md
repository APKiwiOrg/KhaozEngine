# AppDataPaths promotion (Batch 1, item 5)

Status: approved design, pre-implementation
Date: 2026-06-10

## Goal

Promote SpaceGame's OS-correct application-data path resolver into the shared `KhaozEngine.App`
package, parameterised by the app folder name, so all three games resolve saves/settings/logs
through one maintained implementation instead of each hand-inlining
`Environment.GetFolderPath(LocalApplicationData)`.

Current state:
- SpaceGame: `SpaceGame.Core/Systems/AppDataPaths.cs` — a proper static resolver (Win/mac/Linux/
  XDG + fallbacks), but hardcodes `AppFolderName = "SpaceGame"`.
- Nullwake: inlines `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` + `"Nullwake"`
  in `NullwakeGame.cs`, `Systems/LocalSaveSystem.cs`, and the old `Engine/GameLogger.cs`.
- Hardpoint: no app-data path handling.

## Decisions (from brainstorming)

1. **Lives in `KhaozEngine.App`** (the package created for item 3). Pure BCL (`System.IO`), no
   MonoGame / other KE deps.
2. **Instance class** `AppDataPaths`, constructed with the app folder name. No global mutable
   state (rejected the static `Configure`-once pattern, consistent with `LocalizationManager`).
3. **Injected-environment test seam.** The OS/env access is abstracted behind an internal
   `IAppDataEnvironment` so every resolution branch is unit-tested deterministically on any OS.
   Games never touch it — they use the simple `AppDataPaths(appFolderName)` ctor; the engine owns
   all OS-specific nuance in one place. Requires `InternalsVisibleTo KhaozEngine.Tests` on the package.
4. **Full convenience member set** (pure drop-in for SpaceGame): `BaseDirectory`, `SaveFilePath`,
   `SettingsFilePath`, `LogFilePath`, `PreviousLogFilePath`, `GetFilePath(name)`. The conventional
   filenames (`save.json`, `settings.json`, `game.log`, `game.prev.log`) become engine conventions.

## Components

Namespace `KhaozEngine.App`:

```csharp
// Internal test seam. Production code never references this; it exists so the OS-branching
// resolution can be exercised deterministically in headless tests.
internal interface IAppDataEnvironment
{
    bool IsWindows { get; }
    bool IsMacOS { get; }
    bool IsLinux { get; }
    string GetFolderPath(Environment.SpecialFolder folder);
    string? GetEnvironmentVariable(string variable);
}

// Default implementation over the real OS/env. Internal sealed.
internal sealed class SystemAppDataEnvironment : IAppDataEnvironment { /* wraps OperatingSystem.* + Environment.* */ }

public sealed class AppDataPaths
{
    /// <summary>Resolves OS-correct app-data paths under <paramref name="appFolderName"/>.</summary>
    /// <exception cref="ArgumentException">appFolderName is null, empty, or whitespace.</exception>
    public AppDataPaths(string appFolderName);

    // Test seam: same behaviour, environment injected.
    internal AppDataPaths(string appFolderName, IAppDataEnvironment environment);

    public string BaseDirectory { get; }        // resolved once, created (lazy) on first access, cached
    public string SaveFilePath { get; }          // Path.Combine(BaseDirectory, "save.json")
    public string SettingsFilePath { get; }      // Path.Combine(BaseDirectory, "settings.json")
    public string LogFilePath { get; }           // Path.Combine(BaseDirectory, "game.log")
    public string PreviousLogFilePath { get; }   // Path.Combine(BaseDirectory, "game.prev.log")
    public string GetFilePath(string fileName);  // Path.Combine(BaseDirectory, fileName)
}
```

The public `AppDataPaths(string appFolderName)` ctor delegates to the internal ctor passing a
`new SystemAppDataEnvironment()`.

## Behaviour contract

Resolution preserved verbatim from SpaceGame's `ResolveBaseDirectory`, parameterised by
`appFolderName` and reading through `IAppDataEnvironment`:

1. Windows (`env.IsWindows`): `env.GetFolderPath(SpecialFolder.ApplicationData)` (`%APPDATA%`); if
   non-whitespace → `Path.Combine(that, appFolderName)`.
2. macOS (`env.IsMacOS`): `env.GetFolderPath(SpecialFolder.ApplicationData)`; if non-whitespace →
   `Path.Combine(that, appFolderName)`.
3. Linux (`env.IsLinux`): `env.GetEnvironmentVariable("XDG_DATA_HOME")` if non-whitespace →
   `Path.Combine(that, appFolderName)`; else `env.GetEnvironmentVariable("HOME")` if non-whitespace
   → `Path.Combine(home, ".local", "share", appFolderName)`.
4. Fallback (any OS, if the above did not yield): `env.GetFolderPath(SpecialFolder.LocalApplicationData)`;
   if non-whitespace → `Path.Combine(that, appFolderName)`.
5. Last resort: `env.GetFolderPath(SpecialFolder.UserProfile)` →
   `Path.Combine(that, "." + appFolderName.ToLowerInvariant())`.

- `BaseDirectory` resolves on first access, calls `Directory.CreateDirectory` on the resolved path,
  caches the result, and returns it (matches SpaceGame's auto-create-on-access). Subsequent reads
  return the cached value (resolution + creation happen once).
- All file-path members compose off `BaseDirectory` via `Path.Combine`, so reading any of them also
  ensures the directory exists.
- Constructor throws `ArgumentException` when `appFolderName` is null, empty, or whitespace.

## Testing (headless, KhaozEngine.Tests)

Uses the internal env ctor via `InternalsVisibleTo`.

- `FakeAppDataEnvironment`: a test double with settable `IsWindows`/`IsMacOS`/`IsLinux`, a
  dictionary of `SpecialFolder → path`, and a dictionary of env-var → value. All paths point under
  a **temp root** the test creates and deletes in a `finally`, so nothing pollutes real app-data.
- One test per resolution branch, each asserting `BaseDirectory == Path.Combine(fakeRoot, appFolderName)`
  (or the `.<lower>` form for last-resort) and `Directory.Exists(BaseDirectory)`:
  1. Windows → ApplicationData root.
  2. macOS → ApplicationData root.
  3. Linux with `XDG_DATA_HOME` set → that root.
  4. Linux without XDG, `HOME` set → `<home>/.local/share/<appFolderName>`.
  5. No OS flag / empty primary → LocalApplicationData fallback.
  6. Everything empty except UserProfile → `<profile>/.<applower>`.
- File-path members: `SaveFilePath`/`SettingsFilePath`/`LogFilePath`/`PreviousLogFilePath` equal
  `Path.Combine(BaseDirectory, <expected filename>)`; `GetFilePath("custom.dat")` composes correctly.
- Caching: reading `BaseDirectory` twice resolves once (assert via a counting fake env or by
  asserting the value is stable).
- Ctor validation: null / `""` / `"   "` `appFolderName` throws `ArgumentException`.

## Project / packaging changes

- Add files to the existing `KhaozEngine.App` project (no new package):
  `AppDataPaths.cs`, `IAppDataEnvironment.cs`, `SystemAppDataEnvironment.cs` (or grouped sensibly).
- Add `<ItemGroup><InternalsVisibleTo Include="KhaozEngine.Tests" /></ItemGroup>` to
  `KhaozEngine.App/KhaozEngine.App.csproj`.
- No slnx / Tests-csproj wiring changes (the package + reference already exist from item 3).
- Inherits the shared `<Version>` from `Directory.Build.props`.

## Release handling

Item 5 of Batch 1. No `<Version>` bump, no `CHANGELOG.md` entry, no `dotnet pack` here. The single
`3.0.0 → 3.1.0` bump happens once at the end of the batch.

## Out of scope

- Migrating the games to consume it (adopt PRs, after release). NOTE for that phase: Nullwake's
  data dir moves from `LocalApplicationData/Nullwake` to the OS-correct location, so existing user
  saves/logs would need a one-time migration or would appear "reset". SpaceGame is a near drop-in.
- Reworking save/settings persistence (items 8/10) — only the path resolution moves here.
