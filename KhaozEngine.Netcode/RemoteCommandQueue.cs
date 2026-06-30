using System;
using System.Collections.Generic;

namespace KhaozEngine.Netcode;

/// <summary>
/// Host-side per-slot command queue. Commands arrive tagged with a monotonic sequence number; the host
/// dequeues them in seq order once per simulation tick, independent of tick-number alignment between
/// client and host. Duplicate deliveries (the client's redundancy retransmit) are silently ignored.
/// Determinism-neutral: it only orders and de-duplicates, never altering command values.
///
/// Hostile-input bounding: every (slot, seq) is attacker-controlled off the wire, so the queue rejects
/// a seq at or below the slot's processed high-water mark (replays and stale/regressed seqs cannot be
/// reprocessed and cannot move the acknowledged seq backwards), caps the per-slot buffer (keeping only
/// the most recent commands so a flood cannot grow memory without bound), and caps the number of distinct
/// slots (so spraying slot ids cannot grow the slot map without bound).
/// </summary>
public sealed class RemoteCommandQueue<TCommand>
{
    private readonly Dictionary<int, SortedList<int, TCommand>> queuesBySlot = new();
    private readonly Dictionary<int, int> lastAcknowledgedSeqBySlot = new();
    private readonly TCommand neutralCommand;
    private readonly int maxQueuedPerSlot;
    private readonly int maxSlots;
    private readonly int catchUpThreshold;

    /// <param name="neutralCommand">Returned by <see cref="Dequeue"/> when a slot's queue is empty.</param>
    /// <param name="maxQueuedPerSlot">Max buffered (undequeued) commands per slot. When full, the oldest
    /// buffered command is dropped to make room for a newer one. Must be positive.</param>
    /// <param name="maxSlots">Max number of distinct slots tracked. A command for a new slot beyond this
    /// cap is ignored. Must be positive.</param>
    /// <param name="catchUpThreshold">Backlog catch-up cap. When a slot has MORE than this many buffered commands,
    /// the next <see cref="Dequeue"/> skips the stale ones and jumps to the most recent (advancing the processed
    /// high-water past everything skipped), so the host stays at most this many commands behind live input instead of
    /// replaying a deep backlog one command per tick. <b>0 (the default) disables it</b>: <see cref="Dequeue"/> then
    /// returns strictly oldest-first, one at a time, exactly as before. Enabling it is <b>lossy</b> (skipped commands
    /// are discarded), so it is only correct for a latest-wins command stream such as movement, where the freshest
    /// command supersedes the older ones. Must not be negative.</param>
    public RemoteCommandQueue(TCommand neutralCommand, int maxQueuedPerSlot = 256, int maxSlots = 64,
        int catchUpThreshold = 0)
    {
        if (maxQueuedPerSlot <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedPerSlot), maxQueuedPerSlot, "must be positive");
        if (maxSlots <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSlots), maxSlots, "must be positive");
        if (catchUpThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(catchUpThreshold), catchUpThreshold, "must not be negative");

        this.neutralCommand = neutralCommand;
        this.maxQueuedPerSlot = maxQueuedPerSlot;
        this.maxSlots = maxSlots;
        this.catchUpThreshold = catchUpThreshold;
    }

    /// <summary>Clears all per-slot queues and acknowledgement tracking.</summary>
    public void Reset()
    {
        queuesBySlot.Clear();
        lastAcknowledgedSeqBySlot.Clear();
    }

    /// <summary>
    /// Drops all per-slot state for <paramref name="slot"/>: its buffered commands and its processed high-water
    /// mark. Idempotent for an unknown slot. Call this when a slot is released so the next session to recycle it
    /// restarts cleanly from high-water -1 (its seqs legitimately begin at 0 again). Replay protection is unweakened:
    /// it holds within a live session; a recycled slot is a new session whose seqs reset by design.
    /// </summary>
    public void Forget(int slot)
    {
        queuesBySlot.Remove(slot);
        lastAcknowledgedSeqBySlot.Remove(slot);
    }

    /// <summary>
    /// Stores a command. Ignored when: seq is negative; seq is at or below the slot's processed
    /// high-water mark (replay / stale); the (slot, seq) pair is already buffered; or the slot is new and
    /// the distinct-slot cap is reached. When the per-slot buffer is full, the oldest buffered command is
    /// evicted to admit a newer seq (a seq not newer than the oldest buffered is dropped instead).
    /// </summary>
    public void Store(int slot, int seq, in TCommand command)
    {
        if (seq < 0)
        {
            return;
        }

        // Reject replays and stale seqs against the per-slot processed high-water mark.
        if (seq <= lastAcknowledgedSeqBySlot.GetValueOrDefault(slot, -1))
        {
            return;
        }

        if (!queuesBySlot.TryGetValue(slot, out SortedList<int, TCommand>? queue))
        {
            // Bound the number of distinct slots so a hostile peer cannot grow the slot map without limit.
            if (queuesBySlot.Count >= maxSlots)
            {
                return;
            }

            queue = new SortedList<int, TCommand>();
            queuesBySlot[slot] = queue;
        }

        if (queue.ContainsKey(seq))
        {
            return;
        }

        if (queue.Count >= maxQueuedPerSlot)
        {
            // Buffer full: keep the most recent commands. Drop the incoming seq if it is not newer than
            // the oldest buffered; otherwise evict the oldest (lowest seq) to make room.
            if (seq <= queue.Keys[0])
            {
                return;
            }

            queue.RemoveAt(0);
        }

        queue[seq] = command;
    }

    /// <summary>
    /// Dequeues the lowest-seq command for <paramref name="slot"/>, or the neutral command if empty.
    /// <paramref name="lastAcknowledgedSeq"/> reflects the highest seq processed so far (the host stamps
    /// this on its snapshot so the client can reconcile). The high-water mark only ever advances.
    /// </summary>
    public TCommand Dequeue(int slot, out int lastAcknowledgedSeq)
    {
        int prevAck = lastAcknowledgedSeqBySlot.GetValueOrDefault(slot, -1);
        lastAcknowledgedSeq = prevAck;

        if (!queuesBySlot.TryGetValue(slot, out SortedList<int, TCommand>? queue) || queue.Count == 0)
        {
            return neutralCommand;
        }

        // Catch-up: a backlog deeper than the threshold (a reconnect flush, a delivery burst, an ungated client)
        // would otherwise crawl out one command per Dequeue, replaying stale input for as many ticks as it is deep.
        // Skip straight to the newest buffered command, advancing the high-water past everything skipped, so the host
        // is at most catchUpThreshold behind live. Lossy by design and only valid for a latest-wins (movement) stream;
        // disabled (0) keeps the strict oldest-first order.
        if (catchUpThreshold > 0 && queue.Count > catchUpThreshold)
        {
            int newestIndex = queue.Count - 1;
            int newestSeq = queue.Keys[newestIndex];
            TCommand newest = queue.Values[newestIndex];
            queue.Clear();
            lastAcknowledgedSeqBySlot[slot] = newestSeq;   // Store guarantees buffered seqs > prevAck, so this advances
            lastAcknowledgedSeq = newestSeq;
            return newest;
        }

        int seq = queue.Keys[0];
        TCommand command = queue.Values[0];
        queue.RemoveAt(0);
        int ack = seq > prevAck ? seq : prevAck; // monotonic; Store guarantees seq > prevAck, belt-and-suspenders
        lastAcknowledgedSeqBySlot[slot] = ack;
        lastAcknowledgedSeq = ack;
        return command;
    }

    /// <summary>The highest seq dequeued for <paramref name="slot"/> so far, or -1 if none.</summary>
    public int GetLastAcknowledgedSeq(int slot) =>
        lastAcknowledgedSeqBySlot.GetValueOrDefault(slot, -1);
}
