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

    /// <summary>Raised on the background worker thread when a write fails after all retry attempts. A subscriber's own exception is caught and logged, never killing the writer.</summary>
    public event EventHandler<PersistenceWriteFailedEventArgs>? WriteFailed;

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

            WriteWithRetry(path, json);
        }
    }

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
}
