using System;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The most recent frame times in milliseconds, oldest dropped first, for the <c>performance</c> section. Feed it one
/// sample per frame and snapshot it from the same thread. A sample that is not a positive finite number is ignored,
/// as <see cref="KhaozEngine.Diagnostics.FrameStats"/> does, so the section never has to write a value JSON cannot hold.
/// </summary>
public sealed class FrameTimeRing
{
    /// <summary>How many frame times a ring keeps by default.</summary>
    public const int DefaultCapacity = 300;

    readonly float[] ring;
    int head;
    int count;

    /// <summary>Creates a ring holding at most <paramref name="capacity"/> frame times.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than 1.</exception>
    public FrameTimeRing(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ring = new float[capacity];
    }

    /// <summary>Records one frame time, dropping the oldest when the ring is full.</summary>
    public void Add(float milliseconds)
    {
        if (!(milliseconds > 0f) || float.IsInfinity(milliseconds)) return;
        if (count < ring.Length)
        {
            ring[(head + count) % ring.Length] = milliseconds;
            count++;
        }
        else
        {
            ring[head] = milliseconds;
            head = (head + 1) % ring.Length;
        }
    }

    /// <summary>A copy of the kept frame times, oldest first.</summary>
    public float[] Snapshot()
    {
        var times = new float[count];
        for (int i = 0; i < count; i++)
            times[i] = ring[(head + i) % ring.Length];
        return times;
    }
}
