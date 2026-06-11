# Atomic JSON Writer + Persistence Queue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote the duplicated crash-safe save-write machinery from Nullwake/SpaceGame into `KhaozEngine.Persistence` as a synchronous `AtomicJsonWriter` and a coalesced async `PersistenceQueue`.

**Architecture:** `AtomicJsonWriter` is a static temp-file-then-move primitive (throws on failure). `PersistenceQueue` (implements the coordinator-fixed `IPersistenceQueue` seam) layers per-path coalescing, a schedule-on-demand ThreadPool worker, transient retry with backoff, a `WriteFailed` event, optional `ILogger` logging, and a blocking `Flush()` / `IDisposable` for clean shutdown. Both take explicit string paths plus `AppDataPaths` convenience overloads.

**Tech Stack:** net10.0, System.Text.Json, System.Threading (BCL only). Depends on `KhaozEngine.Diagnostics` (ILogger, already referenced) and adds `KhaozEngine.App` (AppDataPaths). xUnit headless tests in `KhaozEngine.Tests`.

**Baseline:** 268 tests passing before any change. Branch: `worktree-batch2-item8-persistence-queue`. Spec: `docs/superpowers/specs/2026-06-11-persistence-queue-design.md`.

---

## File Structure

New files in `KhaozEngine.Persistence/`:
- `IPersistenceQueue.cs` — coordinator-fixed seam (verbatim text; item 10 adds identical text).
- `AtomicJsonWriter.cs` — static synchronous atomic write.
- `PersistenceWriteFailedEventArgs.cs` — failure event payload.
- `PersistenceQueue.cs` — coalesced async writer.

New test files in `KhaozEngine.Tests/`:
- `AtomicJsonWriterTests.cs`
- `PersistenceQueueTests.cs`

Modified:
- `KhaozEngine.Persistence/KhaozEngine.Persistence.csproj` — add `KhaozEngine.App` ProjectReference.
- `KhaozEngine.Persistence/README.md` — document the new types.

Reused test helpers (already in `KhaozEngine.Tests`, namespace `KhaozEngine.Tests`):
- `FakeAppDataEnvironment` (internal, in `AppDataPathsTests.cs`) — drives `new AppDataPaths(folder, env)`.
- `FakeLogger` (internal, in `SaveEncoderTests.cs`) — captures `Entries` of `(LogLevel Level, string Message)`.

All commands run from the worktree root: `/Users/antonio/KhaozEngine/.claude/worktrees/batch2-item8-persistence-queue`.

---

## Task 1: Add KhaozEngine.App project reference to Persistence

**Files:**
- Modify: `KhaozEngine.Persistence/KhaozEngine.Persistence.csproj`

- [ ] **Step 1: Add the ProjectReference**

In `KhaozEngine.Persistence/KhaozEngine.Persistence.csproj`, change the existing `ItemGroup` that holds the Diagnostics reference from:

```xml
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj" />
  </ItemGroup>
```

to:

```xml
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.App/KhaozEngine.App.csproj" />
    <ProjectReference Include="../KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Verify it still builds**

Run: `dotnet build KhaozEngine.Persistence/KhaozEngine.Persistence.csproj`
Expected: Build succeeded (no cycle — `KhaozEngine.App` has no project references).

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Persistence/KhaozEngine.Persistence.csproj
git commit -m "Persistence: reference KhaozEngine.App for AppDataPaths overloads"
```

> Coordinator note: item 10 adds the same reference. If it lands first, rebase this commit (it is a one-line ItemGroup edit and merges cleanly).

---

## Task 2: Add the IPersistenceQueue seam

**Files:**
- Create: `KhaozEngine.Persistence/IPersistenceQueue.cs`

- [ ] **Step 1: Create the interface (verbatim, coordinator-fixed)**

Create `KhaozEngine.Persistence/IPersistenceQueue.cs` with EXACTLY this text (item 10 adds the identical text in its branch; byte-identical so the two copies merge with zero conflict — do not reword the comments):

```csharp
namespace KhaozEngine.Persistence;

public interface IPersistenceQueue
{
    // Enqueue a write of json to path; rapid repeats to the same path coalesce
    // (per-path, last-writer-wins).
    void Enqueue(string path, string json);
    // Flush all pending writes synchronously (e.g. on shutdown).
    void Flush();
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build KhaozEngine.Persistence/KhaozEngine.Persistence.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Persistence/IPersistenceQueue.cs
git commit -m "Persistence: add IPersistenceQueue seam (item 10 consumes this)"
```

---

## Task 3: AtomicJsonWriter

**Files:**
- Create: `KhaozEngine.Persistence/AtomicJsonWriter.cs`
- Test: `KhaozEngine.Tests/AtomicJsonWriterTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/AtomicJsonWriterTests.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class AtomicJsonWriterTests
{
    private sealed record Sample(string Name, int Value);

    [Fact]
    public void WriteText_CreatesFileWithContents()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "a.json");
            AtomicJsonWriter.WriteText(path, "hello");
            Assert.Equal("hello", File.ReadAllText(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WriteText_OverwritesExistingFile()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "a.json");
            AtomicJsonWriter.WriteText(path, "first");
            AtomicJsonWriter.WriteText(path, "second");
            Assert.Equal("second", File.ReadAllText(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WriteText_CreatesMissingParentDirectory()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "nested", "deep", "a.json");
            AtomicJsonWriter.WriteText(path, "x");
            Assert.True(File.Exists(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WriteText_LeavesNoTempFile()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "a.json");
            AtomicJsonWriter.WriteText(path, "x");
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Write_RoundTripsValueAsIndentedJson()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "a.json");
            var value = new Sample("n", 7);
            AtomicJsonWriter.Write(path, value);
            string json = File.ReadAllText(path);
            Assert.Contains("\n", json); // WriteIndented default
            Sample? back = JsonSerializer.Deserialize<Sample>(json);
            Assert.Equal(value, back);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WriteText_AppDataPathsOverload_WritesToResolvedFilePath()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;
            var paths = new AppDataPaths("MyGame", env);

            AtomicJsonWriter.WriteText(paths, "save.json", "data");

            Assert.Equal("data", File.ReadAllText(paths.GetFilePath("save.json")));
        }
        finally { Cleanup(root); }
    }

    private static string NewTempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-atomicwriter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AtomicJsonWriterTests"`
Expected: FAIL — does not compile, `AtomicJsonWriter` does not exist.

- [ ] **Step 3: Implement AtomicJsonWriter**

Create `KhaozEngine.Persistence/AtomicJsonWriter.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.App;

namespace KhaozEngine.Persistence;

/// <summary>
/// Writes text or JSON to disk crash-safely: content goes to a sibling <c>.tmp</c> file which is then
/// moved over the target, so a crash mid-write never leaves a half-written destination. Synchronous and
/// <b>throws</b> on IO failure; the caller decides whether to catch. For fire-and-forget background
/// writes that coalesce and retry, use <see cref="PersistenceQueue"/>.
/// </summary>
public static class AtomicJsonWriter
{
    private static readonly JsonSerializerOptions DefaultOptions = new() { WriteIndented = true };

    /// <summary>Atomically writes <paramref name="contents"/> to <paramref name="path"/>, creating the parent directory if needed.</summary>
    public static void WriteText(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(contents);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>Serializes <paramref name="value"/> to JSON (indented by default) and atomically writes it to <paramref name="path"/>.</summary>
    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
        => WriteText(path, JsonSerializer.Serialize(value, options ?? DefaultOptions));

    /// <summary>Atomically writes <paramref name="contents"/> to <paramref name="fileName"/> inside the app-data directory.</summary>
    public static void WriteText(AppDataPaths paths, string fileName, string contents)
    {
        ArgumentNullException.ThrowIfNull(paths);
        WriteText(paths.GetFilePath(fileName), contents);
    }

    /// <summary>Serializes <paramref name="value"/> and atomically writes it to <paramref name="fileName"/> inside the app-data directory.</summary>
    public static void Write<T>(AppDataPaths paths, string fileName, T value, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Write(paths.GetFilePath(fileName), value, options);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AtomicJsonWriterTests"`
Expected: PASS — 6 tests.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Persistence/AtomicJsonWriter.cs KhaozEngine.Tests/AtomicJsonWriterTests.cs
git commit -m "Persistence: add AtomicJsonWriter (synchronous temp-then-move)"
```

---

## Task 4: PersistenceQueue core (enqueue, coalesce, flush, dispose)

**Files:**
- Create: `KhaozEngine.Persistence/PersistenceQueue.cs`
- Test: `KhaozEngine.Tests/PersistenceQueueTests.cs`

This task builds the queue WITHOUT retry or the failure event. The worker performs a single write attempt and logs an Error on failure (Task 5 replaces that with retry + the `WriteFailed` event). The constructor already accepts `maxAttempts`/`retryDelay` so the signature is stable across tasks; they are stored now and used in Task 5.

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/PersistenceQueueTests.cs`:

```csharp
using System;
using System.IO;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class PersistenceQueueTests
{
    [Fact]
    public void EnqueueThenFlush_WritesFileToDisk()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "save.json");
            using var queue = new PersistenceQueue();

            queue.Enqueue(path, "payload");
            queue.Flush();

            Assert.True(File.Exists(path));
            Assert.Equal("payload", File.ReadAllText(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Enqueue_SamePathRepeatedly_LastWriteWins()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "save.json");
            using var queue = new PersistenceQueue();

            queue.Enqueue(path, "a");
            queue.Enqueue(path, "b");
            queue.Enqueue(path, "c");
            queue.Flush();

            Assert.Equal("c", File.ReadAllText(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Enqueue_DifferentPaths_BothWritten()
    {
        string root = NewTempRoot();
        try
        {
            string p1 = Path.Combine(root, "save.json");
            string p2 = Path.Combine(root, "settings.json");
            using var queue = new PersistenceQueue();

            queue.Enqueue(p1, "one");
            queue.Enqueue(p2, "two");
            queue.Flush();

            Assert.Equal("one", File.ReadAllText(p1));
            Assert.Equal("two", File.ReadAllText(p2));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Dispose_FlushesPendingWrite_AndBlocksFurtherEnqueue()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "save.json");
            var queue = new PersistenceQueue();

            queue.Enqueue(path, "data");
            queue.Dispose();

            Assert.Equal("data", File.ReadAllText(path));
            Assert.Throws<ObjectDisposedException>(() => queue.Enqueue(path, "more"));
        }
        finally { Cleanup(root); }
    }

    internal static string NewTempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-persistqueue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PersistenceQueueTests"`
Expected: FAIL — does not compile, `PersistenceQueue` does not exist.

- [ ] **Step 3: Implement the queue core**

Create `KhaozEngine.Persistence/PersistenceQueue.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using KhaozEngine.App;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Persistence;

/// <summary>
/// Coalesced asynchronous JSON writer. Each <c>Enqueue</c> records the latest payload per target path
/// (rapid repeats to one path collapse to the last) and schedules a single background ThreadPool worker
/// that drains pending writes via <see cref="AtomicJsonWriter"/>. Writes never throw into the caller;
/// failures retry briefly, then log and raise <see cref="WriteFailed"/>. <see cref="Flush"/> blocks until
/// the queue is drained (use on shutdown); the type is <see cref="IDisposable"/> and flushes on dispose.
/// </summary>
public sealed class PersistenceQueue : IPersistenceQueue, IDisposable
{
    private static readonly JsonSerializerOptions DefaultOptions = new() { WriteIndented = true };

    private readonly object sync = new();
    private readonly Dictionary<string, string> pending = new(StringComparer.Ordinal);
    private readonly ILogger? logger;
    private readonly int maxAttempts;
    private readonly TimeSpan retryDelay;
    private bool workerScheduled;
    private bool disposed;

    /// <summary>Creates a queue. <paramref name="maxAttempts"/> total write attempts per payload (>= 1); <paramref name="retryDelay"/> backoff between attempts (default 50 ms). Pass an <paramref name="logger"/> to record failures.</summary>
    public PersistenceQueue(ILogger? logger = null, int maxAttempts = 3, TimeSpan? retryDelay = null)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "At least one attempt is required.");
        }

        this.logger = logger;
        this.maxAttempts = maxAttempts;
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(50);
    }

    /// <inheritdoc/>
    public void Enqueue(string path, string json)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(json);

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending[path] = json;
            if (workerScheduled)
            {
                return;
            }

            workerScheduled = true;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static state => ((PersistenceQueue)state!).DrainPending(), this);
    }

    /// <summary>Serializes <paramref name="value"/> (indented by default) and enqueues it for <paramref name="path"/>.</summary>
    public void Enqueue<T>(string path, T value, JsonSerializerOptions? options = null)
        => Enqueue(path, JsonSerializer.Serialize(value, options ?? DefaultOptions));

    /// <inheritdoc/>
    public void Flush()
    {
        lock (sync)
        {
            while (pending.Count > 0 || workerScheduled)
            {
                Monitor.Wait(sync);
            }
        }
    }

    /// <summary>Flushes all pending writes, then disposes. Enqueuing after dispose throws.</summary>
    public void Dispose()
    {
        Flush();
        lock (sync)
        {
            disposed = true;
        }
    }

    private void DrainPending()
    {
        while (true)
        {
            string path;
            string json;

            lock (sync)
            {
                if (pending.Count == 0)
                {
                    workerScheduled = false;
                    Monitor.PulseAll(sync);
                    return;
                }

                path = string.Empty;
                json = string.Empty;
                foreach (KeyValuePair<string, string> entry in pending)
                {
                    path = entry.Key;
                    json = entry.Value;
                    break;
                }

                pending.Remove(path);
            }

            WriteOne(path, json);
        }
    }

    private void WriteOne(string path, string json)
    {
        try
        {
            AtomicJsonWriter.WriteText(path, json);
        }
        catch (Exception ex)
        {
            logger?.Error($"[PersistenceQueue] write to '{path}' failed", ex);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PersistenceQueueTests"`
Expected: PASS — 4 tests.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Persistence/PersistenceQueue.cs KhaozEngine.Tests/PersistenceQueueTests.cs
git commit -m "Persistence: add PersistenceQueue core (coalesce, flush, dispose)"
```

---

## Task 5: PersistenceQueue failure handling (retry, WriteFailed event, logging)

**Files:**
- Create: `KhaozEngine.Persistence/PersistenceWriteFailedEventArgs.cs`
- Modify: `KhaozEngine.Persistence/PersistenceQueue.cs` (replace `WriteOne` with retry + event; add the event)
- Test: `KhaozEngine.Tests/PersistenceQueueTests.cs` (add failure tests)

- [ ] **Step 1: Write the failing tests**

Add these two tests inside the `PersistenceQueueTests` class in `KhaozEngine.Tests/PersistenceQueueTests.cs` (before the `NewTempRoot` helper). They need `using KhaozEngine.Diagnostics;` (for `LogLevel`) — add it to the file's using block.

```csharp
    [Fact]
    public void Enqueue_PermanentFailure_RaisesWriteFailedAndLogsAndDoesNotThrow()
    {
        string root = NewTempRoot();
        try
        {
            // Make the parent path a FILE so Directory.CreateDirectory throws on every attempt.
            string blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "x");
            string badPath = Path.Combine(blocker, "save.json");

            var log = new FakeLogger();
            using var queue = new PersistenceQueue(log, maxAttempts: 2, retryDelay: TimeSpan.FromMilliseconds(1));
            PersistenceWriteFailedEventArgs? failure = null;
            queue.WriteFailed += (_, e) => failure = e;

            queue.Enqueue(badPath, "data"); // must not throw
            queue.Flush();

            Assert.NotNull(failure);
            Assert.Equal(badPath, failure!.Path);
            Assert.Equal(2, failure.AttemptCount);
            Assert.NotNull(failure.Exception);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WriteFailed_SubscriberThrows_DoesNotKillWriter()
    {
        string root = NewTempRoot();
        try
        {
            string blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "x");
            string badPath = Path.Combine(blocker, "save.json");
            string goodPath = Path.Combine(root, "good.json");

            using var queue = new PersistenceQueue(maxAttempts: 1, retryDelay: TimeSpan.FromMilliseconds(1));
            queue.WriteFailed += (_, _) => throw new InvalidOperationException("subscriber blew up");

            queue.Enqueue(badPath, "data"); // triggers the throwing subscriber on the worker thread
            queue.Enqueue(goodPath, "ok");  // worker must survive and still service this
            queue.Flush();

            Assert.Equal("ok", File.ReadAllText(goodPath));
        }
        finally { Cleanup(root); }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PersistenceQueueTests"`
Expected: FAIL — `PersistenceWriteFailedEventArgs` and `PersistenceQueue.WriteFailed` do not exist (does not compile).

- [ ] **Step 3: Create the event-args type**

Create `KhaozEngine.Persistence/PersistenceWriteFailedEventArgs.cs`:

```csharp
using System;

namespace KhaozEngine.Persistence;

/// <summary>
/// Raised by <see cref="PersistenceQueue"/> when a write to a path has failed after all retry attempts.
/// </summary>
public sealed class PersistenceWriteFailedEventArgs : EventArgs
{
    /// <summary>Creates the event payload.</summary>
    public PersistenceWriteFailedEventArgs(string path, Exception exception, int attemptCount)
    {
        Path = path;
        Exception = exception;
        AttemptCount = attemptCount;
    }

    /// <summary>The target path the write was destined for.</summary>
    public string Path { get; }

    /// <summary>The exception from the final failed attempt.</summary>
    public Exception Exception { get; }

    /// <summary>How many attempts were made before giving up (equals the queue's configured max attempts).</summary>
    public int AttemptCount { get; }
}
```

- [ ] **Step 4: Add the WriteFailed event to PersistenceQueue**

In `KhaozEngine.Persistence/PersistenceQueue.cs`, add the event declaration immediately after the field block (right before the constructor):

```csharp
    /// <summary>Raised on the background worker thread when a write fails after all retry attempts. A subscriber's own exception is caught and logged, never killing the writer.</summary>
    public event EventHandler<PersistenceWriteFailedEventArgs>? WriteFailed;
```

- [ ] **Step 5: Replace WriteOne with retry + event**

In `KhaozEngine.Persistence/PersistenceQueue.cs`, replace the entire `WriteOne` method:

```csharp
    private void WriteOne(string path, string json)
    {
        try
        {
            AtomicJsonWriter.WriteText(path, json);
        }
        catch (Exception ex)
        {
            logger?.Error($"[PersistenceQueue] write to '{path}' failed", ex);
        }
    }
```

with:

```csharp
    private void WriteWithRetry(string path, string json)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                AtomicJsonWriter.WriteText(path, json);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger?.Warn($"[PersistenceQueue] write to '{path}' failed (attempt {attempt}/{maxAttempts}), retrying", ex);
                if (retryDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(retryDelay);
                }
            }
            catch (Exception ex)
            {
                logger?.Error($"[PersistenceQueue] write to '{path}' failed after {maxAttempts} attempts, giving up", ex);
                RaiseWriteFailed(path, ex, attempt);
                return;
            }
        }
    }

    private void RaiseWriteFailed(string path, Exception exception, int attemptCount)
    {
        EventHandler<PersistenceWriteFailedEventArgs>? handler = WriteFailed;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new PersistenceWriteFailedEventArgs(path, exception, attemptCount));
        }
        catch (Exception ex)
        {
            logger?.Error("[PersistenceQueue] a WriteFailed subscriber threw", ex);
        }
    }
```

- [ ] **Step 6: Point the worker at the new method**

In `KhaozEngine.Persistence/PersistenceQueue.cs`, in `DrainPending`, change the call `WriteOne(path, json);` to:

```csharp
            WriteWithRetry(path, json);
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PersistenceQueueTests"`
Expected: PASS — 6 tests.

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.Persistence/PersistenceWriteFailedEventArgs.cs KhaozEngine.Persistence/PersistenceQueue.cs KhaozEngine.Tests/PersistenceQueueTests.cs
git commit -m "Persistence: add retry + WriteFailed event to PersistenceQueue"
```

---

## Task 6: PersistenceQueue AppDataPaths overloads

**Files:**
- Modify: `KhaozEngine.Persistence/PersistenceQueue.cs` (add `AppDataPaths` enqueue overloads)
- Test: `KhaozEngine.Tests/PersistenceQueueTests.cs` (add overload tests)

- [ ] **Step 1: Write the failing tests**

Add these tests inside `PersistenceQueueTests` (before the `NewTempRoot` helper). They need `using KhaozEngine.App;` — add it to the file's using block.

```csharp
    [Fact]
    public void Enqueue_AppDataPathsOverload_WritesToResolvedFilePath()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;
            var paths = new AppDataPaths("MyGame", env);

            using var queue = new PersistenceQueue();
            queue.Enqueue(paths, "save.json", "data");
            queue.Flush();

            Assert.Equal("data", File.ReadAllText(paths.GetFilePath("save.json")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void EnqueueGeneric_SerializesValueAsJson()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "save.json");
            using var queue = new PersistenceQueue();

            queue.Enqueue(path, new { Score = 42 });
            queue.Flush();

            string json = File.ReadAllText(path);
            Assert.Contains("\"Score\": 42", json);
        }
        finally { Cleanup(root); }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PersistenceQueueTests"`
Expected: FAIL — no `Enqueue(AppDataPaths, string, string)` overload (does not compile). (`EnqueueGeneric_SerializesValueAsJson` uses the `Enqueue<T>` added in Task 4 and will compile, but the file fails to build until the AppDataPaths overload exists.)

- [ ] **Step 3: Add the AppDataPaths overloads**

In `KhaozEngine.Persistence/PersistenceQueue.cs`, add these two methods immediately after the existing `Enqueue<T>(string, T, JsonSerializerOptions?)` method:

```csharp
    /// <summary>Enqueues a write of <paramref name="json"/> to <paramref name="fileName"/> inside the app-data directory.</summary>
    public void Enqueue(AppDataPaths paths, string fileName, string json)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Enqueue(paths.GetFilePath(fileName), json);
    }

    /// <summary>Serializes <paramref name="value"/> and enqueues it to <paramref name="fileName"/> inside the app-data directory.</summary>
    public void Enqueue<T>(AppDataPaths paths, string fileName, T value, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Enqueue(paths.GetFilePath(fileName), value, options);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PersistenceQueueTests"`
Expected: PASS — 8 tests.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Persistence/PersistenceQueue.cs KhaozEngine.Tests/PersistenceQueueTests.cs
git commit -m "Persistence: add AppDataPaths enqueue overloads to PersistenceQueue"
```

---

## Task 7: Document the new types in the package README

**Files:**
- Modify: `KhaozEngine.Persistence/README.md`

- [ ] **Step 1: Read the current README**

Run: `cat KhaozEngine.Persistence/README.md`
Note its existing structure (it currently documents `SaveEncoder`). Match its heading style and tone.

- [ ] **Step 2: Append sections for the new types**

Add the following to `KhaozEngine.Persistence/README.md` after the existing `SaveEncoder` content (adjust heading level to match the file):

```markdown
## AtomicJsonWriter

Static crash-safe writer: content is written to a sibling `.tmp` file then moved over the target, so a
crash mid-write never leaves a half-written destination. Synchronous and throws on IO failure.

```csharp
AtomicJsonWriter.WriteText(path, json);
AtomicJsonWriter.Write(path, myValue);              // serialize (indented) then write
AtomicJsonWriter.Write(appDataPaths, "save.json", myValue);
```

## PersistenceQueue

Coalesced asynchronous writer (`IPersistenceQueue`). Each `Enqueue` records the latest payload per target
path (rapid repeats to one path collapse to the last) and a background worker drains them via
`AtomicJsonWriter`. Writes never throw into the caller; they retry briefly, then log via the optional
`ILogger` and raise `WriteFailed`. `Flush()` blocks until drained and the queue is `IDisposable`
(disposing flushes), so games can guarantee a clean write on shutdown.

```csharp
using var queue = new PersistenceQueue(logger);     // optional logger, maxAttempts, retryDelay
queue.WriteFailed += (_, e) => Notify(e.Path, e.Exception);
queue.Enqueue(appDataPaths, "save.json", saveData); // or Enqueue(path, json) / Enqueue<T>(path, value)
// on shutdown:
queue.Flush();
```
```

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Persistence/README.md
git commit -m "Persistence: document AtomicJsonWriter and PersistenceQueue in README"
```

---

## Task 8: Full suite green

**Files:** none (verification only)

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS — 268 baseline + 14 new = **282 tests**, 0 failed.

- [ ] **Step 2: Confirm the package builds in isolation**

Run: `dotnet build KhaozEngine.Persistence/KhaozEngine.Persistence.csproj -c Release`
Expected: Build succeeded.

> Do NOT bump `Directory.Build.props` `<Version>`, edit `CHANGELOG.md`, or `dotnet pack` into the shared `local-feed`. The coordinating chat owns the batched 3.3.0 release.

---

## Done criteria

- `IPersistenceQueue`, `AtomicJsonWriter`, `PersistenceQueue`, `PersistenceWriteFailedEventArgs` public in `KhaozEngine.Persistence`.
- `PersistenceQueue : IPersistenceQueue, IDisposable`; `WriteFailed` event and retry policy on the concrete class only.
- `KhaozEngine.Persistence -> KhaozEngine.App` ProjectReference added.
- 14 new tests, full suite at 282, baseline behavior unchanged.
- No version/changelog/pack/feed changes (coordinator owns the release).
