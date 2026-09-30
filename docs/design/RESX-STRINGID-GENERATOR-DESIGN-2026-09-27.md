# Resx to StringId Source Generator

Status: implemented for the next staged engine release. Issue: [#66](https://github.com/APKiwiOrg/KhaozEngine/issues/66).

## Problem

`StringId`, `LocalizedText`, and the KELOC analyzer diagnostics already make player-facing localization
explicit at compile time. A consumer still had to keep a second C# list beside its neutral resx file:

```csharp
internal static class Strings
{
    public static readonly StringId Pause = new("Menu.Pause");
}
```

That list could drift in either direction. A removed or renamed resx key could leave a compiling field that
resolved to its own key at runtime. A new resx key could remain unreachable through the typed surface. The
Showcase demonstrated the cost with 136 hand-written fields that exactly mirrored 136 neutral resource keys.

## Decision

`KhaozEngine.Localization.Analyzers` also ships an incremental source generator. A consumer opts one neutral
resx file in by adding it as an `AdditionalFiles` item and naming the exact generated type:

```xml
<ItemGroup>
  <AdditionalFiles Include="Strings.resx"
                   KhaozStringIdType="MyGame.Strings" />
</ItemGroup>
```

The resx remains an ordinary SDK `EmbeddedResource`. The extra item only gives Roslyn a compile-time view of
the same file. Satellite files are not opted in. They remain the concern of
`KhaozEngine.Localization.TestKit`, which checks translation completeness and placeholder parity.

The generated class is `internal` by default. A shared keys assembly can request a public class:

```xml
<AdditionalFiles Include="Strings.resx"
                 KhaozStringIdType="MyGame.Strings"
                 KhaozStringIdAccessibility="public" />
```

Every generated member stays public so reflection-based coverage tests can read the key set. Each field is a
`public static readonly global::KhaozEngine.App.StringId` built from the exact resx key.

## Why explicit AdditionalFiles wins

Three shapes were considered:

1. Explicit AdditionalFiles metadata in the existing analyzer package.
2. A project-wide switch that discovers every embedded resx file and derives target names from paths.
3. A separate generator package or an attributed partial marker class.

The first is the smallest complete contract. The target namespace and type never depend on a file move. Test
fixtures, satellite files, and mixed resource files remain untouched unless selected. The generator rides the
same analyzer assembly and the same `Game2D` and `Game3D` package edge consumers already have.

Automatic discovery was rejected because this repository alone contains neutral fixtures that are not product
catalogs. Culture suffix detection and strongly typed resource designer coexistence would add convention and
special cases. A separate package or marker attribute would add package and syntax surface while the resx still
needed AdditionalFiles wiring.

## Member naming and determinism

The generated member name is derived only from the resource key:

1. Non-letter and non-digit characters separate runs.
2. The first letter or digit after a separator is uppercased with invariant casing.
3. Runs are concatenated.
4. A leading digit receives an underscore.

Examples:

| Resource key | Generated member |
|---|---|
| `Hub.Title` | `HubTitle` |
| `Room.Gui2D.Title` | `RoomGui2DTitle` |
| `menu.play` | `MenuPlay` |
| `fly_speed` | `FlySpeed` |

Keys are emitted in ordinal key order. Reordering XML does not reorder generated source. The generated hint
name comes from the fully qualified target type, so two different file paths cannot collapse onto one hint.
String literals use Roslyn's C# formatter, preserving quotes, slashes, and control characters in the exact key.

The generator never invents numeric suffixes for a collision. `Menu.Play` and `Menu_Play` both map to
`MenuPlay`, so accepting both would make public member names depend on unrelated keys. The build fails instead.

## Inputs and failures

Only string data rows produce members. A row with `mimetype` is ignored. A row with `type` is generated only
when it is `System.String` or an assembly-qualified `System.String`. This permits ordinary neutral localization
resx files and ignores binary or array resources in a mixed file.

Generator configuration and input failures are errors because continuing would leave the requested typed API
absent or ambiguous:

| Id | Meaning |
|---|---|
| `KELOC004` | The opted file is not resx, the target type is not a valid fully qualified C# name, or accessibility is not `internal` or `public`. |
| `KELOC005` | The XML is malformed, the root shape is invalid, or a string row has an empty or unusable key. |
| `KELOC006` | Two keys map to the same generated member name. |
| `KELOC007` | Source already declares the target type or two opted resources request the same target type. |

An empty `KhaozStringIdType` means the AdditionalFile is not opted in. MSBuild emits empty
`CompilerVisibleItemMetadata` values for unrelated AdditionalFiles such as the file-size baseline, so treating
empty as invalid would break every project that consumes both analyzers.

Roslyn generators cannot see types emitted by other generators in the ordinary generation phase. If another
generator independently emits the same target type, the C# compiler reports that duplicate. Source types and
other opted resx inputs receive the targeted `KELOC007` error.

## Packaging and proof

The existing analyzer DLL already ships under `analyzers/dotnet/cs`, which is also the NuGet location for a
source generator. A new `buildTransitive/KhaozEngine.Localization.Analyzers.props` exposes
`KhaozStringIdType` and `KhaozStringIdAccessibility` as AdditionalFiles metadata. The existing include-all
dependency from `Game2D`, inherited by `Game3D`, carries both the analyzer assembly and these build assets.

The in-repo Showcase uses a source `ProjectReference`, which does not import packed buildTransitive assets. Its
project declares the same two compiler-visible metadata names explicitly. `ShowcaseStrings.resx` now generates
the `KhaozEngine.Showcase.ShowcaseStrings` class, and the former 136-field C# mirror is deleted. A headless test
compares every generated field key with every neutral string resource key in both directions.

## Scope boundary

This generator removes neutral resx to `StringId` declaration drift. It does not validate satellite culture
coverage or format placeholders. It does not convert engine key classes backed by built-in dictionaries. It does
not generate `ResourceManager`, catalog wiring, format methods, or strongly typed string values.
