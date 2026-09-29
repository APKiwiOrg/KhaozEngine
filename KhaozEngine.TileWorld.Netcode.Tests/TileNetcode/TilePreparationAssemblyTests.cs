using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld.Netcode;
using Xunit;
using static KhaozEngine.Tests.TileNetcode.TilePreparationWireTests;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationAssemblyTests
{
    [Fact]
    public void Malformed_final_chunk_preserves_the_previous_complete_frame()
    {
        var assembler = new TileCombatPreparationAssembler();
        Assert.True(assembler.TryAddState(new(90, 0, 1), [State(17)], out TilePreparationStateFrame? published));
        Assert.NotNull(published);
        TilePreparationStateFrame previous = published;
        TileCombatPreparation[] before = published.Records.ToArray();
        Assert.True(assembler.TryAddState(new(100, 0, 2), [State(21)], out TilePreparationStateFrame? pending));
        Assert.Null(pending);

        bool accepted = assembler.TryAddState(new(100, 1, 2), [State(22) with { Revision = 0 }], out TilePreparationStateFrame? complete);
        if (accepted && complete is not null) published = complete;

        Assert.False(accepted);
        Assert.Null(complete);
        Assert.Same(previous, published);
        Assert.Equal(before, published.Records);
        Assert.True(assembler.TryAddState(new(101, 0, 1), [State(23, 101)], out complete));
        Assert.Equal(23L, Assert.Single(complete!.Records).AttackerNetId);
        Assert.Equal(before, previous.Records);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void An_overdue_record_assembles_below_its_strike_ticks(byte strike)
    {
        var assembler = new TileCombatPreparationAssembler();
        TileCombatPreparation live = Overdue(strike, strike - 1);
        Assert.True(assembler.TryAddState(new(100, 0, 1), [live], out TilePreparationStateFrame? complete));
        Assert.Equal(live, Assert.Single(complete!.Records));

        Assert.False(assembler.TryAddState(new(100, 0, 1), [Overdue(strike, strike)], out complete));
        Assert.Null(complete);
        Assert.False(new TileCombatPreparationAssembler().TryAddState(new(100, 0, 1),
            [Overdue(strike, strike)], out complete));
        Assert.Null(complete);
    }

    [Fact]
    public void Incoming_lists_and_completed_arrays_are_never_reused_as_pending_storage()
    {
        var assembler = new TileCombatPreparationAssembler();
        var incoming = new List<TileCombatPreparation> { State(1) };
        Assert.True(assembler.TryAddState(new(100, 0, 2), incoming, out _));
        incoming[0] = State(9);
        Assert.True(assembler.TryAddState(new(100, 1, 2), [State(2)], out TilePreparationStateFrame? first));
        Assert.Equal(new long[] { 1, 2 }, first!.Records.Select(x => x.AttackerNetId));
        Assert.True(assembler.TryAddState(new(101, 0, 1), [State(3, 101)], out TilePreparationStateFrame? second));
        Assert.NotSame(first.Records, second!.Records);
        Assert.Equal(new long[] { 1, 2 }, first.Records.Select(x => x.AttackerNetId));
    }

    [Theory]
    [InlineData("tick")]
    [InlineData("count")]
    [InlineData("restart")]
    [InlineData("skipped")]
    [InlineData("empty final")]
    [InlineData("duplicate attacker")]
    [InlineData("unsorted attacker")]
    public void Inconsistent_continuations_clear_pending_assembly(string mutation)
    {
        var assembler = new TileCombatPreparationAssembler();
        Assert.True(assembler.TryAddState(new(100, 0, 2), [State(10)], out _));
        TilePreparationChunkHeader header = new(100, 1, 2);
        TileCombatPreparation[] next = [State(11)];
        switch (mutation)
        {
            case "tick": header = new(101, 1, 2); break;
            case "count": header = new(100, 1, 3); break;
            case "restart": header = new(100, 0, 2); break;
            case "skipped": header = new(100, 2, 3); break;
            case "empty final": next = []; break;
            case "duplicate attacker": next = [State(10)]; break;
            case "unsorted attacker": next = [State(9)]; break;
        }
        Assert.False(assembler.TryAddState(header, next, out TilePreparationStateFrame? complete));
        Assert.Null(complete);
        Assert.False(assembler.TryAddState(new(100, 1, 2), [State(11)], out _));
        Assert.True(assembler.TryAddState(new(101, 0, 1), [State(12, 101)], out complete));
        Assert.Single(complete!.Records);
    }

    [Fact]
    public void State_and_terminal_chunk_sets_cannot_interleave()
    {
        var assembler = new TileCombatPreparationAssembler();
        Assert.True(assembler.TryAddState(new(100, 0, 2), [State(1)], out _));
        Assert.False(assembler.TryAddTerminals(new(100, 0, 1), [Terminal(1)], out _));
        Assert.False(assembler.TryAddState(new(100, 1, 2), [State(2)], out _));
        Assert.True(assembler.TryAddTerminals(new(100, 0, 1), [Terminal(1)], out TilePreparationTerminalFrame? complete));
        Assert.Single(complete!.Records);
    }

    [Fact]
    public void Clear_discards_incomplete_work_and_allows_a_new_set()
    {
        var assembler = new TileCombatPreparationAssembler();
        Assert.True(assembler.TryAddTerminals(new(100, 0, 2), [Terminal(1)], out _));
        assembler.Clear();
        Assert.False(assembler.TryAddTerminals(new(100, 1, 2), [Terminal(2)], out _));
        Assert.True(assembler.TryAddState(new(101, 0, 1), [], out TilePreparationStateFrame? empty));
        Assert.Empty(empty!.Records);
        Assert.False(assembler.TryAddTerminals(new(101, 0, 1), [], out _));
    }

    [Fact]
    public void The_maximum_set_holds_65280_records_in_256_bounded_chunks()
    {
        var assembler = new TileCombatPreparationAssembler();
        TilePreparationStateFrame? complete = null;
        for (ushort chunk = 0; chunk < 256; chunk++)
        {
            TileCombatPreparation[] records = Enumerable.Range(chunk * 255 + 1, 255).Select(i => State(i)).ToArray();
            Assert.True(assembler.TryAddState(new(100, chunk, 256), records, out complete));
            if (chunk < 255) Assert.Null(complete);
        }
        Assert.NotNull(complete);
        Assert.Equal(65280, complete.Records.Length);
        Assert.Equal(1L, complete.Records[0].AttackerNetId);
        Assert.Equal(65280L, complete.Records[^1].AttackerNetId);
        Assert.False(assembler.TryAddState(new(100, 0, 257), [State(1)], out _));
        Assert.False(assembler.TryAddState(new(100, 0, 1), Enumerable.Range(1, 256).Select(i => State(i)).ToArray(), out _));
    }

    [Fact]
    public void Header_limits_are_checked_before_accessing_record_storage()
    {
        var assembler = new TileCombatPreparationAssembler();
        var tooMany = new UnreadableList<TileCombatPreparation>(int.MaxValue);
        Assert.False(assembler.TryAddState(new(100, 0, 1), tooMany, out _));
        Assert.False(assembler.TryAddState(new(100, 0, ushort.MaxValue), new UnreadableList<TileCombatPreparation>(1), out _));
        Assert.False(assembler.TryAddState(new(-1, 0, 1), [State(1)], out _));
        Assert.False(assembler.TryAddState(new(100, 0, 1), null!, out _));
        Assert.False(assembler.TryAddTerminals(new(100, 0, 1), new UnreadableList<TileCombatTerminal>(256), out _));
    }

    [Fact]
    public void Terminal_order_and_owned_storage_survive_chunk_boundaries()
    {
        var assembler = new TileCombatPreparationAssembler();
        var incoming = new List<TileCombatTerminal> { Cancellation(10), Cancellation(10) with { AttackId = 2 } };
        Assert.True(assembler.TryAddTerminals(new(100, 0, 2), incoming, out _));
        incoming.Clear();
        TileCombatTerminal[] results = [Terminal(20), Terminal(3)];
        Assert.True(assembler.TryAddTerminals(new(100, 1, 2), results, out TilePreparationTerminalFrame? complete));
        Assert.Equal(new long[] { 10, 10, 20, 3 }, complete!.Records.Select(x => x.AttackerNetId));
        Assert.Equal(2UL, complete.Records[1].AttackId);
        results[0] = Terminal(999);
        Assert.Equal(20L, complete.Records[2].AttackerNetId);
        Assert.True(assembler.TryAddTerminals(new(101, 0, 1), [Terminal(4, 101)], out TilePreparationTerminalFrame? later));
        Assert.NotSame(complete.Records, later!.Records);
        Assert.Equal(20L, complete.Records[2].AttackerNetId);
    }

    [Fact]
    public void A_cancellation_cannot_follow_a_result_or_reverse_cancelled_attacker_order()
    {
        var assembler = new TileCombatPreparationAssembler();
        Assert.True(assembler.TryAddTerminals(new(100, 0, 2), [Terminal(1)], out _));
        Assert.False(assembler.TryAddTerminals(new(100, 1, 2), [Cancellation(2)], out _));
        Assert.True(assembler.TryAddTerminals(new(100, 0, 2), [Cancellation(10)], out _));
        Assert.False(assembler.TryAddTerminals(new(100, 1, 2), [Cancellation(9)], out _));
    }

    [Theory]
    [InlineData(2UL, 1UL)]
    [InlineData(1UL, 1UL)]
    [InlineData(ulong.MaxValue, 1UL)]
    public void Cancellation_attack_ids_must_increase_across_chunks(ulong first, ulong second)
    {
        var assembler = new TileCombatPreparationAssembler();
        Assert.True(assembler.TryAddTerminals(new(100, 0, 2), [Cancellation(10) with { AttackId = first }], out _));

        Assert.False(assembler.TryAddTerminals(new(100, 1, 2),
            [Cancellation(10) with { AttackId = second, Revision = 9 }], out TilePreparationTerminalFrame? complete));

        Assert.Null(complete);
        Assert.True(assembler.TryAddTerminals(new(101, 0, 1), [Terminal(10, 101)], out complete));
        Assert.Single(complete!.Records);
    }

    [Theory]
    [InlineData(1U)]
    [InlineData(9U)]
    public void Cancellation_and_resolution_cannot_share_an_attack_id_across_chunks(uint revision)
    {
        var assembler = new TileCombatPreparationAssembler();
        Assert.True(assembler.TryAddTerminals(new(100, 0, 3), [Cancellation(10)], out _));
        Assert.True(assembler.TryAddTerminals(new(100, 1, 3), [Cancellation(20)], out _));

        Assert.False(assembler.TryAddTerminals(new(100, 2, 3),
            [Terminal(10) with { Revision = revision }], out TilePreparationTerminalFrame? complete));

        Assert.Null(complete);
        Assert.True(assembler.TryAddTerminals(new(101, 0, 1), [Terminal(10, 101)], out complete));
        Assert.Single(complete!.Records);
    }

    [Fact]
    public void Valid_terminal_identity_tracking_preserves_roll_order_and_resets_after_completion()
    {
        var assembler = new TileCombatPreparationAssembler();
        TileCombatTerminal[] first = [Cancellation(10) with { AttackId = 2 }, Cancellation(20) with { AttackId = 7 }];
        TileCombatTerminal[] last = [Terminal(30), Terminal(20) with { AttackId = 8 }, Terminal(10) with { AttackId = 3 }];
        Assert.True(assembler.TryAddTerminals(new(100, 0, 2), first, out _));
        Assert.True(assembler.TryAddTerminals(new(100, 1, 2), last, out TilePreparationTerminalFrame? complete));
        Assert.Equal(new long[] { 10, 20, 30, 20, 10 }, complete!.Records.Select(x => x.AttackerNetId));
        Assert.True(assembler.TryAddTerminals(new(100, 0, 2), first, out _));
        Assert.True(assembler.TryAddTerminals(new(100, 1, 2), last, out TilePreparationTerminalFrame? repeated));
        Assert.Equal(complete.Records, repeated!.Records);
    }

    [Theory]
    [InlineData(2UL, 1UL, false)]
    [InlineData(1UL, 1UL, false)]
    [InlineData(1UL, 1UL, true)]
    public void Invalid_terminal_identities_are_rejected_within_one_assembly_chunk(ulong first, ulong second, bool resolved)
    {
        var assembler = new TileCombatPreparationAssembler();
        TileCombatTerminal later = (resolved ? Terminal(10) : Cancellation(10)) with { AttackId = second, Revision = 9 };
        Assert.False(assembler.TryAddTerminals(new(100, 0, 1),
            [Cancellation(10) with { AttackId = first }, later], out TilePreparationTerminalFrame? complete));
        Assert.Null(complete);
    }

    sealed class UnreadableList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => throw new InvalidOperationException("Record storage must not be read.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Records must not be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
