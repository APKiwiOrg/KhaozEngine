# LocalizationManager promotion (Batch 1, item 1)

Status: approved design, pre-implementation
Date: 2026-06-10

## Goal

Centralise the `LocalizationManager` class that is currently copy-pasted (with small,
partly-buggy divergences) across all three games into a new shared KhaozEngine package, so
there is one maintained copy.

Consumers (each keeps its own `Resources.resx`, only the manager class is shared):
- Nullwake:  `Nullwake.Core/Localization/LocalizationManager.cs`
- Hardpoint: `Hardpoint.Core/Localization/LocalizationManager.cs`
- SpaceGame: `SpaceGame.Core/Localization/LocalizationManager.cs`

## Why the three copies are not safe to lift verbatim

| Aspect | Nullwake | Hardpoint | SpaceGame |
|---|---|---|---|
| `DEFAULT_CULTURE_CODE` | `"en-US"` | `"en-EN"` | `"en-EN"` |
| Resource base name | hardcoded `Nullwake.Core.Localization.Resources` | hardcoded | hardcoded |
| `SetCulture("")` | throws | throws | falls back to default |
| Assembly lookup | `Assembly.GetExecutingAssembly()` | same | same |

Two of these are real problems, not cosmetics:

1. **`GetExecutingAssembly()` breaks on promotion.** Once this code lives in `KhaozEngine.dll`,
   `Assembly.GetExecutingAssembly()` returns the *engine* assembly, not the game's. Satellite
   resource assemblies live with the game assembly, so culture discovery would silently find
   nothing. The owning resource source must be injected, not discovered.
2. **`"en-EN"` is malformed.** `EN` is not a region. On modern .NET (ICU) it does not throw; it
   resolves to a bogus custom culture. Must not be propagated.

## Decisions (from brainstorming)

1. **New package `KhaozEngine.Localization`**, pure BCL, no MonoGame / `.Input` / other KE deps.
2. **API takes an injected `ResourceManager`** (constructor parameter), rather than
   `(Assembly, baseName)` or static `Configure`. Caller owns `ResourceManager` construction;
   this also makes the class headless-testable with a fake `ResourceManager`.
3. **`DefaultCultureCode` = `"en-US"`** (a valid culture; drops the malformed `"en-EN"`).
4. **`SetCulture(null/empty)` throws.** No silent fallback in shared code. A game wanting
   fallback writes `SetCulture(code ?? LocalizationManager.DefaultCultureCode)` at its own
   call site. Implementation uses `ArgumentException.ThrowIfNullOrEmpty` (the BCL idiom:
   `ArgumentNullException` for null, `ArgumentException` for empty); tests assert the base
   `ArgumentException` (via `ThrowsAny`) for empty and `ArgumentNullException` for null.

> **Post-review revisions (2026-06-10):** the const was renamed `DEFAULT_CULTURE_CODE` →
> `DefaultCultureCode` (C# convention) and `SetCulture` switched from a single
> `ArgumentNullException` to `ArgumentException.ThrowIfNullOrEmpty` (correct exception type for
> empty input). API block and contract below reflect the final state.

## Public API

Namespace `KhaozEngine.Localization`:

```csharp
public class LocalizationManager
{
    public const string DefaultCultureCode = "en-US";

    public LocalizationManager(ResourceManager resourceManager);

    // instance: enumerates cultures that have a resource set in the injected ResourceManager
    public List<CultureInfo> GetSupportedCultures();

    // static: pure thread-culture mutator; does not touch resources
    public static void SetCulture(string cultureCode);
}
```

Sub-decisions:
- **`SetCulture` is `static`.** It only mutates `Thread.CurrentThread.CurrentCulture` /
  `CurrentUICulture` and is called at startup before resources matter. `GetSupportedCultures`
  is the only resource-dependent member, so it is the only instance member. The class
  intentionally mixes one static and one instance method.
- **Return type stays `List<CultureInfo>`** (not `IReadOnlyList`) so consumer adopt PRs are
  pure drop-in at existing call sites.
- **Class is `public`** (was `internal`). XML doc comments preserved from the originals.
- **Algorithm unchanged:** iterate `CultureInfo.GetCultures(CultureTypes.SpecificCultures)`,
  call `GetResourceSet(culture, true, false)`, swallow `MissingManifestResourceException`,
  add a culture when its resource set is non-null, and always append
  `CultureInfo.InvariantCulture` at the end.

## Behaviour contract

- `GetSupportedCultures()`: returns one entry per specific culture whose `GetResourceSet`
  returns non-null, plus `CultureInfo.InvariantCulture` (always, appended last). Cultures whose
  lookup throws `MissingManifestResourceException` are skipped.
- `SetCulture(code)`: sets both `CurrentCulture` and `CurrentUICulture` on the current thread to
  `new CultureInfo(code)`. Throws on null/empty `code`.
- `DefaultCultureCode`: `"en-US"`.

## Project / packaging changes

- New `KhaozEngine.Localization/KhaozEngine.Localization.csproj`, modelled on the existing
  package csprojs: `<PackageId>KhaozEngine.Localization</PackageId>`, a `Description`, a
  `README.md` packed in, and `InternalsVisibleTo KhaozEngine.Tests` (only needed if any helper
  is internal; the public API does not require it, include for parity).
- No MonoGame `PackageReference`. BCL `System.Resources`/`System.Globalization` need no package
  reference on net10.0.
- Add the project to `KhaozEngine.slnx`.
- Inherits the shared `<Version>` from `Directory.Build.props`.

## Testing (headless, KhaozEngine.Tests)

The injected `ResourceManager` removes any need for a real `.resx` build pipeline.

- `FakeResourceSet : ResourceSet` exposing the protected parameterless ctor, used as a non-null
  sentinel.
- `FakeResourceManager : ResourceManager` overriding
  `GetResourceSet(CultureInfo, bool, bool)` to:
  - return a `FakeResourceSet` for a configured set of "supported" cultures,
  - throw `MissingManifestResourceException` for at least one culture (exercises the catch),
  - return `null` for everything else.
- Tests:
  1. `GetSupportedCultures()` returns exactly the configured supported cultures plus
     `InvariantCulture` (and the throwing culture is absent).
  2. `SetCulture(valid)` sets both `CurrentCulture` and `CurrentUICulture`.
  3. `SetCulture(null)` throws `ArgumentNullException`; `SetCulture("")` throws
     `ArgumentException` (asserted on the base type via `ThrowsAny`).
  4. `DefaultCultureCode == "en-US"`.
- Culture-mutating tests save and restore `Thread.CurrentThread.CurrentCulture` /
  `CurrentUICulture` in a `finally` so state does not leak across the runner.

## Release handling

This is item 1 of Batch 1. Batch 1 ships as a single KE release, so this commit does **not**
bump `<Version>` or add a `CHANGELOG.md` entry. The `2.4.0 → next` bump, CHANGELOG entry,
`docs/CONSUMERS.md` update, and `dotnet pack -c Release -o ./local-feed` happen once at the end
of the batch, followed by the per-consumer bump-and-adopt PRs (each game deletes its local copy
and references `KhaozEngine.Localization`).

## Out of scope

- Migrating the games to consume the package (separate adopt PRs, after the batch release).
- Any change to the games' `Resources.resx` files or localization content.
- Reworking how games store/select the active culture (settings layer).
