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
}
