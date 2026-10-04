using System;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The server ticks a <see cref="WorldClient"/> has ingested, stamped on its presentation clock exactly as
/// <c>ClientReplicationView.RecordInterpolationSample</c> stamps the remote samples, and bracketed by
/// <see cref="At"/> exactly as <c>ClientReplicationView.InterpolateAt</c> brackets them. Evaluated at the remote render
/// time it names the fractional server tick the remote bodies are drawn at. A fixed ring allocated at construction, so
/// <see cref="Record"/> and <see cref="At"/> allocate nothing.
/// </summary>
internal sealed class ServerTickTimeline
{
    private readonly double[] stamps;
    private readonly long[] ticks;
    private int head;   // ring index of the oldest entry
    private int count;

    public ServerTickTimeline(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        stamps = new double[capacity];
        ticks = new long[capacity];
    }

    /// <summary>Entries held.</summary>
    public int Count => count;

    /// <summary>Records <paramref name="tick"/> ingested at <paramref name="stamp"/>. A stamp at or below the newest
    /// overwrites the newest entry (ingests collapsed into one presentation frame share a stamp, as the view's samples
    /// do) and keeps the larger tick. A full ring overwrites its oldest entry.</summary>
    public void Record(double stamp, long tick)
    {
        if (count > 0)
        {
            int newest = Index(count - 1);
            if (stamp <= stamps[newest])
            {
                stamps[newest] = stamp;
                if (tick > ticks[newest]) ticks[newest] = tick;
                return;
            }
        }
        if (count == stamps.Length)
        {
            head = Index(1);
            count--;
        }
        int slot = Index(count);
        stamps[slot] = stamp;
        ticks[slot] = tick;
        count++;
    }

    /// <summary>The tick at <paramref name="renderTime"/>: <c>-1</c> when empty, the oldest tick before the oldest
    /// stamp, the newest tick at or past the newest stamp (no extrapolation), else the lerp of the two entries
    /// bracketing it by their true stamps. Entries below the lower bracket are pruned, so feed a rising render
    /// time.</summary>
    public double At(double renderTime)
    {
        if (count == 0) return -1;
        // The last entry at or before renderTime is the lower bracket (entries are stamp-ascending).
        int lo = -1;
        for (int i = 0; i < count; i++)
        {
            if (stamps[Index(i)] <= renderTime) lo = i;
            else break;
        }
        if (lo < 0) return ticks[head];
        if (lo >= count - 1)
        {
            Drop(lo);
            return ticks[head];
        }
        int a = Index(lo);
        int b = Index(lo + 1);
        double span = stamps[b] - stamps[a];
        double frac = span > 0 ? Math.Clamp((renderTime - stamps[a]) / span, 0.0, 1.0) : 1.0;
        double tick = ticks[a] + (ticks[b] - ticks[a]) * frac;
        Drop(lo);
        return tick;
    }

    /// <summary>Forgets every entry.</summary>
    public void Clear()
    {
        head = 0;
        count = 0;
    }

    private int Index(int offset) => (head + offset) % stamps.Length;

    private void Drop(int entries)
    {
        head = Index(entries);
        count -= entries;
    }
}
