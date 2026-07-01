# KhaozEngine.App

Game-agnostic app identity / runtime helpers. Pure BCL, no MonoGame dependency.

`BuildMetadata.Read` reads `AssemblyMetadata` items (emitted by a project's `Directory.Build.props`)
back at runtime, so a game can surface its own version / build name / bundle id without re-deriving
them. The caller passes the assemblies to probe - the engine never guesses via
`GetExecutingAssembly` (that would resolve to the engine, not the game).

```csharp
using System.Reflection;
using KhaozEngine.App;

// Probe the game's own assembly, then the entry assembly, else fall back:
string version = BuildMetadata.Read(
    "MyGame.Version", "0.0.0",
    typeof(MyGameMarker).Assembly, Assembly.GetEntryAssembly());
```

First assembly with a matching, non-whitespace `AssemblyMetadata` value wins; null assemblies are
skipped; otherwise the fallback is returned.

## AppInstallStamp

A local record of when the current app version first ran on this machine and when it last changed
(what an About screen shows as "Installed" / "Updated"). Distinct from the build's release date, which
stays a per-game `BuildMetadata` property. The core is a pure, storage-free resolver - `utcNow` is
injected, so it is deterministic and snapshot/headless replay stays stable.

```csharp
AppInstallStampResult r = AppInstallStamp.Resolve(previousStamp, currentVersion, DateTime.UtcNow);
if (r.Changed) Persist(r.Stamp);   // first run, or a version change
```

First run (previous null) sets both dates; same version returns the previous stamp untouched; a
different version (upgrade or downgrade - ordinal string inequality only) preserves
`FirstInstalledAtUtc` and bumps `Version` + `UpdatedAtUtc`. Store the stamp on the game's existing
settings DTO rather than a separate file; `KhaozEngine.Persistence` adds a `SettingsManager<T>.StampInstall(...)`
convenience that resolves and saves only when changed.

## LocalizationManager

Localization helper (absorbed from the retired `KhaozEngine.Localization` package in 9.0.0). It does
two things:

- **Discover supported cultures** from a `ResourceManager` you inject (the cultures that
  actually have a satellite resource set), always including the invariant culture.
- **Set the current thread culture** (both `CurrentCulture` and `CurrentUICulture`).

```csharp
using System.Resources;
using KhaozEngine.App;

// Point it at YOUR game's resources (your assembly owns the satellite .resx files):
var rm = new ResourceManager("MyGame.Core.Localization.Resources", typeof(MyGameMarker).Assembly);
var loc = new LocalizationManager(rm);

List<CultureInfo> cultures = loc.GetSupportedCultures();

LocalizationManager.SetCulture("en-US");
// Want a fallback instead of an exception on empty input? Do it at the call site:
LocalizationManager.SetCulture(code ?? LocalizationManager.DefaultCultureCode);
```

`SetCulture` throws on null/empty input. `DefaultCultureCode` is `"en-US"`.
