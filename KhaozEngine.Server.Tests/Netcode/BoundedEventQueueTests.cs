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
