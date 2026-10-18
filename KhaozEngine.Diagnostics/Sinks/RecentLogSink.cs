using System;
using System.Collections.Generic;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Keeps the most recent formatted log lines in a fixed ring, oldest dropped first, for attaching to a bug
/// or crash report. Thread-safe. Lines are laid out by <see cref="LogFormatter.Format"/> and an over-long
/// line is cut to <see cref="MaxEntryChars"/> with a <c> [truncated]</c> marker, so the ring's memory stays
/// bounded whatever the callers log.
/// </summary>
public sealed class RecentLogSink : ILogSink
{
    /// <summary>The longest line kept before the truncation marker is appended.</summary>
    public const int MaxEntryChars = 2000;

    private const string TruncationMarker = " [truncated]";

    private readonly object gate = new();
    private readonly string[] ring;
    private int head;
    private int count;

    /// <summary>Creates a ring holding at most <paramref name="capacity"/> lines.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than 1.</exception>
    public RecentLogSink(int capacity = 500)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        }
        ring = new string[capacity];
    }

    /// <summary>The most lines the ring holds.</summary>
    public int Capacity => ring.Length;

    /// <summary>A copy of the kept lines, oldest first. Later emits do not change it.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (gate)
        {
            var lines = new string[count];
            for (int i = 0; i < count; i++)
            {
                lines[i] = ring[(head + i) % ring.Length];
            }
            return lines;
        }
    }

    /// <inheritdoc />
    public void Emit(in LogEntry entry)
    {
        try
        {
            string line = Cut(LogFormatter.Format(entry));
            lock (gate)
            {
                if (count < ring.Length)
                {
                    ring[(head + count) % ring.Length] = line;
                    count++;
                }
                else
                {
                    ring[head] = line;
                    head = (head + 1) % ring.Length;
                }
            }
        }
        catch { /* never throw */ }
    }

    /// <inheritdoc />
    public void Flush() { }

    /// <inheritdoc />
    public void Dispose() { }

    private static string Cut(string line)
    {
        if (line.Length <= MaxEntryChars) return line;

        // Never leave half a surrogate pair at the cut, which would encode as a replacement character.
        int keep = char.IsHighSurrogate(line[MaxEntryChars - 1]) ? MaxEntryChars - 1 : MaxEntryChars;
        return string.Concat(line.AsSpan(0, keep), TruncationMarker.AsSpan());
    }
}
