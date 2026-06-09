# KhaozEngine.Content — shared config loading + JSON-schema validation (design)

**Date:** 2026-06-08
**Status:** Approved (pending written-spec review)
**Package:** new `KhaozEngine.Content`. Released with the suite at **2.1.0** (unified versioning — adding
a package bumps all packages to `2.1.0`; Content debuts there).

## Goal

A game-agnostic package for **config-driven content**: load typed config from JSON (embedded resource
or disk), and validate JSON against JSON Schema — once, shared, instead of each game replicating
Nullwake's `ConfigLoader` + `ValidateSchemas` tool + MSBuild target. First new consumer is the Hardpoint
tower catalog (next cycle); SpaceGame can adopt later. Nullwake keeps its working per-repo version and
migrates only when convenient (not forced).

## Reference (what we're generalizing)

Nullwake's proven setup: `Data/*.json` config (`EmbeddedResource`) each declaring a `$schema`;
`Data/schemas/*.schema.json`; `ConfigLoader.Load<T>` (disk-first, then embedded, `System.Text.Json`);
a `Nullwake.Tools.ValidateSchemas` console (uses `JsonSchema.Net`) run by a `ValidateJsonSchemas`
MSBuild target `BeforeBuild`. `KhaozEngine.Content` lifts the generic core of this into a package.

## Scope decision recap

- **Config carries data + mechanic *parameters*; behavior stays in C#.** (e.g. a `SplashRadius` number
  in JSON; the code that applies AoE lives in the game.)
- **Schema rule (refined):** hand-authored / config JSON always has a schema, build- **and** test-
  validated so drift fails. Machine-round-tripped JSON (serializer output, save files) is exempt — the
  (de)serializer + a round-trip test is its contract. This package serves the former.

## Package shape

`KhaozEngine.Content` is **pure .NET, no MonoGame dependency** (content/config tooling). It inherits the
shared `Directory.Build.props` (net10.0, version, SourceLink, embedded LICENSE). It adds **one** package
dependency: `JsonSchema.Net` (the library Nullwake's schemas are already written for — the engine's first
non-MonoGame dependency, isolated to this opt-in package).

## Library API

```csharp
namespace KhaozEngine.Content;

public static class ConfigLoader
{
    /// Loads <typeparamref name="T"/> from JSON: <paramref name="diskPath"/> if it exists, else the
    /// embedded resource named <paramref name="resourceName"/> in <paramref name="assembly"/>.
    /// Throws InvalidOperationException if neither is found or deserialization yields null.
    public static T Load<T>(Assembly assembly, string resourceName,
                            string? diskPath = null, JsonSerializerOptions? options = null);
}

public sealed record ValidationReport(bool IsValid, IReadOnlyList<string> Errors);

public static class JsonSchemaValidator
{
    /// Validates an instance JSON string against a schema JSON string.
    public static ValidationReport Validate(string instanceJson, string schemaJson);

    /// Validates every *.json in <paramref name="dataDir"/> against the schema named by its `$schema`
    /// property (resolved relative to dataDir, e.g. "schemas/x.schema.json"). Logs FAIL/WARN lines to
    /// <paramref name="log"/>. Returns true iff all files with a `$schema` validate. Files without a
    /// `$schema` are warned and skipped. Lenient parse (skip comments, allow trailing commas).
    public static bool ValidateDirectory(string dataDir, TextWriter log);
}
```
- `ConfigLoader.Load<T>` generalizes Nullwake's loader: assembly + resource name are parameters (not a
  hardcoded prefix). Default options: `PropertyNameCaseInsensitive`, `ReadCommentHandling = Skip`,
  `AllowTrailingCommas = true`.
- `JsonSchemaValidator` uses `Json.Schema` with `OutputFormat.List`; `ValidateDirectory` mirrors
  Nullwake's tool logic exactly, so it is the single engine used by **both** the build target and tests.

## Build-time enforcement (shared, no per-repo tool)

The package ships:
- A bundled console validator (`KhaozEngine.Content.Validator`, references the library) packed into the
  nupkg under `tools/`. Entry point: `return JsonSchemaValidator.ValidateDirectory(args[0], Console.Out) ? 0 : 1;`.
- `buildTransitive/KhaozEngine.Content.targets` with a `ValidateKhaozContentSchemas` target
  (`BeforeTargets="BeforeBuild"`), **gated on `$(KhaozContentDataDir)` being set** (so referencing the
  package doesn't force validation on projects with no data dir). It runs the bundled validator via
  `dotnet exec` over `$(KhaozContentDataDir)`, failing the build on a non-zero exit.

A consumer gets build-time validation by referencing the package and adding
`<KhaozContentDataDir>$(MSBuildProjectDirectory)/Data</KhaozContentDataDir>`. No replicated tool.

> The `buildTransitive` `.targets` + bundled tool is the one fiddly piece (NuGet build tooling always
> is). If the packaging fights implementation, the fallback is the shipped library + a one-line per-repo
> `<Exec>`/test calling `ValidateDirectory` — the shared validator is the durable win either way. End-to-
> end firing of the `.targets` is proven when Hardpoint adopts it next cycle (this cycle unit-tests the
> validator logic directly).

## Testing (in KhaozEngine.Tests, which references the package)

- `ConfigLoader.Load<T>`: loads from an **embedded** test resource; loads from a **disk** path
  (overrides embedded); **throws** with a clear message when neither exists; respects custom options.
- `JsonSchemaValidator.Validate`: a valid instance → `IsValid` true, no errors; an invalid instance
  (wrong type / missing required) → false with errors.
- `JsonSchemaValidator.ValidateDirectory`: a fixture dir with a valid JSON (+ its `schemas/` schema)
  passes; an invalid JSON fails; a JSON with no `$schema` is skipped (warned), not failed.

## Versioning & release

Unified: bump `Directory.Build.props` to **2.1.0**; repack all packages (Input/Screens/UI/Ecs/Content)
to `2.1.0`; changelog entry; pack to the local feed cumulatively; tag `v2.1.0`, push from `main`, CI
publishes. `KhaozEngine.Content` debuts at `2.1.0`.

## Out of scope / deferred

Hot-reload / file-watching config; non-JSON formats; auto-generating schemas from C# types; migrating
Nullwake onto the package (later, unforced); the Hardpoint tower catalog itself (next cycle, the first
consumer).
