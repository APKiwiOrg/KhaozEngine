# BuildMetadata promotion (Batch 1, item 3)

Status: approved design, pre-implementation
Date: 2026-06-10

## Goal

Centralise the `AssemblyMetadataAttribute` reader that is copy-pasted across all three games'
`BuildConfig` types into a shared KhaozEngine helper, so the reflection + multi-assembly fallback
logic is maintained once. Each game keeps its own typed `BuildConfig` facade (their exposed
property sets differ); only the duplicated reading machinery moves.

Consumers (each keeps its own `BuildConfig` facade):
- Nullwake:  `Nullwake.Core/Config/BuildConfig.cs`
- Hardpoint: `Hardpoint.Core/Config/BuildConfig.cs`
- SpaceGame: `SpaceGame.Core/Settings/BuildConfig.cs`

## What is duplicated

Two private methods are near-identical in all three games:

1. `TryReadMetadata(Assembly assembly, string key, out string value)` - the reflection loop over
   `AssemblyMetadataAttribute`s. Byte-identical except Hardpoint fully-qualifies
   `System.Reflection.Assembly` / `System.Reflection.AssemblyMetadataAttribute` to avoid a
   namespace clash with its sibling `Hardpoint.Core.Assembly` namespace (CS0118). Logic identical.
2. `ReadMetadataValue(string key, string fallback)` - the orchestration: probe the game's own
   assembly (`typeof(BuildConfig).Assembly`, or `typeof(SpaceGameGame).Assembly` in SpaceGame),
   then `Assembly.GetEntryAssembly()` (null-checked), then return `fallback`. Identical shape.

The typed property sets diverge and stay per-game:
- Nullwake / Hardpoint: `Product`, `Version`, `BuildName`, `InformationalVersion`,
  `BundleIdentifier`, `DisplayVersion`.
- SpaceGame: `DisplayName`, `BuildVersion`, `BuildSubVersion`, `WebsiteBuildVersion`, `BuildName`,
  `MenuTitle`, `BundleIdentifier`, `DisplayBuildVersion`, `MainMenuSubtitle`.

The engine currently has no metadata reader (verified).

## Decisions (from brainstorming)

1. **Full helper**, not just the primitive: KE absorbs BOTH the reflection loop and the
   multi-assembly fallback orchestration (the orchestration is identical across all three games).
2. **New package `KhaozEngine.App`**, pure BCL, no MonoGame / other KE deps. Intended home for
   app identity / runtime / environment helpers; `BuildMetadata` now, `AppDataPaths` (#5) likely later.
3. Assembly resolution is **explicit** (caller passes the assemblies). The helper must not call
   `Assembly.GetExecutingAssembly()` - once this code lives in `KhaozEngine.dll` that would return
   the engine assembly, not the game's (the same trap as `LocalizationManager`).

## Public API

Namespace `KhaozEngine.App`:

```csharp
public static class BuildMetadata
{
    /// <summary>
    /// Probes each assembly in order for an AssemblyMetadataAttribute whose Key equals
    /// <paramref name="key"/> (Ordinal) with a non-whitespace Value; returns the first such value,
    /// or <paramref name="fallback"/> if none match. Null entries in <paramref name="assemblies"/>
    /// are skipped.
    /// </summary>
    public static string Read(string key, string fallback, params Assembly[] assemblies);
}
```

## Behaviour contract

Preserves the games' exact semantics so the facades are a clean drop-in:

- Iterate `assemblies` in order; skip any that are `null` (so callers can pass
  `Assembly.GetEntryAssembly()`, which may be null, directly).
- For each assembly, scan its `AssemblyMetadataAttribute`s. On the first attribute whose `Key`
  equals `key` (`StringComparison.Ordinal`):
  - if its `Value` is null or whitespace, treat this assembly as a miss and move to the next
    assembly (matches the originals' `break`-then-`false` quirk: a blank-valued matching key does
    NOT fall through to a later attribute with the same key in the same assembly, but DOES fall
    through to the next assembly);
  - otherwise return that `Value`.
- If no assembly yields a value, return `fallback` unchanged.
- `key == null` throws `ArgumentNullException` (programming error). `fallback` is returned as-is;
  callers always pass non-null (empty string is valid, e.g. SpaceGame's `DefaultBuildSubVersion`).
- Zero assemblies passed → returns `fallback`.

## Consumer shape after adopt

Each game keeps its typed `BuildConfig` facade; the two duplicated private methods collapse to:

```csharp
private static string ReadMetadataValue(string key, string fallback) =>
    BuildMetadata.Read(key, fallback, typeof(BuildConfig).Assembly, Assembly.GetEntryAssembly());
```

SpaceGame uses `typeof(SpaceGameGame).Assembly` as the first probe (same shape). The duplicated
`TryReadMetadata` loop and the hand-written fallback are deleted from all three.

## Project / packaging changes

- New `KhaozEngine.App/KhaozEngine.App.csproj`, modelled on the existing package csprojs:
  `<PackageId>KhaozEngine.App</PackageId>`, a `Description`, a packed `README.md`. No MonoGame
  reference, no other KE references, no `InternalsVisibleTo` (public API + test fixtures need none).
- Add the project to `KhaozEngine.slnx`.
- Add a `ProjectReference` to `KhaozEngine.Tests`.
- Inherits the shared `<Version>` from `Directory.Build.props`.

## Testing (headless, KhaozEngine.Tests)

Declare `[assembly: AssemblyMetadata(...)]` fixtures in a dedicated test file (no csproj changes):

- key present in the test assembly → `Read` returns its value;
- key absent → returns `fallback`;
- multi-assembly ordering: probe `[typeof(object).Assembly, testAssemblyWithKey]` → returns the
  value (proves it falls through a miss to a later assembly);
- a `null` assembly in the list is skipped, no crash;
- whitespace-valued matching key → treated as a miss → returns `fallback`;
- `key == null` → throws `ArgumentNullException`.

## Release handling

Item 3 of Batch 1. No `<Version>` bump, no `CHANGELOG.md` entry, no `dotnet pack` here. The single
`3.0.0 → 3.1.0` bump + CHANGELOG + `docs/CONSUMERS.md` update + pack happen once at the end of the
batch, followed by the per-consumer bump-and-adopt PRs.

## Out of scope

- Migrating the games' `BuildConfig` facades to call the helper (separate adopt PRs, after release).
- Any change to the `AssemblyMetadata` items emitted by each game's `Directory.Build.props`.
- The typed property facades themselves (stay per-game).
