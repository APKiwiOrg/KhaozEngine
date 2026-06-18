# Cross-platform storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `AppDataPaths` publisher-rooted and mobile-aware, and add a `GameStorage` facade so games stop hand-wiring paths + queue + storages.

**Architecture:** `AppDataPaths` (in `KhaozEngine.App`) changes to a `(publisher, appName)` constructor and resolves `<os-base>/<publisher>/<appName>/` with new Android/iOS branches via the internal `IAppDataEnvironment` seam. A new `GameStorage` facade (in `KhaozEngine.Persistence`, which already depends on `KhaozEngine.App`) assembles the publisher-rooted paths, a shared `PersistenceQueue`, a `FileSettingsStorage`, and an optional `SaveEncoder`, exposing generic typed `Save<T>`/`Load<T>` (plaintext or transparently encoded).

**Tech Stack:** C# / net10.0, xUnit, BCL-only (no MonoGame in these packages). Spec: `docs/superpowers/specs/2026-06-18-crossplatform-storage-design.md`.

**Release target:** 5.x line `<KhaozEngine5xVersion>` 5.58.0 → **5.59.0** (breaking-as-minor).

**Worktree:** This is engine work that ships a package (public API + tests + release ritual), so per `CLAUDE.md` it MUST run in a dedicated worktree, not loose on `main`. Create it with the native `EnterWorktree` tool, branch `feature/crossplatform-storage`, before Task 1.

**Test command:** `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Single test: append `--filter "FullyQualifiedName~<ClassName>.<TestName>"`.

---

## File Structure

- Modify `KhaozEngine.App/IAppDataEnvironment.cs` - add `IsAndroid` / `IsIOS`.
- Modify `KhaozEngine.App/SystemAppDataEnvironment.cs` - map them to `OperatingSystem.IsAndroid()/IsIOS()`.
- Modify `KhaozEngine.App/AppDataPaths.cs` - `(publisher, appName)` ctor, publisher-rooted resolution, mobile branches.
- Modify `KhaozEngine.Tests/AppDataPathsTests.cs` - migrate existing tests to the new ctor, add publisher/mobile/fallback tests, extend `FakeAppDataEnvironment`.
- Modify `KhaozEngine.Tests/SettingsManagerTests.cs`, `FileSettingsStorageTests.cs`, `PersistenceQueueTests.cs`, `AtomicJsonWriterTests.cs` - add the publisher arg to their `new AppDataPaths(...)` call sites.
- Create `KhaozEngine.Persistence/GameStorageOptions.cs` - options object.
- Create `KhaozEngine.Persistence/GameStorage.cs` - the facade.
- Create `KhaozEngine.Tests/GameStorageTests.cs` - facade tests.
- Release: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`.

---

## Task 1: Extend the environment seam with mobile flags

**Files:**
- Modify: `KhaozEngine.App/IAppDataEnvironment.cs`
- Modify: `KhaozEngine.App/SystemAppDataEnvironment.cs`
- Modify: `KhaozEngine.Tests/AppDataPathsTests.cs` (the `FakeAppDataEnvironment` at the bottom)

This task is additive and keeps the build green (no ctor change yet). Adding interface members forces updating the system impl and the fake, so all three move together.

- [ ] **Step 1: Add the two flags to the interface**

In `KhaozEngine.App/IAppDataEnvironment.cs`, after the `IsLinux` member:

```csharp
    /// <summary>True when running on Android.</summary>
    bool IsAndroid { get; }

    /// <summary>True when running on iOS.</summary>
    bool IsIOS { get; }
```

- [ ] **Step 2: Map them in the system implementation**

In `KhaozEngine.App/SystemAppDataEnvironment.cs`, add after the `IsLinux` line:

```csharp
    public bool IsAndroid => OperatingSystem.IsAndroid();
    public bool IsIOS => OperatingSystem.IsIOS();
```

- [ ] **Step 3: Add settable flags to the test fake**

In `KhaozEngine.Tests/AppDataPathsTests.cs`, in `FakeAppDataEnvironment`, after the `IsLinux` auto-property:

```csharp
    public bool IsAndroid { get; set; }
    public bool IsIOS { get; set; }
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build KhaozEngine.App/KhaozEngine.App.csproj`
Expected: Build succeeded. (The full test project still compiles too since the ctor is unchanged.)

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.App/IAppDataEnvironment.cs KhaozEngine.App/SystemAppDataEnvironment.cs KhaozEngine.Tests/AppDataPathsTests.cs
git commit -m "app(5.59.0): add IsAndroid/IsIOS to the app-data environment seam"
```

---

## Task 2: Publisher-rooted `AppDataPaths` (breaking ctor) + migrate desktop tests

**Files:**
- Modify: `KhaozEngine.App/AppDataPaths.cs`
- Modify: `KhaozEngine.Tests/AppDataPathsTests.cs`
- Modify: `KhaozEngine.Tests/SettingsManagerTests.cs:128`
- Modify: `KhaozEngine.Tests/FileSettingsStorageTests.cs:33`
- Modify: `KhaozEngine.Tests/PersistenceQueueTests.cs:179`
- Modify: `KhaozEngine.Tests/AtomicJsonWriterTests.cs:92`

The ctor change is breaking and must land with all test call-site updates in one task, or the test project won't compile. Mobile branches are added in Task 3 (TDD); this task implements desktop resolution + fallbacks under the new publisher-rooted ctor.

- [ ] **Step 1: Rewrite `AppDataPaths.cs`**

Replace the whole file with:

```csharp
using System;
using System.IO;

namespace KhaozEngine.App;

/// <summary>
/// Resolves the OS-correct application-data directory (for saves, settings, logs) under a
/// publisher root, and exposes conventional file paths inside it. Layout is
/// <c>&lt;os-base&gt;/&lt;publisher&gt;/&lt;appName&gt;/</c> so every game from one publisher nests together.
/// <list type="bullet">
///   <item>Windows: <c>%APPDATA%\&lt;publisher&gt;\&lt;appName&gt;\</c></item>
///   <item>macOS: <c>~/Library/Application Support/&lt;publisher&gt;/&lt;appName&gt;/</c></item>
///   <item>Linux: <c>$XDG_DATA_HOME/&lt;publisher&gt;/&lt;appName&gt;/</c> (else <c>~/.local/share/&lt;publisher&gt;/&lt;appName&gt;/</c>)</item>
///   <item>Android / iOS: <c>&lt;app-sandbox&gt;/&lt;publisher&gt;/&lt;appName&gt;/</c></item>
/// </list>
/// </summary>
public sealed class AppDataPaths
{
    private readonly string publisher;
    private readonly string appName;
    private readonly IAppDataEnvironment environment;
    private readonly Lazy<string> resolvedBaseDir;

    /// <summary>Creates a resolver for the given publisher and app name using the real OS environment.</summary>
    /// <exception cref="ArgumentException"><paramref name="publisher"/> or <paramref name="appName"/> is null, empty, or whitespace.</exception>
    public AppDataPaths(string publisher, string appName)
        : this(publisher, appName, new SystemAppDataEnvironment())
    {
    }

    internal AppDataPaths(string publisher, string appName, IAppDataEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            throw new ArgumentException("A publisher name must be provided.", nameof(publisher));
        }
        if (string.IsNullOrWhiteSpace(appName))
        {
            throw new ArgumentException("An app name must be provided.", nameof(appName));
        }

        this.publisher = publisher;
        this.appName = appName;
        this.environment = environment;
        this.resolvedBaseDir = new Lazy<string>(CreateBaseDirectory);
    }

    /// <summary>
    /// The root app-data directory. Resolved and created on first access, then cached; resolution
    /// and directory creation happen exactly once even under concurrent access (backed by
    /// <see cref="Lazy{T}"/>).
    /// </summary>
    public string BaseDirectory => resolvedBaseDir.Value;

    private string CreateBaseDirectory()
    {
        string dir = ResolveBaseDirectory();
        Directory.CreateDirectory(dir);
        return dir;
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
        // Mobile sandboxes are checked first so a platform that also reports a desktop flag
        // cannot shadow them.
        if (environment.IsAndroid || environment.IsIOS)
        {
            string sandbox = environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(sandbox))
            {
                return Nest(sandbox);
            }
        }
        else if (environment.IsWindows)
        {
            string appData = environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                return Nest(appData);
            }
        }
        else if (environment.IsMacOS)
        {
            string appSupport = environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appSupport))
            {
                return Nest(appSupport);
            }
        }
        else if (environment.IsLinux)
        {
            string? xdgDataHome = environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                return Nest(xdgDataHome);
            }

            string? home = environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".local", "share", publisher, appName);
            }
        }

        string localAppData = environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Nest(localAppData);
        }

        string homeDir = environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, "." + publisher.ToLowerInvariant(), appName);
    }

    private string Nest(string baseDir) => Path.Combine(baseDir, publisher, appName);
}
```

- [ ] **Step 2: Migrate `AppDataPathsTests.cs`**

Replace the test class body (the `[Fact]`/`[Theory]` methods and the `AppFolder` constant; keep the `FakeAppDataEnvironment` from Task 1 and the helpers `NewTempRoot`/`Cleanup`). Use these two constants and updated tests:

```csharp
    private const string Publisher = "APKiwi";
    private const string AppName = "MyGame";

    [Fact]
    public void BaseDirectory_Windows_UsesApplicationDataUnderPublisher()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_MacOS_UsesApplicationDataUnderPublisher()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsMacOS = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
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

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
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

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, ".local", "share", Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_NoOsMatch_FallsBackToLocalApplicationData()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment();
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_LastResort_UsesUserProfileDotPublisherThenAppName()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment();
            env.Folders[Environment.SpecialFolder.UserProfile] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, "." + Publisher.ToLowerInvariant(), AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_OsBranchWithBlankPath_FallsThroughToLocalApplicationData()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = "   ";
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
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

            var paths = new AppDataPaths(Publisher, AppName, env);

            string first = paths.BaseDirectory;
            string second = paths.BaseDirectory;

            Assert.Equal(first, second);
            Assert.Equal(1, env.GetFolderPathCalls);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_InvalidPublisher_Throws(string? badPublisher)
    {
        Assert.Throws<ArgumentException>(() => new AppDataPaths(badPublisher!, AppName, new FakeAppDataEnvironment()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_InvalidAppName_Throws(string? badAppName)
    {
        Assert.Throws<ArgumentException>(() => new AppDataPaths(Publisher, badAppName!, new FakeAppDataEnvironment()));
    }

    [Fact]
    public void FilePaths_ComposeOffBaseDirectory()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);
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

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(paths.BaseDirectory, "custom.dat"), paths.GetFilePath("custom.dat"));
        }
        finally { Cleanup(root); }
    }
```

- [ ] **Step 3: Fix the other four test call sites**

Add a publisher arg (`"APKiwi"`) to each `new AppDataPaths(...)`:

- `KhaozEngine.Tests/SettingsManagerTests.cs:128` → `var paths = new AppDataPaths("APKiwi", "Item10Settings", env);`
- `KhaozEngine.Tests/FileSettingsStorageTests.cs:33` → `return new AppDataPaths("APKiwi", "Item10Settings", env);`
- `KhaozEngine.Tests/PersistenceQueueTests.cs:179` → `var paths = new AppDataPaths("APKiwi", "MyGame", env);`
- `KhaozEngine.Tests/AtomicJsonWriterTests.cs:92` → `var paths = new AppDataPaths("APKiwi", "MyGame", env);`

(These tests assert on file content via `GetFilePath`, so the extra publisher folder is transparent.)

- [ ] **Step 4: Run the affected tests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AppDataPathsTests|FullyQualifiedName~SettingsManagerTests|FullyQualifiedName~FileSettingsStorageTests|FullyQualifiedName~PersistenceQueueTests|FullyQualifiedName~AtomicJsonWriterTests"`
Expected: PASS (all green).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.App/AppDataPaths.cs KhaozEngine.Tests/AppDataPathsTests.cs KhaozEngine.Tests/SettingsManagerTests.cs KhaozEngine.Tests/FileSettingsStorageTests.cs KhaozEngine.Tests/PersistenceQueueTests.cs KhaozEngine.Tests/AtomicJsonWriterTests.cs
git commit -m "app(5.59.0): AppDataPaths publisher root (breaking ctor: publisher + appName)"
```

---

## Task 3: Android/iOS resolution (TDD)

**Files:**
- Modify: `KhaozEngine.Tests/AppDataPathsTests.cs`
- (The code branch already exists from Task 2; this task proves it via tests. If Task 2's mobile branch were missing, these tests would catch it.)

- [ ] **Step 1: Write the failing mobile tests**

Add to `AppDataPathsTests.cs`:

```csharp
    [Fact]
    public void BaseDirectory_Android_UsesLocalApplicationDataSandboxUnderPublisher()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsAndroid = true };
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_IOS_UsesLocalApplicationDataSandboxUnderPublisher()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsIOS = true };
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = root;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(root, Publisher, AppName), paths.BaseDirectory);
            Assert.True(Directory.Exists(paths.BaseDirectory));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseDirectory_Android_TakesPrecedenceOverDesktopFlag()
    {
        string sandbox = NewTempRoot();
        string desktop = NewTempRoot();
        try
        {
            // Both Android and a desktop flag set: the mobile sandbox must win.
            var env = new FakeAppDataEnvironment { IsAndroid = true, IsLinux = true };
            env.Folders[Environment.SpecialFolder.LocalApplicationData] = sandbox;
            env.EnvVars["XDG_DATA_HOME"] = desktop;

            var paths = new AppDataPaths(Publisher, AppName, env);

            Assert.Equal(Path.Combine(sandbox, Publisher, AppName), paths.BaseDirectory);
        }
        finally { Cleanup(sandbox); Cleanup(desktop); }
    }
```

- [ ] **Step 2: Run to verify (should PASS - code from Task 2 already implements it)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AppDataPathsTests.BaseDirectory_Android|FullyQualifiedName~AppDataPathsTests.BaseDirectory_IOS"`
Expected: PASS. If any FAIL, the mobile branch in `AppDataPaths.ResolveBaseDirectory` is wrong - fix it there (mobile `if` must be the first branch and `Nest` the sandbox path).

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/AppDataPathsTests.cs
git commit -m "test(5.59.0): cover Android/iOS app-data sandbox resolution"
```

---

## Task 4: `GameStorage` facade core (plaintext save/load, settings, lifetime)

**Files:**
- Create: `KhaozEngine.Persistence/GameStorageOptions.cs`
- Create: `KhaozEngine.Persistence/GameStorage.cs`
- Create: `KhaozEngine.Tests/GameStorageTests.cs`

- [ ] **Step 1: Write the failing core tests**

Create `KhaozEngine.Tests/GameStorageTests.cs`:

```csharp
using System;
using System.IO;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class GameStorageTests
{
    public sealed class Save
    {
        public string Name { get; set; } = "";
        public int Level { get; set; }
    }

    public sealed class Prefs
    {
        public int Volume { get; set; } = 5;
    }

    private static GameStorage NewStorage(out string root, GameStorageOptions? options = null)
    {
        root = Path.Combine(Path.GetTempPath(), "ke-gamestorage-" + Path.GetRandomFileName());
        var env = new FakeAppDataEnvironment { IsMacOS = true };
        env.Folders[Environment.SpecialFolder.ApplicationData] = root;
        // Build AppDataPaths with the fake env (KhaozEngine.App exposes internals to the test
        // assembly), then hand it to the public AppDataPaths-accepting GameStorage ctor. This is the
        // test seam; KhaozEngine.Persistence cannot see App's internal IAppDataEnvironment itself.
        var paths = new AppDataPaths("APKiwi", "TestGame", env);
        return new GameStorage(paths, options);
    }

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Paths_AreRootedUnderPublisherAndApp()
    {
        var storage = NewStorage(out string root);
        try
        {
            Assert.Equal(Path.Combine(root, "APKiwi", "TestGame"), storage.Paths.BaseDirectory);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void SaveThenLoad_PlaintextRoundTrips()
    {
        var storage = NewStorage(out string root);
        try
        {
            storage.Save("save.json", new Save { Name = "Ada", Level = 7 });
            storage.Flush();

            Save loaded = storage.Load<Save>("save.json");
            Assert.Equal("Ada", loaded.Name);
            Assert.Equal(7, loaded.Level);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void Load_AbsentFile_ReturnsNewInstance()
    {
        var storage = NewStorage(out string root);
        try
        {
            Save loaded = storage.Load<Save>("missing.json");
            Assert.NotNull(loaded);
            Assert.Equal("", loaded.Name);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void Exists_And_Delete()
    {
        var storage = NewStorage(out string root);
        try
        {
            Assert.False(storage.Exists("save.json"));

            storage.Save("save.json", new Save { Name = "x", Level = 1 });
            storage.Flush();
            Assert.True(storage.Exists("save.json"));

            storage.Delete("save.json");
            Assert.False(storage.Exists("save.json"));

            // Deleting an absent file is a no-op, not an error.
            storage.Delete("save.json");
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void Settings_SaveThenLoad_RoundTrips()
    {
        var storage = NewStorage(out string root);
        try
        {
            storage.Settings.SaveSettings(new Prefs { Volume = 9 });
            storage.Flush();

            Prefs loaded = storage.Settings.LoadSettings<Prefs>();
            Assert.Equal(9, loaded.Volume);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void Dispose_FlushesPendingWrites()
    {
        var storage = NewStorage(out string root);
        try
        {
            storage.Save("save.json", new Save { Name = "Grace", Level = 3 });
            storage.Dispose(); // must flush before returning

            string path = Path.Combine(root, "APKiwi", "TestGame", "save.json");
            Assert.True(File.Exists(path));
            Assert.Contains("Grace", File.ReadAllText(path));
        }
        finally { Cleanup(root); }
    }
}
```

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GameStorageTests"`
Expected: FAIL - `GameStorage` / `GameStorageOptions` do not exist yet (the test helper calls `new GameStorage(paths, options)`, an overload that does not exist).

- [ ] **Step 3: Create `GameStorageOptions.cs`**

Create `KhaozEngine.Persistence/GameStorageOptions.cs`:

```csharp
using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Persistence;

/// <summary>
/// Optional configuration for <see cref="GameStorage"/>. Every field is optional; a null options
/// object (or an unset field) means the default. Encoded save/load requires <see cref="Encoder"/>.
/// </summary>
public sealed class GameStorageOptions
{
    /// <summary>Encoder used by <c>Save(..., encode: true)</c> and transparent decode on load. Null disables encoded saves.</summary>
    public SaveEncoder? Encoder { get; set; }

    /// <summary>Logger passed to the internal <see cref="PersistenceQueue"/> and any settings manager. Defaults to the ambient log.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Total write attempts per payload for the internal queue (>= 1). Defaults to 3.</summary>
    public int MaxWriteAttempts { get; set; } = 3;

    /// <summary>Backoff between write attempts. Defaults to 50 ms (capped at 1 s by the queue).</summary>
    public TimeSpan? RetryDelay { get; set; }
}
```

- [ ] **Step 4: Create `GameStorage.cs` (core; encoded methods + CreateSettingsManager added in Task 5)**

Create `KhaozEngine.Persistence/GameStorage.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.App;
using KhaozEngine.Diagnostics;
using KhaozEngine.Serialization;

namespace KhaozEngine.Persistence;

/// <summary>
/// One-call facade over the engine's storage stack: publisher-rooted <see cref="AppDataPaths"/>, a
/// shared coalesced <see cref="PersistenceQueue"/>, a <see cref="FileSettingsStorage"/>, and an
/// optional <see cref="SaveEncoder"/>. Exposes generic typed save/load (plaintext or transparently
/// encoded) so games stop hand-assembling paths + queue + storages. Owns the write queue and
/// flushes/disposes it on <see cref="Dispose"/>.
/// </summary>
public sealed class GameStorage : IDisposable
{
    private readonly ILogger logger;

    /// <summary>Publisher-rooted paths: <c>&lt;os-base&gt;/&lt;publisher&gt;/&lt;appName&gt;/</c>.</summary>
    public AppDataPaths Paths { get; }

    /// <summary>The single shared write queue (atomic, coalesced) all writes go through.</summary>
    public PersistenceQueue WriteQueue { get; }

    /// <summary>Settings storage over <see cref="Paths"/> and <see cref="WriteQueue"/>.</summary>
    public ISettingsStorage Settings { get; }

    /// <summary>The configured save encoder, or null when none was provided.</summary>
    public SaveEncoder? Encoder { get; }

    /// <summary>Creates a storage facade rooted at <c>&lt;os-base&gt;/&lt;publisher&gt;/&lt;appName&gt;/</c> using the real OS environment.</summary>
    public GameStorage(string publisher, string appName, GameStorageOptions? options = null)
        : this(new AppDataPaths(publisher, appName), options)
    {
    }

    /// <summary>
    /// Creates a storage facade over an already-built <paramref name="paths"/>. Use this overload to
    /// supply a custom <see cref="AppDataPaths"/> (it is also the seam tests use, building paths over a
    /// fake environment).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public GameStorage(AppDataPaths paths, GameStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        options ??= new GameStorageOptions();
        this.logger = options.Logger ?? Log.For<GameStorage>();
        Paths = paths;
        WriteQueue = new PersistenceQueue(options.Logger, options.MaxWriteAttempts, options.RetryDelay);
        Settings = new FileSettingsStorage(Paths, WriteQueue);
        Encoder = options.Encoder;
    }

    /// <summary>Serializes <paramref name="value"/> to indented JSON, optionally encodes it, and queues a write to <paramref name="fileName"/> in the app-data dir.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="encode"/> is true but no encoder was configured.</exception>
    public void Save<T>(string fileName, T value, bool encode = false)
    {
        string json = JsonSerializer.Serialize(value, JsonDefaults.IndentedWrite);
        if (encode)
        {
            if (Encoder is null)
            {
                throw new InvalidOperationException("Encoded save requested but no SaveEncoder was configured (set GameStorageOptions.Encoder).");
            }
            json = Encoder.Encode(json);
        }
        WriteQueue.Enqueue(Paths.GetFilePath(fileName), json);
    }

    /// <summary>
    /// Loads <paramref name="fileName"/> and deserializes to <typeparamref name="T"/>. Returns a new
    /// <typeparamref name="T"/> if the file is absent. If an encoder is configured and the content is
    /// encoded, it is decoded transparently first (lenient: recovers JSON even on HMAC mismatch).
    /// </summary>
    public T Load<T>(string fileName) where T : new()
    {
        string path = Paths.GetFilePath(fileName);
        if (!File.Exists(path))
        {
            return new T();
        }

        string content = File.ReadAllText(path);
        if (Encoder is not null && Encoder.IsEncoded(content))
        {
            content = Encoder.Decode(content) ?? content;
        }

        return JsonSerializer.Deserialize<T>(content) ?? new T();
    }

    /// <summary>True when <paramref name="fileName"/> exists in the app-data directory.</summary>
    public bool Exists(string fileName) => File.Exists(Paths.GetFilePath(fileName));

    /// <summary>Deletes <paramref name="fileName"/> if present. Absent file is a no-op.</summary>
    public void Delete(string fileName)
    {
        string path = Paths.GetFilePath(fileName);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"failed to delete '{path}'", ex);
        }
    }

    /// <summary>Drains all pending writes (use on shutdown).</summary>
    public void Flush() => WriteQueue.Flush();

    /// <summary>Flushes pending writes, then disposes the write queue.</summary>
    public void Dispose() => WriteQueue.Dispose();
}
```

- [ ] **Step 5: Run the core tests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GameStorageTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Persistence/GameStorageOptions.cs KhaozEngine.Persistence/GameStorage.cs KhaozEngine.Tests/GameStorageTests.cs
git commit -m "persistence(5.59.0): GameStorage facade core (paths + queue + settings + plaintext save/load)"
```

---

## Task 5: `GameStorage` encoded save/load + `CreateSettingsManager<T>`

**Files:**
- Modify: `KhaozEngine.Persistence/GameStorage.cs`
- Modify: `KhaozEngine.Tests/GameStorageTests.cs`

- [ ] **Step 1: Write the failing encoded + settings-manager tests**

Add these test methods to `GameStorageTests` (after the existing ones). They need an encoder helper; add it and the new tests:

```csharp
    private static GameStorage NewEncodedStorage(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "ke-gamestorage-" + Path.GetRandomFileName());
        var env = new FakeAppDataEnvironment { IsMacOS = true };
        env.Folders[Environment.SpecialFolder.ApplicationData] = root;
        var encoder = new SaveEncoder(new byte[] { 1, 2, 3, 4 }, "KESAVE");
        var paths = new AppDataPaths("APKiwi", "TestGame", env);
        return new GameStorage(paths, new GameStorageOptions { Encoder = encoder });
    }

    [Fact]
    public void SaveEncoded_ThenLoad_RoundTripsAndFileIsEncodedOnDisk()
    {
        var storage = NewEncodedStorage(out string root);
        try
        {
            storage.Save("save.json", new Save { Name = "Lin", Level = 4 }, encode: true);
            storage.Flush();

            // On-disk content is in the encoded format (not raw JSON).
            string raw = File.ReadAllText(Path.Combine(root, "APKiwi", "TestGame", "save.json"));
            Assert.StartsWith("KESAVE:", raw);
            Assert.DoesNotContain("\"Name\"", raw);

            // Load decodes transparently (no flag passed).
            Save loaded = storage.Load<Save>("save.json");
            Assert.Equal("Lin", loaded.Name);
            Assert.Equal(4, loaded.Level);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void Load_PlaintextFile_WithEncoderConfigured_StillReadsAsJson()
    {
        var storage = NewEncodedStorage(out string root);
        try
        {
            // Write plaintext (encode: false) even though an encoder is configured.
            storage.Save("save.json", new Save { Name = "Plain", Level = 1 }, encode: false);
            storage.Flush();

            Save loaded = storage.Load<Save>("save.json");
            Assert.Equal("Plain", loaded.Name);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void SaveEncoded_WithoutEncoder_Throws()
    {
        var storage = NewStorage(out string root);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                storage.Save("save.json", new Save { Name = "x", Level = 1 }, encode: true));
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void CreateSettingsManager_LoadsExistingSettingsOnConstruct()
    {
        var storage = NewStorage(out string root);
        try
        {
            storage.Settings.SaveSettings(new Prefs { Volume = 8 });
            storage.Flush();

            var manager = storage.CreateSettingsManager<Prefs>();
            Assert.Equal(8, manager.Settings.Volume);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GameStorageTests.CreateSettingsManager_LoadsExistingSettingsOnConstruct"`
Expected: FAIL - `CreateSettingsManager` is not defined. (The encoded tests already pass against Task 4's code; this step proves the new method is missing.)

- [ ] **Step 3: Add `CreateSettingsManager<T>` to `GameStorage`**

In `KhaozEngine.Persistence/GameStorage.cs`, add this method after `Save<T>` (the encoded `Save`/`Load` are already implemented in Task 4):

```csharp
    /// <summary>
    /// Builds a <see cref="SettingsManager{T}"/> over <see cref="Settings"/> (which loads on
    /// construction), using the facade's logger. <paramref name="sanitizeOnLoad"/> is applied after
    /// every load (clamp fields, migrate a schema version, etc.).
    /// </summary>
    public SettingsManager<T> CreateSettingsManager<T>(Func<T, T>? sanitizeOnLoad = null) where T : new()
        => new SettingsManager<T>(Settings, logger, sanitizeOnLoad);
```

- [ ] **Step 4: Run all GameStorage tests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GameStorageTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Persistence/GameStorage.cs KhaozEngine.Tests/GameStorageTests.cs
git commit -m "persistence(5.59.0): GameStorage encoded save/load + CreateSettingsManager<T>"
```

---

## Task 6: Full suite + release ritual

**Files:**
- Modify: `Directory.Build.props`
- Modify: `CHANGELOG.md`
- Modify: `docs/CONSUMERS.md`
- Modify: `docs/ROADMAP.md`
- Modify: `README.md`

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all green - was 1272 + new tests).

- [ ] **Step 2: Bump the 5.x version**

In `Directory.Build.props`, change `<KhaozEngine5xVersion>5.58.0</KhaozEngine5xVersion>` to `<KhaozEngine5xVersion>5.59.0</KhaozEngine5xVersion>`. Leave the legacy `<Version>4.12.0</Version>` untouched.

- [ ] **Step 3: Add the CHANGELOG entry (newest-first, top of `CHANGELOG.md`, above the 5.58.0 entry)**

```markdown
## 5.59.0 (custom 5.x line)

Cross-platform storage: `AppDataPaths` is now publisher-rooted and mobile-aware, plus a new
`GameStorage` facade in `KhaozEngine.Persistence`.

- **BREAKING (`KhaozEngine.App`):** `AppDataPaths` now takes `(string publisher, string appName)`
  and resolves `<os-base>/<publisher>/<appName>/`. The old single-arg `AppDataPaths(appFolderName)`
  is removed. Migrate call sites: `new AppDataPaths("MyGame")` becomes
  `new AppDataPaths("APKiwi", "MyGame")` (or switch to `GameStorage`). No on-disk migration is
  performed; data under the old single-folder layout is orphaned.
- New Android/iOS branches resolve the app sandbox (`SpecialFolder.LocalApplicationData`) and are
  checked before desktop branches. `IAppDataEnvironment` gains `IsAndroid`/`IsIOS`. BCL-only.
- New `GameStorage` / `GameStorageOptions` (`KhaozEngine.Persistence`): one object assembling the
  publisher-rooted `AppDataPaths`, a shared `PersistenceQueue`, a `FileSettingsStorage`, and an
  optional `SaveEncoder`. Generic typed `Save<T>`/`Load<T>` (plaintext or transparently encoded),
  `Exists`/`Delete`, `CreateSettingsManager<T>`, and `Flush`/`Dispose` (flushes the queue).
```

- [ ] **Step 4: Update the three guarded version declarations to 5.59.0**

- `docs/CONSUMERS.md`: the "**Engine current version:** `5.58.0`" line → `5.59.0` (keep the rest of the sentence). Add a note in the matrix area that adopting 5.59.0 requires moving `new AppDataPaths(name)` call sites to the publisher form or switching to `GameStorage`.
- `docs/ROADMAP.md`: the "Current released version: **5.58.0**" line → `5.59.0`.
- `README.md`: the four `<PackageReference ... Version="5.58.0" />` examples → `5.59.0`.

Verify: `bash scripts/check-doc-versions.sh`
Expected: exits 0 (declarations match `<KhaozEngine5xVersion>`).

- [ ] **Step 5: Pack to the local feed (cumulative; do not delete old versions)**

Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: pack succeeds; `local-feed` now contains `KhaozEngine.App.5.59.0.nupkg`, `KhaozEngine.Persistence.5.59.0.nupkg`, and the rest of the 5.x packages at 5.59.0.

- [ ] **Step 6: Commit the release**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "release(5.59.0): cross-platform storage (publisher root + mobile + GameStorage)"
```

- [ ] **Step 7: Finish the branch**

Use the `superpowers:finishing-a-development-branch` skill to present the merge/push options. The
engine convention ("merge to main implies push") means the chosen merge path also pushes `main` and
the `v5.59.0` tag (CI publishes to GitHub Packages on the tag). Tagging happens on `main` after the
merge: `git tag v5.59.0 && git push origin main v5.59.0`.

- [ ] **Step 8: Old-dir cleanup on the dev box (post-merge, per user request - no migration)**

Find the folder names the consumers passed to `AppDataPaths` and remove the corresponding old
single-folder app-data dirs on macOS:

```bash
grep -rn "new AppDataPaths(" ~/Hardpoint ~/Nullwake ~/SpaceGame --include="*.cs" 2>/dev/null | grep -v -E "/(bin|obj)/"
```

For each old folder name `<Name>` found, remove `~/Library/Application Support/<Name>` (the
pre-publisher location) after confirming it is not the new `APKiwi/<Name>` path. Report what was
removed.

---

## Notes for the implementer

- Packages here are BCL-only (no MonoGame). Do not add any MonoGame or mobile-SDK references.
- `KhaozEngine.Persistence` already references `KhaozEngine.App`, `KhaozEngine.Serialization`, and
  `KhaozEngine.Diagnostics` - all types used by `GameStorage` (`AppDataPaths`, `JsonDefaults`,
  `ILogger`/`Log`) are already on the package's dependency graph; no new csproj refs are needed.
- `JsonDefaults.IndentedWrite` is the engine's standard write options (matches `FileSettingsStorage`
  and `PersistenceQueue.Enqueue<T>`), so encoded and plaintext saves serialize consistently.
- Per `CLAUDE.md`, no em-dashes in commit messages or the changelog.
- `KhaozEngine.App`'s `IAppDataEnvironment` is `internal` and exposed only to `KhaozEngine.Tests`,
  not to `KhaozEngine.Persistence`. That is why `GameStorage` takes an already-built `AppDataPaths`
  (public `(AppDataPaths, options)` ctor) as its test seam rather than an environment: tests build
  `AppDataPaths` over the fake env (they can see App's internal 3-arg ctor) and pass it in. Do not
  try to reference `IAppDataEnvironment` from `GameStorage`.
```

