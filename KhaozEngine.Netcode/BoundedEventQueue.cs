using System;
using System.Collections.Generic;

namespace KhaozEngine.Netcode;

/// <summary>
/// A FIFO event queue with a hard upper bound on how many undrained items it will hold. The documented
/// poll/drain contract is "drain to empty every tick", so under correct operation this never exceeds a single
/// tick's worth of events; the cap is purely defensive. When the queue is full and a new item arrives, the
/// oldest buffered item is evicted to admit the newest (drop-oldest, keep-newest), so a host that stalls or is
/// flooded cannot grow memory without bound. Each dropped item is counted in <see cref="DroppedCount"/> so the
/// overflow is observable (a non-zero value means the host is not draining as contracted, or is under attack).
///
/// This mirrors the bounding <see cref="RemoteCommandQueue{TCommand}"/> applies to per-slot command buffers,
/// applied here to the session/transport event inboxes whose Data items each pin a payload buffer until drained.
/// Single-threaded by contract, like the rest of the netcode stack: enqueue and drain from the host-loop thread.
/// </summary>
public sealed class BoundedEventQueue<T>
{
    /// <summary>Default cap when a capacity is not specified. Generous enough to never bite a correctly draining
    /// host (a single poll's events), small enough to bound memory for a stalled or hostile one.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly Queue<T> queue = new();
    private readonly int capacity;
    private long dropped;

    /// <param name="capacity">Max buffered (undrained) items. When full, the oldest is evicted to admit a newer
    /// one. Must be positive.</param>
    public BoundedEventQueue(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "must be positive");
        this.capacity = capacity;
    }

    /// <summary>The hard upper bound on buffered items.</summary>
    public int Capacity => capacity;

    /// <summary>Items currently buffered (never exceeds <see cref="Capacity"/>).</summary>
    public int Count => queue.Count;

    /// <summary>Total items dropped over this queue's lifetime because the cap was hit. Non-zero signals a host
    /// that is not draining to empty as contracted (a stall or a flood).</summary>
    public long DroppedCount => dropped;

    /// <summary>Appends <paramref name="item"/>. If the queue is at capacity, the oldest buffered item is evicted
    /// first (keeping the newest), so the count never grows past <see cref="Capacity"/>.</summary>
    public void Enqueue(T item)
    {
        if (queue.Count >= capacity)
        {
            queue.Dequeue();
            dropped++;
        }
        queue.Enqueue(item);
    }

    /// <summary>Removes and returns the oldest buffered item. False (and default) when empty.</summary>
    public bool TryDequeue(out T item)
    {
        if (queue.Count > 0) { item = queue.Dequeue(); return true; }
        item = default!;
        return false;
    }
}
