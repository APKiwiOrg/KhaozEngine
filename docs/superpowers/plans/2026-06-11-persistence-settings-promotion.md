# Settings-System Promotion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote SpaceGame's generic settings stack (`ISettingsStorage`, `SettingsManager<T>`, file-based JSON storage) into the existing `KhaozEngine.Persistence` package, writing through a shared `IPersistenceQueue` seam that item 8 will satisfy.

**Architecture:** Three shipped types plus one throwaway. `SettingsManager<T>` holds current settings and persists via an `ISettingsStorage`. `FileSettingsStorage` serializes to JSON under a `KhaozEngine.App.AppDataPaths` directory and *writes* through `IPersistenceQueue` (reads are direct). A `TempDirectPersistenceQueue` (synchronous temp-file-then-move) stands in until the coordinator wires item 8's real queue at merge.

**Tech Stack:** net10.0, C# (nullable enabled), System.Text.Json, xUnit. No MonoGame. New package dep: `KhaozEngine.Persistence -> KhaozEngine.App` (Diagnostics ref already present).

**Working location:** worktree `.claude/worktrees/item10-settings`, branch `batch2-item10-settings`. All paths below are relative to the worktree root. Run all commands from the worktree root.

**Release discipline (do NOT violate):** no `<Version>` edit, no `CHANGELOG.md`, no `docs/CONSUMERS.md`, no `dotnet pack`/local-feed. Coordinator owns the batched 3.3.0 release.

**Shared-file note:** Task 2 edits `KhaozEngine.Persistence.csproj` (App ref) and Task 1 creates `IPersistenceQueue.cs` — both are also touched by item 8. The `.cs` filenames here are kept distinct from item 8's writer files; the coordinator sequences the csproj commit/rebase.

---

## File structure

Create (production, `KhaozEngine.Persistence/`):
- `IPersistenceQueue.cs` — the write seam (verbatim; item 8 adds identical text).
- `TempDirectPersistenceQueue.cs` — TEMP throwaway synchronous queue (dropped at merge).
- `ISettingsStorage.cs` — storage contract.
- `FileSettingsStorage.cs` — concrete JSON storage over `AppDataPaths` + `IPersistenceQueue`.
- `SettingsManager.cs` — `SettingsManager<T>`.

Modify:
- `KhaozEngine.Persistence/KhaozEngine.Persistence.csproj` — add App `ProjectReference`.
- `KhaozEngine.Persistence/README.md` — add a "Settings" section.

Create (tests, `KhaozEngine.Tests/`):
- `TempDirectPersistenceQueueTests.cs` — TEMP (dropped at merge with the impl).
- `FileSettingsStorageTests.cs`
- `SettingsManagerTests.cs`

Reuse (already in `KhaozEngine.Tests`, same `KhaozEngine.Tests` namespace, `internal`): `FakeLogger` (in `SaveEncoderTests.cs`), `FakeAppDataEnvironment` (in `AppDataPathsTests.cs`).

---

## Task 1: Write seam + throwaway queue

**Files:**
- Create: `KhaozEngine.Persistence/IPersistenceQueue.cs`
- Create: `KhaozEngine.Persistence/TempDirectPersistenceQueue.cs`
- Test: `KhaozEngine.Tests/TempDirectPersistenceQueueTests.cs`

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/TempDirectPersistenceQueueTests.cs`:

```csharp
using System.IO;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

// TEMP - drop at merge alongside TempDirectPersistenceQueue.
public class TempDirectPersistenceQueueTests
{
    [Fact]
    public void Enqueue_WritesJsonAtomically_AndCleansTempFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-item10-" + Path.GetRandomFileName());
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var queue = new TempDirectPersistenceQueue();

            queue.Enqueue(path, "{\"a\":1}");

            Assert.True(File.Exists(path));
            Assert.Equal("{\"a\":1}", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Enqueue_OverwritesExistingFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-item10-" + Path.GetRandomFileName());
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var queue = new TempDirectPersistenceQueue();

            queue.Enqueue(path, "{\"v\":1}");
            queue.Enqueue(path, "{\"v\":2}");

            Assert.Equal("{\"v\":2}", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TempDirectPersistenceQueueTests"`
Expected: build FAILS (`IPersistenceQueue` / `TempDirectPersistenceQueue` do not exist).

- [ ] **Step 3: Create the seam interface (verbatim)**

Create `KhaozEngine.Persistence/IPersistenceQueue.cs` with EXACTLY this text (item 8 adds the identical text; do not add XML docs or reorder, so the two copies merge cleanly):

```csharp
namespace KhaozEngine.Persistence;

public interface IPersistenceQueue
{
    void Enqueue(string path, string json);   // per-path coalescing, last-writer-wins
    void Flush();                              // flush pending writes (e.g. on shutdown)
}
```

- [ ] **Step 4: Create the throwaway queue**

Create `KhaozEngine.Persistence/TempDirectPersistenceQueue.cs`:

```csharp
// TEMP - drop at merge. Throwaway synchronous IPersistenceQueue so Batch 2 item 10 (settings)
// builds and tests before item 8's real coalescing PersistenceQueue lands. At merge the coordinator
// deletes this file (and TempDirectPersistenceQueueTests) and wires item 8's PersistenceQueue in.
using System.IO;

namespace KhaozEngine.Persistence;

/// <summary>
/// TEMP - drop at merge. Writes synchronously (temp file then atomic move); no coalescing.
/// </summary>
public sealed class TempDirectPersistenceQueue : IPersistenceQueue
{
    /// <inheritdoc />
    public void Enqueue(string path, string json)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Preserve legacy behavior: swallow persistence errors.
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        // No-op: Enqueue already writes synchronously.
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TempDirectPersistenceQueueTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Persistence/IPersistenceQueue.cs KhaozEngine.Persistence/TempDirectPersistenceQueue.cs KhaozEngine.Tests/TempDirectPersistenceQueueTests.cs
git commit -m "Add IPersistenceQueue seam + throwaway TempDirectPersistenceQueue"
```

---

## Task 2: ISettingsStorage + FileSettingsStorage

**Files:**
- Modify: `KhaozEngine.Persistence/KhaozEngine.Persistence.csproj`
- Create: `KhaozEngine.Persistence/ISettingsStorage.cs`
- Create: `KhaozEngine.Persistence/FileSettingsStorage.cs`
- Test: `KhaozEngine.Tests/FileSettingsStorageTests.cs`

- [ ] **Step 1: Add the App project reference**

In `KhaozEngine.Persistence/KhaozEngine.Persistence.csproj`, the existing `<ItemGroup>` holds the Diagnostics reference:

```xml
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj" />
  </ItemGroup>
```

Add the App reference so it reads:

```xml
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.App/KhaozEngine.App.csproj" />
    <ProjectReference Include="../KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing test**

Create `KhaozEngine.Tests/FileSettingsStorageTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class FileSettingsStorageTests
{
    private sealed class Sample
    {
        public int Score { get; set; }
        public string Name { get; set; } = "";
    }

    // Records enqueued writes so the storage's contract with the queue can be asserted.
    private sealed class RecordingQueue : IPersistenceQueue
    {
        public readonly List<(string Path, string Json)> Writes = new();
        public int Flushes;
        public void Enqueue(string path, string json) => Writes.Add((path, json));
        public void Flush() => Flushes++;
    }

    private static AppDataPaths TempPaths(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "ke-item10-" + Path.GetRandomFileName());
        var env = new FakeAppDataEnvironment { IsMacOS = true };
        env.Folders[Environment.SpecialFolder.ApplicationData] = root;
        return new AppDataPaths("Item10Settings", env);
    }

    [Fact]
    public void Ctor_NullArgs_Throws()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            Assert.Throws<ArgumentNullException>(() => new FileSettingsStorage(null!, new RecordingQueue()));
            Assert.Throws<ArgumentNullException>(() => new FileSettingsStorage(paths, null!));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SaveSettings_EnqueuesSerializedJsonAtExpectedPath()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var queue = new RecordingQueue();
            var storage = new FileSettingsStorage(paths, queue);

            storage.SaveSettings(new Sample { Score = 7, Name = "x" });

            var write = Assert.Single(queue.Writes);
            Assert.Equal(paths.GetFilePath("settings.json"), write.Path);
            Assert.Contains("\"Score\": 7", write.Json);   // WriteIndented => "Score": 7
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void CustomSettingsFileName_IsHonored()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var queue = new RecordingQueue();
            var storage = new FileSettingsStorage(paths, queue) { SettingsFileName = "leaderboard.json" };

            storage.SaveSettings(new Sample());

            Assert.Equal(paths.GetFilePath("leaderboard.json"), Assert.Single(queue.Writes).Path);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RoundTrip_SaveThenLoad_ReturnsEqual()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            // Real synchronous queue so the file lands on disk for the load.
            var storage = new FileSettingsStorage(paths, new TempDirectPersistenceQueue());

            Assert.False(storage.SettingsExist());
            storage.SaveSettings(new Sample { Score = 42, Name = "neo" });
            Assert.True(storage.SettingsExist());

            Sample loaded = storage.LoadSettings<Sample>();
            Assert.Equal(42, loaded.Score);
            Assert.Equal("neo", loaded.Name);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LoadSettings_NoFile_ReturnsDefaults()
    {
        AppDataPaths paths = TempPaths(out string root);
        try
        {
            var storage = new FileSettingsStorage(paths, new RecordingQueue());

            Sample loaded = storage.LoadSettings<Sample>();

            Assert.Equal(0, loaded.Score);
            Assert.Equal("", loaded.Name);
        }
        finally { Cleanup(root); }
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~FileSettingsStorageTests"`
Expected: build FAILS (`ISettingsStorage` / `FileSettingsStorage` do not exist).

- [ ] **Step 4: Create the storage contract**

Create `KhaozEngine.Persistence/ISettingsStorage.cs`:

```csharp
namespace KhaozEngine.Persistence;

/// <summary>
/// Saves and loads strongly-typed application settings.
/// </summary>
public interface ISettingsStorage
{
    /// <summary>The settings file name used by the storage.</summary>
    string SettingsFileName { get; set; }

    /// <summary>Saves <paramref name="settings"/> to storage.</summary>
    /// <typeparam name="T">The settings type (must have a parameterless constructor).</typeparam>
    void SaveSettings<T>(T settings) where T : new();

    /// <summary>Loads settings from storage, or a new <typeparamref name="T"/> if none exist.</summary>
    /// <typeparam name="T">The settings type (must have a parameterless constructor).</typeparam>
    T LoadSettings<T>() where T : new();

    /// <summary>Returns true if a settings file exists in storage.</summary>
    bool SettingsExist();
}
```

- [ ] **Step 5: Create the file-based storage**

Create `KhaozEngine.Persistence/FileSettingsStorage.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.App;

namespace KhaozEngine.Persistence;

/// <summary>
/// File-based <see cref="ISettingsStorage"/> that serializes settings to indented JSON under the
/// app-data directory resolved by <see cref="AppDataPaths"/>. Writes go through an
/// <see cref="IPersistenceQueue"/> (which owns the atomic-write strategy); reads are direct.
/// </summary>
public sealed class FileSettingsStorage : ISettingsStorage
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly AppDataPaths appDataPaths;
    private readonly IPersistenceQueue writeQueue;

    /// <summary>Creates a storage rooted at <paramref name="appDataPaths"/>, writing via <paramref name="writeQueue"/>.</summary>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public FileSettingsStorage(AppDataPaths appDataPaths, IPersistenceQueue writeQueue)
    {
        this.appDataPaths = appDataPaths ?? throw new ArgumentNullException(nameof(appDataPaths));
        this.writeQueue = writeQueue ?? throw new ArgumentNullException(nameof(writeQueue));
    }

    /// <summary>The settings file name within the app-data directory. Defaults to "settings.json".</summary>
    public string SettingsFileName { get; set; } = "settings.json";

    private string SettingsFilePath => appDataPaths.GetFilePath(SettingsFileName);

    /// <summary>Serializes <paramref name="settings"/> to JSON and queues an atomic write.</summary>
    public void SaveSettings<T>(T settings) where T : new()
    {
        string json = JsonSerializer.Serialize(settings, SerializerOptions);
        writeQueue.Enqueue(SettingsFilePath, json);
    }

    /// <summary>Loads settings from disk, or returns a new <typeparamref name="T"/> if none exist.</summary>
    public T LoadSettings<T>() where T : new()
    {
        if (!SettingsExist())
        {
            return new T();
        }

        string json = File.ReadAllText(SettingsFilePath);
        return JsonSerializer.Deserialize<T>(json) ?? new T();
    }

    /// <summary>True when the settings file exists on disk.</summary>
    public bool SettingsExist()
    {
        string path = SettingsFilePath;
        return !string.IsNullOrEmpty(path) && File.Exists(path);
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~FileSettingsStorageTests"`
Expected: PASS (5 tests).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Persistence/KhaozEngine.Persistence.csproj KhaozEngine.Persistence/ISettingsStorage.cs KhaozEngine.Persistence/FileSettingsStorage.cs KhaozEngine.Tests/FileSettingsStorageTests.cs
git commit -m "Add ISettingsStorage + FileSettingsStorage (Persistence -> App dep)"
```

---

## Task 3: SettingsManager<T>

**Files:**
- Create: `KhaozEngine.Persistence/SettingsManager.cs`
- Test: `KhaozEngine.Tests/SettingsManagerTests.cs`

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/SettingsManagerTests.cs`:

```csharp
using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class SettingsManagerTests
{
    private sealed class Prefs
    {
        public int Volume { get; set; }
    }

    // In-memory ISettingsStorage with optional fault injection.
    private sealed class FakeStorage : ISettingsStorage
    {
        public string SettingsFileName { get; set; } = "settings.json";
        public object? Saved;
        public object? ToLoad;
        public bool ThrowOnSave;
        public bool ThrowOnLoad;

        public void SaveSettings<T>(T settings) where T : new()
        {
            if (ThrowOnSave) throw new InvalidOperationException("save boom");
            Saved = settings;
        }

        public T LoadSettings<T>() where T : new()
        {
            if (ThrowOnLoad) throw new InvalidOperationException("load boom");
            return ToLoad is T typed ? typed : new T();
        }

        public bool SettingsExist() => ToLoad is not null;
    }

    [Fact]
    public void Ctor_NullStorage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsManager<Prefs>(null!));
    }

    [Fact]
    public void Ctor_LoadsFromStorage_AndRaisesSettingsLoaded()
    {
        var storage = new FakeStorage { ToLoad = new Prefs { Volume = 9 } };
        Prefs? loaded = null;

        // Subscribe before construction is impossible (Load runs in ctor); assert via Settings,
        // then verify the event fires on an explicit reload.
        var manager = new SettingsManager<Prefs>(storage);
        Assert.Equal(9, manager.Settings.Volume);

        manager.SettingsLoaded += p => loaded = p;
        manager.Load();
        Assert.NotNull(loaded);
        Assert.Equal(9, loaded!.Volume);
    }

    [Fact]
    public void Save_PersistsAndRaisesSettingsSaved()
    {
        var storage = new FakeStorage();
        var manager = new SettingsManager<Prefs>(storage);
        manager.Settings.Volume = 3;
        Prefs? saved = null;
        manager.SettingsSaved += p => saved = p;

        manager.Save();

        Assert.Same(manager.Settings, storage.Saved);
        Assert.Same(manager.Settings, saved);
    }

    [Fact]
    public void Load_StorageThrows_UsesDefaults_AndLogsError()
    {
        var storage = new FakeStorage { ThrowOnLoad = true };
        var logger = new FakeLogger();

        var manager = new SettingsManager<Prefs>(storage, logger);

        Assert.Equal(0, manager.Settings.Volume);   // defaults
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Save_StorageThrows_Swallowed_AndLogsError()
    {
        var storage = new FakeStorage { ThrowOnSave = true };
        var logger = new FakeLogger();
        var manager = new SettingsManager<Prefs>(storage, logger);
        bool savedRaised = false;
        manager.SettingsSaved += _ => savedRaised = true;

        manager.Save();   // must not throw

        Assert.False(savedRaised);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SettingsManagerTests"`
Expected: build FAILS (`SettingsManager<T>` does not exist).

- [ ] **Step 3: Create SettingsManager**

Create `KhaozEngine.Persistence/SettingsManager.cs`:

```csharp
using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Persistence;

/// <summary>
/// Holds the current settings of type <typeparamref name="T"/> and persists them through an
/// <see cref="ISettingsStorage"/>. Load/save failures are swallowed (and logged via the optional
/// <see cref="ILogger"/>) so a corrupt settings file never crashes the game.
/// </summary>
/// <typeparam name="T">The settings type (must have a parameterless constructor).</typeparam>
public sealed class SettingsManager<T> where T : new()
{
    private readonly ISettingsStorage storage;
    private readonly ILogger? logger;
    private T settings = new();

    /// <summary>The underlying storage.</summary>
    public ISettingsStorage Storage => storage;

    /// <summary>The current settings. Never null.</summary>
    public T Settings => settings;

    /// <summary>Raised after settings are loaded (including when defaults are substituted).</summary>
    public event Action<T>? SettingsLoaded;

    /// <summary>Raised after settings are successfully saved.</summary>
    public event Action<T>? SettingsSaved;

    /// <summary>Creates a manager over <paramref name="storage"/> and immediately loads.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public SettingsManager(ISettingsStorage storage, ILogger? logger = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.logger = logger;
        Load();
    }

    /// <summary>Saves the current settings. Failures are swallowed and logged.</summary>
    public void Save()
    {
        try
        {
            storage.SaveSettings(settings);
            SettingsSaved?.Invoke(settings);
        }
        catch (Exception ex)
        {
            logger?.Error("Failed to save settings.", ex);
        }
    }

    /// <summary>Loads settings, falling back to defaults on failure. Always raises <see cref="SettingsLoaded"/>.</summary>
    public void Load()
    {
        try
        {
            settings = storage.LoadSettings<T>() ?? new T();
        }
        catch (Exception ex)
        {
            logger?.Error("Failed to load settings; using defaults.", ex);
            settings = new T();
        }

        SettingsLoaded?.Invoke(settings);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SettingsManagerTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Persistence/SettingsManager.cs KhaozEngine.Tests/SettingsManagerTests.cs
git commit -m "Add SettingsManager<T> with optional ILogger"
```

---

## Task 4: README + full-suite verification

**Files:**
- Modify: `KhaozEngine.Persistence/README.md`

- [ ] **Step 1: Add a Settings section to the README**

Append this section to the end of `KhaozEngine.Persistence/README.md` (keep it a distinct trailing section to avoid friction with item 8's README edits):

````markdown

## Settings

`SettingsManager<T>` holds a strongly-typed settings object and persists it through an
`ISettingsStorage`. `FileSettingsStorage` serializes to indented JSON under a
`KhaozEngine.App.AppDataPaths` directory and writes through an `IPersistenceQueue` (atomic,
per-path coalescing); reads are direct. Load/save failures are swallowed and reported via an
optional `KhaozEngine.Diagnostics.ILogger`.

```csharp
using KhaozEngine.App;
using KhaozEngine.Persistence;

var paths = new AppDataPaths("MyGame");
var storage = new FileSettingsStorage(paths, persistenceQueue);   // queue supplied by the host
var settings = new SettingsManager<MySettings>(storage, Log.For<SettingsManager<MySettings>>());

settings.Settings.MasterVolume = 0.8f;
settings.Save();
```
````

- [ ] **Step 2: Run the FULL test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, total = 268 (baseline) + 12 new = 280, Failed: 0.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Persistence/README.md
git commit -m "Document settings system in Persistence README"
```

---

## Done criteria

- All five production types compile in `KhaozEngine.Persistence`; full suite green (280 tests).
- No `<Version>` / `CHANGELOG` / `CONSUMERS.md` / pack changes.
- Branch `batch2-item10-settings` holds 4 commits (+ the earlier spec commit) ready for the coordinator.

## Report to coordinator (compile after Task 4)

- Branch `batch2-item10-settings`, worktree `.claude/worktrees/item10-settings`.
- Package touched: `KhaozEngine.Persistence` (new `Persistence -> App` ProjectReference).
- Files added: `IPersistenceQueue.cs`, `TempDirectPersistenceQueue.cs` (TEMP), `ISettingsStorage.cs`, `FileSettingsStorage.cs`, `SettingsManager.cs`, three test files, README section.
- Test delta: +12 (268 -> 280).
- Open items: shared `KhaozEngine.Persistence.csproj` App-ref edit (sequence with item 8); `IPersistenceQueue.cs` dual-add (identical text); at merge drop `TempDirectPersistenceQueue.cs` + `TempDirectPersistenceQueueTests.cs` and wire item 8's real `PersistenceQueue`; confirm public API shape of `FileSettingsStorage` / `SettingsManager<T>`.
