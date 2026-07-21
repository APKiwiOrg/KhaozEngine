using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Persistence;

/// <summary>
/// Bounded async batch-write queue for an append-only server-side log (chat, an economy ledger, admin
/// actions, and similar durable-but-non-critical record streams). <see cref="Enqueue"/> is safe to call
/// from a hot path (e.g. a sim tick): it only pushes onto a bounded in-memory queue and never blocks or
/// does IO. <see cref="Update"/>, driven by the host on its own schedule, drains up to
/// <c>maxBatch</c> records on an interval and fires ONE batched write off-thread through the injected
/// sink. There is no internal timer: nothing flushes unless the host calls <see cref="Update"/>, so the
/// host owns cadence (e.g. wiring it into the same fixed-tick loop that drives everything else).
/// </summary>
/// <remarks>
/// A whole-batch write failure is salvaged by retrying every record individually (see the private
/// <c>SalvageAsync</c>), on the assumption that a sink opening a fresh connection/transaction per call
/// turns a singleton batch into "one row, one transaction": that isolates a single poisoned record
/// instead of losing the whole batch to it. A record that still fails alone is logged (with its own
/// content, so the bad one is diagnosable) and dropped; the rest survive. Queue overflow drops the
/// OLDEST queued record(s) rather than rejecting the newest, on the theory that for a forensic log an
/// old entry nobody has read yet is cheaper to lose than the one just produced; the total is counted via
/// <see cref="DroppedCount"/> and periodically logged. A <c>null</c> sink (e.g. no backing store
/// configured, such as local dev with no database) makes every member a no-op, so a host can wire this
/// unconditionally and let a null-sink instance quietly do nothing. Generic over <typeparamref name="T"/>
/// with an injected sink delegate and logger: no storage or game type is referenced here, so this is
/// fully unit-testable with a fake sink.
/// </remarks>
public sealed class BatchedWriter<T>
{
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task>? sink;
    private readonly string label;
    private readonly ILogger logger;
    private readonly int maxQueue;
    private readonly int maxBatch;
    private readonly float flushIntervalSeconds;

    private readonly object gate = new();
    private readonly Queue<T> queue = new();
    private readonly object inflightGate = new();
    private readonly List<Task> inflight = new();
    private float sinceFlush;
    private long dropped;
    private long reportedDropped;

    /// <summary>
    /// Creates a writer. <paramref name="sink"/> performs the actual batched write (e.g. a store's
    /// <c>AppendAsync</c>); pass <c>null</c> to disable the writer entirely, at which point
    /// <see cref="Enqueue"/>, <see cref="Update"/>, and <see cref="FlushAsync"/> all become no-ops.
    /// <paramref name="label"/> prefixes every log line (e.g. <c>"chatlog"</c>, <c>"ledger"</c>) so
    /// several writers sharing one log stream stay distinguishable. <paramref name="logger"/> defaults
    /// to the ambient <see cref="Log.For{T}"/> logger for this type. <paramref name="maxQueue"/> is the
    /// bounded in-memory queue capacity (drop-oldest on overflow, clamped to at least 1).
    /// <paramref name="maxBatch"/> caps how many records a single <see cref="Update"/> flush dispatches
    /// in one write (clamped to at least 1). <paramref name="flushIntervalSeconds"/> is how often
    /// <see cref="Update"/> actually flushes, in the same time unit as the <c>dt</c> it is called with.
    /// </summary>
    public BatchedWriter(Func<IReadOnlyList<T>, CancellationToken, Task>? sink, string label, ILogger? logger = null,
        int maxQueue = 4096, int maxBatch = 256, float flushIntervalSeconds = 5f)
    {
        ArgumentNullException.ThrowIfNull(label);
        this.sink = sink;
        this.label = label;
        this.logger = logger ?? Log.For<BatchedWriter<T>>();
        this.maxQueue = maxQueue < 1 ? 1 : maxQueue;
        this.maxBatch = maxBatch < 1 ? 1 : maxBatch;
        this.flushIntervalSeconds = flushIntervalSeconds;
    }

    /// <summary>Total records dropped on queue overflow since construction. Backs the periodic drop report.</summary>
    public long DroppedCount { get { lock (gate) return dropped; } }

    /// <summary>Enqueues a record. Non-blocking; drops the OLDEST queued record(s) if already at capacity. A no-op when the writer was constructed with a <c>null</c> sink.</summary>
    public void Enqueue(T record)
    {
        if (sink is null) return;
        lock (gate)
        {
            while (queue.Count >= maxQueue) { queue.Dequeue(); dropped++; }
            queue.Enqueue(record);
        }
    }

    /// <summary>
    /// Drain-and-write on the flush interval. Call this every tick/frame from the host loop; the write
    /// itself runs off-thread, so this never blocks the caller on IO. Nothing happens before
    /// <c>flushIntervalSeconds</c> of accumulated <paramref name="dt"/> has elapsed since the last
    /// flush, and nothing happens at all when the writer was constructed with a <c>null</c> sink.
    /// </summary>
    public void Update(float dt)
    {
        if (sink is null) return;
        sinceFlush += dt;
        if (sinceFlush < flushIntervalSeconds) return;
        sinceFlush = 0f;
        FlushOnce();
        ReportDrops();
    }

    /// <summary>
    /// Shutdown drain: flushes EVERYTHING still queued (ignoring both the interval and
    /// <c>maxBatch</c> batching cadence, though each individual dispatched write still respects
    /// <c>maxBatch</c>), then awaits every in-flight write before returning. A no-op when the writer
    /// was constructed with a <c>null</c> sink.
    /// </summary>
    public async Task FlushAsync()
    {
        if (sink is null) return;
        while (FlushOnce()) { }
        Task[] pending;
        lock (inflightGate) { pending = inflight.ToArray(); inflight.Clear(); }
        await Task.WhenAll(pending).ConfigureAwait(false);
        ReportDrops();
    }

    // Drains up to maxBatch records and fires one batched write. Returns true if a batch was dispatched.
    private bool FlushOnce()
    {
        List<T> batch;
        lock (gate)
        {
            if (queue.Count == 0) return false;
            int take = queue.Count < maxBatch ? queue.Count : maxBatch;
            batch = new List<T>(take);
            for (int i = 0; i < take; i++) batch.Add(queue.Dequeue());
        }
        Track(WriteAsync(batch));
        return true;
    }

    private async Task WriteAsync(IReadOnlyList<T> batch)
    {
        try { await sink!(batch, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            logger.Warn($"[{label}] batch write of {batch.Count} record(s) failed: {ex.GetBaseException().Message}; salvaging individually.");
            await SalvageAsync(batch).ConfigureAwait(false);
        }
    }

    // A whole-batch write failed, most commonly because one bad row poisoned the sink's shared
    // transaction and rolled the rest back with it. Retry each record through its own sink call: a
    // sink that opens a fresh connection/transaction per call turns a singleton batch into "one row,
    // one transaction", which isolates the genuinely bad row instead of losing the whole batch. A
    // record that still fails alone is logged (with its own content, so the bad row is diagnosable)
    // and dropped; the rest survive.
    private async Task SalvageAsync(IReadOnlyList<T> batch)
    {
        int lost = 0;
        foreach (T record in batch)
        {
            try { await sink!(new[] { record }, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                lost++;
                logger.Error($"[{label}] dropped 1 record after its individual retry also failed: {ex.GetBaseException().Message} | record: {record}");
            }
        }
        if (lost > 0)
            logger.Warn($"[{label}] salvage complete: {batch.Count - lost}/{batch.Count} record(s) recovered, {lost} dropped.");
    }

    private void Track(Task t)
    {
        lock (inflightGate) { inflight.Add(t); inflight.RemoveAll(x => x.IsCompleted); }
    }

    private void ReportDrops()
    {
        long d, prev;
        lock (gate) { d = dropped; prev = reportedDropped; reportedDropped = dropped; }
        if (d != prev) logger.Warn($"[{label}] dropped {d - prev} record(s) on queue overflow (total {d}).");
    }
}
