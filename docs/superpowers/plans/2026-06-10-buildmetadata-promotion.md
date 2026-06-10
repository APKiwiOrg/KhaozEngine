# BuildMetadata Promotion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the duplicated `AssemblyMetadataAttribute` reader from the three games' `BuildConfig` into a new shared, pure-BCL `KhaozEngine.App` package as `BuildMetadata.Read(key, fallback, params Assembly?[])`.

**Architecture:** New net10.0 class-library package (no MonoGame, no other KE deps) holding one `public static class BuildMetadata` with a single `Read` method: probe each supplied assembly in order (skipping nulls), return the first non-whitespace `AssemblyMetadataAttribute` value whose key matches, else the fallback. Headless xUnit tests drive it via `[assembly: AssemblyMetadata(...)]` fixtures in the test assembly.

**Tech Stack:** C# / net10.0, `System.Reflection`, xUnit. Solution file is `KhaozEngine.slnx` (slnx format).

**Spec:** `docs/superpowers/specs/2026-06-10-buildmetadata-promotion-design.md`

---

## File Structure

- `KhaozEngine.App/KhaozEngine.App.csproj` — new package project (pure BCL).
- `KhaozEngine.App/README.md` — packed package readme.
- `KhaozEngine.App/BuildMetadata.cs` — the helper (sole responsibility: read assembly metadata with fallback).
- `KhaozEngine.slnx` — add the new project.
- `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — add a `ProjectReference` to the new package.
- `KhaozEngine.Tests/BuildMetadataFixtures.cs` — `[assembly: AssemblyMetadata(...)]` test fixtures.
- `KhaozEngine.Tests/BuildMetadataTests.cs` — the tests.

No version bump / CHANGELOG / pack in this plan — deferred to the single end-of-batch 3.1.0 release.

All commands run from the worktree root: `/Users/antonio/KhaozEngine/.claude/worktrees/batch1-promote`.

---

## Task 1: Scaffold the KhaozEngine.App package

**Files:**
- Create: `KhaozEngine.App/KhaozEngine.App.csproj`
- Create: `KhaozEngine.App/README.md`
- Modify: `KhaozEngine.slnx`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj`

- [ ] **Step 1: Create the package csproj**

Create `KhaozEngine.App/KhaozEngine.App.csproj` (mirrors `KhaozEngine.Localization` — pure BCL, no MonoGame, no `InternalsVisibleTo`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.App</PackageId>
    <Description>Game-agnostic app identity / runtime helpers. BuildMetadata reads AssemblyMetadata items at runtime. Pure BCL, no MonoGame dependency.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

`<TargetFramework>net10.0</TargetFramework>`, `<Version>`, `<Nullable>enable</Nullable>` are inherited from `Directory.Build.props`. Default root namespace is `KhaozEngine.App`.

- [ ] **Step 2: Create the package README**

Create `KhaozEngine.App/README.md`:

```markdown
# KhaozEngine.App

Game-agnostic app identity / runtime helpers. Pure BCL, no MonoGame dependency.

`BuildMetadata.Read` reads `AssemblyMetadata` items (emitted by a project's `Directory.Build.props`)
back at runtime, so a game can surface its own version / build name / bundle id without re-deriving
them. The caller passes the assemblies to probe — the engine never guesses via
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
```

- [ ] **Step 3: Add the project to the solution**

Modify `KhaozEngine.slnx` — add the new project as the FIRST `<Project>` line (alphabetical: "App" sorts before "Content"):

```xml
<Solution>
  <Project Path="KhaozEngine.App/KhaozEngine.App.csproj" />
  <Project Path="KhaozEngine.Content/KhaozEngine.Content.csproj" />
```

- [ ] **Step 4: Reference the package from the test project**

Modify `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — add the project reference as the FIRST `<ProjectReference>` (before Content):

```xml
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.App/KhaozEngine.App.csproj" />
    <ProjectReference Include="../KhaozEngine.Content/KhaozEngine.Content.csproj" />
```

- [ ] **Step 5: Build to verify the empty project compiles and is referenced**

Run: `dotnet build KhaozEngine.Tests/KhaozEngine.Tests.csproj -v q`
Expected: `Build succeeded`, `0 Error(s)`. (The App project has no source files yet but still builds.)

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.App/KhaozEngine.App.csproj KhaozEngine.App/README.md KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj
git commit -m "Scaffold KhaozEngine.App package"
```

---

## Task 2: BuildMetadata.Read (TDD)

**Files:**
- Create: `KhaozEngine.Tests/BuildMetadataFixtures.cs`
- Create: `KhaozEngine.Tests/BuildMetadataTests.cs`
- Create: `KhaozEngine.App/BuildMetadata.cs`

- [ ] **Step 1: Write the assembly-metadata fixtures**

Create `KhaozEngine.Tests/BuildMetadataFixtures.cs` (assembly-level attributes; this file contains only attributes, no namespace):

```csharp
using System.Reflection;

// Fixtures for BuildMetadataTests: real AssemblyMetadata items baked into the test assembly.
[assembly: AssemblyMetadata("KhaozEngine.Tests.BuildMetadata.Present", "present-value")]
[assembly: AssemblyMetadata("KhaozEngine.Tests.BuildMetadata.Blank", "   ")]
```

- [ ] **Step 2: Write the failing tests**

Create `KhaozEngine.Tests/BuildMetadataTests.cs`:

```csharp
using System;
using System.Reflection;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests;

public class BuildMetadataTests
{
    private const string PresentKey = "KhaozEngine.Tests.BuildMetadata.Present";
    private const string BlankKey = "KhaozEngine.Tests.BuildMetadata.Blank";

    private static Assembly TestAssembly => typeof(BuildMetadataTests).Assembly;

    [Fact]
    public void Read_KeyPresent_ReturnsValue()
    {
        Assert.Equal("present-value", BuildMetadata.Read(PresentKey, "fallback", TestAssembly));
    }

    [Fact]
    public void Read_KeyAbsent_ReturnsFallback()
    {
        Assert.Equal("fallback", BuildMetadata.Read("no.such.key", "fallback", TestAssembly));
    }

    [Fact]
    public void Read_FallsThroughMissingAssemblyToLaterAssembly()
    {
        // First assembly (corelib) lacks the key; second (test asm) has it.
        Assert.Equal(
            "present-value",
            BuildMetadata.Read(PresentKey, "fallback", typeof(object).Assembly, TestAssembly));
    }

    [Fact]
    public void Read_NullAssembly_IsSkipped()
    {
        Assert.Equal("present-value", BuildMetadata.Read(PresentKey, "fallback", null, TestAssembly));
    }

    [Fact]
    public void Read_WhitespaceValue_ReturnsFallback()
    {
        Assert.Equal("fallback", BuildMetadata.Read(BlankKey, "fallback", TestAssembly));
    }

    [Fact]
    public void Read_NoAssemblies_ReturnsFallback()
    {
        Assert.Equal("fallback", BuildMetadata.Read("any.key", "fallback"));
    }

    [Fact]
    public void Read_NullKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BuildMetadata.Read(null!, "fallback", TestAssembly));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~BuildMetadataTests" -v q`
Expected: FAIL — compile error, `BuildMetadata` does not exist in namespace `KhaozEngine.App`.

- [ ] **Step 4: Write the implementation**

Create `KhaozEngine.App/BuildMetadata.cs`:

```csharp
using System;
using System.Reflection;

namespace KhaozEngine.App;

/// <summary>
/// Reads <see cref="AssemblyMetadataAttribute"/> items (emitted into an assembly by its
/// <c>Directory.Build.props</c>) back at runtime, so a game can surface its own build identity
/// without re-deriving it. The caller supplies the assemblies to probe; this type never calls
/// <see cref="Assembly.GetExecutingAssembly"/> (which would resolve to the engine assembly).
/// </summary>
public static class BuildMetadata
{
    /// <summary>
    /// Probes <paramref name="assemblies"/> in order (skipping null entries) for an
    /// <see cref="AssemblyMetadataAttribute"/> whose <see cref="AssemblyMetadataAttribute.Key"/>
    /// equals <paramref name="key"/> (ordinal) with a non-whitespace value. Returns the first such
    /// value, or <paramref name="fallback"/> if none match.
    /// </summary>
    /// <param name="key">The metadata key to look up.</param>
    /// <param name="fallback">Returned verbatim when no assembly yields a value.</param>
    /// <param name="assemblies">Assemblies to probe, in priority order; null entries are skipped.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    public static string Read(string key, string fallback, params Assembly?[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(key);

        foreach (Assembly? assembly in assemblies)
        {
            if (assembly is null)
            {
                continue;
            }

            if (TryReadFrom(assembly, key, out string value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static bool TryReadFrom(Assembly assembly, string key, out string value)
    {
        object[] attributes = assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false);
        for (int i = 0; i < attributes.Length; i++)
        {
            if (attributes[i] is not AssemblyMetadataAttribute metadata)
            {
                continue;
            }

            if (!string.Equals(metadata.Key, key, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(metadata.Value))
            {
                break;
            }

            value = metadata.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~BuildMetadataTests" -v q`
Expected: PASS — 7 passed.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.App/BuildMetadata.cs KhaozEngine.Tests/BuildMetadataFixtures.cs KhaozEngine.Tests/BuildMetadataTests.cs
git commit -m "Add KhaozEngine.App.BuildMetadata.Read"
```

---

## Task 3: Full suite green + isolated build check

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -v q`
Expected: PASS — baseline (~182) + 7 new, 0 failed. (Confirm the baseline count at the start of work; the delta is +7.)

- [ ] **Step 2: Build the package project in isolation (confirm no stray deps)**

Run: `dotnet build KhaozEngine.App/KhaozEngine.App.csproj -v q`
Expected: `Build succeeded`, `0 Error(s)`. Confirms the package compiles with no MonoGame / KE dependencies.

No commit needed (verification only).

---

## Notes for the release / adopt phase (do NOT do here)

- End-of-batch: bump `<Version>` 3.0.0 → 3.1.0 in `Directory.Build.props`, one `CHANGELOG.md` entry for the whole batch, update `docs/CONSUMERS.md`, `dotnet pack -c Release -o ./local-feed`.
- Per-consumer adopt: each game's `BuildConfig` keeps its typed properties but replaces both private methods with `BuildMetadata.Read(key, fallback, typeof(BuildConfig).Assembly, Assembly.GetEntryAssembly())` (SpaceGame: `typeof(SpaceGameGame).Assembly` as the first probe). Hardpoint can drop its `System.Reflection`-fully-qualified workaround.
