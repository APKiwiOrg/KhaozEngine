using System;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// The 10.0.0 <see cref="NetIdAllocator"/>: monotonic per-node allocation, the node-prefix pack/unpack invariants
/// (high 16 bits = node, low 48 = counter), high-water resume, and cross-node non-collision.
/// </summary>
public class NetIdAllocatorTests
{
    [Fact]
    public void Node0_hands_out_1_2_3_numerically_unchanged_from_the_old_counter()
    {
        var a = new NetIdAllocator();   // node 0
        Assert.Equal(0, a.NodeId);
        Assert.Equal(1L, a.Next().Value);
        Assert.Equal(2L, a.Next().Value);
        Assert.Equal(3L, a.Next().Value);
        Assert.Equal(4L, a.NextValue);   // peek: one past the highest handed out
    }

    [Fact]
    public void Pack_unpack_round_trips_the_node_and_counter()
    {
        for (ushort node = 0; node < 4; node++)
        {
            long id = NetIdAllocator.Pack(node, 123456789L);
            Assert.Equal(node, NetIdAllocator.NodeOf(id));
            Assert.Equal(123456789L, NetIdAllocator.CounterOf(id));
        }
        // Boundary: the maximum node id and counter.
        long maxId = NetIdAllocator.Pack((ushort)NetIdAllocator.MaxNodeId, NetIdAllocator.MaxCounter);
        Assert.Equal((ushort)NetIdAllocator.MaxNodeId, NetIdAllocator.NodeOf(maxId));
        Assert.Equal(NetIdAllocator.MaxCounter, NetIdAllocator.CounterOf(maxId));
    }

    [Fact]
    public void A_non_zero_node_stamps_the_high_bits_and_keeps_a_small_counter()
    {
        var a = new NetIdAllocator(nodeId: 7);
        NetId first = a.Next();
        Assert.Equal(7, first.Node);
        Assert.Equal(1L, first.Counter);
        Assert.Equal(NetIdAllocator.Pack(7, 1), first.Value);
    }

    [Fact]
    public void Two_nodes_never_collide()
    {
        var n1 = new NetIdAllocator(nodeId: 1);
        var n2 = new NetIdAllocator(nodeId: 2);
        var seen = new System.Collections.Generic.HashSet<long>();
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(seen.Add(n1.Next().Value));
            Assert.True(seen.Add(n2.Next().Value));
        }
    }

    [Fact]
    public void EnsureNextAtLeast_raises_but_never_lowers()
    {
        var a = new NetIdAllocator();
        a.EnsureNextAtLeast(1000);
        Assert.Equal(1000L, a.Next().Value);
        a.EnsureNextAtLeast(500);            // lower -> ignored
        Assert.Equal(1001L, a.Next().Value);
    }

    [Fact]
    public void EnsureNextAtLeast_ignores_a_different_nodes_high_water()
    {
        var a = new NetIdAllocator(nodeId: 0);
        a.EnsureNextAtLeast(NetIdAllocator.Pack(nodeId: 5, counter: 9999));   // node 5's high-water: not ours
        Assert.Equal(1L, a.Next().Value);   // node 0's counter is untouched
    }

    [Fact]
    public void Resume_from_persisted_high_water_never_reuses_a_restored_id()
    {
        // The persistence path stores NextValue and resumes above it after a restart.
        var run1 = new NetIdAllocator();
        long a = run1.Next().Value;   // 1
        long b = run1.Next().Value;   // 2
        long highWater = run1.NextValue;   // 3

        var run2 = new NetIdAllocator();
        run2.EnsureNextAtLeast(highWater);
        long c = run2.Next().Value;
        Assert.True(c > a && c > b);
        Assert.Equal(3L, c);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(NetIdAllocator.MaxCounter + 1)]
    public void Ctor_rejects_an_out_of_range_start_counter(long start) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetIdAllocator(nodeId: 0, startCounter: start));
}
