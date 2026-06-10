# Atomic JSON writer + coalesced async persistence queue

Batch 2, item 8. Promote the duplicated crash-safe save-write machinery out of the games into the
existing `KhaozEngine.Persistence` package.

## Problem

Three games hand-roll the same "write JSON to disk without corrupting it on a crash" logic:

- **Nullwake `LocalSaveSystem`** writes synchronously: serialize, `File.WriteAllText(path + ".tmp")`,
  `File.Move(tmp, path, overwrite: true)`. Atomic, but blocks the caller.
- **SpaceGame `SaveSystem`** (static) and **`BaseSettingsStorage`** (instance) carry an *identical*
  coalesced-async writer, duplicated between the two: a single pending `(path, tempPath, json)` slot
  guarded by a lock plus a `saveWorkerScheduled` flag, a one-shot `ThreadPool.UnsafeQueueUserWorkItem`
  worker, and a flush loop that drains the latest pending and does temp-write-then-move. Both swallow
  IO errors silently and **neither flushes on shutdown** (fire-and-forget: a pending write is lost if
  the process exits before the worker runs).

These are the same primitive at two fidelities. Unify on the async coalesced model, promote it once,
and close the gaps (shutdown safety, failure visibility) the games never had.

## Scope

In scope: the atomic-write primitive and the coalescing async queue, plus headless tests. Out of
scope (game-specific, stays in the games): `SaveEncoder` (already promoted in Batch 1), save schema /
migration / sanitize logic, the `ISaveSystem` / `ISettingsStorage` interfaces, and `SettingsManager`
(that is item 10, which will build *on top of* this queue).

## Components

All three new files land in `KhaozEngine.Persistence/`. They are deliberately separate files so item 10
can extend the package without editing the same files.

### `AtomicJsonWriter.cs` — synchronous atomic-write primitive

Static class. The crash-safe write both games do inline, extracted once.

```csharp
public static class AtomicJsonWriter
{
    // Create the parent directory if missing, write `contents` to `path + ".tmp"`,
    // then File.Move(tmp, path, overwrite: true). Throws on IO failure.
    public static void WriteText(string path, string contents);

    // Serialize `value` (default options: WriteIndented = true, matching the games'
    // on-disk format) then WriteText.
    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null);

    // AppDataPaths convenience: resolve paths.GetFilePath(fileName), then the above.
    public static void WriteText(AppDataPaths paths, string fileName, string contents);
    public static void Write<T>(AppDataPaths paths, string fileName, T value, JsonSerializerOptions? options = null);
}
```

Synchronous and **throws** on IO failure: the caller decides whether to catch. This is the primitive
Nullwake's synchronous save path uses directly (no queue), and it is the single place that performs
the temp-then-move so the queue does not re-implement it.

### `PersistenceQueue.cs` — coalesced async writer (`sealed`, `IDisposable`)

The machinery duplicated between SpaceGame's `SaveSystem` and `BaseSettingsStorage`, unified and
generalized to multiple target paths.

```csharp
public sealed class PersistenceQueue : IDisposable
{
    public PersistenceQueue(ILogger? logger = null, int maxAttempts = 3, TimeSpan? retryDelay = null);

    public event EventHandler<PersistenceWriteFailedEventArgs>? WriteFailed;

    public void Enqueue(string path, string json);
    public void Enqueue<T>(string path, T value, JsonSerializerOptions? options = null);
    public void Enqueue(AppDataPaths paths, string fileName, string json);
    public void Enqueue<T>(AppDataPaths paths, string fileName, T value, JsonSerializerOptions? options = null);

    public void Flush();    // block until the queue is drained and no write is in flight
    public void Dispose();  // Flush() then seal; Enqueue after Dispose throws ObjectDisposedException
}
```

**Per-path coalescing.** Pending writes live in a `Dictionary<string, string>` keyed by the resolved
path. Enqueuing the same path again before the worker picks it up overwrites the entry (last write
wins); different paths (`save.json` + `settings.json`) coexist and both get written. This generalizes
both sources, each of which only ever drove one file.

**Serialize on the caller thread.** The `Enqueue<T>` overloads serialize immediately and store the
resulting string, so the snapshot reflects state at enqueue time (matching the games, which serialize
in `Save()` before queuing). Only the raw IO is deferred to the worker.

**Threading: schedule-on-demand ThreadPool worker.** Exactly the sources' model. A lock guards
`_pending` and a `_workerScheduled` flag. `Enqueue` sets the pending entry and, only if no worker is
already scheduled, queues one via `ThreadPool.UnsafeQueueUserWorkItem`. The worker loops: under the
lock, if `_pending` is empty it sets `_workerScheduled = false`, pulses any `Flush` waiters, and
returns; otherwise it removes one entry, releases the lock, and writes it. This means **zero idle
cost** — no thread is held when nothing is queued. (Alternative considered: a dedicated long-lived
background thread + blocking channel. Cleaner shutdown, but always holds a thread; rejected to keep
idle cost zero and stay faithful to the proven source behavior.)

**Retry with backoff.** Each write is attempted up to `maxAttempts` (default 3); on an IO exception
the worker waits `retryDelay` (default ~50 ms) and retries the captured snapshot. The common failures
here are transient (file locked by AV / cloud-sync, momentary disk pressure). A newer `Enqueue` to the
same path during a retry lands as a fresh pending entry and is written after the current attempt
finishes, so last-write-wins still holds.

**Failure handling.** Never throws into the `Enqueue` caller — async writes must not crash the game
loop. On a failed attempt it logs `Warn` via the optional `ILogger`; on final give-up after
`maxAttempts` it logs `Error` and raises `WriteFailed`. The event lets a game react (retry at a higher
level, show a "save failed" prompt, bump a telemetry counter). The event is raised on the worker
thread and wrapped in try/catch so a subscriber's own exception cannot kill the writer (it is caught
and logged).

**Flush / shutdown.** `Flush()` blocks (via `Monitor.Wait`) until `_pending` is empty *and*
`_workerScheduled` is false — i.e. the in-flight write, including any retry/backoff, has completed
(success or final failure). Because the worker only clears `_workerScheduled` after it loops back and
finds nothing pending, this precisely captures "no write in flight". `Dispose()` calls `Flush()` then
marks the queue disposed; any subsequent `Enqueue` throws `ObjectDisposedException`. This is the gap
both games have today.

### `PersistenceWriteFailedEventArgs.cs`

```csharp
public sealed class PersistenceWriteFailedEventArgs : EventArgs
{
    public string Path { get; }
    public Exception Exception { get; }
    public int AttemptCount { get; }   // attempts made before giving up (== maxAttempts)
}
```

## Wiring (the only project-level edits)

- `KhaozEngine.Persistence/KhaozEngine.Persistence.csproj`: add
  `<ProjectReference Include="../KhaozEngine.App/KhaozEngine.App.csproj" />`. Verified safe — `KhaozEngine.App`
  has no project references (pure BCL), so no cycle. **This is the one shared-package dependency change
  to flag to the coordinator.**
- No `.slnx` change: `KhaozEngine.Persistence` is already a solution member.
- No `KhaozEngine.Tests.csproj` change: it already references both `KhaozEngine.Persistence` and
  `KhaozEngine.App`.
- Update `KhaozEngine.Persistence/README.md` to mention the new types.
- Do **not** touch `Directory.Build.props` `<Version>`, `CHANGELOG.md`, or `local-feed`. The
  coordinating chat owns the batched 3.3.0 release.

## Testing

Headless xUnit in `KhaozEngine.Tests`, two new files. Each test writes under a fresh temp directory and
cleans up in a `finally`, matching `AppDataPathsTests`. AppDataPaths-overload tests construct
`new AppDataPaths(folder, FakeAppDataEnvironment{...})` (the internal ctor, reachable via the existing
`InternalsVisibleTo`) pointed at a temp root.

`AtomicJsonWriterTests.cs`
- `WriteText` creates the file with the given contents.
- `WriteText` overwrites an existing file.
- `WriteText` creates a missing parent directory.
- No `.tmp` file remains after a successful write.
- `Write<T>` produces JSON that round-trips back to an equal value; default output is indented.
- An `AppDataPaths` overload writes to `paths.GetFilePath(fileName)`.

`PersistenceQueueTests.cs`
- Per-path coalescing: several rapid `Enqueue` calls to one path, then `Flush()`, leaves the file with
  the *last* payload.
- Two different paths both land with their correct contents after `Flush()`.
- `Flush()` guarantees the file is on disk by the time it returns.
- Permanent failure (target path engineered to fail the move, e.g. the destination already exists as a
  directory) raises `WriteFailed` exactly once with `AttemptCount == maxAttempts`, logs an `Error`
  through a capturing test `ILogger`, and never throws out of `Enqueue`/`Flush`. Uses a small
  `maxAttempts` and tiny `retryDelay` for speed.
- A `WriteFailed` subscriber that throws does not kill the writer: a later good write still flushes.
- `Dispose()` flushes a pending write; a post-`Dispose` `Enqueue` throws `ObjectDisposedException`.

Expected delta: +12 to +14 tests. Baseline before changes: 268 passing.

## Coordinator surface (relayed by the user)

1. **New public API** other items/games may depend on: `AtomicJsonWriter`, `PersistenceQueue`,
   `PersistenceWriteFailedEventArgs`. Item 10 (SettingsManager) is expected to build on
   `PersistenceQueue`.
2. **Shared-package csproj dependency addition**: `KhaozEngine.Persistence -> KhaozEngine.App`
   (no cycle; App is pure BCL).
3. Async/coalescing design as above: per-path coalescing, schedule-on-demand ThreadPool worker,
   blocking `Flush()` + `IDisposable`, transient-retry with backoff, and a `WriteFailed` event.
