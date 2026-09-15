using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Quarantine;

/// <summary>
/// Spec 17 row 16, quarantine byte preservation, plus the version refusal contracts 10.5 asks for. The
/// bytes of a failed item are kept VERBATIM, and this is the durable envelope that keeps them: nothing
/// truncated, nothing normalized and nothing re-encoded (contracts 10.2).
/// <para>
/// The ordinal table is the other half. A <c>ReasonCode</c> byte outlives the release that wrote it and a
/// tool reading an old page has only the number, so spec 12.4 assigns the ordinals and this class pins that
/// they are what the type writes. An assigned number is never reused, even when a reason is withdrawn.
/// </para>
/// </summary>
public class QuarantineWrapperTests
{
    /// <summary>Spec 12.4's table, copied in as it stands. A change here is a change to stored bytes.</summary>
    public static TheoryData<byte, string> Ordinals() => new()
    {
        { 1, "payload-too-long" },
        { 2, "field-truncated" },
        { 3, "kind-out-of-order" },
        { 4, "kind-duplicate" },
        { 5, "varint-not-minimal" },
        { 6, "varint-overflow" },
        { 7, "socket-nesting" },
        { 8, "field-malformed" },
        { 9, "unknown-definition" },
        { 10, "unknown-content-reference" },
        { 11, "instance-id-missing" },
        { 12, "instance-id-duplicate" },
        { 13, "stack-not-instanceable" },
    };

    [Fact]
    public void Wrap_then_unwrap_returns_the_original_bytes_exactly()
    {
        byte[] original = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .AddByte(InstancePropertyKind.Rarity, 3)
            .ToArray();

        byte[] wrapper = QuarantineWrapper.Wrap(InstancePayloadReason.FieldMalformed, 17, original);

        Assert.True(QuarantineWrapper.Verify(wrapper));
        Assert.True(QuarantineWrapper.TryUnwrap(
            wrapper,
            out ReadOnlySpan<byte> kept,
            out string? reason,
            out int stampedVersion));

        Assert.Equal(original, kept.ToArray());
        Assert.Equal(InstancePayloadReason.FieldMalformed, reason);
        Assert.Equal(17, stampedVersion);
    }

    [Fact]
    public void An_original_above_MaxInstancePayloadBytes_wraps_and_unwraps_unchanged()
    {
        // payload-too-long IS a reason, so refusing to wrap the thing that failed for being too big would
        // destroy exactly the item the wrapper exists to keep (spec 12.4). The page entry's own length check
        // is what bounds it, at the 2 MiB section cap.
        var original = new byte[ItemInstancePayload.MaxInstancePayloadBytes + 64];
        for (int index = 0; index < original.Length; index++)
        {
            original[index] = (byte)(index & 0xFF);
        }

        byte[] wrapper = QuarantineWrapper.Wrap(InstancePayloadReason.PayloadTooLong, 4, original);

        Assert.True(wrapper.Length > ItemInstancePayload.MaxInstancePayloadBytes);
        Assert.True(QuarantineWrapper.Verify(wrapper));
        Assert.True(QuarantineWrapper.TryUnwrap(wrapper, out ReadOnlySpan<byte> kept, out string? reason, out int stamped));
        Assert.Equal(original, kept.ToArray());
        Assert.Equal(InstancePayloadReason.PayloadTooLong, reason);
        Assert.Equal(4, stamped);
    }

    [Fact]
    public void An_empty_original_wraps_and_unwraps_as_empty()
    {
        // An entry that quarantined for a reason the validator raised may carry no payload at all, and a
        // wrapper over nothing is still a wrapper rather than an absence.
        byte[] wrapper = QuarantineWrapper.Wrap(
            InstanceQuarantineReason.InstanceIdMissing,
            9,
            ReadOnlySpan<byte>.Empty);

        Assert.True(QuarantineWrapper.Verify(wrapper));
        Assert.True(QuarantineWrapper.TryUnwrap(wrapper, out ReadOnlySpan<byte> kept, out string? reason, out int stamped));
        Assert.True(kept.IsEmpty);
        Assert.Equal(InstanceQuarantineReason.InstanceIdMissing, reason);
        Assert.Equal(9, stamped);
    }

    [Fact]
    public void Verify_accepts_a_well_formed_wrapper_and_refuses_a_bare_payload()
    {
        // The whole reason a wrapper carries a magic and a payload does not (contracts 15): a wrapper must
        // be tellable from a payload by a human reading a hex dump and by a tool that never saw the entry
        // flag.
        byte[] payload = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .ToArray();

        Assert.True(QuarantineWrapper.Verify(QuarantineWrapper.Wrap(InstancePayloadReason.FieldTruncated, 1, payload)));
        Assert.False(QuarantineWrapper.Verify(payload));
        Assert.False(QuarantineWrapper.Verify(ReadOnlySpan<byte>.Empty));
        Assert.False(QuarantineWrapper.Verify(new byte[] { 0x4B, 0x45, 0x43 }));

        // A byte of the magic changed, which is the case the four bytes exist to catch.
        byte[] wrong = QuarantineWrapper.Wrap(InstancePayloadReason.FieldTruncated, 1, payload);
        wrong[2] = (byte)'X';
        Assert.False(QuarantineWrapper.Verify(wrong));
    }

    [Fact]
    public void A_wrapper_version_other_than_1_is_refused_rather_than_guessed()
    {
        // Contracts 15: a mismatched version is a REFUSAL of the whole record, never a best effort partial
        // read. The wrapper is durable, so version 1 is the only version until someone ships a second one.
        Assert.Equal(1, QuarantineWrapper.Version);

        byte[] wrapper = QuarantineWrapper.Wrap(InstancePayloadReason.SocketNesting, 3, new byte[] { 0x02, 0x01, 0x44 });
        Assert.True(QuarantineWrapper.Verify(wrapper));

        byte[] ahead = (byte[])wrapper.Clone();
        ahead[4] = 2;
        Assert.False(QuarantineWrapper.Verify(ahead));
        Assert.False(QuarantineWrapper.TryUnwrap(ahead, out _, out _, out _));

        byte[] zero = (byte[])wrapper.Clone();
        zero[4] = 0;
        Assert.False(QuarantineWrapper.Verify(zero));

        // The version is a uint16 LE, so the high byte is part of it and a wrapper claiming 256 is refused
        // rather than read as 0.
        byte[] high = (byte[])wrapper.Clone();
        high[5] = 1;
        Assert.False(QuarantineWrapper.Verify(high));
    }

    [Theory]
    [MemberData(nameof(Ordinals))]
    public void The_reason_code_ordinal_round_trips_to_the_same_closed_token(byte ordinal, string reason)
    {
        byte[] wrapper = QuarantineWrapper.Wrap(reason, 11, new byte[] { 0x02, 0x01, 0x44 });

        // The ordinal is at a FIXED offset, past the four magic bytes and the uint16 version, which is what
        // a tool reading an old page has to rely on.
        Assert.Equal(ordinal, wrapper[6]);
        Assert.True(QuarantineWrapper.TryUnwrap(wrapper, out _, out string? read, out _));
        Assert.Equal(reason, read);

        Assert.True(InstanceQuarantineReason.TryGetOrdinal(reason, out byte looked));
        Assert.Equal(ordinal, looked);
        Assert.True(InstanceQuarantineReason.TryGetReason(ordinal, out string? back));
        Assert.Equal(reason, back);
    }

    [Fact]
    public void Ordinal_0_is_reserved_and_an_unassigned_ordinal_is_refused()
    {
        // A zeroed byte is never a valid reason, so a half written wrapper is detectable. An ordinal past
        // the table is a wrapper written by a LATER engine, and guessing at it would put the wrong meaning
        // on a durable byte.
        byte[] wrapper = QuarantineWrapper.Wrap(InstancePayloadReason.FieldMalformed, 2, new byte[] { 0x02, 0x01, 0x44 });

        byte[] reserved = (byte[])wrapper.Clone();
        reserved[6] = 0;
        Assert.False(QuarantineWrapper.Verify(reserved));

        byte[] unassigned = (byte[])wrapper.Clone();
        unassigned[6] = (byte)(InstanceQuarantineReason.All.Count + 1);
        Assert.False(QuarantineWrapper.Verify(unassigned));

        Assert.False(InstanceQuarantineReason.TryGetReason(0, out _));
        Assert.False(InstanceQuarantineReason.TryGetOrdinal("no-such-reason", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuarantineWrapper.Wrap("no-such-reason", 1, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void A_wrapper_whose_declared_OriginalLength_lies_is_refused()
    {
        byte[] original = new byte[] { 0x02, 0x01, 0x44, 0x05 };
        byte[] wrapper = QuarantineWrapper.Wrap(InstancePayloadReason.KindDuplicate, 6, original);

        // The length is the last varint before the original, and 4 fits in one byte, so the lie is one byte
        // either way: more bytes than are present, and fewer.
        int lengthAt = wrapper.Length - original.Length - 1;
        Assert.Equal(original.Length, wrapper[lengthAt]);

        byte[] tooLong = (byte[])wrapper.Clone();
        tooLong[lengthAt] = (byte)(original.Length + 1);
        Assert.False(QuarantineWrapper.Verify(tooLong));

        byte[] tooShort = (byte[])wrapper.Clone();
        tooShort[lengthAt] = (byte)(original.Length - 1);
        Assert.False(QuarantineWrapper.Verify(tooShort));

        // Trailing bytes are the same failure from the other side: the declared length has to account for
        // every byte present, or a reader cannot tell the original from whatever followed it.
        Assert.False(QuarantineWrapper.Verify(wrapper.Concat(new byte[] { 0x00 }).ToArray()));
        Assert.False(QuarantineWrapper.Verify(wrapper.AsSpan(0, wrapper.Length - 1)));
    }

    [Fact]
    public void A_wrapper_whose_header_is_cut_short_is_refused_at_every_length()
    {
        // Total means total: every prefix of a legal wrapper is answered false rather than read past.
        byte[] wrapper = QuarantineWrapper.Wrap(InstancePayloadReason.VarintOverflow, 300, new byte[] { 0x02, 0x01, 0x44 });
        for (int length = 0; length < wrapper.Length; length++)
        {
            Assert.False(QuarantineWrapper.Verify(wrapper.AsSpan(0, length)));
        }

        Assert.True(QuarantineWrapper.Verify(wrapper));
    }

    [Fact]
    public void The_stamped_version_survives_at_every_varint_width()
    {
        // The stamp is the page version the record failed under (contracts 7.2), so it is a number a live
        // shard reaches rather than a small constant: it is written as an unsigned varint and read back as
        // the same int at every width.
        foreach (int stamped in new[] { 0, 1, 127, 128, 16_383, 16_384, int.MaxValue })
        {
            byte[] wrapper = QuarantineWrapper.Wrap(InstancePayloadReason.FieldTruncated, stamped, new byte[] { 0x02 });
            Assert.True(QuarantineWrapper.TryUnwrap(wrapper, out _, out _, out int read));
            Assert.Equal(stamped, read);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuarantineWrapper.Wrap(InstancePayloadReason.FieldTruncated, -1, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void The_size_a_wrap_will_take_is_answerable_before_writing_it()
    {
        // The page codec sizes its entry before it writes one, so the arithmetic is public rather than
        // implied by the array Wrap happens to return.
        byte[] original = new byte[200];
        int size = QuarantineWrapper.Size(InstancePayloadReason.PayloadTooLong, 300, original.Length);
        byte[] wrapper = QuarantineWrapper.Wrap(InstancePayloadReason.PayloadTooLong, 300, original);

        Assert.Equal(size, wrapper.Length);

        // Four magic, two version, one reason, a two byte stamp varint, a two byte length varint.
        Assert.Equal(4 + 2 + 1 + 2 + 2 + original.Length, size);

        var destination = new byte[size + 8];
        Assert.Equal(size, QuarantineWrapper.Wrap(InstancePayloadReason.PayloadTooLong, 300, original, destination));
        Assert.Equal(wrapper, destination[..size]);
        Assert.Throws<ArgumentException>(() =>
            QuarantineWrapper.Wrap(InstancePayloadReason.PayloadTooLong, 300, original, new byte[size - 1]));
    }

    [Fact]
    public void The_first_eight_ordinals_are_the_codecs_closed_set_in_its_own_order()
    {
        // Spec 12.4 numbers 1 to 8 as contracts 9.7's eight tokens in the order 9.7 lists them, which is
        // exactly InstancePayloadReason.All. Deriving them rather than re-typing them is what stops a ninth
        // reason from renumbering a durable byte.
        Assert.Equal(13, InstanceQuarantineReason.All.Count);
        Assert.Equal(InstancePayloadReason.All, InstanceQuarantineReason.All.Take(8).ToArray());

        foreach (string reason in InstancePayloadReason.All)
        {
            Assert.True(InstanceQuarantineReason.TryGetOrdinal(reason, out _));
        }

        // The five beyond the codec's eight are the validator's, and task 9 raises them. They are numbered
        // here because the ordinal is durable and the type that first WRITES one is this one.
        Assert.Equal(
            new[]
            {
                InstanceQuarantineReason.UnknownDefinition,
                InstanceQuarantineReason.UnknownContentReference,
                InstanceQuarantineReason.InstanceIdMissing,
                InstanceQuarantineReason.InstanceIdDuplicate,
                InstanceQuarantineReason.StackNotInstanceable,
            },
            InstanceQuarantineReason.All.Skip(8).ToArray());
    }

    [Fact]
    public void A_wrapped_payload_is_never_confused_for_a_payload_by_the_decoder()
    {
        // Spec 4.4's argument for the entry flag, checked: 'K' is 0x4B, which is a perfectly legal property
        // kind varint, so a wrapper can decode as a payload and the sniff is ambiguous in that direction
        // too. The flag is what tells them apart, and Verify is what the door checks once it is set.
        byte[] wrapper = QuarantineWrapper.Wrap(
            InstancePayloadReason.KindOutOfOrder,
            2,
            new byte[] { 0x02, 0x01, 0x44, 0x05, 0x02, 0x5A, 0x64 });

        Assert.Equal((byte)'K', wrapper[0]);
        Assert.True(QuarantineWrapper.Verify(wrapper));

        // The bare payload IS a valid payload and is NOT a wrapper, which is the direction Verify decides.
        // The other direction is the entry flag's job rather than a sniff.
        byte[] bare = { 0x02, 0x01, 0x44, 0x05, 0x02, 0x5A, 0x64 };
        Assert.Null(ItemInstancePayload.Validate(bare));
        Assert.False(QuarantineWrapper.Verify(bare));
    }

    [Fact]
    public void The_wrapper_writes_the_varints_the_one_varint_definition_writes()
    {
        // Contracts 15 wants ONE varint definition in the tree, so the wrapper writes ContentVarint's bytes
        // rather than a second LEB128 that agrees with it today.
        const int stamped = 300;
        byte[] original = new byte[] { 0x02, 0x01, 0x44 };
        byte[] wrapper = QuarantineWrapper.Wrap(InstancePayloadReason.FieldMalformed, stamped, original);

        Span<byte> expected = stackalloc byte[10];
        int written = ContentVarint.Write(expected, stamped);
        Assert.Equal(expected[..written].ToArray(), wrapper.AsSpan(7, written).ToArray());

        int offset = 7;
        Assert.True(ContentVarint.TryRead(wrapper, ref offset, out uint readStamp, out _));
        Assert.Equal((uint)stamped, readStamp);
        Assert.True(ContentVarint.TryRead(wrapper, ref offset, out uint readLength, out _));
        Assert.Equal((uint)original.Length, readLength);
        Assert.Equal(original, wrapper.AsSpan(offset).ToArray());
    }

    [Fact]
    public void A_non_minimal_varint_in_the_header_is_refused()
    {
        // The varint rules are the payload's rules, because they are ContentVarint's: a non minimal stamp
        // would give one wrapper two byte forms, and a page digest rests on there being one.
        byte[] original = new byte[] { 0x02, 0x01, 0x44 };
        var handWritten = new List<byte> { 0x4B, 0x45, 0x43, 0x51, 0x01, 0x00, 0x08, 0x81, 0x00, 0x03 };
        handWritten.AddRange(original);

        Assert.False(QuarantineWrapper.Verify(handWritten.ToArray()));
    }
}
