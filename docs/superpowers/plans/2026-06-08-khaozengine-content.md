# KhaozEngine.Content - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. TDD where it fits.

**Goal:** New `KhaozEngine.Content` package: shared config loader + JSON-schema validator (library), a bundled validator tool + `buildTransitive` `.targets` for build-time enforcement, released with the suite at `2.1.0`.

**Architecture:** Pure-.NET package (no MonoGame), depends on `JsonSchema.Net`. Library = `ConfigLoader.Load<T>` + `JsonSchemaValidator` (the single validation engine used by both the build tool and tests). Build enforcement = a bundled console validator + a `.targets` gated on `$(KhaozContentDataDir)`. Unified versioning → all packages bump to `2.1.0`.

**Tech Stack:** C#, .NET 10, `System.Text.Json`, `JsonSchema.Net` (`Json.Schema`), xUnit.

**Companion spec:** `docs/superpowers/specs/2026-06-08-khaozengine-content-design.md`.

**Paths:** Repo root `~/KhaozEngine`. Branch off `main` (`git checkout -b khaozengine-content`). Solution: `KhaozEngine.slnx`.

---

## Task 1: Library + tests

**Files:** Create `KhaozEngine.Content/KhaozEngine.Content.csproj`, `ConfigLoader.cs`, `JsonSchemaValidator.cs`, `README.md`; register in `KhaozEngine.slnx`; modify `KhaozEngine.Tests/KhaozEngine.Tests.csproj`; Test `KhaozEngine.Tests/ContentTests.cs` (+ an embedded fixture).

- [ ] **Step 1: Create the project + register it**

`KhaozEngine.Content/KhaozEngine.Content.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Content</PackageId>
    <Description>Game-agnostic config loading (embedded/disk JSON) + JSON-schema validation, with build-time schema enforcement.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="JsonSchema.Net" Version="9.1.3" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```
Add a one-paragraph `KhaozEngine.Content/README.md`. Register the project in `KhaozEngine.slnx` (add a `<Project Path="KhaozEngine.Content/KhaozEngine.Content.csproj" />` entry alongside the others).

- [ ] **Step 2: `ConfigLoader.cs`**
```csharp
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace KhaozEngine.Content;

/// <summary>Loads typed config from JSON - disk path first (if it exists), else an embedded resource.</summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static T Load<T>(Assembly assembly, string resourceName, string? diskPath = null, JsonSerializerOptions? options = null)
    {
        string json;
        if (diskPath is not null && File.Exists(diskPath))
        {
            json = File.ReadAllText(diskPath);
        }
        else
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                throw new InvalidOperationException(
                    $"Config not found: no file at '{diskPath ?? "(none)"}' and no embedded resource '{resourceName}' in {assembly.GetName().Name}.");
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        return JsonSerializer.Deserialize<T>(json, options ?? Default)
            ?? throw new InvalidOperationException($"Config '{resourceName}' deserialized to null.");
    }
}
```

- [ ] **Step 3: `JsonSchemaValidator.cs`** (verify the `Json.Schema` v9.1.3 API while implementing - adjust the error-extraction to the actual `EvaluationResults` shape)
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace KhaozEngine.Content;

public sealed record ValidationReport(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>Validates JSON instances against JSON Schema (Json.Schema / JsonSchema.Net).</summary>
public static class JsonSchemaValidator
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ValidationReport Validate(string instanceJson, string schemaJson)
    {
        JsonSchema schema = JsonSchema.FromText(schemaJson);
        JsonNode? node = JsonNode.Parse(instanceJson, documentOptions: DocOptions);
        EvaluationResults results = schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid) return new ValidationReport(true, Array.Empty<string>());

        var errors = new List<string>();
        CollectErrors(results, errors);
        if (errors.Count == 0) errors.Add("schema validation failed");
        return new ValidationReport(false, errors);
    }

    /// <summary>Validates every *.json in <paramref name="dataDir"/> against the schema named by its
    /// `$schema` property (resolved relative to dataDir). Logs results. Returns true iff all schema'd files pass.</summary>
    public static bool ValidateDirectory(string dataDir, TextWriter log)
    {
        if (!Directory.Exists(dataDir)) { log.WriteLine($"FAIL  data directory not found: {dataDir}"); return false; }

        bool allValid = true;
        foreach (string jsonFile in Directory.EnumerateFiles(dataDir, "*.json"))
        {
            string fileName = Path.GetFileName(jsonFile);
            string json = File.ReadAllText(jsonFile);
            string? schemaRef;
            try { schemaRef = JsonNode.Parse(json, documentOptions: DocOptions)?["$schema"]?.GetValue<string>(); }
            catch (JsonException ex) { log.WriteLine($"FAIL  {fileName}: invalid JSON -- {ex.Message}"); allValid = false; continue; }

            if (string.IsNullOrWhiteSpace(schemaRef)) { log.WriteLine($"WARN  {fileName}: no $schema, skipping"); continue; }

            string schemaPath = Path.Combine(dataDir, schemaRef);
            if (!File.Exists(schemaPath)) { log.WriteLine($"FAIL  {fileName}: schema not found at {schemaRef}"); allValid = false; continue; }

            ValidationReport report = Validate(json, File.ReadAllText(schemaPath));
            if (report.IsValid) { log.WriteLine($"OK    {fileName}"); }
            else { allValid = false; log.WriteLine($"FAIL  {fileName}:"); foreach (string e in report.Errors) log.WriteLine($"        {e}"); }
        }
        return allValid;
    }

    private static void CollectErrors(EvaluationResults node, List<string> errors)
    {
        if (node.HasErrors && node.Errors is not null)
            foreach (var kv in node.Errors) errors.Add($"{node.InstanceLocation}: {kv.Value}");
        if (node.Details is not null)
            foreach (EvaluationResults child in node.Details) CollectErrors(child, errors);
    }
}
```
> `$schema` convention: a data file points at its schema by a path relative to the data dir, e.g.
> `"$schema": "schemas/towers.schema.json"`.

- [ ] **Step 4: Reference the package from the test project**

In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, add to the `ProjectReference` group:
```xml
    <ProjectReference Include="../KhaozEngine.Content/KhaozEngine.Content.csproj" />
```

- [ ] **Step 5: Tests + embedded fixture**

Create `KhaozEngine.Tests/Fixtures/sample.json` and mark it `EmbeddedResource` in the test csproj:
```xml
  <ItemGroup>
    <EmbeddedResource Include="Fixtures/sample.json" />
  </ItemGroup>
```
`Fixtures/sample.json`: `{ "name": "abc", "count": 3 }`

`KhaozEngine.Tests/ContentTests.cs`:
```csharp
using System.IO;
using System.Reflection;
using KhaozEngine.Content;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class SampleConfig { public string Name { get; set; } = ""; public int Count { get; set; } }

public class ContentTests
{
    private static readonly Assembly Asm = typeof(ContentTests).Assembly;
    private const string SampleResource = "KhaozEngine.Tests.Fixtures.sample.json";

    [Fact]
    public void LoadsFromEmbeddedResource()
    {
        var c = ConfigLoader.Load<SampleConfig>(Asm, SampleResource);
        Assert.Equal("abc", c.Name);
        Assert.Equal(3, c.Count);
    }

    [Fact]
    public void DiskPathOverridesEmbedded()
    {
        string tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "{ \"name\": \"disk\", \"count\": 9 }");
        try
        {
            var c = ConfigLoader.Load<SampleConfig>(Asm, SampleResource, diskPath: tmp);
            Assert.Equal("disk", c.Name);
            Assert.Equal(9, c.Count);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void MissingConfigThrows()
    {
        Assert.Throws<System.InvalidOperationException>(
            () => ConfigLoader.Load<SampleConfig>(Asm, "KhaozEngine.Tests.Nope.json"));
    }

    private const string Schema = """
        { "$schema":"https://json-schema.org/draft/2020-12/schema",
          "type":"object","required":["name","count"],
          "properties":{ "name":{"type":"string"}, "count":{"type":"integer"} } }
        """;

    [Fact]
    public void ValidateAcceptsValidAndRejectsInvalid()
    {
        Assert.True(JsonSchemaValidator.Validate("{ \"name\":\"x\", \"count\":1 }", Schema).IsValid);
        var bad = JsonSchemaValidator.Validate("{ \"name\":\"x\" }", Schema);   // missing required count
        Assert.False(bad.IsValid);
        Assert.NotEmpty(bad.Errors);
    }

    [Fact]
    public void ValidateDirectoryPassesValidFailsInvalidSkipsUnschemad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-content-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "schemas"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "schemas", "s.schema.json"), Schema);
            File.WriteAllText(Path.Combine(dir, "good.json"), "{ \"$schema\":\"schemas/s.schema.json\", \"name\":\"x\", \"count\":1 }");
            File.WriteAllText(Path.Combine(dir, "noschema.json"), "{ \"name\":\"x\" }");
            Assert.True(JsonSchemaValidator.ValidateDirectory(dir, new StringWriter()));   // good passes, noschema skipped

            File.WriteAllText(Path.Combine(dir, "bad.json"), "{ \"$schema\":\"schemas/s.schema.json\", \"name\":\"x\" }");
            Assert.False(JsonSchemaValidator.ValidateDirectory(dir, new StringWriter()));  // bad now fails
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 6: Build + test; commit**
```bash
cd ~/KhaozEngine
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add -A
git commit -m "KhaozEngine.Content: ConfigLoader + JsonSchemaValidator (library + tests)"
```

---

## Task 2: Validator tool + build-time `.targets`

**Files:** Create `KhaozEngine.Content.Validator/KhaozEngine.Content.Validator.csproj`, `Program.cs`; `KhaozEngine.Content/build/KhaozEngine.Content.targets`; modify `KhaozEngine.Content.csproj` (bundle tool + targets), `KhaozEngine.slnx`.

- [ ] **Step 1: Console validator**

`KhaozEngine.Content.Validator/KhaozEngine.Content.Validator.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Content/KhaozEngine.Content.csproj" />
  </ItemGroup>
</Project>
```
`KhaozEngine.Content.Validator/Program.cs`:
```csharp
using KhaozEngine.Content;
if (args.Length < 1) { System.Console.Error.WriteLine("Usage: validate <DataDir>"); return 1; }
return JsonSchemaValidator.ValidateDirectory(args[0], System.Console.Out) ? 0 : 1;
```
Register in `KhaozEngine.slnx`.

- [ ] **Step 2: The `buildTransitive` targets**

`KhaozEngine.Content/build/KhaozEngine.Content.targets`:
```xml
<Project>
  <!-- Runs only if the consumer opts in by setting KhaozContentDataDir. -->
  <Target Name="ValidateKhaozContentSchemas" BeforeTargets="BeforeBuild"
          Condition="'$(KhaozContentDataDir)' != ''">
    <Exec Command="dotnet exec &quot;$(MSBuildThisFileDirectory)../tools/KhaozEngine.Content.Validator.dll&quot; &quot;$(KhaozContentDataDir)&quot;" />
  </Target>
</Project>
```

- [ ] **Step 3: Bundle the tool + targets into the package**

In `KhaozEngine.Content.csproj`, publish the validator and pack its output under `tools/`, and ship the
targets under `build/` and `buildTransitive/`:
```xml
  <ItemGroup>
    <None Include="build/KhaozEngine.Content.targets" Pack="true" PackagePath="build/KhaozEngine.Content.targets" />
    <None Include="build/KhaozEngine.Content.targets" Pack="true" PackagePath="buildTransitive/KhaozEngine.Content.targets" />
  </ItemGroup>

  <!-- Publish the validator (framework-dependent) and include its output under tools/ in the nupkg. -->
  <Target Name="BundleValidator" BeforeTargets="GenerateNuspec" DependsOnTargets="Build">
    <Exec Command="dotnet publish &quot;$(MSBuildThisFileDirectory)../KhaozEngine.Content.Validator/KhaozEngine.Content.Validator.csproj&quot; -c $(Configuration) -o &quot;$(IntermediateOutputPath)validator&quot;" />
    <ItemGroup>
      <None Include="$(IntermediateOutputPath)validator/**/*" Pack="true" PackagePath="tools/" />
    </ItemGroup>
  </Target>
```
> This bundling is the fiddly piece. **Verification:** `dotnet pack KhaozEngine.Content/...` then unzip
> the `.nupkg` and confirm it contains `tools/KhaozEngine.Content.Validator.dll` (+ deps),
> `build/` and `buildTransitive/KhaozEngine.Content.targets`. The target **firing** is verified end-to-end
> when Hardpoint adopts the package next cycle (sets `KhaozContentDataDir` → build validates `towers.json`).
> **Fallback** if the publish-into-pack proves unworkable: drop the bundled tool, keep the library, and
> have consumers enforce via (a) a test calling `JsonSchemaValidator.ValidateDirectory` and (b) a one-line
> per-repo `<Exec>` target - the shared validator is still the win. Note the fallback in the changelog if taken.

- [ ] **Step 4: Verify package contents; commit**
```bash
cd ~/KhaozEngine
dotnet pack KhaozEngine.Content/KhaozEngine.Content.csproj -c Release -o /tmp/ke-content
unzip -l /tmp/ke-content/KhaozEngine.Content.*.nupkg | grep -E "tools/|buildTransitive/|build/"
git add -A
git commit -m "KhaozEngine.Content: bundled schema validator + buildTransitive targets for build-time enforcement"
```

---

## Task 3: Release 2.1.0 (unified)

**Files:** Modify `Directory.Build.props`, `CHANGELOG.md`.

- [ ] **Step 1: Bump shared version** - `Directory.Build.props` `<Version>2.0.0</Version>` → `<Version>2.1.0</Version>`.

- [ ] **Step 2: Changelog** - prepend under the title in `CHANGELOG.md`:
```markdown
## KhaozEngine 2.1.0

- New package **KhaozEngine.Content** (pure .NET, depends on JsonSchema.Net): `ConfigLoader.Load<T>`
  (embedded/disk JSON) and `JsonSchemaValidator` (instance + directory validation), plus a bundled
  validator tool and a `buildTransitive` target that validates a consumer's `Data/` against its schemas
  when `KhaozContentDataDir` is set. Generalizes Nullwake's config pattern; opt-in. All packages bump to
  2.1.0 (unified versioning); no changes to the existing four.
```

- [ ] **Step 3: Test, pack all, commit**
```bash
cd ~/KhaozEngine
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj          # full suite green
for p in Input Screens UI Ecs Content; do dotnet pack KhaozEngine.$p/KhaozEngine.$p.csproj -c Release -o ./local-feed; done
ls local-feed/ | grep "2.1.0.nupkg"   # expect all five
git add -A
git commit -m "Release KhaozEngine 2.1.0 (add KhaozEngine.Content)"
```
> Tag `v2.1.0` and push from `main` after the branch merges (the finishing step).

---

## Self-Review

**Spec coverage:** `ConfigLoader.Load<T>` (embedded/disk/throw) → Task 1; `JsonSchemaValidator` Validate + ValidateDirectory → Task 1; bundled tool + `buildTransitive` targets → Task 2; pure-.NET + JsonSchema.Net dep → Task 1 csproj; unified 2.1.0 across all packages → Task 3; tests for loader + validator → Task 1.

**Placeholder scan:** none in logic; the `Json.Schema` error-extraction is flagged to verify against the installed API version (Task 1 Step 3).

**Risk:** the `tools/` bundling (Task 2 Step 3) is the fiddly part; structural verification + documented fallback included; functional firing proven at Hardpoint adoption.

---

## Execution Handoff

After all tasks green, finish the branch (merge `khaozengine-content` → `main`), tag `v2.1.0`, push so CI publishes all five `2.1.0` packages. Then the **Hardpoint tower catalog** cycle is the first consumer (and the end-to-end proof of the build target).
