# Versioned save-migration chain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reusable, registered versioned migration chain to `KhaozEngine.Persistence` that steps a loaded value from its stored schema version up to the current version, then runs the existing sanitize/clamp pass.

**Architecture:** A standalone immutable `MigrationChain<T>` built through a fluent `MigrationChainBuilder<T>` (validated at build time). It is wired into `SettingsManager<T>` (runs before `sanitizeOnLoad`) and `GameStorage` (`CreateSettingsManager` + raw `Load<T>`). Version-field access is via caller-supplied `getVersion`/`setVersion` delegates, with an opt-in `ISchemaVersioned` interface + zero-config factory overload.

**Tech Stack:** C# / net10.0, xUnit (headless tests), `KhaozEngine.Diagnostics.ILogger`. MonoGame-free.

---

## File structure

- **Create** `KhaozEngine.Persistence/ISchemaVersioned.cs` — opt-in interface (`int SchemaVersion { get; set; }`).
- **Create** `KhaozEngine.Persistence/MigrationChain.cs` — static `MigrationChain` factory, `MigrationChainBuilder<T>`, `MigrationChain<T>`.
- **Create** `KhaozEngine.Tests/MigrationChainTests.cs` — unit tests for the chain itself.
- **Modify** `KhaozEngine.Persistence/SettingsManager.cs` — optional `migrations` ctor arg, run before sanitize.
- **Modify** `KhaozEngine.Persistence/GameStorage.cs` — optional `migrations` arg on `CreateSettingsManager` + `Load<T>`.
- **Modify** `KhaozEngine.Tests/SettingsManagerTests.cs` — integration tests (reuses its private `FakeStorage` and the file-based helpers already in this file).

`FakeLogger` (in `KhaozEngine.Tests/SaveEncoderTests.cs`, namespace `KhaozEngine.Tests`, `internal sealed`) is reused across the test project: it exposes `IReadOnlyList<Entry> Entries` where `Entry` is `record struct Entry(LogLevel Level, string Message)`.

---

## Task 1: `ISchemaVersioned` + `MigrationChain<T>` core (delegate form, ordered stepping + auto-stamp)

**Files:**
- Create: `KhaozEngine.Persistence/ISchemaVersioned.cs`
- Create: `KhaozEngine.Persistence/MigrationChain.cs`
- Test: `KhaozEngine.Tests/MigrationChainTests.cs`

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/MigrationChainTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class MigrationChainTests
{
    // Plain POCO exercised through the delegate form (no interface).
    private sealed class Poco
    {
        public int Ver { get; set; }
        public List<int> Steps { get; } = new();
    }

    // Implements the opt-in interface for the zero-config form (used later).
    private sealed class Doc : ISchemaVersioned
    {
        public int SchemaVersion { get; set; }
        public List<int> Steps { get; } = new();
    }

    [Fact]
    public void Migrate_RunsStepsInOrder_AndStampsVersionEachStep()
    {
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => { p.Steps.Add(1); return p; })   // v1 -> v2
            .Step(2, p => { p.Steps.Add(2); return p; })   // v2 -> v3
            .Build(currentVersion: 3);

        var result = chain.Migrate(new Poco { Ver = 1 });

        Assert.Equal(3, result.Ver);
        Assert.Equal(new[] { 1, 2 }, result.Steps);
        Assert.Equal(3, chain.CurrentVersion);
    }

    [Fact]
    public void Migrate_AlreadyCurrent_IsNoOp()
    {
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => { p.Steps.Add(1); return p; })
            .Build(2);

        var result = chain.Migrate(new Poco { Ver = 2 });

        Assert.Equal(2, result.Ver);
        Assert.Empty(result.Steps);   // no step ran
    }

    [Fact]
    public void Migrate_NewerFileThanCurrent_IsNoOp()
    {
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => { p.Steps.Add(1); return p; })
            .Build(2);

        var result = chain.Migrate(new Poco { Ver = 5 });   // file from a newer build

        Assert.Equal(5, result.Ver);
        Assert.Empty(result.Steps);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter MigrationChainTests`
Expected: FAIL to compile — `MigrationChain`, `ISchemaVersioned` do not exist.

- [ ] **Step 3: Write the interface**

Create `KhaozEngine.Persistence/ISchemaVersioned.cs`:

```csharp
namespace KhaozEngine.Persistence;

/// <summary>
/// Opt-in contract for types that carry an integer schema-version field. Implementing this lets a
/// type use the zero-config <see cref="MigrationChain.For{T}()"/> factory instead of supplying
/// get/set delegates. Any POCO can still be migrated via the delegate overload without this interface.
/// </summary>
public interface ISchemaVersioned
{
    /// <summary>The persisted schema version of this value.</summary>
    int SchemaVersion { get; set; }
}
```

- [ ] **Step 4: Write the chain (factory + builder + chain)**

Create `KhaozEngine.Persistence/MigrationChain.cs`:

```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Persistence;

/// <summary>
/// Static entry points for building a <see cref="MigrationChain{T}"/> of ordered, versioned save/settings
/// migration steps.
/// </summary>
public static class MigrationChain
{
    /// <summary>
    /// Begins a chain for any type, with caller-supplied accessors for the schema-version field. Use this
    /// for POCOs that do not implement <see cref="ISchemaVersioned"/>.
    /// </summary>
    public static MigrationChainBuilder<T> For<T>(Func<T, int> getVersion, Action<T, int> setVersion)
        where T : new()
        => new(getVersion, setVersion);

    /// <summary>
    /// Begins a chain for a type that implements <see cref="ISchemaVersioned"/>; the version field is read
    /// and written through <see cref="ISchemaVersioned.SchemaVersion"/>.
    /// </summary>
    public static MigrationChainBuilder<T> For<T>()
        where T : ISchemaVersioned, new()
        => new(v => v.SchemaVersion, (v, n) => v.SchemaVersion = n);
}

/// <summary>
/// Fluent builder for a <see cref="MigrationChain{T}"/>. Register one <see cref="Step"/> per schema
/// version, then <see cref="Build"/> to validate and freeze the chain.
/// </summary>
/// <typeparam name="T">The migrated value type.</typeparam>
public sealed class MigrationChainBuilder<T> where T : new()
{
    private readonly Func<T, int> getVersion;
    private readonly Action<T, int> setVersion;
    private readonly Dictionary<int, Func<T, T>> steps = new();

    internal MigrationChainBuilder(Func<T, int> getVersion, Action<T, int> setVersion)
    {
        this.getVersion = getVersion ?? throw new ArgumentNullException(nameof(getVersion));
        this.setVersion = setVersion ?? throw new ArgumentNullException(nameof(setVersion));
    }

    /// <summary>
    /// Registers the transform that takes a value from <paramref name="fromVersion"/> to
    /// <paramref name="fromVersion"/> + 1. The transform does ONLY the data change (mutate in place or
    /// return a replacement); the chain stamps the version field afterwards. Returning null keeps the input.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="migrate"/> is null.</exception>
    /// <exception cref="ArgumentException">A step from <paramref name="fromVersion"/> is already registered.</exception>
    public MigrationChainBuilder<T> Step(int fromVersion, Func<T, T> migrate)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        if (steps.ContainsKey(fromVersion))
            throw new ArgumentException($"A migration step from version {fromVersion} is already registered.", nameof(fromVersion));
        steps.Add(fromVersion, migrate);
        return this;
    }

    /// <summary>
    /// Validates and freezes the chain. The registered step keys must form the contiguous run
    /// <c>{ start .. currentVersion-1 }</c> with no gaps; no step may target at or beyond
    /// <paramref name="currentVersion"/>. An empty chain (no steps) is allowed and acts as a no-op.
    /// </summary>
    /// <exception cref="ArgumentException">A step targets >= <paramref name="currentVersion"/>, or there is a gap.</exception>
    public MigrationChain<T> Build(int currentVersion)
    {
        foreach (int from in steps.Keys)
        {
            if (from >= currentVersion)
                throw new ArgumentException(
                    $"Migration step from version {from} targets version {from + 1}, at or beyond the current version {currentVersion}.");
        }

        if (steps.Count > 0)
        {
            int start = int.MaxValue;
            foreach (int k in steps.Keys)
                if (k < start) start = k;

            for (int v = start; v < currentVersion; v++)
            {
                if (!steps.ContainsKey(v))
                    throw new ArgumentException(
                        $"Migration chain has a gap: no step registered from version {v} (steps must be contiguous from {start} to {currentVersion - 1}).");
            }
        }

        return new MigrationChain<T>(getVersion, setVersion, new Dictionary<int, Func<T, T>>(steps), currentVersion);
    }
}

/// <summary>
/// An immutable, validated chain of versioned migration steps. Reusable and stateless across loads:
/// <see cref="Migrate"/> steps a value from its stored version up to <see cref="CurrentVersion"/>.
/// Never throws on the value it is handed (corrupt/odd data is logged and the best-effort value returned),
/// consistent with the engine's "a bad save never crashes the game" stance.
/// </summary>
/// <typeparam name="T">The migrated value type.</typeparam>
public sealed class MigrationChain<T> where T : new()
{
    private readonly Func<T, int> getVersion;
    private readonly Action<T, int> setVersion;
    private readonly Dictionary<int, Func<T, T>> steps;
    private readonly int startVersion;

    /// <summary>The version a fully-migrated value ends at.</summary>
    public int CurrentVersion { get; }

    internal MigrationChain(Func<T, int> getVersion, Action<T, int> setVersion, Dictionary<int, Func<T, T>> steps, int currentVersion)
    {
        this.getVersion = getVersion;
        this.setVersion = setVersion;
        this.steps = steps;
        CurrentVersion = currentVersion;

        int start = currentVersion;
        foreach (int k in steps.Keys)
            if (k < start) start = k;
        startVersion = start;
    }

    /// <summary>
    /// Runs the chain on <paramref name="value"/> from its stored version up to <see cref="CurrentVersion"/>.
    /// A value already at/above current is returned untouched. A value older than the oldest step is logged
    /// (Warn) and returned unchanged. A step that throws is logged (Error) and halts the chain, returning the
    /// partially-migrated value (its version reflects only the completed steps).
    /// </summary>
    /// <param name="value">The value to migrate. A null value is returned as-is.</param>
    /// <param name="logger">Optional logger; defaults to the "MigrationChain" category.</param>
    public T Migrate(T value, ILogger? logger = null)
    {
        if (value is null) return value;
        logger ??= Log.Get("MigrationChain");

        try
        {
            int v = getVersion(value);

            if (v >= CurrentVersion)
                return value;   // already current, or a save from a newer build

            if (v < startVersion)
            {
                logger.Warn($"Schema version {v} predates the oldest migration step ({startVersion}); leaving value as-is.");
                return value;
            }

            while (v < CurrentVersion)
            {
                if (!steps.TryGetValue(v, out Func<T, T>? migrate))
                {
                    // Unreachable for a Build-validated chain; guard anyway.
                    logger.Warn($"No migration step from version {v}; halting.");
                    break;
                }

                value = migrate(value) ?? value;
                v++;
                setVersion(value, v);
            }

            return value;
        }
        catch (Exception ex)
        {
            logger.Error("Migration chain failed; returning value as-is.", ex);
            return value;
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter MigrationChainTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Persistence/ISchemaVersioned.cs KhaozEngine.Persistence/MigrationChain.cs KhaozEngine.Tests/MigrationChainTests.cs
git commit -m "persistence: MigrationChain core (delegate form, ordered stepping)"
```

---

## Task 2: Build-time validation (fail-fast on misconfiguration)

**Files:**
- Test: `KhaozEngine.Tests/MigrationChainTests.cs`
- (No production change expected — `Build`/`Step` already validate. This task proves it.)

- [ ] **Step 1: Write the failing tests**

Add to `MigrationChainTests.cs` (inside the class):

```csharp
    [Fact]
    public void Build_GapInSteps_Throws()
    {
        var builder = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => p)    // 1 -> 2
            .Step(3, p => p);   // 3 -> 4, but 2 -> 3 is missing
        Assert.Throws<ArgumentException>(() => builder.Build(4));
    }

    [Fact]
    public void Build_StepAtOrAboveCurrent_Throws()
    {
        var builder = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => p)
            .Step(2, p => p);   // targets 3, but current is 2
        Assert.Throws<ArgumentException>(() => builder.Build(2));
    }

    [Fact]
    public void Step_DuplicateFromVersion_Throws()
    {
        var builder = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v).Step(1, p => p);
        Assert.Throws<ArgumentException>(() => builder.Step(1, p => p));
    }

    [Fact]
    public void Build_EmptyChain_IsAllowed_AndNoOps()
    {
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v).Build(3);
        var result = chain.Migrate(new Poco { Ver = 1 });
        Assert.Equal(1, result.Ver);   // no steps, nothing to do
        Assert.Empty(result.Steps);
    }
```

- [ ] **Step 2: Run tests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter MigrationChainTests`
Expected: PASS (the validation in Task 1's `Build`/`Step` already covers these). If `Build_EmptyChain_IsAllowed_AndNoOps` fails because an empty chain warns/throws, that is a defect in Task 1's code — fix `Migrate` so `startVersion == currentVersion` for an empty chain makes every `v < currentVersion` value hit the "predates oldest step" branch and return unchanged (it does: with no steps, `startVersion = currentVersion`, so `v=1 < startVersion=3` returns unchanged). The assertion only checks the value is unchanged, so this passes.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/MigrationChainTests.cs
git commit -m "persistence: tests for MigrationChain build-time validation"
```

---

## Task 3: Lenient runtime edges (too-old warn, step-throws halt, delegate-throws swallow)

**Files:**
- Test: `KhaozEngine.Tests/MigrationChainTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `MigrationChainTests.cs`:

```csharp
    [Fact]
    public void Migrate_VersionBelowOldestStep_LeavesValueAndWarns()
    {
        var logger = new FakeLogger();
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(2, p => { p.Steps.Add(2); return p; })   // oldest step is from v2
            .Build(3);

        var result = chain.Migrate(new Poco { Ver = 1 }, logger);   // file is older than any step

        Assert.Equal(1, result.Ver);
        Assert.Empty(result.Steps);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warn);
    }

    [Fact]
    public void Migrate_StepThrows_HaltsAndKeepsCompletedSteps_AndLogsError()
    {
        var logger = new FakeLogger();
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => { p.Steps.Add(1); return p; })                       // 1 -> 2 ok
            .Step(2, p => throw new InvalidOperationException("boom"))          // 2 -> 3 throws
            .Step(3, p => { p.Steps.Add(3); return p; })                       // never reached
            .Build(4);

        var result = chain.Migrate(new Poco { Ver = 1 }, logger);

        Assert.Equal(2, result.Ver);                  // only the first step's bump stuck
        Assert.Equal(new[] { 1 }, result.Steps);      // step 3 never ran
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Migrate_GetVersionDelegateThrows_Swallowed_ReturnsValue_AndLogsError()
    {
        var logger = new FakeLogger();
        var chain = MigrationChain.For<Poco>(_ => throw new InvalidOperationException("bad get"), (p, v) => p.Ver = v)
            .Step(1, p => p)
            .Build(2);
        var value = new Poco { Ver = 1 };

        var result = chain.Migrate(value, logger);

        Assert.Same(value, result);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Migrate_NullValue_ReturnsNull()
    {
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v).Step(1, p => p).Build(2);
        Assert.Null(chain.Migrate(null!));
    }
```

- [ ] **Step 2: Run tests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter MigrationChainTests`
Expected: PASS — the Task 1 `Migrate` already implements all these branches (Warn for too-old, try/catch around the loop for step + delegate throws, null guard).

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/MigrationChainTests.cs
git commit -m "persistence: tests for MigrationChain lenient runtime behaviour"
```

---

## Task 4: Interface form (`For<T>()` over `ISchemaVersioned`)

**Files:**
- Test: `KhaozEngine.Tests/MigrationChainTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `MigrationChainTests.cs` (uses the `Doc` type already declared in Task 1):

```csharp
    [Fact]
    public void For_InterfaceForm_ReadsAndWritesSchemaVersion()
    {
        var chain = MigrationChain.For<Doc>()                       // zero-config: uses ISchemaVersioned
            .Step(1, d => { d.Steps.Add(1); return d; })
            .Step(2, d => { d.Steps.Add(2); return d; })
            .Build(3);

        var result = chain.Migrate(new Doc { SchemaVersion = 1 });

        Assert.Equal(3, result.SchemaVersion);
        Assert.Equal(new[] { 1, 2 }, result.Steps);
    }
```

- [ ] **Step 2: Run test**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter MigrationChainTests`
Expected: PASS — `MigrationChain.For<T>()` (interface overload) was implemented in Task 1.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/MigrationChainTests.cs
git commit -m "persistence: test MigrationChain ISchemaVersioned zero-config form"
```

---

## Task 5: `SettingsManager<T>` integration (chain runs before sanitize)

**Files:**
- Modify: `KhaozEngine.Persistence/SettingsManager.cs`
- Modify: `KhaozEngine.Tests/SettingsManagerTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `SettingsManagerTests.cs` (reuses the file's private `FakeStorage` and `Box`):

```csharp
    [Fact]
    public void Migrations_RunOnLoad_BeforeSanitize()
    {
        // Box starts at version 1 with Value 1. Chain bumps Value to 10 (v1->v2), sanitize then clamps to <= 5.
        var storage = new FakeStorage { ToLoad = new VersionedBox { SchemaVersion = 1, Value = 1 } };
        var chain = MigrationChain.For<VersionedBox>()
            .Step(1, b => { b.Value = 10; return b; })
            .Build(2);

        var mgr = new SettingsManager<VersionedBox>(
            storage, logger: null,
            sanitizeOnLoad: b => { b.Value = Math.Min(b.Value, 5); return b; },
            migrations: chain);

        Assert.Equal(2, mgr.Settings.SchemaVersion);   // chain ran
        Assert.Equal(5, mgr.Settings.Value);           // sanitize ran AFTER the chain (10 -> clamp 5)
    }

    [Fact]
    public void Migrations_RunOnInitialCtorLoad_NoSanitize()
    {
        var storage = new FakeStorage { ToLoad = new VersionedBox { SchemaVersion = 1, Value = 0 } };
        var chain = MigrationChain.For<VersionedBox>()
            .Step(1, b => { b.Value = 42; return b; })
            .Build(2);

        var mgr = new SettingsManager<VersionedBox>(storage, logger: null, sanitizeOnLoad: null, migrations: chain);

        Assert.Equal(2, mgr.Settings.SchemaVersion);
        Assert.Equal(42, mgr.Settings.Value);
    }

    [Fact]
    public void Migrations_Null_BehaviourUnchanged()
    {
        var storage = new FakeStorage { ToLoad = new VersionedBox { SchemaVersion = 1, Value = 7 } };
        var mgr = new SettingsManager<VersionedBox>(storage);   // no chain, no sanitize
        Assert.Equal(1, mgr.Settings.SchemaVersion);            // untouched
        Assert.Equal(7, mgr.Settings.Value);
    }
```

Also add this helper type near the bottom of the class (next to `Box`):

```csharp
    private sealed class VersionedBox : ISchemaVersioned
    {
        public int SchemaVersion { get; set; }
        public int Value { get; set; }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SettingsManagerTests`
Expected: FAIL to compile — `SettingsManager<T>` has no `migrations` parameter.

- [ ] **Step 3: Add the `migrations` parameter and run-before-sanitize**

In `KhaozEngine.Persistence/SettingsManager.cs`, add a field beside `sanitizeOnLoad`:

```csharp
    private readonly Func<T, T>? sanitizeOnLoad;
    private readonly MigrationChain<T>? migrations;
```

Change the ctor signature and body (append the optional param; keep the existing doc comments above it):

```csharp
    public SettingsManager(ISettingsStorage storage, ILogger? logger = null, Func<T, T>? sanitizeOnLoad = null, MigrationChain<T>? migrations = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        // Generic type name would render as "SettingsManager`1"; use a clean fixed category instead.
        this.logger = logger ?? Log.Get("SettingsManager");
        this.sanitizeOnLoad = sanitizeOnLoad;
        this.migrations = migrations;
        Load();
    }
```

In `Load()`, insert the chain step AFTER the load/fallback block and BEFORE the `sanitizeOnLoad` block:

```csharp
        if (migrations is not null)
        {
            loaded = migrations.Migrate(loaded, logger);
        }

        if (sanitizeOnLoad is not null)
        {
```

(Leave the existing `sanitizeOnLoad` block, assignment, and `SettingsLoaded` raise unchanged.)

Add this line to the ctor doc comment (after the existing `<param name="sanitizeOnLoad">` block):

```csharp
    /// <param name="migrations">
    /// Optional versioned migration chain run on every load BEFORE <paramref name="sanitizeOnLoad"/>: it
    /// steps the loaded value from its stored schema version up to the chain's current version. Null = no
    /// migration (back-compat). See <see cref="MigrationChain{T}"/>.
    /// </param>
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SettingsManagerTests`
Expected: PASS (all existing SettingsManager tests + the 3 new ones).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Persistence/SettingsManager.cs KhaozEngine.Tests/SettingsManagerTests.cs
git commit -m "persistence: SettingsManager runs an optional MigrationChain before sanitize"
```

---

## Task 6: `GameStorage` integration (`CreateSettingsManager` + raw `Load<T>`)

**Files:**
- Modify: `KhaozEngine.Persistence/GameStorage.cs`
- Modify: `KhaozEngine.Tests/SettingsManagerTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `SettingsManagerTests.cs` (the file already has `using System.IO;` and `using KhaozEngine.App;`, and uses `FakeAppDataEnvironment` + `AppDataPaths` + `FileSettingsStorage` in the corrupt-file test):

```csharp
    private sealed class SaveDoc : ISchemaVersioned
    {
        public int SchemaVersion { get; set; }
        public System.Collections.Generic.List<string> Items { get; set; } = new();
    }

    [Fact]
    public void GameStorage_Load_WithChain_MigratesOldFileToCurrent()
    {
        string root = Path.Combine(Path.GetTempPath(), "ke-migrate-" + Path.GetRandomFileName());
        try
        {
            var env = new FakeAppDataEnvironment { IsMacOS = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;
            var paths = new AppDataPaths("APKiwi", "MigrateSave", env);
            File.WriteAllText(paths.GetFilePath("save.json"), "{\"SchemaVersion\":1,\"Items\":[]}");

            using var storage = new GameStorage(paths);
            var chain = MigrationChain.For<SaveDoc>()
                .Step(1, s => { s.Items.Add("from-v1"); return s; })
                .Build(2);

            var loaded = storage.Load<SaveDoc>("save.json", chain);

            Assert.Equal(2, loaded.SchemaVersion);
            Assert.Contains("from-v1", loaded.Items);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GameStorage_CreateSettingsManager_ForwardsChain()
    {
        string root = Path.Combine(Path.GetTempPath(), "ke-migrate-sm-" + Path.GetRandomFileName());
        try
        {
            var env = new FakeAppDataEnvironment { IsMacOS = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;
            var paths = new AppDataPaths("APKiwi", "MigrateSm", env);

            using var storage = new GameStorage(paths);
            // Persist a v1 doc through the same storage, then flush so it lands on disk.
            File.WriteAllText(paths.GetFilePath(storage.Settings.SettingsFileName), "{\"SchemaVersion\":1,\"Value\":3}");

            var chain = MigrationChain.For<VersionedBox>()
                .Step(1, b => { b.Value += 100; return b; })
                .Build(2);

            var mgr = storage.CreateSettingsManager<VersionedBox>(sanitizeOnLoad: null, migrations: chain);

            Assert.Equal(2, mgr.Settings.SchemaVersion);
            Assert.Equal(103, mgr.Settings.Value);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
```

Note: `FileSettingsStorage`'s default settings file name is `settings.json`; the test writes to `storage.Settings.SettingsFileName` so it loads regardless. `VersionedBox` (added in Task 5) is reused; it serializes `SchemaVersion` + `Value`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SettingsManagerTests`
Expected: FAIL to compile — `Load<T>` and `CreateSettingsManager<T>` have no `migrations` parameter.

- [ ] **Step 3: Add the `migrations` parameter to both methods**

In `KhaozEngine.Persistence/GameStorage.cs`, replace `CreateSettingsManager`:

```csharp
    /// <summary>
    /// Builds a <see cref="SettingsManager{T}"/> over <see cref="Settings"/> (which loads on
    /// construction), using the facade's logger. <paramref name="sanitizeOnLoad"/> is applied after
    /// every load (clamp fields, normalize, etc.); <paramref name="migrations"/> is an optional versioned
    /// migration chain run before it.
    /// </summary>
    public SettingsManager<T> CreateSettingsManager<T>(Func<T, T>? sanitizeOnLoad = null, MigrationChain<T>? migrations = null) where T : new()
        => new SettingsManager<T>(Settings, logger, sanitizeOnLoad, migrations);
```

Replace `Load<T>` so it applies the chain after deserialize (and after the absent-file `new T()` path):

```csharp
    public T Load<T>(string fileName, MigrationChain<T>? migrations = null) where T : new()
    {
        string path = Paths.GetFilePath(fileName);
        T value;
        if (!File.Exists(path))
        {
            value = new T();
        }
        else
        {
            string content = File.ReadAllText(path);
            if (Encoder is not null && Encoder.IsEncoded(content))
            {
                content = Encoder.Decode(content) ?? content;
            }

            value = JsonSerializer.Deserialize<T>(content, JsonDefaults.TolerantRead) ?? new T();
        }

        return migrations is null ? value : migrations.Migrate(value, logger);
    }
```

Update the `Load<T>` doc comment's first line to mention the optional chain:

```csharp
    /// Loads <paramref name="fileName"/> and deserializes to <typeparamref name="T"/>, then runs the optional
    /// <paramref name="migrations"/> chain. Returns a new <typeparamref name="T"/> if the file is absent. If an
```

(Keep the rest of the existing comment.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SettingsManagerTests`
Expected: PASS (existing + 2 new GameStorage tests).

- [ ] **Step 5: Full suite green**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (whole suite).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Persistence/GameStorage.cs KhaozEngine.Tests/SettingsManagerTests.cs
git commit -m "persistence: GameStorage.Load + CreateSettingsManager accept an optional MigrationChain"
```

---

## Task 7: Docs + release ritual (7.31.0 -> 7.32.0)

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props` change `<KhaozEngine5xVersion>7.31.0</KhaozEngine5xVersion>` to `<KhaozEngine5xVersion>7.32.0</KhaozEngine5xVersion>`.

- [ ] **Step 2: CHANGELOG.md — add the newest-first entry**

Insert directly under the `# Changelog` preamble, above `## 7.31.0`:

```markdown
## 7.32.0

Reusable versioned save-migration chain in `KhaozEngine.Persistence`. Previously `SettingsManager<T>` offered only a single `sanitizeOnLoad: Func<T,T>` hook, so consumers hand-rolled all schema migration as one branching blob with a manual version bump inside that callback (e.g. Hardpoint's `CampaignSave.Sanitize`). New `MigrationChain<T>` is a standalone, immutable, validated chain of per-version steppers. Build it with the fluent `MigrationChain.For<T>(getVersion, setVersion)` (any POCO) or the zero-config `MigrationChain.For<T>()` for types implementing the new `ISchemaVersioned { int SchemaVersion }` interface, then register one `Step(fromVersion, Func<T,T>)` per version and `Build(currentVersion)`. Each step does ONLY the data transform; the chain stamps the version field after each successful step. `Build` is fail-fast: a gap in the step run, a duplicate `fromVersion`, or a step targeting at/beyond the current version throws `ArgumentException` at startup (a misconfigured chain can never reach runtime). `Migrate` is lenient on user data and never throws: a value already at/above current is a no-op (so a save from a newer build is left intact), a value older than the oldest step is logged at Warn and returned unchanged, and a step that throws is logged at Error and halts the chain returning the partially-migrated value (version stamped only for completed steps). Recommended convention: default a type's version field to the current version so a fresh `new T()` no-ops silently. Wired into `SettingsManager<T>` via a new optional `migrations` ctor arg that runs the chain on every load BEFORE `sanitizeOnLoad` (clamp/normalize still runs last), and into `GameStorage` via optional `migrations` params on `CreateSettingsManager<T>` and the raw `Load<T>` (which had no migration hook at all before). All additions are appended optional parameters, so existing call sites are byte-for-byte unchanged. Headless-tested: ordered stepping + auto-stamp, no-op at/above current, too-old Warn, step-throws-halts-with-partial, delegate-throws-swallowed, build-time gap/duplicate/out-of-range validation, both factory forms, `SettingsManager` order-before-sanitize + ctor-load + back-compat, and `GameStorage` load-with-chain + `CreateSettingsManager` forwarding. SemVer: additive, minor.
```

- [ ] **Step 3: CHANGENOTES.md — add the one-line digest**

Insert as the new first bullet under the intro line, above the `- **7.31.0**:` bullet:

```markdown
- **7.32.0**: Reusable versioned save-migration chain (`KhaozEngine.Persistence`). New `MigrationChain<T>` (built via `MigrationChain.For<T>(...)` + `Step(fromVersion, ...)` + `Build(currentVersion)`, with an opt-in `ISchemaVersioned` interface) steps a loaded value from its stored schema version up to current, then the normal sanitize pass runs. Build-time validation is fail-fast (gaps/dupes/out-of-range throw); runtime is lenient (never throws on a bad save: at/above current = no-op, too-old = Warn + unchanged, a throwing step halts with the partial value). Wired into `SettingsManager<T>` (optional `migrations` arg, runs before `sanitizeOnLoad`) and `GameStorage` (`CreateSettingsManager` + raw `Load<T>`). Replaces the single ad-hoc `sanitizeOnLoad` migration blob. Additive optional params, back-compat, headless-tested, minor.
```

- [ ] **Step 4: Update the three guard declarations to 7.32.0**

- `docs/CONSUMERS.md` line 7: `**Engine current version:** `7.31.0`` -> `**Engine current version:** `7.32.0``
- `docs/ROADMAP.md` line 3: `Current released version: **7.31.0**` -> `Current released version: **7.32.0**`
- `README.md` lines 122-125: change each `Version="7.31.0"` to `Version="7.32.0"` (all four `PackageReference` examples).

- [ ] **Step 5: USING-KHAOZENGINE.md — document the feature**

In `docs/USING-KHAOZENGINE.md`, the `KhaozEngine.Persistence` bullet is around line 715. Immediately AFTER that bullet's block (before the next package bullet), add a focused subsection:

```markdown

### Versioned save migrations (`MigrationChain<T>`)

`SettingsManager<T>` and `GameStorage` take an optional `MigrationChain<T>` that upgrades an old on-disk
schema to the current one on load, before the `sanitizeOnLoad` clamp pass. Register one stepper per version
instead of branching inside a single sanitize callback:

```csharp
// Type carries an int version field; implement ISchemaVersioned for the zero-config factory.
public sealed class CampaignSaveData : ISchemaVersioned
{
    public int SchemaVersion { get; set; } = 3;   // default = current, so a fresh save no-ops
    // ... fields ...
}

var migrations = MigrationChain.For<CampaignSaveData>()      // or For<T>(getVersion, setVersion) for a plain POCO
    .Step(1, d => { /* v1 -> v2 data change */ return d; })
    .Step(2, d => { /* v2 -> v3 data change */ return d; })
    .Build(currentVersion: 3);

// Settings file:
var mgr = storage.CreateSettingsManager<CampaignSaveData>(sanitizeOnLoad: Clamp, migrations: migrations);
// Or a raw save file:
var save = storage.Load<CampaignSaveData>("campaign.json", migrations);
```

Each `Step` does only the data transform; the chain stamps the version after each step. `Build` throws on a
gap, a duplicate `fromVersion`, or a step at/beyond `currentVersion` (caught at startup). `Migrate` never
throws on a bad save: a save at/above current is left untouched, one older than the oldest step is logged and
returned as-is, and a throwing step halts the chain with the partially-migrated value.
```

- [ ] **Step 6: Verify the doc-version guard passes**

Run: `./scripts/check-doc-versions.sh`
Expected: `ok` for all three declarations at `7.32.0`, exit 0.

- [ ] **Step 7: Full test suite green on the merged result**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Pack to local-feed**

Run:
```bash
mkdir -p local-feed && dotnet pack -c Release -o ./local-feed
```
Expected: `KhaozEngine.Persistence.7.32.0.nupkg` (and the rest of the line) written to `local-feed/`.

- [ ] **Step 9: Commit the release**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md docs/USING-KHAOZENGINE.md
git commit -m "persistence(7.32.0): versioned save-migration chain (MigrationChain<T>)"
```

- [ ] **Step 10: Finish — merge, tag, push (deliberate final action)**

This is the engine release ritual finish. Merge the branch into `main`, run the suite on the merged result, then:

```bash
git tag v7.32.0
git push origin main
git push origin v7.32.0
```

Then clean up the worktree/branch. (Pushing `main` + the `v*` tag triggers CI publish to GitHub Packages — this is the engine's standing release behaviour. Hold only if the user is batching other engine work.)

---

## Self-review notes

- **Spec coverage:** ISchemaVersioned + delegate form (T1, T4), standalone chain (T1), fail-fast Build (T2), lenient runtime incl. too-old/step-throws/delegate-throws/no-op (T1, T3), SettingsManager integration before sanitize (T5), GameStorage both surfaces (T6), tests mirroring SettingsManagerTests style (T5/T6 reuse FakeStorage + file helpers), release ritual incl. the three guards + USING doc + pack + tag (T7). All spec sections map to a task.
- **Deviation from spec wording:** duplicate-`fromVersion` is rejected at `Step(...)` time (a `Dictionary` cannot hold a dup) rather than at `Build`. Same guarantee — misconfiguration caught before runtime — just earlier. Test asserts the throw at `Step`.
- **Type consistency:** `MigrationChain.For<T>(get,set)` / `For<T>()`, `MigrationChainBuilder<T>.Step(int, Func<T,T>)` / `.Build(int)`, `MigrationChain<T>.Migrate(T, ILogger?)` / `.CurrentVersion`, `ISchemaVersioned.SchemaVersion`, `SettingsManager<T>(.., migrations)`, `GameStorage.Load<T>(file, migrations)` / `CreateSettingsManager<T>(sanitize, migrations)` — used identically across all tasks. Test helpers `Poco`/`Doc` (MigrationChainTests) and `VersionedBox`/`SaveDoc` (SettingsManagerTests) are each declared once and reused.
```
