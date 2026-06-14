using System.Collections.Generic;

namespace KhaozEngine.Netcode;

/// <summary>
/// Host-side per-slot command queue. Commands arrive tagged with a monotonic sequence number; the host
/// dequeues them in seq order once per simulation tick, independent of tick-number alignment between
/// client and host. Duplicate deliveries (the client's redundancy retransmit) are silently ignored.
/// Determinism-neutral: it only orders and de-duplicates, never altering command values.
/// </summary>
public sealed class RemoteCommandQueue<TCommand>
{
    private readonly Dictionary<int, SortedList<int, TCommand>> queuesBySlot = new();
    private readonly Dictionary<int, int> lastAcknowledgedSeqBySlot = new();
    private readonly TCommand neutralCommand;

    /// <param name="neutralCommand">Returned by <see cref="Dequeue"/> when a slot's queue is empty.</param>
    public RemoteCommandQueue(TCommand neutralCommand)
    {
        this.neutralCommand = neutralCommand;
    }

    /// <summary>Clears all per-slot queues and acknowledgement tracking.</summary>
    public void Reset()
    {
        queuesBySlot.Clear();
        lastAcknowledgedSeqBySlot.Clear();
    }

    /// <summary>Stores a command. Negative seq and duplicate (slot, seq) pairs are ignored.</summary>
    public void Store(int slot, int seq, in TCommand command)
    {
        if (seq < 0)
        {
            return;
        }

        if (!queuesBySlot.TryGetValue(slot, out SortedList<int, TCommand>? queue))
        {
            queue = new SortedList<int, TCommand>();
            queuesBySlot[slot] = queue;
        }

        if (!queue.ContainsKey(seq))
        {
            queue[seq] = command;
        }
    }

    /// <summary>
    /// Dequeues the lowest-seq command for <paramref name="slot"/>, or the neutral command if empty.
    /// <paramref name="lastAcknowledgedSeq"/> reflects the highest seq processed so far (the host stamps
    /// this on its snapshot so the client can reconcile).
    /// </summary>
    public TCommand Dequeue(int slot, out int lastAcknowledgedSeq)
    {
        lastAcknowledgedSeq = lastAcknowledgedSeqBySlot.GetValueOrDefault(slot, -1);

        if (!queuesBySlot.TryGetValue(slot, out SortedList<int, TCommand>? queue) || queue.Count == 0)
        {
            return neutralCommand;
        }

        int seq = queue.Keys[0];
        TCommand command = queue.Values[0];
        queue.RemoveAt(0);
        lastAcknowledgedSeqBySlot[slot] = seq;
        lastAcknowledgedSeq = seq;
        return command;
    }

    /// <summary>The highest seq dequeued for <paramref name="slot"/> so far, or -1 if none.</summary>
    public int GetLastAcknowledgedSeq(int slot) =>
        lastAcknowledgedSeqBySlot.GetValueOrDefault(slot, -1);
}
