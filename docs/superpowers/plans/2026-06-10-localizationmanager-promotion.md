# LocalizationManager Promotion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the copy-pasted `LocalizationManager` from the three games into a new shared, pure-BCL `KhaozEngine.Localization` package with a `ResourceManager`-injected API.

**Architecture:** New net10.0 class-library package (no MonoGame, no other KE deps). One `public class LocalizationManager` with a `ResourceManager`-injected ctor, an instance `GetSupportedCultures()`, a static `SetCulture(string)`, and a `DEFAULT_CULTURE_CODE` const. Headless xUnit tests drive it via a fake `ResourceManager`, so no `.resx` build pipeline is needed.

**Tech Stack:** C# / net10.0, `System.Globalization`, `System.Resources`, xUnit. Solution file is `KhaozEngine.slnx` (slnx format).

**Spec:** `docs/superpowers/specs/2026-06-10-localizationmanager-promotion-design.md`

> **As-built note (post code-review):** two changes were applied after the tasks below were
> executed, and supersede the code blocks here: (1) the public const is `DefaultCultureCode`,
> not `DEFAULT_CULTURE_CODE` (C# convention); (2) `SetCulture` uses
> `ArgumentException.ThrowIfNullOrEmpty(cultureCode)` rather than a single `ArgumentNullException`
> (so empty input throws `ArgumentException`, null throws `ArgumentNullException`). The committed
> code in `KhaozEngine.Localization/` is the source of truth.

---

## File Structure

- `KhaozEngine.Localization/KhaozEngine.Localization.csproj` — new package project (pure BCL).
- `KhaozEngine.Localization/README.md` — packed package readme.
- `KhaozEngine.Localization/LocalizationManager.cs` — the promoted class (sole responsibility: culture discovery + thread-culture mutation).
- `KhaozEngine.slnx` — add the new project.
- `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — add a `ProjectReference` to the new package.
- `KhaozEngine.Tests/LocalizationManagerTests.cs` — tests + test-local `FakeResourceManager`/`FakeResourceSet`.

No version bump or CHANGELOG entry in this plan — that happens once at the end of Batch 1 (see spec "Release handling").

All commands run from the worktree root: `/Users/antonio/KhaozEngine/.claude/worktrees/batch1-promote`.

---

## Task 1: Scaffold the KhaozEngine.Localization package

**Files:**
- Create: `KhaozEngine.Localization/KhaozEngine.Localization.csproj`
- Create: `KhaozEngine.Localization/README.md`
- Modify: `KhaozEngine.slnx`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj`

- [ ] **Step 1: Create the package csproj**

Create `KhaozEngine.Localization/KhaozEngine.Localization.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Localization</PackageId>
    <Description>Game-agnostic localization helper: discover satellite-resource cultures and set the current thread culture. Pure BCL, no MonoGame dependency.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

Note: no `MonoGame.Framework.DesktopGL` reference (unlike the other packages) and no `InternalsVisibleTo` (the public API + test-local fakes need no internal access). Shared `<TargetFramework>net10.0</TargetFramework>` and `<Version>` come from `Directory.Build.props`. Default root namespace is `KhaozEngine.Localization`.

- [ ] **Step 2: Create the package README**

Create `KhaozEngine.Localization/README.md`:

```markdown
# KhaozEngine.Localization

Game-agnostic localization helper. Pure BCL, no MonoGame dependency.

`LocalizationManager` does two things:

- **Discover supported cultures** from a `ResourceManager` you inject (the cultures that
  actually have a satellite resource set), always including the invariant culture.
- **Set the current thread culture** (both `CurrentCulture` and `CurrentUICulture`).

```csharp
using System.Resources;
using KhaozEngine.Localization;

// Point it at YOUR game's resources (your assembly owns the satellite .resx files):
var rm = new ResourceManager("MyGame.Core.Localization.Resources", typeof(MyGameMarker).Assembly);
var loc = new LocalizationManager(rm);

List<CultureInfo> cultures = loc.GetSupportedCultures();

LocalizationManager.SetCulture("en-US");
// Want a fallback instead of an exception on empty input? Do it at the call site:
LocalizationManager.SetCulture(code ?? LocalizationManager.DEFAULT_CULTURE_CODE);
```

`SetCulture` throws on null/empty input. `DEFAULT_CULTURE_CODE` is `"en-US"`.
```

- [ ] **Step 3: Add the project to the solution**

Modify `KhaozEngine.slnx` — add the new project line in alphabetical order, immediately after the `KhaozEngine.Input` line:

```xml
  <Project Path="KhaozEngine.Input/KhaozEngine.Input.csproj" />
  <Project Path="KhaozEngine.Localization/KhaozEngine.Localization.csproj" />
  <Project Path="KhaozEngine.Screens/KhaozEngine.Screens.csproj" />
```

- [ ] **Step 4: Reference the package from the test project**

Modify `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — add the project reference in alphabetical order, after the `KhaozEngine.Input` reference:

```xml
    <ProjectReference Include="../KhaozEngine.Input/KhaozEngine.Input.csproj" />
    <ProjectReference Include="../KhaozEngine.Localization/KhaozEngine.Localization.csproj" />
    <ProjectReference Include="../KhaozEngine.Screens/KhaozEngine.Screens.csproj" />
```

- [ ] **Step 5: Build to verify the empty project compiles and is referenced**

Run: `dotnet build KhaozEngine.Tests/KhaozEngine.Tests.csproj -v q`
Expected: `Build succeeded`, `0 Error(s)`. (The Localization project has no source files yet but still builds to an assembly.)

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Localization/KhaozEngine.Localization.csproj KhaozEngine.Localization/README.md KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj
git commit -m "Scaffold KhaozEngine.Localization package"
```

---

## Task 2: LocalizationManager class + DEFAULT_CULTURE_CODE (TDD)

**Files:**
- Create: `KhaozEngine.Tests/LocalizationManagerTests.cs`
- Create: `KhaozEngine.Localization/LocalizationManager.cs`

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/LocalizationManagerTests.cs`:

```csharp
using KhaozEngine.Localization;
using Xunit;

namespace KhaozEngine.Tests;

public class LocalizationManagerTests
{
    [Fact]
    public void DefaultCultureCode_IsEnUs()
    {
        Assert.Equal("en-US", LocalizationManager.DEFAULT_CULTURE_CODE);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizationManagerTests" -v q`
Expected: FAIL — compile error, `LocalizationManager` does not exist in namespace `KhaozEngine.Localization`.

- [ ] **Step 3: Write the minimal implementation**

Create `KhaozEngine.Localization/LocalizationManager.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Threading;

namespace KhaozEngine.Localization;

/// <summary>
/// Manages localization settings for a game: retrieving the cultures backed by satellite
/// resources, and setting the current thread culture.
/// </summary>
public class LocalizationManager
{
    /// <summary>
    /// The culture code the game defaults to.
    /// </summary>
    public const string DEFAULT_CULTURE_CODE = "en-US";
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizationManagerTests" -v q`
Expected: PASS — 1 passed.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Localization/LocalizationManager.cs KhaozEngine.Tests/LocalizationManagerTests.cs
git commit -m "Add LocalizationManager with DEFAULT_CULTURE_CODE"
```

---

## Task 3: Static SetCulture (TDD)

**Files:**
- Modify: `KhaozEngine.Tests/LocalizationManagerTests.cs`
- Modify: `KhaozEngine.Localization/LocalizationManager.cs`

- [ ] **Step 1: Write the failing tests**

Add these three tests inside the `LocalizationManagerTests` class in `KhaozEngine.Tests/LocalizationManagerTests.cs` (and add `using System;`, `using System.Globalization;`, `using System.Threading;` at the top of the file):

```csharp
    [Fact]
    public void SetCulture_ValidCode_SetsCurrentAndUiCulture()
    {
        CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
        CultureInfo originalUiCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            LocalizationManager.SetCulture("fr-FR");

            Assert.Equal("fr-FR", Thread.CurrentThread.CurrentCulture.Name);
            Assert.Equal("fr-FR", Thread.CurrentThread.CurrentUICulture.Name);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
            Thread.CurrentThread.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void SetCulture_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LocalizationManager.SetCulture(null!));
    }

    [Fact]
    public void SetCulture_Empty_Throws()
    {
        // Asserted on the base ArgumentException; implementation throws ArgumentNullException.
        // ThrowsAny (not Throws) because xUnit 2.x Throws<T> is exact-type.
        Assert.ThrowsAny<ArgumentException>(() => LocalizationManager.SetCulture(""));
    }
```

Note: `Assert.Throws<ArgumentNullException>` for the null case is exact. For the empty case, assert on the base type with `Assert.ThrowsAny<ArgumentException>` — xUnit 2.x `Assert.Throws<T>` uses **exact** type matching and would reject the derived `ArgumentNullException`; `ThrowsAny<T>` is the one that accepts subtypes.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizationManagerTests" -v q`
Expected: FAIL — compile error, `LocalizationManager` does not contain a definition for `SetCulture`.

- [ ] **Step 3: Add the SetCulture method**

In `KhaozEngine.Localization/LocalizationManager.cs`, add this method inside the class, after the `DEFAULT_CULTURE_CODE` const:

```csharp
    /// <summary>
    /// Sets the current thread's culture and UI culture from a culture code (e.g. "en-US").
    /// </summary>
    /// <param name="cultureCode">A non-empty culture code.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cultureCode"/> is null or empty.</exception>
    public static void SetCulture(string cultureCode)
    {
        if (string.IsNullOrEmpty(cultureCode))
            throw new ArgumentNullException(nameof(cultureCode), "A culture code must be provided.");

        CultureInfo culture = new CultureInfo(cultureCode);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizationManagerTests" -v q`
Expected: PASS — 4 passed.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Localization/LocalizationManager.cs KhaozEngine.Tests/LocalizationManagerTests.cs
git commit -m "Add LocalizationManager.SetCulture"
```

---

## Task 4: Ctor + GetSupportedCultures with fake ResourceManager (TDD)

**Files:**
- Modify: `KhaozEngine.Tests/LocalizationManagerTests.cs`
- Modify: `KhaozEngine.Localization/LocalizationManager.cs`

- [ ] **Step 1: Write the failing tests + fakes**

Add the following tests inside the `LocalizationManagerTests` class, then add the two fake classes at the bottom of `KhaozEngine.Tests/LocalizationManagerTests.cs` (top-level, after the test class). Add `using System.Collections.Generic;` and `using System.Resources;` to the file's usings.

Tests (inside the class):

```csharp
    [Fact]
    public void GetSupportedCultures_ReturnsCulturesWithResourceSets_PlusInvariant()
    {
        var rm = new FakeResourceManager(
            supported: new HashSet<string> { "fr-FR", "es-ES" },
            throwing: new HashSet<string> { "de-DE" });
        var manager = new LocalizationManager(rm);

        List<CultureInfo> result = manager.GetSupportedCultures();

        Assert.Contains(result, c => c.Name == "fr-FR");
        Assert.Contains(result, c => c.Name == "es-ES");
        Assert.Contains(result, c => c.Equals(CultureInfo.InvariantCulture));
        Assert.DoesNotContain(result, c => c.Name == "de-DE");
    }

    [Fact]
    public void GetSupportedCultures_NoResourceSets_ReturnsOnlyInvariant()
    {
        var rm = new FakeResourceManager(
            supported: new HashSet<string>(),
            throwing: new HashSet<string>());
        var manager = new LocalizationManager(rm);

        List<CultureInfo> result = manager.GetSupportedCultures();

        Assert.Single(result);
        Assert.Equal(CultureInfo.InvariantCulture, result[0]);
    }

    [Fact]
    public void Ctor_NullResourceManager_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalizationManager(null!));
    }
```

Fakes (top-level classes at the bottom of the file):

```csharp
/// <summary>A non-null sentinel ResourceSet; never read, only checked for non-null.</summary>
internal sealed class FakeResourceSet : ResourceSet
{
    // Uses ResourceSet's protected parameterless ctor.
}

/// <summary>
/// Test double: returns a sentinel resource set for "supported" cultures, throws
/// MissingManifestResourceException for "throwing" cultures, and null for everything else.
/// </summary>
internal sealed class FakeResourceManager : ResourceManager
{
    private readonly HashSet<string> _supported;
    private readonly HashSet<string> _throwing;

    public FakeResourceManager(HashSet<string> supported, HashSet<string> throwing)
    {
        _supported = supported;
        _throwing = throwing;
    }

    public override ResourceSet? GetResourceSet(CultureInfo culture, bool createIfNotExists, bool tryParents)
    {
        if (_throwing.Contains(culture.Name))
            throw new MissingManifestResourceException($"no resources for {culture.Name}");
        if (_supported.Contains(culture.Name))
            return new FakeResourceSet();
        return null;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizationManagerTests" -v q`
Expected: FAIL — compile error: `LocalizationManager` has no constructor taking one argument and no `GetSupportedCultures` method.

- [ ] **Step 3: Add the ctor, field, and GetSupportedCultures**

In `KhaozEngine.Localization/LocalizationManager.cs`, add a backing field, a constructor, and the `GetSupportedCultures` method. The full file now reads:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Threading;

namespace KhaozEngine.Localization;

/// <summary>
/// Manages localization settings for a game: retrieving the cultures backed by satellite
/// resources, and setting the current thread culture.
/// </summary>
public class LocalizationManager
{
    /// <summary>
    /// The culture code the game defaults to.
    /// </summary>
    public const string DEFAULT_CULTURE_CODE = "en-US";

    private readonly ResourceManager _resourceManager;

    /// <summary>
    /// Creates a manager that discovers supported cultures from the given resource manager.
    /// The resource manager must be built against the assembly that owns the satellite
    /// resources (typically the game's own assembly).
    /// </summary>
    /// <param name="resourceManager">The resource manager to probe for localized resource sets.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="resourceManager"/> is null.</exception>
    public LocalizationManager(ResourceManager resourceManager)
    {
        _resourceManager = resourceManager ?? throw new ArgumentNullException(nameof(resourceManager));
    }

    /// <summary>
    /// Sets the current thread's culture and UI culture from a culture code (e.g. "en-US").
    /// </summary>
    /// <param name="cultureCode">A non-empty culture code.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cultureCode"/> is null or empty.</exception>
    public static void SetCulture(string cultureCode)
    {
        if (string.IsNullOrEmpty(cultureCode))
            throw new ArgumentNullException(nameof(cultureCode), "A culture code must be provided.");

        CultureInfo culture = new CultureInfo(cultureCode);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    /// <summary>
    /// Retrieves the specific cultures that have a localized resource set in the injected
    /// resource manager, always including <see cref="CultureInfo.InvariantCulture"/> (the base,
    /// non-localized resources) as the last entry.
    /// </summary>
    /// <returns>The list of supported cultures.</returns>
    public List<CultureInfo> GetSupportedCultures()
    {
        List<CultureInfo> supportedCultures = new List<CultureInfo>();

        CultureInfo[] cultures = CultureInfo.GetCultures(CultureTypes.SpecificCultures);

        foreach (CultureInfo culture in cultures)
        {
            try
            {
                ResourceSet? resourceSet = _resourceManager.GetResourceSet(culture, true, false);
                if (resourceSet != null)
                {
                    supportedCultures.Add(culture);
                }
            }
            catch (MissingManifestResourceException)
            {
                // No .resx for this culture; skip it.
            }
        }

        // Always add the default (invariant) culture - the base .resx file.
        supportedCultures.Add(CultureInfo.InvariantCulture);

        return supportedCultures;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LocalizationManagerTests" -v q`
Expected: PASS — 7 passed.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Localization/LocalizationManager.cs KhaozEngine.Tests/LocalizationManagerTests.cs
git commit -m "Add LocalizationManager.GetSupportedCultures"
```

---

## Task 5: Full suite green + final check

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -v q`
Expected: PASS — 164 passed (157 baseline + 7 new), 0 failed.

- [ ] **Step 2: Build the package project in isolation (confirm no stray deps)**

Run: `dotnet build KhaozEngine.Localization/KhaozEngine.Localization.csproj -v q`
Expected: `Build succeeded`, `0 Error(s)`. Confirms the package compiles with no MonoGame / KE dependencies.

No commit needed if nothing changed (verification only).

---

## Notes for the next batch items / release

- Deferred to end-of-batch (do NOT do here): bump `<Version>` in `Directory.Build.props`, add a `CHANGELOG.md` entry, update `docs/CONSUMERS.md`, run `dotnet pack -c Release -o ./local-feed`, then the per-consumer bump-and-adopt PRs (each game deletes its local `LocalizationManager.cs` and references `KhaozEngine.Localization`; SpaceGame's fallback call site becomes `SetCulture(code ?? LocalizationManager.DEFAULT_CULTURE_CODE)`).
