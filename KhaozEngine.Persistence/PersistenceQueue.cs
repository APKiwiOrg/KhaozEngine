using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using KhaozEngine.App;
using KhaozEngine.Diagnostics;
using KhaozEngine.Serialization;

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
    private readonly Dictionary<string, string> pending = new(StringComparer.Ordinal);
    // Failure notifications awaiting delivery, guarded by sync. Drain workers append here and the
    // single active notifier (see notifying) delivers FIFO, so WriteFailed handlers never run
    // concurrently and failures arrive in the order they happened.
    private readonly Queue<PersistenceWriteFailedEventArgs> deferredFailures = new();
    private readonly ILogger logger;
    private readonly int maxAttempts;
    private readonly TimeSpan retryDelay;
    private bool workerScheduled;
    private bool notifying;
    private bool disposed;

    /// <summary>Raised when a write fails after all retry attempts. Notifications are delivered on a background worker thread, one at a time and in failure order, never concurrently. Delivery happens after the drain worker has released the queue's internal latch, so a subscriber may call <see cref="Flush"/> or <see cref="Dispose"/> from the handler without deadlocking. A subscriber's own exception is caught and logged, never killing the writer.</summary>
    public event EventHandler<PersistenceWriteFailedEventArgs>? WriteFailed;

    /// <summary>Creates a queue. <paramref name="maxAttempts"/> total write attempts per payload (>= 1); <paramref name="retryDelay"/> backoff between attempts (default 50 ms). <paramref name="logger"/> defaults to the ambient <c>Log</c> facade (category <c>PersistenceQueue</c>).</summary>
    public PersistenceQueue(ILogger? logger = null, int maxAttempts = 3, TimeSpan? retryDelay = null)
    {
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
        List<PersistenceWriteFailedEventArgs>? failures = null;
        try
        {
            while (true)
            {
                string path;
                string json;

                lock (sync)
                {
                    if (pending.Count == 0)
                    {
                        break;
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

                PersistenceWriteFailedEventArgs? failure = WriteWithRetry(path, json);
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
                    // The tail worker inherits the queued failures. Raising them on this thread
                    // instead would run handlers concurrently with the tail drain and its raises.
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
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                AtomicJsonWriter.WriteText(path, json);
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
}
