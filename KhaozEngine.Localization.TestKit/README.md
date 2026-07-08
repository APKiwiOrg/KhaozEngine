# KhaozEngine.Localization.TestKit

Test-only helper for asserting localization coverage. Reference it from a game's **test project** (it is in no
umbrella metapackage). Pure BCL + `KhaozEngine.App`; framework-agnostic (works from xUnit / NUnit / MSTest).

It collapses the reflection-based coverage test several games hand-rolled: reflect over a keys class, then assert
every key resolves in the neutral resx **and** in each shipped satellite culture with **parent fallback disabled**
(so a missing translation fails the test instead of silently reading the neutral language), plus placeholder-index
integrity between each neutral template and its translation.

## Quick start

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

A gap throws `LocalizationCoverageException` listing every missing key and placeholder mismatch, which fails the
test. Pass no satellite cultures to check the neutral resx only.

## What it checks

- **Keys** are read off a plain constants class: every `public const string` field's value, plus every
  `public static readonly StringId` (`KhaozEngine.App`) field's `.Key`. Add a key on either side and it is covered
  the moment it exists.
- **Neutral coverage** - every key present in the invariant (neutral) resx.
- **Satellite coverage** - every key present in each named culture, resolved with `tryParents: false`, so a
  satellite that is missing an entry fails here rather than quietly resolving to the neutral value.
- **Placeholder integrity** - each translation carries the same set of composite-format placeholder indices
  (`{0}`, `{1}`, ...) as its neutral template, so a translation can never drop or renumber an argument slot
  (escaped `{{`/`}}` literals are ignored).

## API

- `LocalizationCoverage.AssertComplete(Type keysType, ResourceManager resources, params string[] satelliteCultures)`
  - throws `LocalizationCoverageException` on any gap.
- `LocalizationCoverage.Keys(Type keysType)` - the extracted keys, exposed so a game can drive an xUnit `[Theory]`
  off the same source.
