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
/// <para>TERMINAL items (<see cref="EnqueueTerminal"/>) are exempt from that policy: they never count towards the
/// cap and are never the item an eviction drops. A terminal event is one whose loss is not recoverable on a later
/// event, so dropping it corrupts state instead of merely losing traffic: a peer's Disconnected releases its
/// player slot, and nothing else ever will, so an evicted Disconnected leaked that slot for the lifetime of the
/// process. That was reachable from outside: flood the server past the cap and the slot table drains one seat at
/// a time until real players are refused as "server full". The trade is deliberate. A terminal item is rare and
/// self-limiting (at most one per connected peer, and it carries no payload buffer), so buffering every one of
/// them costs far less than dropping any one of them, whereas Data items are unbounded in number and each pins a
/// payload buffer, which is what the cap exists for. <see cref="Count"/> may therefore exceed
/// <see cref="Capacity"/> by the number of buffered terminal items.</para>
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

    private readonly record struct Entry(T Item, bool Terminal);

    private readonly Queue<Entry> queue = new();
    // Terminal items lifted off the head of `queue` while looking for something evictable. They were the OLDEST
    // buffered items at the moment they moved, so draining this first (see TryDequeue) hands the consumer exactly
    // the FIFO order it would have seen without the eviction. Each item moves at most once, so a sustained flood
    // still costs O(1) per enqueue rather than a scan.
    private readonly Queue<T> rescued = new();
    private readonly int capacity;
    private long dropped;
    // Buffered items the cap applies to, i.e. everything except terminal ones. Never exceeds `capacity`.
    private int evictable;

    /// <param name="capacity">Max buffered (undrained) items. When full, the oldest is evicted to admit a newer
    /// one. Must be positive.</param>
    public BoundedEventQueue(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "must be positive");
        this.capacity = capacity;
    }

    /// <summary>The hard upper bound on buffered items the drop-oldest policy applies to. Terminal items
    /// (<see cref="EnqueueTerminal"/>) sit outside it.</summary>
    public int Capacity => capacity;

    /// <summary>Items currently buffered. Never exceeds <see cref="Capacity"/> plus the number of buffered
    /// terminal items.</summary>
    public int Count => queue.Count + rescued.Count;

    /// <summary>Total items dropped over this queue's lifetime because the cap was hit. Non-zero signals a host
    /// that is not draining to empty as contracted (a stall or a flood).</summary>
    public long DroppedCount => dropped;

    /// <summary>Appends <paramref name="item"/>. If the queue is at capacity, the oldest evictable buffered item
    /// is dropped first (keeping the newest), so the evictable count never grows past <see cref="Capacity"/>.</summary>
    public void Enqueue(T item)
    {
        if (evictable >= capacity) DropOldestEvictable();
        evictable++;
        queue.Enqueue(new Entry(item, Terminal: false));
    }

    /// <summary>Appends <paramref name="item"/> as a TERMINAL item: it does not count towards
    /// <see cref="Capacity"/> and no later overflow will drop it. For events whose loss corrupts state rather
    /// than merely losing traffic (a peer's Disconnected, a session's Left), which are rare and self-limiting.
    /// FIFO order against ordinary items is preserved on drain either way.</summary>
    public void EnqueueTerminal(T item) => queue.Enqueue(new Entry(item, Terminal: true));

    // Drops the oldest item the cap applies to, lifting any terminal items ahead of it into `rescued` so a
    // Disconnected is never what an overflow throws away. Only called with evictable >= capacity >= 1, so there
    // is always one to find.
    private void DropOldestEvictable()
    {
        while (queue.Count > 0)
        {
            Entry oldest = queue.Dequeue();
            if (oldest.Terminal) { rescued.Enqueue(oldest.Item); continue; }
            dropped++;
            evictable--;
            return;
        }
    }

    /// <summary>Removes and returns the oldest buffered item. False (and default) when empty.</summary>
    public bool TryDequeue(out T item)
    {
        if (rescued.Count > 0) { item = rescued.Dequeue(); return true; }
        if (queue.Count > 0)
        {
            Entry entry = queue.Dequeue();
            if (!entry.Terminal) evictable--;
            item = entry.Item;
            return true;
        }
        item = default!;
        return false;
    }
}
