# KhaozEngine.Localization.TestKit

Test-only helper for asserting localization coverage. Reference it from a game's **test project** (it is in no
umbrella metapackage). Pure BCL + `KhaozEngine.App`; framework-agnostic (works from xUnit / NUnit / MSTest).

It collapses the coverage test several games hand-rolled: take a key universe (a keys class, the neutral resx's
own entries, or an explicit key sequence), then assert every key resolves in the neutral resx **and** in each
shipped satellite culture with **parent fallback disabled** (so a missing translation fails the test instead of
silently reading the neutral language), plus placeholder-index integrity between each neutral template and its
translation.

## Quick start

With a keys class (`public const string` or `StringId` fields):

```csharp
using KhaozEngine.Localization.TestKit;
using Xunit;

public class LocalizationCoverageTests
{
    [Fact]
    public void EveryKey_IsTranslatedInEveryShippedCulture()
        => LocalizationCoverage.AssertComplete(
            typeof(MyGameStrings),                              // keys class
            MyGameLocalization.CreateCatalogResources(),        // the game's ResourceManager
            "es-ES", "fr-FR");                                  // shipped satellite cultures
}
```

With no keys class (keys live directly in the neutral resx, referenced via the MSBuild-generated designer
properties): pass just the `ResourceManager` and the neutral resx's own string entries become the key universe.

```csharp
[Fact]
public void EveryResxKey_IsTranslatedInEveryShippedCulture()
    => LocalizationCoverage.AssertComplete(Resources.ResourceManager, "es-ES", "fr-FR");
```

A gap throws `LocalizationCoverageException` listing every missing key and placeholder mismatch, which fails the
test. Pass no satellite cultures to check the neutral resx only.

## What it checks

- **Keys** come from one of three universes with identical checking semantics:
  - a plain constants class: every `public const string` field's value, plus every
    `public static readonly StringId` (`KhaozEngine.App`) field's `.Key`. Add a key on either side and it is
    covered the moment it exists.
  - the neutral resx itself (the `ResourceManager`-only overload): every string entry of the neutral (invariant)
    resource set. Add a key to the resx and it is covered the moment it exists.
  - an explicit key sequence: e.g. `NeutralKeys(rm)` filtered down to exclude intentionally untranslated keys.
- **Neutral coverage** - every key present in the invariant (neutral) resx (vacuously true for the
  resx-driven universe).
- **Satellite coverage** - every key present in each named culture, resolved with `tryParents: false`, so a
  satellite that is missing an entry fails here rather than quietly resolving to the neutral value.
- **Placeholder integrity** - each translation carries the same set of composite-format placeholder indices
  (`{0}`, `{1}`, ...) as its neutral template, so a translation can never drop or renumber an argument slot
  (escaped `{{`/`}}` literals are ignored).

## API

- `LocalizationCoverage.AssertComplete(Type keysType, ResourceManager resources, params string[] satelliteCultures)`
  - keys-class universe; throws `LocalizationCoverageException` on any gap.
- `LocalizationCoverage.AssertComplete(ResourceManager resources, params string[] satelliteCultures)`
  - neutral-resx universe (no keys class needed); also throws if the neutral set cannot load or has no string
    entries, so it can never pass vacuously.
- `LocalizationCoverage.AssertComplete(IEnumerable<string> keys, ResourceManager resources, params string[] satelliteCultures)`
  - explicit key universe (duplicates checked once, blank keys skipped, empty universe throws).
- `LocalizationCoverage.Keys(Type keysType)` - the extracted keys-class keys, exposed so a game can drive an
  xUnit `[Theory]` off the same source.
- `LocalizationCoverage.NeutralKeys(ResourceManager resources)` - the neutral resx's string-entry keys
  (ordinally sorted), for a `[Theory]` or for filtering before the key-sequence overload.
