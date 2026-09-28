using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationWireTests
{
    const long Tick = 0x0102_0304_0506_0708;
    static readonly TilePreparationChunkHeader Header = new(Tick, 0, 1);
    static readonly TileCombatPreparation GoldenState = new(
        0x1112_1314_1516_1718, 0x2122_2324_2526_2728, 0x8182_8384_8586_8788UL,
        0x9192_9394U, 0xA1A2_A3A4U, 0x0102_0304_0506_0709, 0x0102_0304_0506_070C, 1, 14);

    [Fact]
    public void Preparation_frames_have_exact_little_endian_bytes()
    {
        byte[] expected =
        [
            4, 1, 8, 7, 6, 5, 4, 3, 2, 1, 0, 0, 1, 0, 1, 0,
            0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,
            0x28, 0x27, 0x26, 0x25, 0x24, 0x23, 0x22, 0x21,
            0x88, 0x87, 0x86, 0x85, 0x84, 0x83, 0x82, 0x81,
            0x94, 0x93, 0x92, 0x91, 0xA4, 0xA3, 0xA2, 0xA1,
            9, 7, 6, 5, 4, 3, 2, 1,
            12, 7, 6, 5, 4, 3, 2, 1, 1, 14
        ];
        byte[] encoded = TileProtocol.EncodePreparationChunk(Header, [GoldenState], 0, 1);
        Assert.Equal(66, encoded.Length);
        Assert.Equal(expected, encoded);
        var decoded = new List<TileCombatPreparation>();
        Assert.True(TileProtocol.TryDecodePreparationChunk(expected, out TilePreparationChunkHeader header, decoded));
        Assert.Equal(Header, header);
        Assert.Equal(GoldenState, Assert.Single(decoded));
    }

    [Fact]
    public void Terminal_frames_preserve_resolution_and_all_nine_reasons()
    {
        var resolved = new TileCombatTerminal(GoldenState.AttackerNetId, GoldenState.TargetNetId,
            GoldenState.AttackId, GoldenState.Revision, GoldenState.PresentationKey, Tick,
            TileCombatTerminalKind.Resolved, TileCombatPreparationEndReason.None, 0xB1B2, 0xC1, 3);
        byte[] expected =
        [
            5, 1, 8, 7, 6, 5, 4, 3, 2, 1, 0, 0, 1, 0, 1, 0,
            0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,
            0x28, 0x27, 0x26, 0x25, 0x24, 0x23, 0x22, 0x21,
            0x88, 0x87, 0x86, 0x85, 0x84, 0x83, 0x82, 0x81,
            0x94, 0x93, 0x92, 0x91, 0xA4, 0xA3, 0xA2, 0xA1,
            8, 7, 6, 5, 4, 3, 2, 1, 1, 0, 0xB2, 0xB1, 0xC1, 3
        ];
        Assert.Equal(expected, TileProtocol.EncodePreparationTerminalChunk(Header, [resolved], 0, 1));
        var decoded = new List<TileCombatTerminal>();
        Assert.True(TileProtocol.TryDecodePreparationTerminalChunk(expected, out TilePreparationChunkHeader header, decoded));
        Assert.Equal(Header, header);
        Assert.Equal(resolved, Assert.Single(decoded));
        Assert.Equal(62, expected.Length);

        for (byte reason = 1; reason <= 9; reason++)
        {
            var cancelled = resolved with
            {
                Kind = TileCombatTerminalKind.Cancelled,
                Reason = (TileCombatPreparationEndReason)reason,
                ImpactTick = Tick + 4,
                Amount = 0,
                HitKind = 0,
                Flags = 0
            };
            byte[] bytes = TileProtocol.EncodePreparationTerminalChunk(Header, [cancelled], 0, 1);
            Assert.Equal(reason, bytes[57]);
            Assert.True(TileProtocol.TryDecodePreparationTerminalChunk(bytes, out _, decoded));
            Assert.Equal(cancelled, Assert.Single(decoded));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public void State_chunk_counts_are_bounded_and_empty_state_has_only_a_header(int count)
    {
        TileCombatPreparation[] records = Enumerable.Range(1, count).Select(i => State(i)).ToArray();
        var header = new TilePreparationChunkHeader(100, 0, 1);
        byte[] bytes = TileProtocol.EncodePreparationChunk(header, records, 0, count);
        Assert.Equal(16 + 50 * count, bytes.Length);
        var decoded = new List<TileCombatPreparation> { State(999) };
        Assert.True(TileProtocol.TryDecodePreparationChunk(bytes, out TilePreparationChunkHeader back, decoded));
        Assert.Equal(header, back);
        Assert.Equal(records, decoded);
    }

    [Fact]
    public void A_256_record_set_is_sliced_into_two_chunks_without_reordering()
    {
        TileCombatPreparation[] records = Enumerable.Range(1, 256).Select(i => State(i)).ToArray();
        byte[] first = TileProtocol.EncodePreparationChunk(new(100, 0, 2), records, 0, 255);
        byte[] last = TileProtocol.EncodePreparationChunk(new(100, 1, 2), records, 255, 1);
        var decoded = new List<TileCombatPreparation>();
        Assert.True(TileProtocol.TryDecodePreparationChunk(first, out _, decoded));
        Assert.Equal(255, decoded.Count);
        Assert.True(TileProtocol.TryDecodePreparationChunk(last, out TilePreparationChunkHeader header, decoded));
        Assert.Equal(new TilePreparationChunkHeader(100, 1, 2), header);
        Assert.Equal(256L, Assert.Single(decoded).AttackerNetId);
        Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationChunk(new(100, 0, 1), records, 0, 256));
    }

    [Fact]
    public void Counts_lengths_and_deadlines_are_validated_atomically()
    {
        byte[] valid = TileProtocol.EncodePreparationChunk(Header, [GoldenState], 0, 1);
        var into = new List<TileCombatPreparation>();
        for (int length = 0; length < valid.Length; length++)
        {
            into.Add(GoldenState);
            Assert.False(TileProtocol.TryDecodePreparationChunk(valid.AsSpan(0, length), out _, into));
            Assert.Empty(into);
        }
        Assert.False(TileProtocol.TryDecodePreparationChunk([.. valid, 0], out _, into));

        (string Name, Action<byte[]> Change)[] mutations =
        [
            ("tag", b => b[0] = 99), ("schema", b => b[1] = 2),
            ("negative tick", b => b[9] = 0x80),
            ("zero chunks", b => Write16(b, 12, 0)), ("too many chunks", b => Write16(b, 12, 257)),
            ("bad index", b => Write16(b, 10, 1)),
            ("short count", b => Write16(b, 14, 0)), ("long count", b => Write16(b, 14, 2)),
            ("over chunk cap", b => Write16(b, 14, 256)),
            ("zero attacker", b => b.AsSpan(16, 8).Clear()), ("zero target", b => b.AsSpan(24, 8).Clear()),
            ("zero id", b => b.AsSpan(32, 8).Clear()), ("zero revision", b => b.AsSpan(40, 4).Clear()),
            ("negative prepare", b => Write64(b, 48, -1)),
            ("due now", b => Write64(b, 56, Tick)), ("past due", b => Write64(b, 56, Tick - 1)),
            ("zero lead", b => Write64(b, 48, Tick + 4)),
            ("reverse lead", b => Write64(b, 48, Tick + 5)),
            ("long lead", b => Write64(b, 48, 0)),
            ("negative impact", b => Write64(b, 56, long.MinValue)),
            ("zero strike", b => b[64] = 0), ("long strike", b => b[64] = 4),
            ("short cadence", b => b[65] = 2), ("zero cadence", b => b[65] = 0)
        ];
        foreach (var mutation in mutations)
        {
            byte[] bytes = (byte[])valid.Clone();
            mutation.Change(bytes);
            into.Add(GoldenState);
            Assert.False(TileProtocol.TryDecodePreparationChunk(bytes, out TilePreparationChunkHeader header, into), mutation.Name);
            Assert.Empty(into);
            Assert.Equal(default, header);
        }

        byte[] two = TileProtocol.EncodePreparationChunk(new(100, 0, 1), [State(1), State(2)], 0, 2);
        two.AsSpan(16 + 50 + 24, 4).Clear();
        Assert.False(TileProtocol.TryDecodePreparationChunk(two, out _, into));
        Assert.Empty(into);
        Assert.False(TileProtocol.TryDecodePreparationChunk(valid, out _, null!));
    }

    [Fact]
    public void State_records_require_unique_sorted_attackers_but_allow_a_zero_presentation_key()
    {
        TileCombatPreparation[] records = [State(1) with { PresentationKey = 0 }, State(2)];
        byte[] valid = TileProtocol.EncodePreparationChunk(new(100, 0, 1), records, 0, 2);
        var into = new List<TileCombatPreparation>();
        Assert.True(TileProtocol.TryDecodePreparationChunk(valid, out _, into));
        Assert.Equal(0U, into[0].PresentationKey);
        Write64(valid, 16 + 50, 1);
        Assert.False(TileProtocol.TryDecodePreparationChunk(valid, out _, into));
        Assert.Empty(into);
        Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationChunk(new(100, 0, 1), [State(2), State(1)], 0, 2));
        Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationChunk(new(100, 0, 1), [State(1), State(1)], 0, 2));
    }

    [Fact]
    public void Terminal_fields_are_strict_and_failure_never_exposes_a_valid_prefix()
    {
        byte[] resolved = TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), [Terminal(1)], 0, 1);
        byte[] cancelled = TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), [Cancellation(1)], 0, 1);
        var into = new List<TileCombatTerminal>();
        for (int length = 0; length < resolved.Length; length++)
        {
            into.Add(Terminal(2));
            Assert.False(TileProtocol.TryDecodePreparationTerminalChunk(resolved.AsSpan(0, length), out _, into));
            Assert.Empty(into);
        }
        Assert.False(TileProtocol.TryDecodePreparationTerminalChunk([.. resolved, 0], out _, into));
        (string Name, byte[] Basis, Action<byte[]> Change)[] mutations =
        [
            ("tag", resolved, b => b[0] = 4), ("schema", resolved, b => b[1] = 0),
            ("negative header tick", resolved, b => Write64(b, 2, -1)),
            ("zero chunks", resolved, b => Write16(b, 12, 0)),
            ("too many chunks", resolved, b => Write16(b, 12, 257)),
            ("bad index", resolved, b => Write16(b, 10, 1)),
            ("bad count", resolved, b => Write16(b, 14, 2)),
            ("over chunk cap", resolved, b => Write16(b, 14, 256)),
            ("zero attacker", resolved, b => b.AsSpan(16, 8).Clear()),
            ("zero target", resolved, b => b.AsSpan(24, 8).Clear()),
            ("zero id", resolved, b => b.AsSpan(32, 8).Clear()),
            ("zero revision", resolved, b => b.AsSpan(40, 4).Clear()),
            ("unknown kind", resolved, b => b[56] = 3),
            ("unknown flags", resolved, b => b[61] = 4),
            ("resolution reason", resolved, b => b[57] = 1),
            ("wrong resolution tick", resolved, b => Write64(b, 48, 101)),
            ("negative impact", cancelled, b => Write64(b, 48, -1)),
            ("zero cancellation reason", cancelled, b => b[57] = 0),
            ("unknown cancellation reason", cancelled, b => b[57] = 10),
            ("cancelled amount", cancelled, b => b[58] = 1),
            ("cancelled hit kind", cancelled, b => b[60] = 1),
            ("cancelled flags", cancelled, b => b[61] = 1)
        ];
        foreach (var mutation in mutations)
        {
            byte[] bytes = (byte[])mutation.Basis.Clone();
            mutation.Change(bytes);
            into.Add(Terminal(2));
            Assert.False(TileProtocol.TryDecodePreparationTerminalChunk(bytes, out TilePreparationChunkHeader header, into), mutation.Name);
            Assert.Empty(into);
            Assert.Equal(default, header);
        }
        byte[] two = TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), [Terminal(2), Terminal(1)], 0, 2);
        two[16 + 46 + 45] = 0x80;
        Assert.False(TileProtocol.TryDecodePreparationTerminalChunk(two, out _, into));
        Assert.Empty(into);
        Assert.False(TileProtocol.TryDecodePreparationTerminalChunk(resolved, out _, null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Known_result_flags_are_independent_and_zero_presentation_keys_are_valid(byte flags)
    {
        TileCombatTerminal record = Terminal(1) with { Flags = flags, PresentationKey = 0 };
        byte[] bytes = TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), [record], 0, 1);
        var into = new List<TileCombatTerminal>();
        Assert.True(TileProtocol.TryDecodePreparationTerminalChunk(bytes, out _, into));
        Assert.Equal(record, Assert.Single(into));
    }

    [Fact]
    public void A_cancellation_can_name_a_past_or_future_intended_deadline()
    {
        foreach (long impact in new long[] { 90, 100, 110 })
        {
            TileCombatTerminal record = Cancellation(1) with { ImpactTick = impact };
            byte[] bytes = TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), [record], 0, 1);
            var into = new List<TileCombatTerminal>();
            Assert.True(TileProtocol.TryDecodePreparationTerminalChunk(bytes, out _, into));
            Assert.Equal(record, Assert.Single(into));
        }
    }

    [Fact]
    public void Terminal_chunks_enforce_cancel_then_result_order_without_sorting_results()
    {
        var header = new TilePreparationChunkHeader(100, 0, 1);
        Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationTerminalChunk(header,
            [Terminal(1), Cancellation(2)], 0, 2));
        Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationTerminalChunk(header,
            [Cancellation(10), Cancellation(9)], 0, 2));
        byte[] bytes = TileProtocol.EncodePreparationTerminalChunk(header, [Cancellation(1), Terminal(2)], 0, 2);
        byte[] reversed = [.. bytes.AsSpan(0, 16), .. bytes.AsSpan(62, 46), .. bytes.AsSpan(16, 46)];
        var into = new List<TileCombatTerminal>();
        Assert.False(TileProtocol.TryDecodePreparationTerminalChunk(reversed, out _, into));
        Assert.Empty(into);
    }

    [Theory]
    [InlineData(2UL, 1UL)]
    [InlineData(1UL, 1UL)]
    [InlineData(ulong.MaxValue, 1UL)]
    public void Terminal_encoders_reject_reversed_or_duplicate_cancellation_ids(ulong first, ulong second)
    {
        TileCombatTerminal[] records =
        [
            Cancellation(10) with { AttackId = first },
            Cancellation(10) with { AttackId = second, Revision = 9 }
        ];
        Assert.ThrowsAny<ArgumentException>(() =>
            TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), records, 0, 2));
    }

    [Theory]
    [InlineData(2UL, 1UL)]
    [InlineData(1UL, 1UL)]
    [InlineData(ulong.MaxValue, 1UL)]
    public void Terminal_decoders_reject_reversed_or_duplicate_cancellation_ids(ulong first, ulong second)
    {
        TileCombatTerminal[] valid = [Cancellation(10), Cancellation(10) with { AttackId = 2, Revision = 9 }];
        byte[] bytes = TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), valid, 0, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32, 8), first);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(78, 8), second);
        var into = new List<TileCombatTerminal> { Terminal(99) };

        Assert.False(TileProtocol.TryDecodePreparationTerminalChunk(bytes, out TilePreparationChunkHeader header, into));

        Assert.Empty(into);
        Assert.Equal(default, header);
    }

    [Theory]
    [InlineData(1U)]
    [InlineData(9U)]
    public void Terminal_encoders_refuse_cancellation_and_resolution_of_one_attack_id(uint revision)
    {
        TileCombatTerminal[] records = [Cancellation(10), Terminal(10) with { Revision = revision }];
        Assert.ThrowsAny<ArgumentException>(() =>
            TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), records, 0, 2));
    }

    [Theory]
    [InlineData(1U)]
    [InlineData(9U)]
    public void Terminal_decoders_refuse_cancellation_and_resolution_of_one_attack_id(uint revision)
    {
        TileCombatTerminal[] valid = [Cancellation(10), Terminal(10) with { AttackId = 2, Revision = revision }];
        byte[] bytes = TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), valid, 0, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(78, 8), 1);
        var into = new List<TileCombatTerminal> { Terminal(99) };

        Assert.False(TileProtocol.TryDecodePreparationTerminalChunk(bytes, out TilePreparationChunkHeader header, into));

        Assert.Empty(into);
        Assert.Equal(default, header);
    }

    [Fact]
    public void Per_attacker_transition_checks_preserve_the_existing_order_of_other_attackers_results()
    {
        TileCombatTerminal[] records =
        [
            Cancellation(10) with { AttackId = 2 },
            Cancellation(20) with { AttackId = 7 },
            Terminal(30), Terminal(20) with { AttackId = 8 }, Terminal(10) with { AttackId = 3 }
        ];
        byte[] bytes = TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), records, 0, records.Length);
        var into = new List<TileCombatTerminal>();
        Assert.True(TileProtocol.TryDecodePreparationTerminalChunk(bytes, out _, into));
        Assert.Equal(records, into);
    }

    [Fact]
    public void Encoders_refuse_invalid_local_arguments_and_noncanonical_empty_sets()
    {
        TileCombatPreparation[] state = [State(1)];
        TileCombatTerminal[] terminal = [Terminal(1)];
        Assert.Throws<ArgumentNullException>(() => TileProtocol.EncodePreparationChunk(new(100, 0, 1), null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), null!, 0, 0));
        foreach ((int start, int count) in new[] { (-1, 1), (0, -1), (2, 0), (1, 1), (int.MaxValue, 1), (0, int.MaxValue) })
        {
            Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationChunk(new(100, 0, 1), state, start, count));
            Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), terminal, start, count));
        }
        foreach (TilePreparationChunkHeader header in new TilePreparationChunkHeader[]
            { new(-1, 0, 1), new(100, 0, 0), new(100, 1, 1), new(100, 0, 257) })
        {
            Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationChunk(header, state, 0, 1));
            Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationTerminalChunk(header, terminal, 0, 1));
        }
        Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationChunk(new(100, 0, 2), state, 0, 0));
        Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), terminal, 0, 0));
        Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationChunk(new(100, 0, 1), [State(1) with { ImpactTick = 100 }], 0, 1));
        Assert.ThrowsAny<ArgumentException>(() => TileProtocol.EncodePreparationTerminalChunk(new(100, 0, 1), [Terminal(1) with { Reason = TileCombatPreparationEndReason.Disengaged }], 0, 1));
    }

    internal static TileCombatPreparation State(long attacker, long tick = 100) =>
        new(attacker, 999999, 1, 1, 7, tick, tick + 3, 1, 14);

    internal static TileCombatTerminal Terminal(long attacker, long tick = 100) =>
        new(attacker, 999999, 1, 1, 7, tick, TileCombatTerminalKind.Resolved, TileCombatPreparationEndReason.None, 5, 7, 1);

    internal static TileCombatTerminal Cancellation(long attacker, long tick = 100) =>
        new(attacker, 999999, 1, 1, 7, tick + 3, TileCombatTerminalKind.Cancelled,
            TileCombatPreparationEndReason.Disengaged, 0, 0, 0);

    static void Write16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    static void Write64(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset, 8), value);
}
