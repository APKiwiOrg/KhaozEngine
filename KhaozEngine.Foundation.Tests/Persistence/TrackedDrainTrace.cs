using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace KhaozEngine.Tests;

// Bounded in-memory evidence for the tracked drain scenario (#1317). The sequence number and the
// Stopwatch timestamp are taken under one lock, so sequence order and timestamp order always agree.
// Recording never writes output. The owner renders the trace once, after cleanup. When full, the
// earliest entries are kept, later ones are counted as dropped and the render says so.
internal sealed class TrackedDrainTrace
{
    public const int DefaultCapacity = 256;

    private readonly object sync = new();
    private readonly List<TrackedDrainTraceEntry> entries;
    private readonly long start = Stopwatch.GetTimestamp();
    private long sequence;
    private long dropped;

    public TrackedDrainTrace(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        entries = new List<TrackedDrainTraceEntry>(capacity);
    }

    public int Capacity { get; }

    public long Dropped
    {
        get
        {
            lock (sync)
            {
                return dropped;
            }
        }
    }

    public void Record(string kind, string detail = "")
    {
        int thread = Environment.CurrentManagedThreadId;
        lock (sync)
        {
            long next = ++sequence;
            long timestamp = Stopwatch.GetTimestamp();
            if (entries.Count < Capacity)
            {
                entries.Add(new TrackedDrainTraceEntry(next, timestamp, thread, kind, detail));
            }
            else
            {
                dropped++;
            }
        }
    }

    public IReadOnlyList<TrackedDrainTraceEntry> Snapshot()
    {
        lock (sync)
        {
            return entries.ToArray();
        }
    }

    // Records a callback failure where it happens, then rethrows the same exception unchanged.
    public void Guard(string origin, Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            Record("callback-failed", $"{origin} {ex.GetType().FullName}: {ex.Message}");
            throw;
        }
    }

    public string Render()
    {
        TrackedDrainTraceEntry[] copy;
        long total;
        long lost;
        lock (sync)
        {
            copy = entries.ToArray();
            total = sequence;
            lost = dropped;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"trace: {copy.Length} of capacity {Capacity} kept, {lost} dropped, {total} recorded").AppendLine();
        if (lost > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"TRUNCATED: entries after sequence {Capacity} were dropped, {lost} dropped").AppendLine();
        }

        foreach (TrackedDrainTraceEntry entry in copy)
        {
            double elapsedMs = (entry.Timestamp - start) * 1000.0 / Stopwatch.Frequency;
            text.Append(CultureInfo.InvariantCulture,
                $"#{entry.Sequence} +{elapsedMs:0.000}ms t{entry.ThreadId} {entry.Kind} {entry.Detail}").AppendLine();
        }

        return text.ToString();
    }
}

internal readonly record struct TrackedDrainTraceEntry(long Sequence, long Timestamp, int ThreadId, string Kind, string Detail);
