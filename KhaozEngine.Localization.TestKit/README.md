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

A gap throws `LocalizationCoverageException`, which fails the test. Its `Findings` list every missing key and
placeholder mismatch, and its message lists the same gaps for the developer reading the failed run (see
[Refusals](#refusals)). Pass no satellite cultures to check the neutral resx only.

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

## Refusals

Every refusal throws `LocalizationCoverageException`, which derives from `Exception`. `Reason` names the rule and
the message stays developer prose, never a contract. A null keys type, `ResourceManager` or key sequence throws
`ArgumentNullException`.

The reasons are `LocalizationCoverageRefusal` members.

A coverage failure checks every key in every culture before it throws. `Findings` holds one
`LocalizationCoverageFinding` per gap, in the order the message lists them: the neutral resx first, then each
satellite in the order passed, with its missing translations before its placeholder mismatches. `Reason` is the
first finding's reason. A coverage failure also sets `BaseName` (the `ResourceManager`'s base name), `KeyCount`
(the distinct keys checked) and, for the keys-class overload, `KeysType`.

| `Reason` | What it refuses | Identifiers |
| --- | --- | --- |
| `KeyUniverseEmpty` | a keys class with no key fields, a neutral resx with no string entries or a key sequence with no non-empty key, before any check | `KeysType` (keys class) or `BaseName` (resx), `Findings` null |
| `NeutralResourcesUnavailable` | a neutral (invariant) resource set that cannot be loaded | `BaseName`. From `NeutralKeys` and the resx overload, which read the universe first, `Findings` is null. From the other overloads it is a finding |
| `NeutralKeysMissing` | keys the neutral resx does not hold | finding `MissingKeys` |
| `CultureNameInvalid` | a satellite name that is not a valid culture name | finding `CultureName` |
| `SatelliteResourcesUnavailable` | a satellite with no resource set of its own, which leaves all `KeyCount` keys unresolved | finding `CultureName` |
| `TranslationsMissing` | keys a satellite does not hold, read with parent fallback off | finding `CultureName`, `MissingKeys` |
| `PlaceholderSetMismatch` | a translation whose placeholder indices differ from its neutral template's, one finding per key | finding `CultureName`, `Key`, `ExpectedIndices`, `ActualIndices` |

`MissingKeys` follows the key universe's order, and both index lists are ascending. Every collection is an owned,
read-only snapshot.
