using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class RemoteCommandQueueTests
{
    private static RemoteCommandQueue<int> NewQueue() => new(neutralCommand: -999);

    [Fact]
    public void Dequeue_InSeqOrder_RegardlessOfStoreOrder()
    {
        var q = NewQueue();
        q.Store(slot: 0, seq: 2, command: 22);
        q.Store(slot: 0, seq: 0, command: 20);
        q.Store(slot: 0, seq: 1, command: 21);
        Assert.Equal(20, q.Dequeue(0, out int a0)); Assert.Equal(0, a0);
        Assert.Equal(21, q.Dequeue(0, out int a1)); Assert.Equal(1, a1);
        Assert.Equal(22, q.Dequeue(0, out int a2)); Assert.Equal(2, a2);
    }

    [Fact]
    public void Store_Duplicate_IsIgnored()
    {
        var q = NewQueue();
        q.Store(0, 0, 100);
        q.Store(0, 0, 999); // same (slot,seq) -> ignored, first value kept
        Assert.Equal(100, q.Dequeue(0, out _));
    }

    [Fact]
    public void Store_NegativeSeq_IsIgnored()
    {
        var q = NewQueue();
        q.Store(0, -1, 5);
        Assert.Equal(-999, q.Dequeue(0, out int ack)); // neutral
        Assert.Equal(-1, ack);
    }

    [Fact]
    public void Dequeue_EmptySlot_ReturnsNeutral_AndLastAck()
    {
        var q = NewQueue();
        q.Store(0, 0, 7);
        q.Dequeue(0, out _); // ack now 0
        Assert.Equal(-999, q.Dequeue(0, out int ack)); // empty -> neutral, but ack preserved
        Assert.Equal(0, ack);
    }

    [Fact]
    public void Slots_AreIsolated()
    {
        var q = NewQueue();
        q.Store(0, 0, 10);
        q.Store(1, 0, 20);
        Assert.Equal(20, q.Dequeue(1, out _));
        Assert.Equal(10, q.Dequeue(0, out _));
        Assert.Equal(0, q.GetLastAcknowledgedSeq(0));
        Assert.Equal(0, q.GetLastAcknowledgedSeq(1));
        Assert.Equal(-1, q.GetLastAcknowledgedSeq(2)); // untouched slot
    }

    [Fact]
    public void Reset_Clears()
    {
        var q = NewQueue();
        q.Store(0, 0, 1);
        q.Dequeue(0, out _);
        q.Reset();
        Assert.Equal(-1, q.GetLastAcknowledgedSeq(0));
        Assert.Equal(-999, q.Dequeue(0, out _));
    }

    [Fact]
    public void Store_ReplayOfAlreadyDequeuedSeq_IsRejected()
    {
        var q = NewQueue();
        q.Store(0, 0, 100);
        q.Dequeue(0, out _);          // process seq 0; high-water mark is now 0
        q.Store(0, 0, 999);           // replay of an already-processed seq -> must be dropped
        Assert.Equal(-999, q.Dequeue(0, out int ack)); // nothing reprocessed
        Assert.Equal(0, ack);
    }

    [Fact]
    public void Store_StaleLowerSeqAfterHigherProcessed_DoesNotRegressAck()
    {
        var q = NewQueue();
        q.Store(0, 5, 55);
        q.Dequeue(0, out _);          // ack high-water -> 5
        q.Store(0, 3, 33);           // stale (below high-water) -> dropped
        Assert.Equal(-999, q.Dequeue(0, out int ack));
        Assert.Equal(5, ack);        // ack must not regress to 3
    }

    [Fact]
    public void Store_PerSlotQueue_IsCappedToMostRecentCommands()
    {
        // A hostile peer flooding distinct seqs must not grow the per-slot queue without bound.
        var q = new RemoteCommandQueue<int>(neutralCommand: -999, maxQueuedPerSlot: 3, maxSlots: 8);
        for (int seq = 0; seq < 100; seq++) q.Store(0, seq, seq);
        Assert.Equal(97, q.Dequeue(0, out _)); // only the 3 most recent survive
        Assert.Equal(98, q.Dequeue(0, out _));
        Assert.Equal(99, q.Dequeue(0, out _));
        Assert.Equal(-999, q.Dequeue(0, out _)); // queue never held more than the cap
    }

    [Fact]
    public void Store_DistinctSlotCount_IsCapped()
    {
        // A hostile peer spraying distinct slot ids must not grow the slot map without bound.
        var q = new RemoteCommandQueue<int>(neutralCommand: -999, maxQueuedPerSlot: 8, maxSlots: 2);
        q.Store(0, 0, 10);
        q.Store(1, 0, 11);
        q.Store(2, 0, 12);           // third distinct slot, over the cap -> ignored
        Assert.Equal(10, q.Dequeue(0, out _));
        Assert.Equal(11, q.Dequeue(1, out _));
        Assert.Equal(-999, q.Dequeue(2, out _)); // never stored
    }

    [Fact]
    public void Forget_ClearsSlotStateAndAck()
    {
        // Drive a slot's high-water mark up the way a session does (store + dequeue, repeatedly).
        var q = NewQueue();
        q.Store(0, 0, 10); q.Dequeue(0, out _);
        q.Store(0, 1, 11); q.Dequeue(0, out _);
        q.Store(0, 2, 12); q.Dequeue(0, out _);
        Assert.Equal(2, q.GetLastAcknowledgedSeq(0));   // high-water now 2

        q.Forget(0);                                    // slot released + recycled to a new session
        Assert.Equal(-1, q.GetLastAcknowledgedSeq(0));  // high-water reset to "never processed"

        // A recycled session restarts its seq at 0; that must now be accepted again, not rejected as a replay.
        q.Store(0, 0, 77);
        Assert.Equal(77, q.Dequeue(0, out int ack));    // command flows again
        Assert.Equal(0, ack);                           // ack is the fresh seq, not the stale mark
    }

    [Fact]
    public void Forget_UnknownSlot_IsNoOp()
    {
        var q = NewQueue();
        q.Forget(123);                                  // never seen -> no throw
        Assert.Equal(-1, q.GetLastAcknowledgedSeq(123));
    }

    [Fact]
    public void Forget_DoesNotAffectOtherSlots()
    {
        var q = NewQueue();
        q.Store(0, 0, 1); q.Dequeue(0, out _);          // slot 0 high-water 0
        q.Store(1, 5, 2); q.Dequeue(1, out _);          // slot 1 high-water 5
        q.Forget(0);
        Assert.Equal(-1, q.GetLastAcknowledgedSeq(0));  // only slot 0 forgotten
        Assert.Equal(5, q.GetLastAcknowledgedSeq(1));   // slot 1 untouched
    }

    [Fact]
    public void Forget_DropsBufferedUndequeuedCommands()
    {
        // A command buffered but not yet dequeued for the leaving slot must not leak to the recycling session.
        var q = NewQueue();
        q.Store(0, 7, 70);                              // buffered, never dequeued (high-water still -1)
        q.Forget(0);
        Assert.Equal(-999, q.Dequeue(0, out int ack));  // nothing left to hand the new session
        Assert.Equal(-1, ack);
    }

    [Fact]
    public void Ctor_NonPositiveCaps_Throw()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new RemoteCommandQueue<int>(neutralCommand: 0, maxQueuedPerSlot: 0, maxSlots: 8));
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new RemoteCommandQueue<int>(neutralCommand: 0, maxQueuedPerSlot: 8, maxSlots: 0));
    }
}
