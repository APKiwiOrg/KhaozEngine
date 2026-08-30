using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class BoundedEventQueueTests
{
    private static List<int> DrainAll(BoundedEventQueue<int> q)
    {
        var list = new List<int>();
        while (q.TryDequeue(out int v)) list.Add(v);
        return list;
    }

    [Fact]
    public void WithinCapacity_KeepsEverything_InOrder_NoDrops()
    {
        var q = new BoundedEventQueue<int>(capacity: 5);
        for (int i = 0; i < 3; i++) q.Enqueue(i);

        Assert.Equal(3, q.Count);
        Assert.Equal(0, q.DroppedCount);
        Assert.Equal(new[] { 0, 1, 2 }, DrainAll(q));
    }

    [Fact]
    public void BeyondCapacity_StaysAtCap_DropsOldest_KeepsNewest()
    {
        var q = new BoundedEventQueue<int>(capacity: 3);
        for (int i = 0; i < 10; i++) q.Enqueue(i); // enqueue 0..9 without draining

        // Never grows past the cap, regardless of how many are pushed.
        Assert.Equal(3, q.Count);
        // The 7 oldest (0..6) were evicted; only the newest 3 survive.
        Assert.Equal(7, q.DroppedCount);
        Assert.Equal(new[] { 7, 8, 9 }, DrainAll(q));
    }

    [Fact]
    public void Drain_ThenRefill_BoundsIndependently()
    {
        var q = new BoundedEventQueue<int>(capacity: 2);
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3); // drops 1
        Assert.Equal(1, q.DroppedCount);
        Assert.Equal(new[] { 2, 3 }, DrainAll(q));

        // After draining, the queue is empty and bounds the next burst the same way.
        q.Enqueue(4);
        q.Enqueue(5);
        q.Enqueue(6); // drops 4
        Assert.Equal(2, q.DroppedCount); // cumulative across the queue's lifetime
        Assert.Equal(new[] { 5, 6 }, DrainAll(q));
    }

    [Fact]
    public void TerminalItem_SurvivesAnOverflow_AndKeepsItsPlaceInTheDrainOrder()
    {
        var q = new BoundedEventQueue<int>(capacity: 3);
        q.Enqueue(1);
        q.EnqueueTerminal(-1); // a peer's Disconnected, mixed into ordinary traffic
        for (int i = 2; i <= 10; i++) q.Enqueue(i);

        // The cap still bounds ordinary traffic exactly as before: 10 in, the newest 3 survive, 7 evicted.
        Assert.Equal(7, q.DroppedCount);
        // The terminal item is not one of the 7, and it still drains ahead of every item it preceded.
        Assert.Equal(new[] { -1, 8, 9, 10 }, DrainAll(q));
    }

    [Fact]
    public void TerminalItems_DoNotCountTowardsTheCap()
    {
        var q = new BoundedEventQueue<int>(capacity: 2);
        for (int i = 0; i < 5; i++) q.EnqueueTerminal(i);

        Assert.Equal(0, q.DroppedCount);
        Assert.Equal(5, q.Count); // deliberate: Count may exceed Capacity by the buffered terminal items
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, DrainAll(q));
    }

    [Fact]
    public void Terminal_ThenDrain_LeavesTheCapIntactForTheNextBurst()
    {
        var q = new BoundedEventQueue<int>(capacity: 2);
        q.EnqueueTerminal(-1);
        q.Enqueue(1);
        q.Enqueue(2);
        Assert.Equal(new[] { -1, 1, 2 }, DrainAll(q));

        q.Enqueue(3);
        q.Enqueue(4);
        q.Enqueue(5); // drops 3
        Assert.Equal(1, q.DroppedCount);
        Assert.Equal(new[] { 4, 5 }, DrainAll(q));
    }

    [Fact]
    public void TryDequeue_OnEmpty_ReturnsFalse_AndDefault()
    {
        var q = new BoundedEventQueue<int>(capacity: 4);
        Assert.False(q.TryDequeue(out int v));
        Assert.Equal(0, v);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveCapacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedEventQueue<int>(capacity));
    }

    [Fact]
    public void DefaultCapacity_IsPositive_AndUsedWhenUnspecified()
    {
        var q = new BoundedEventQueue<int>();
        Assert.True(BoundedEventQueue<int>.DefaultCapacity > 0);
        Assert.Equal(BoundedEventQueue<int>.DefaultCapacity, q.Capacity);
    }
}
