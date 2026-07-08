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

### String catalog (`IStringCatalog` / `ResourceStringCatalog`)

`LocalizationManager` sets the culture; a string catalog resolves keys *in* that culture. `IStringCatalog`
is a thin lookup contract; `ResourceStringCatalog` is the standard-library implementation over the same
`ResourceManager`, and `LocalizationManager.Catalog` hands one out over the resources it was built with.

```csharp
using System.Resources;
using KhaozEngine.App;

var loc = new LocalizationManager(rm);
IStringCatalog strings = loc.Catalog;          // over the same rm

LocalizationManager.SetCulture("en-US");
string title = strings.Get("token.enter");     // resolved value, or "token.enter" if the key is absent
string hi    = strings.Format("greeting", playerName);   // culture-aware string.Format of the template
if (strings.TryGet("optional.hint", out string hint)) { /* present */ }
```

- **`Get(key)`** resolves against `CultureInfo.CurrentUICulture` and **never throws**: a missing key returns
  the key itself (a visible, non-fatal placeholder). Reads the culture live, so a later `SetCulture` is picked
  up without re-creating the catalog.
- **`Format(key, args)`** is `string.Format(CurrentUICulture, Get(key), args)` - culture-aware substitution of
  the resolved template.
- **`TryGet(key, out value)`** is a non-throwing probe: `false` with `value == key` when absent, `true` with
  the localized value when present.

`IStringCatalog` and `LocalizationManager` stay separate (single-responsibility): the `Catalog` property is a
convenience factory, not a merge. You can also `new ResourceStringCatalog(rm)` directly.

## Compile-time localization enforcement (`StringId` / `LocalizedText`)

`IStringCatalog` resolves *by string key*, which does nothing to stop a hardcoded literal reaching the UI. The
`StringId` + `LocalizedText` value types close that gap: the Gui text sinks accept a `LocalizedText`, and the
only implicit conversion into it is from `StringId` (never from `string`), so a bare string literal at a sink is
a compile error. `KhaozEngine.Localization.Analyzers` (in the `Game2D`/`Game3D` umbrellas) enforces the rest.

- **`StringId`** - a typed localization key (`new StringId("Menu.Play")` or `StringId.Of(...)`). No implicit
  conversion from `string`, so authoring one is a deliberate act. Author them as constants (a source generator
  from `.resx` is on the roadmap):

  ```csharp
  internal static class Strings
  {
      public static readonly StringId Play = new("Menu.Play");
      public static readonly StringId Score = new("Hud.Score");   // "Score: {0}"
  }
  ```

- **`LocalizedText`** - what a sink takes. Either a localizable `StringId` (+ optional format args) or a raw
  literal. It stores the id/args and **re-resolves on every access**, so a runtime locale switch takes effect on
  the next draw with nothing to invalidate.

  ```csharp
  LocalizedText a = Strings.Play;                       // implicit from StringId
  LocalizedText b = LocalizedText.Of(Strings.Score, 42); // format args -> catalog.Format
  LocalizedText c = LocalizedText.Raw("v1.2.0");        // non-localizable escape hatch (greppable)
  string shown = a.Resolve();                            // resolves against the ambient catalog
  ```

- **`LocalizationContext.Catalog`** - the ambient `IStringCatalog` a `LocalizedText` resolves against when no
  catalog is passed. Wire it once at startup. `LocalizationContext.WireResx` is the one-liner (it builds the
  `ResourceStringCatalog`, installs it, and returns it) that replaces the bridge class every game used to write
  by hand:

  ```csharp
  LocalizationContext.WireResx(rm);                              // over a ResourceManager
  LocalizationContext.WireResx("MyGame.Resources", asm);         // or from a base name + assembly
  // equivalent to: LocalizationContext.Catalog = new ResourceStringCatalog(rm);
  ```

  Culture stays live (the catalog reads `CurrentUICulture` at resolve time), so a runtime `SetCulture` shows up on
  the next draw with nothing to invalidate. Null is legal (headless tests, non-localized apps): a localizable
  value then renders its key as a visible placeholder rather than throwing.

- **`[LocalizationExempt]`** (assembly/type/member) marks a scope where `LocalizedText.Raw` is intentional, so
  the analyzer stays silent there (debug overlays, tools, sample chrome). **`[LocalizationStringSink]`**
  (method/constructor) marks a discouraged raw-`string` sink so the analyzer flags its callers - the engine's
  obsolete `string` Gui overloads carry it, and a game can mark its own sinks.
