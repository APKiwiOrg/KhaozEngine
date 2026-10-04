using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;
using KhaozEngine.Diagnostics;
using KhaozEngine.Serialization;

[assembly: InternalsVisibleTo("KhaozEngine.Foundation.Tests")]

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

    private readonly object sync = new();
    private readonly Dictionary<string, PendingWrite> pending = new(StringComparer.Ordinal);
    // Failure notifications awaiting delivery, guarded by sync. Drain workers append here and the
    // single active notifier (see notifying) delivers FIFO, so WriteFailed handlers never run
    // concurrently and failures arrive in the order they happened.
    private readonly Queue<PersistenceWriteFailedEventArgs> deferredFailures = new();
    private readonly ILogger logger;
    private readonly int maxAttempts;
    private readonly TimeSpan retryDelay;
    private readonly int backupGenerations;
    private readonly Action<string, string> writeText;
    private readonly Action? beforeSupersessionCompletion;
    private readonly Action<bool>? observeDrainDecision;
    // Producer-owned completions remain outstanding after their payload leaves pending.
    private int supersessionsCompleting;
    private bool workerScheduled;
    private bool notifying;
    private bool disposed;

    /// <summary>Raised when a write fails after all retry attempts. Notifications are delivered on a background worker thread, one at a time and in failure order, never concurrently. Delivery happens after the drain worker has released the queue's internal latch, so a subscriber may call <see cref="Flush"/> or <see cref="Dispose"/> from the handler without deadlocking. A subscriber's own exception is caught and logged, never killing the writer.</summary>
    public event EventHandler<PersistenceWriteFailedEventArgs>? WriteFailed;

    /// <summary>Creates a queue. <paramref name="maxAttempts"/> total write attempts per payload (>= 1). <paramref name="retryDelay"/> backoff between attempts (default 50 ms). <paramref name="logger"/> defaults to the ambient <c>Log</c> facade (category <c>PersistenceQueue</c>). <paramref name="backupGenerations"/> is the number of numbered backups to keep per target path via <see cref="SaveBackups"/>, rotated once per committed payload before the write attempt. It defaults to 2, matching the read side (<see cref="FileSettingsStorage.BackupGenerations"/> and <see cref="GameStorageOptions.BackupGenerations"/> both default to 2), so a queue built with defaults writes the generations the recovery ladder goes looking for. It used to default to 0, which left a consumer that constructs the queue directly with a ladder that had nothing to recover from. Pass 0 to turn rotation off.</summary>
    public PersistenceQueue(ILogger? logger = null, int maxAttempts = 3, TimeSpan? retryDelay = null, int backupGenerations = 2)
        : this(AtomicJsonWriter.WriteText, logger, maxAttempts, retryDelay, backupGenerations)
    {
    }

    internal PersistenceQueue(Action<string, string> writeText, ILogger? logger = null, int maxAttempts = 3, TimeSpan? retryDelay = null, int backupGenerations = 2,
        Action? beforeSupersessionCompletion = null, Action<bool>? observeDrainDecision = null)
    {
        ArgumentNullException.ThrowIfNull(writeText);
        this.writeText = writeText;
        this.beforeSupersessionCompletion = beforeSupersessionCompletion;
        this.observeDrainDecision = observeDrainDecision;
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "At least one attempt is required.");
        }

        this.logger = logger ?? Log.For<PersistenceQueue>();
        this.maxAttempts = maxAttempts;
        // Retry backoff runs as Thread.Sleep on the background ThreadPool worker, so cap it to keep a
        // pathological value from tying up a pool thread.
        TimeSpan delay = retryDelay ?? TimeSpan.FromMilliseconds(50);
        this.retryDelay = delay > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
        this.backupGenerations = backupGenerations;
    }

    /// <inheritdoc/>
    public void Enqueue(string path, string json)
        => Enqueue(path, new PendingWrite(json, null));

    /// <summary>
    /// Enqueues <paramref name="json"/> and tracks that exact payload. Saved follows its atomic write,
    /// Superseded means it was replaced while pending, and Failed carries the final error after retries.
    /// Completion continuations run asynchronously outside queue locks. An in-flight payload can finish
    /// while a newer payload for the same path is still pending.
    /// </summary>
    public Task<PersistenceWriteResult> EnqueueTracked(string path, string json)
    {
        var completion = new TaskCompletionSource<PersistenceWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(path, new PendingWrite(json, completion));
        return completion.Task;
    }

    private void Enqueue(string path, PendingWrite request)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(request.Json, "json");

        PendingWrite? replaced;
        bool schedule = false;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending.TryGetValue(path, out replaced);
            pending[path] = request;
            if (replaced?.Completion is not null)
            {
                supersessionsCompleting++;
            }
            if (!workerScheduled)
            {
                workerScheduled = true;
                schedule = true;
            }
        }

        if (replaced?.Completion is not null)
        {
            try
            {
                beforeSupersessionCompletion?.Invoke();
                replaced.Completion.TrySetResult(new PersistenceWriteResult(PersistenceWriteOutcome.Superseded, path, null));
            }
            finally
            {
                lock (sync)
                {
                    supersessionsCompleting--;
                    Monitor.PulseAll(sync);
                }
            }
        }
        if (schedule)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static state => ((PersistenceQueue)state!).DrainPending(), this);
        }
    }

    /// <summary>Serializes <paramref name="value"/> (indented by default) and enqueues it for <paramref name="path"/>.</summary>
    public void Enqueue<T>(string path, T value, JsonSerializerOptions? options = null)
        => Enqueue(path, JsonSerializer.Serialize(value, options ?? JsonDefaults.IndentedWrite));

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

    /// <inheritdoc/>
    public void Flush()
    {
        if (observeDrainDecision is not null)
        {
            bool waiting;
            lock (sync)
            {
                waiting = HasOutstandingWritesLocked;
            }
            observeDrainDecision(waiting);
        }

        lock (sync)
        {
            // Recheck under the lock after observation so a completion pulse cannot be lost.
            while (HasOutstandingWritesLocked)
            {
                Monitor.Wait(sync);
            }
        }
    }

    private bool HasOutstandingWritesLocked => pending.Count > 0 || workerScheduled || supersessionsCompleting > 0;

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
        List<PersistenceWriteFailedEventArgs>? failures = null;
        try
        {
            while (true)
            {
                string path;
                PendingWrite request;

                lock (sync)
                {
                    if (pending.Count == 0)
                    {
                        break;
                    }

                    path = string.Empty;
                    request = null!;
                    foreach (KeyValuePair<string, PendingWrite> entry in pending)
                    {
                        path = entry.Key;
                        request = entry.Value;
                        break;
                    }

                    pending.Remove(path);
                }

                PersistenceWriteFailedEventArgs? failure = WriteWithRetry(path, request.Json);
                request.Completion?.TrySetResult(new PersistenceWriteResult(
                    failure is null ? PersistenceWriteOutcome.Saved : PersistenceWriteOutcome.Failed,
                    path, failure?.Exception));
                if (failure is not null)
                {
                    // Collect the notification, do not raise it yet. Raising it here would run the
                    // subscriber while workerScheduled is still true and only this drain thread can
                    // clear it, so a handler that calls Flush or Dispose would wait on itself forever.
                    (failures ??= new List<PersistenceWriteFailedEventArgs>()).Add(failure);
                }
            }
        }
        finally
        {
            bool notify = false;
            lock (sync)
            {
                // Queue this pass's failures for delivery. The shared queue (never a raise on this
                // thread mid-handoff) is what keeps WriteFailed serial and in failure order across
                // drain handoffs: whichever thread ends up notifying delivers everything FIFO.
                if (failures is not null)
                {
                    foreach (PersistenceWriteFailedEventArgs failure in failures)
                    {
                        deferredFailures.Enqueue(failure);
                    }
                }

                // An Enqueue can land in the window between our pending-empty check above and here:
                // it saw workerScheduled still true, so it added to pending WITHOUT scheduling a
                // worker. If anything is pending, keep the latch and run another drain rather than
                // stranding that write (which would also wedge Flush forever). Otherwise release the
                // latch and wake Flush waiters.
                if (pending.Count > 0)
                {
                    // The tail worker inherits the queued failures so the single notifier delivers
                    // them serially and in order. Raising them on this thread instead would run
                    // handlers concurrently with the tail drain.
                    ThreadPool.UnsafeQueueUserWorkItem(static state => ((PersistenceQueue)state!).DrainPending(), this);
                }
                else
                {
                    workerScheduled = false;
                    Monitor.PulseAll(sync);
                    if (deferredFailures.Count > 0 && !notifying)
                    {
                        notifying = true;
                        notify = true;
                    }
                }
            }

            // Deliver from inside the finally so failures inherited across a handoff are still
            // raised even if a drain pass dies, and only after the lock above released the drain
            // latch, so a handler that calls Flush or Dispose re-entrantly makes progress. See #150.
            if (notify)
            {
                DrainNotifications();
            }
        }
    }

    // Delivers queued WriteFailed notifications outside the lock until none remain. The notifying
    // flag admits one thread at a time, so handlers are never entered concurrently and failures
    // arrive in the order they were queued. A drain that finishes while a notifier is active just
    // queues its failures and the active notifier picks them up on its next loop iteration.
    private void DrainNotifications()
    {
        while (true)
        {
            PersistenceWriteFailedEventArgs args;
            lock (sync)
            {
                if (deferredFailures.Count == 0)
                {
                    notifying = false;
                    return;
                }

                args = deferredFailures.Dequeue();
            }

            RaiseWriteFailed(args);
        }
    }

    // Returns the failure to notify (queued by the caller for ordered delivery once the drain latch is released), or null on success.
    private PersistenceWriteFailedEventArgs? WriteWithRetry(string path, string json)
    {
        // Rotate once per committed payload, never per retry attempt: the primary is copied (not
        // moved) into generation 1, so it stays intact if every attempt below then fails.
        if (backupGenerations > 0)
        {
            try
            {
                SaveBackups.Rotate(path, backupGenerations);
            }
            catch (Exception ex)
            {
                logger.Warn($"backup rotation for '{path}' failed, writing anyway", ex);
            }
        }

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                writeText(path, json);
                return null;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.Warn($"write to '{path}' failed (attempt {attempt}/{maxAttempts}), retrying", ex);
                if (retryDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(retryDelay);
                }
            }
            catch (Exception ex)
            {
                logger.Error($"write to '{path}' failed after {maxAttempts} attempts, giving up", ex);
                return new PersistenceWriteFailedEventArgs(path, ex, attempt);
            }
        }

        return null;
    }

    private void RaiseWriteFailed(PersistenceWriteFailedEventArgs args)
    {
        EventHandler<PersistenceWriteFailedEventArgs>? handler = WriteFailed;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, args);
        }
        catch (Exception ex)
        {
            logger.Error("a WriteFailed subscriber threw", ex);
        }
    }

    private sealed record PendingWrite(string Json, TaskCompletionSource<PersistenceWriteResult>? Completion);
}
