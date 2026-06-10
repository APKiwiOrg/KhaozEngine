# AppDataPaths Promotion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote SpaceGame's OS-correct app-data path resolver into `KhaozEngine.App` as an instance class `AppDataPaths(appFolderName)`, with an internal injected-environment seam so every OS branch is headless-testable.

**Architecture:** `AppDataPaths` resolves the base directory through an `internal IAppDataEnvironment` (default impl wraps `OperatingSystem.*` + `Environment.*`); a second internal ctor injects a fake environment in tests. Exposes `BaseDirectory` (lazy-resolved, created, cached) plus convenience file paths. Pure BCL, no MonoGame.

**Tech Stack:** C# / net10.0, `System.IO`, xUnit. Package `KhaozEngine.App` already exists (from Batch 1 item 3).

**Spec:** `docs/superpowers/specs/2026-06-10-appdatapaths-promotion-design.md`

---

## File Structure

- `KhaozEngine.App/IAppDataEnvironment.cs` — internal OS/env abstraction (test seam).
- `KhaozEngine.App/SystemAppDataEnvironment.cs` — internal default impl over the real OS/env.
- `KhaozEngine.App/AppDataPaths.cs` — the public resolver class.
- `KhaozEngine.App/KhaozEngine.App.csproj` — add `InternalsVisibleTo KhaozEngine.Tests`.
- `KhaozEngine.Tests/AppDataPathsTests.cs` — tests + `FakeAppDataEnvironment` double + temp-dir helpers.

No version bump / CHANGELOG / pack — deferred to the single end-of-batch 3.1.0 release. No slnx or Tests-csproj project-reference changes (the package + reference already exist).

All commands run from the worktree root: `/Users/antonio/KhaozEngine/.claude/worktrees/batch1-promote`.

---

## Task 1: Internal environment seam

**Files:**
- Create: `KhaozEngine.App/IAppDataEnvironment.cs`
- Create: `KhaozEngine.App/SystemAppDataEnvironment.cs`
- Modify: `KhaozEngine.App/KhaozEngine.App.csproj`

- [ ] **Step 1: Add InternalsVisibleTo to the package csproj**

Modify `KhaozEngine.App/KhaozEngine.App.csproj` — add a second `ItemGroup` so tests can see the internal seam. Final file:

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
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the environment interface**

Create `KhaozEngine.App/IAppDataEnvironment.cs`:

```csharp
using System;

namespace KhaozEngine.App;

/// <summary>
/// Abstraction over the OS / environment facts used to resolve the app-data directory. Internal:
/// games never see it. Exists so <see cref="AppDataPaths"/>'s OS-branching resolution can be
/// exercised deterministically in headless tests via a fake implementation.
/// </summary>
internal interface IAppDataEnvironment
{
    /// <summary>True when running on Windows.</summary>
    bool IsWindows { get; }

    /// <summary>True when running on macOS.</summary>
    bool IsMacOS { get; }

    /// <summary>True when running on Linux.</summary>
    bool IsLinux { get; }

    /// <summary>Maps to <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>.</summary>
    string GetFolderPath(Environment.SpecialFolder folder);

    /// <summary>Maps to <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    string? GetEnvironmentVariable(string variable);
}
```

- [ ] **Step 3: Create the default implementation**

Create `KhaozEngine.App/SystemAppDataEnvironment.cs`:

```csharp
using System;

namespace KhaozEngine.App;

/// <summary>Default <see cref="IAppDataEnvironment"/> over the real operating system and environment.</summary>
internal sealed class SystemAppDataEnvironment : IAppDataEnvironment
{
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsMacOS => OperatingSystem.IsMacOS();
    public bool IsLinux => OperatingSystem.IsLinux();
    public string GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
    public string? GetEnvironmentVariable(string variable) => Environment.GetEnvironmentVariable(variable);
}
```

- [ ] **Step 4: Build**

Run: `dotnet build KhaozEngine.Tests/KhaozEngine.Tests.csproj -v q`
Expected: `Build succeeded`, `0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.App/IAppDataEnvironment.cs KhaozEngine.App/SystemAppDataEnvironment.cs KhaozEngine.App/KhaozEngine.App.csproj
git commit -m "Add internal IAppDataEnvironment seam to KhaozEngine.App"
```

---

## Task 2: AppDataPaths ctor + BaseDirectory resolution (TDD)

**Files:**
- Create: `KhaozEngine.Tests/AppDataPathsTests.cs`
- Create: `KhaozEngine.App/AppDataPaths.cs`

- [ ] **Step 1: Write the failing tests + fake environment**

Create `KhaozEngine.Tests/AppDataPathsTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests;

public class AppDataPathsTests
{
    private const string AppFolder = "MyGame";

    [Fact]
    public void BaseDirectory_Windows_UsesApplicationData()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, AppFolder), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_MacOS_UsesApplicationData()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsMacOS = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, AppFolder), paths.BaseDirectory);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_Linux_UsesXdgDataHomeWhenSet()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsLinux = true };
            env.EnvVars["XDG_DATA_HOME"] = root;

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, AppFolder), paths.BaseDirectory);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_Linux_FallsBackToHomeLocalShareWhenNoXdg()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsLinux = true };
            env.EnvVars["HOME"] = root; // XDG_DATA_HOME deliberately absent

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, ".local", "share", AppFolder), paths.BaseDirectory);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_NoOsMatch_FallsBackToLocalApplicationData()
    {
        string root = NewTempRoot();
        try
        {
            // No OS flag set; primary branch never taken.
            var env = new FakeAppDataEnvironment();
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = root;

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, AppFolder), paths.BaseDirectory);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_LastResort_UsesUserProfileDotFolder()
    {
        string root = NewTempRoot();
        try
        {
            // Nothing resolves except UserProfile.
            var env = new FakeAppDataEnvironment();
            env.Folders[Environment.SpecialFolder.UserProfile] = root;

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(root, "." + AppFolder.ToLowerInvariant()), paths.BaseDirectory);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_ResolvesOnceAndCaches()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(AppFolder, env);

            string first = paths.BaseDirectory;
            string second = paths.BaseDirectory;

            Assert.Equal(first, second);
            Assert.Equal(1, env.GetFolderPathCalls); // resolution happened exactly once
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_InvalidAppFolderName_Throws(string? badName)
    {
        Assert.Throws<ArgumentException>(() => new AppDataPaths(badName!, new FakeAppDataEnvironment()));
    }

    // --- helpers ---

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "KhaozEngineAppDataTests", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best-effort */ }
    }
}

/// <summary>Test double for <see cref="IAppDataEnvironment"/> — all facts are settable.</summary>
internal sealed class FakeAppDataEnvironment : IAppDataEnvironment
{
    public bool IsWindows { get; set; }
    public bool IsMacOS { get; set; }
    public bool IsLinux { get; set; }
    public Dictionary<Environment.SpecialFolder, string> Folders { get; } = new();
    public Dictionary<string, string?> EnvVars { get; } = new();
    public int GetFolderPathCalls { get; private set; }

    public string GetFolderPath(Environment.SpecialFolder folder)
    {
        GetFolderPathCalls++;
        return Folders.TryGetValue(folder, out string? value) ? value : string.Empty;
    }

    public string? GetEnvironmentVariable(string variable) =>
        EnvVars.TryGetValue(variable, out string? value) ? value : null;
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AppDataPathsTests" -v q`
Expected: FAIL — compile error, `AppDataPaths` does not exist in namespace `KhaozEngine.App`.

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.App/AppDataPaths.cs`:

```csharp
using System;
using System.IO;

namespace KhaozEngine.App;

/// <summary>
/// Resolves the OS-correct application-data directory (for saves, settings, logs) under a given
/// app folder name, and exposes conventional file paths inside it.
/// <list type="bullet">
///   <item>Windows: <c>%APPDATA%\&lt;appFolderName&gt;\</c></item>
///   <item>macOS: <c>~/Library/Application Support/&lt;appFolderName&gt;/</c></item>
///   <item>Linux: <c>$XDG_DATA_HOME/&lt;appFolderName&gt;/</c> (else <c>~/.local/share/&lt;appFolderName&gt;/</c>)</item>
/// </list>
/// </summary>
public sealed class AppDataPaths
{
    private readonly string appFolderName;
    private readonly IAppDataEnvironment environment;
    private string? resolvedBaseDir;

    /// <summary>Creates a resolver for the given app folder name using the real OS environment.</summary>
    /// <exception cref="ArgumentException"><paramref name="appFolderName"/> is null, empty, or whitespace.</exception>
    public AppDataPaths(string appFolderName)
        : this(appFolderName, new SystemAppDataEnvironment())
    {
    }

    internal AppDataPaths(string appFolderName, IAppDataEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(appFolderName))
        {
            throw new ArgumentException("An app folder name must be provided.", nameof(appFolderName));
        }

        this.appFolderName = appFolderName;
        this.environment = environment;
    }

    /// <summary>The root app-data directory. Resolved once and created (if absent) on first access.</summary>
    public string BaseDirectory
    {
        get
        {
            if (resolvedBaseDir is not null)
            {
                return resolvedBaseDir;
            }

            resolvedBaseDir = ResolveBaseDirectory();
            Directory.CreateDirectory(resolvedBaseDir);
            return resolvedBaseDir;
        }
    }

    /// <summary>Full path to <c>save.json</c> in the app-data directory.</summary>
    public string SaveFilePath => Path.Combine(BaseDirectory, "save.json");

    /// <summary>Full path to <c>settings.json</c> in the app-data directory.</summary>
    public string SettingsFilePath => Path.Combine(BaseDirectory, "settings.json");

    /// <summary>Full path to <c>game.log</c> in the app-data directory.</summary>
    public string LogFilePath => Path.Combine(BaseDirectory, "game.log");

    /// <summary>Full path to <c>game.prev.log</c> in the app-data directory.</summary>
    public string PreviousLogFilePath => Path.Combine(BaseDirectory, "game.prev.log");

    /// <summary>Full path to <paramref name="fileName"/> in the app-data directory.</summary>
    public string GetFilePath(string fileName) => Path.Combine(BaseDirectory, fileName);

    private string ResolveBaseDirectory()
    {
        if (environment.IsWindows)
        {
            string appData = environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                return Path.Combine(appData, appFolderName);
            }
        }
        else if (environment.IsMacOS)
        {
            string appSupport = environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appSupport))
            {
                return Path.Combine(appSupport, appFolderName);
            }
        }
        else if (environment.IsLinux)
        {
            string? xdgDataHome = environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                return Path.Combine(xdgDataHome, appFolderName);
            }

            string? home = environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".local", "share", appFolderName);
            }
        }

        string localAppData = environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, appFolderName);
        }

        string homeDir = environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, $".{appFolderName.ToLowerInvariant()}");
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AppDataPathsTests" -v q`
Expected: PASS — 10 passed (6 branch + 1 caching + 3 theory cases).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.App/AppDataPaths.cs KhaozEngine.Tests/AppDataPathsTests.cs
git commit -m "Add KhaozEngine.App.AppDataPaths with OS-correct resolution"
```

---

## Task 3: Convenience file-path members (TDD)

**Files:**
- Modify: `KhaozEngine.Tests/AppDataPathsTests.cs`

(The file-path members are already implemented in Task 2's `AppDataPaths.cs`; this task adds the tests that pin their contract. Write the tests, run them, and they should pass against the existing implementation — this is the verification half of TDD for those members.)

- [ ] **Step 1: Add the file-path tests**

Add these tests inside the `AppDataPathsTests` class (after the existing tests, before the helpers):

```csharp
    [Fact]
    public void FilePaths_ComposeOffBaseDirectory()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(AppFolder, env);
            string baseDir = paths.BaseDirectory;

            Assert.Equal(Path.Combine(baseDir, "save.json"), paths.SaveFilePath);
            Assert.Equal(Path.Combine(baseDir, "settings.json"), paths.SettingsFilePath);
            Assert.Equal(Path.Combine(baseDir, "game.log"), paths.LogFilePath);
            Assert.Equal(Path.Combine(baseDir, "game.prev.log"), paths.PreviousLogFilePath);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void GetFilePath_ComposesArbitraryNameOffBaseDirectory()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(AppFolder, env);

            Assert.Equal(Path.Combine(paths.BaseDirectory, "custom.dat"), paths.GetFilePath("custom.dat"));
        }
        finally { Cleanup(root); }
    }
```

- [ ] **Step 2: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AppDataPathsTests" -v q`
Expected: PASS — 12 passed (10 from Task 2 + 2 new).

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/AppDataPathsTests.cs
git commit -m "Add AppDataPaths file-path member tests"
```

---

## Task 4: Full suite green + isolated build

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -v q`
Expected: PASS — baseline (189) + 12 new = 201, 0 failed. (Confirm the baseline at the start; the delta is +12.)

- [ ] **Step 2: Build the package project in isolation (confirm no stray deps)**

Run: `dotnet build KhaozEngine.App/KhaozEngine.App.csproj -v q`
Expected: `Build succeeded`, `0 Error(s)`. Confirms the package still compiles pure-BCL with no MonoGame / KE deps.

No commit needed (verification only).

---

## Notes for the release / adopt phase (do NOT do here)

- End-of-batch: bump `<Version>` 3.0.0 → 3.1.0, one `CHANGELOG.md` entry for the batch, update `docs/CONSUMERS.md`, `dotnet pack -c Release -o ./local-feed`.
- Adopt: SpaceGame is a near drop-in (replace its `AppDataPaths` static usages with an instance, or a thin static facade holding `new AppDataPaths("SpaceGame")`). Nullwake replaces its three inlined `LocalApplicationData/Nullwake` sites — NOTE its data dir moves to the OS-correct location, so plan a one-time save/log migration or accept a reset. Hardpoint can adopt when it grows persistence.
