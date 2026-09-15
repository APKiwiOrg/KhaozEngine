using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Allocator;

/// <summary>
/// Spec 17 row 12 over spec 3.6 and contracts 6.2. Four clauses plus the exhaustion throw: Rotate issues
/// no id in the old node's range, a boot on a retired node id throws, an allocator whose persisted store
/// epoch differs from the live one refuses to issue, and a crash skips the unissued remainder of a
/// reserved block.
/// <para>
/// The order fact is the one this section exists for, and it is an ORDER assertion rather than a value
/// one. Persisting after issuing leaves a window in which a crash hands the next boot an id it has
/// already put on an item, and a duplicate instance id is the single failure the reservation rule
/// prevents.
/// </para>
/// </summary>
public class InstanceIdAllocatorTests
{
    const long LiveEpoch = 7;

    static InstanceIdAllocator Fresh(RecordingInstanceIdStore store) => new(store, LiveEpoch);

    static RecordingInstanceIdStore StoreAt(ushort nodeId, long counter, long epoch = LiveEpoch, params ushort[] retired) =>
        new(new InstanceIdState(InstanceIdAllocator.Pack(nodeId, counter), nodeId, epoch, retired));

    [Fact]
    public void Rotate_issues_no_id_in_the_old_nodes_range()
    {
        var store = StoreAt(7, 1);
        var allocator = Fresh(store);
        long before = allocator.Next();
        Assert.Equal((ushort)7, InstanceIdAllocator.NodeOf(before));

        allocator.Rotate(9);

        var issued = new List<long>();
        for (int i = 0; i < 5; i++) issued.Add(allocator.Next());
        Assert.All(issued, id => Assert.Equal((ushort)9, InstanceIdAllocator.NodeOf(id)));
        Assert.DoesNotContain(before, issued);
        Assert.Equal((ushort)9, allocator.NodeId);
        Assert.Contains((ushort)7, store.State.RetiredNodes.ToArray());
    }

    [Fact]
    public void A_boot_on_a_node_id_already_on_the_retired_list_throws()
    {
        var store = StoreAt(7, 1, LiveEpoch, 3, 7);
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => Fresh(store));
        Assert.Contains("retired", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_allocator_whose_persisted_store_epoch_differs_from_the_live_one_refuses_to_issue()
    {
        var store = StoreAt(0, 1, LiveEpoch - 1);
        var allocator = Fresh(store);

        Assert.False(allocator.CanIssue);
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => allocator.Next());
        Assert.Contains("IMutationJournalMaintenance.RotateStoreEpochAsync", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("DURABLE-PLAYER-JOURNAL-DESIGN-2026-09-06.md", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("section 10", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.PersistCount);
    }

    [Fact]
    public void A_crash_skips_the_unissued_remainder_of_a_reserved_block_and_never_reissues()
    {
        var store = new RecordingInstanceIdStore();
        var before = Fresh(store);
        var issued = new List<long> { before.Next(), before.Next(), before.Next() };

        // The crash: the allocator dies and a second one boots over the same durable bytes.
        var after = new InstanceIdAllocator(store, LiveEpoch);
        long first = after.Next();

        Assert.Equal(InstanceIdAllocator.ReservationBlock + 1L, InstanceIdAllocator.CounterOf(first));
        Assert.DoesNotContain(first, issued);
        Assert.All(issued, id => Assert.True(id < first));
    }

    [Fact]
    public void The_range_is_persisted_BEFORE_the_first_id_in_it_is_handed_out()
    {
        var store = new RecordingInstanceIdStore();
        var allocator = Fresh(store);

        for (int i = 0; i < InstanceIdAllocator.ReservationBlock + 2; i++) store.RecordIssue(allocator.Next());

        Assert.StartsWith("persist:", store.Log[0], StringComparison.Ordinal);
        long highWater = 0;
        foreach (string entry in store.Log)
        {
            string[] parts = entry.Split(':');
            long value = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            if (parts[0] == "persist") { highWater = value; continue; }
            Assert.True(value < highWater, $"issued counter {value} was not covered by a persist, high-water {highWater}");
        }

        // Two blocks, because the run crossed one boundary: the batch size is free, the order is not.
        Assert.Equal(2, store.PersistCount);
    }

    [Fact]
    public void Exhausting_the_48_bit_counter_throws_rather_than_wrapping()
    {
        var store = StoreAt(0, InstanceIdAllocator.MaxCounter);
        var allocator = Fresh(store);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => allocator.Next());
        Assert.Contains("exhausted", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.PersistCount);
    }

    [Fact]
    public void Node_zero_ids_are_numerically_identical_to_a_plain_counter()
    {
        var allocator = Fresh(new RecordingInstanceIdStore());
        Assert.Equal(1L, allocator.Next());
        Assert.Equal(2L, allocator.Next());
        Assert.Equal(3L, allocator.Next());
    }

    [Fact]
    public void The_packing_scheme_is_NetIdAllocators_arithmetic()
    {
        Assert.Equal(48, InstanceIdAllocator.CounterBits);
        Assert.Equal(16, InstanceIdAllocator.NodeBits);
        Assert.Equal(0x0000_FFFF_FFFF_FFFF, InstanceIdAllocator.CounterMask);
        Assert.Equal(65535, InstanceIdAllocator.MaxNodeId);

        long packed = InstanceIdAllocator.Pack(65535, 4201);
        Assert.True(packed < 0, "node 65535 sets the high bit, which is why the id is never zig-zagged");
        Assert.Equal((ushort)65535, InstanceIdAllocator.NodeOf(packed));
        Assert.Equal(4201L, InstanceIdAllocator.CounterOf(packed));
    }

    [Fact]
    public void A_node_65535_id_is_ten_varint_bytes()
    {
        long packed = InstanceIdAllocator.Pack(65535, 4201);
        Span<byte> buffer = stackalloc byte[16];
        int written = InstanceIdAllocator.WriteId(buffer, packed);

        Assert.Equal(10, written);
        Assert.Equal(10, InstanceIdAllocator.SizeOf(packed));
    }

    [Fact]
    public void An_id_is_an_unsigned_varint_over_the_bit_pattern_and_is_never_zig_zagged()
    {
        long packed = InstanceIdAllocator.Pack(65535, 4201);
        Span<byte> mine = stackalloc byte[16];
        Span<byte> unsigned = stackalloc byte[16];
        Span<byte> zigzag = stackalloc byte[16];
        int written = InstanceIdAllocator.WriteId(mine, packed);
        int unsignedWritten = ContentVarint.WriteUInt64(unsigned, (ulong)packed);
        int zigzagWritten = ContentVarint.WriteSigned64(zigzag, packed);

        Assert.Equal(unsignedWritten, written);
        Assert.True(mine[..written].SequenceEqual(unsigned[..unsignedWritten]));
        Assert.False(mine[..written].SequenceEqual(zigzag[..zigzagWritten]));

        int offset = 0;
        Assert.True(InstanceIdAllocator.TryReadId(mine[..written], ref offset, out long round, out string? reason));
        Assert.Null(reason);
        Assert.Equal(packed, round);
    }

    [Fact]
    public void A_node_0_id_under_268435456_costs_four_varint_bytes()
    {
        Assert.Equal(4, InstanceIdAllocator.SizeOf(268_435_455));
        Assert.Equal(5, InstanceIdAllocator.SizeOf(268_435_456));
    }

    [Theory]
    [InlineData(0, DeclaredInstanceProperties.None, false)]
    [InlineData(1, DeclaredInstanceProperties.None, true)]
    [InlineData(0, DeclaredInstanceProperties.Durability, true)]
    [InlineData(0, DeclaredInstanceProperties.Sockets, true)]
    [InlineData(0, DeclaredInstanceProperties.PerInstanceField, true)]
    [InlineData(58, DeclaredInstanceProperties.Durability, true)]
    public void An_item_gets_an_id_when_its_payload_is_non_empty_or_its_definition_declares_one(
        int payloadLength, DeclaredInstanceProperties declared, bool expected) =>
        Assert.Equal(expected, InstanceIdAllocator.NeedsInstanceId(payloadLength, declared));

    [Fact]
    public void The_id_rule_reads_the_ITEM_so_a_later_declaration_does_not_reach_a_stored_copy()
    {
        // The stored copy: an empty payload written when the definition declared nothing.
        Assert.False(InstanceIdAllocator.NeedsInstanceId(0, DeclaredInstanceProperties.None));

        // The definition gains durability. A copy encoded at 0 bytes is still a plain stack, because the
        // rule is asked of the item the caller holds rather than replayed over everything ever stored.
        Assert.True(InstanceIdAllocator.NeedsInstanceId(0, DeclaredInstanceProperties.Durability));
    }

    [Fact]
    public void A_rotate_onto_a_node_this_store_already_retired_is_refused()
    {
        var store = StoreAt(7, 1);
        var allocator = Fresh(store);
        allocator.Rotate(9);

        Assert.Throws<ArgumentException>(() => allocator.Rotate(7));
        Assert.Throws<ArgumentException>(() => allocator.Rotate(9));
    }
}
