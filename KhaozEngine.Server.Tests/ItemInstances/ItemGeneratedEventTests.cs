using System;
using System.Collections.Generic;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using Xunit;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>
/// Spec 9.5's <c>item-generated</c> body, its layout byte for byte and its reader.
/// <para>
/// <b>The reader is half the point.</b> The event is the only durable record of what an item looked like
/// when it dropped, because the page it sits on is rewritten whole on every later commit, so a body nothing
/// can read back is not a record at all. It answers false plus a reason rather than throwing, because the
/// bytes come from a store.
/// </para>
/// <para>
/// Nothing here writes process-global state, so no collection attribute is needed.
/// </para>
/// </summary>
public class ItemGeneratedEventTests
{
    /// <summary>A three byte payload, kind 2 at item level 68, which is the worked example's first field.</summary>
    static readonly byte[] SmallPayload = [0x02, 0x01, 0x44];

    [Fact]
    public void The_body_is_spec_9_5s_layout_BYTE_FOR_BYTE()
    {
        var generated = new ItemGeneratedEvent(
            BaseId: 40,
            InstanceId: 4_201,
            RarityId: 3,
            ContentVersion: 7,
            SourceKind: ItemGeneratedEvent.SourceDrop,
            SourceId: 12,
            Payload: SmallPayload);

        Assert.Equal(
            new byte[]
            {
                0x01,                   // event version 1
                0x28,                   // base id 40
                0xE9, 0x20,             // instance id 4201
                0x03,                   // rarity id 3
                0x07,                   // content version 7
                0x01,                   // source kind 1, a drop
                0x0C,                   // source id 12
                0x03,                   // payload length 3
                0x02, 0x01, 0x44,       // the payload, verbatim
            },
            generated.ToArray());
        Assert.Equal(12, generated.ByteCount);
    }

    [Fact]
    public void A_round_trip_returns_every_field_and_the_payload_VERBATIM()
    {
        var generated = new ItemGeneratedEvent(40, 4_201, 3, 7, ItemGeneratedEvent.SourceDrop, 12, SmallPayload);

        Assert.True(ItemGeneratedEvent.TryRead(generated.ToArray(), out ItemGeneratedEvent read, out string? reason), reason);
        Assert.Null(reason);
        Assert.Equal(40, read.BaseId);
        Assert.Equal(4_201L, read.InstanceId);
        Assert.Equal((byte)3, read.RarityId);
        Assert.Equal(7, read.ContentVersion);
        Assert.Equal(ItemGeneratedEvent.SourceDrop, read.SourceKind);
        Assert.Equal(12, read.SourceId);
        Assert.Equal(SmallPayload, read.Payload.ToArray());
    }

    [Fact]
    public void A_plain_stack_with_an_EMPTY_payload_round_trips_at_instance_id_0()
    {
        // An item whose payload is empty takes id 0 and is a plain stack (spec 3.6), and an event about one
        // is still a record: the length varint is 0 and the body ends there.
        var generated = new ItemGeneratedEvent(41, 0, 0, 7, ItemGeneratedEvent.SourceAdminGrant, 0, default);

        Assert.True(ItemGeneratedEvent.TryRead(generated.ToArray(), out ItemGeneratedEvent read, out string? reason), reason);
        Assert.Equal(0L, read.InstanceId);
        Assert.True(read.Payload.IsEmpty);
        Assert.Equal(new byte[] { 0x01, 0x29, 0x00, 0x00, 0x07, 0x03, 0x00, 0x00 }, generated.ToArray());
    }

    [Fact]
    public void An_instance_id_on_the_TOP_node_is_written_unsigned_rather_than_zig_zagged()
    {
        // Contracts 6.2 packs (node << 48) | counter, so node 65535 sets the high bit and is a NEGATIVE
        // long. Unsigned costs the high node and buys node 0, and the two encodings are different bytes, so
        // the format has to say which it means.
        long packed = InstanceIdAllocator.Pack(ushort.MaxValue, 1);
        var generated = new ItemGeneratedEvent(40, packed, 3, 7, ItemGeneratedEvent.SourceDrop, 0, SmallPayload);

        Assert.True(packed < 0);
        Assert.True(ItemGeneratedEvent.TryRead(generated.ToArray(), out ItemGeneratedEvent read, out string? reason), reason);
        Assert.Equal(packed, read.InstanceId);
        Assert.Equal((ushort)ushort.MaxValue, InstanceIdAllocator.NodeOf(read.InstanceId));
    }

    [Fact]
    public void EVERY_prefix_of_a_real_body_is_refused_with_a_reason_rather_than_throwing()
    {
        byte[] body = new ItemGeneratedEvent(40, 4_201, 3, 7, ItemGeneratedEvent.SourceDrop, 12, SmallPayload).ToArray();

        for (int length = 0; length < body.Length; length++)
        {
            Assert.False(
                ItemGeneratedEvent.TryRead(body.AsMemory(0, length), out ItemGeneratedEvent read, out string? reason),
                FormattableString.Invariant($"A {length} byte prefix was accepted."));
            Assert.NotNull(reason);
            Assert.Equal(default, read);
        }
    }

    [Fact]
    public void A_body_version_this_build_does_not_know_is_refused_rather_than_guessed()
    {
        byte[] body = new ItemGeneratedEvent(40, 4_201, 3, 7, ItemGeneratedEvent.SourceDrop, 12, SmallPayload).ToArray();
        body[0] = 2;

        Assert.False(ItemGeneratedEvent.TryRead(body, out _, out string? reason));
        Assert.Equal(ItemGeneratedEvent.UnknownVersion, reason);
    }

    [Fact]
    public void A_declared_payload_length_that_does_not_match_what_is_LEFT_is_refused()
    {
        byte[] body = new ItemGeneratedEvent(40, 4_201, 3, 7, ItemGeneratedEvent.SourceDrop, 12, SmallPayload).ToArray();

        // A payload length that runs PAST the body, and one that leaves trailing bytes behind it. Both are a
        // writer this build cannot read, and neither may be papered over by reading what fits.
        byte[] tooLong = [.. body];
        tooLong[8] = 0x7F;
        Assert.False(ItemGeneratedEvent.TryRead(tooLong, out _, out string? reason));
        Assert.Equal(ItemGeneratedEvent.PayloadLength, reason);

        byte[] tooShort = [.. body];
        tooShort[8] = 0x01;
        Assert.False(ItemGeneratedEvent.TryRead(tooShort, out _, out reason));
        Assert.Equal(ItemGeneratedEvent.PayloadLength, reason);
    }

    [Fact]
    public void A_source_kind_of_0_is_refused_on_BOTH_sides_because_it_names_nothing()
    {
        byte[] body = new ItemGeneratedEvent(40, 4_201, 3, 7, ItemGeneratedEvent.SourceDrop, 12, SmallPayload).ToArray();
        body[6] = 0;

        Assert.False(ItemGeneratedEvent.TryRead(body, out _, out string? reason));
        Assert.Equal(ItemGeneratedEvent.FieldRange, reason);

        var result = new GenerationResult(40, 4_201, SmallPayload, 3, 7, 0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemGeneratedEvent.From(result, 0));
    }

    [Fact]
    public void A_truncated_VARINT_is_a_reason_rather_than_a_read_off_the_end()
    {
        // A body whose final varint claims a continuation byte that is not there. The prefix sweep cannot
        // produce this one, because every prefix of a real body cuts on a whole field.
        byte[] body = [0x01, 0xE9, 0x20, 0x80];

        Assert.False(ItemGeneratedEvent.TryRead(body, out _, out string? reason));
        Assert.Equal(ItemGeneratedEvent.MalformedVarint, reason);
    }

    [Fact]
    public void It_is_built_from_a_GenerationResult_and_carries_the_payload_by_REFERENCE()
    {
        var payload = new byte[] { 0x02, 0x01, 0x44 };
        var result = new GenerationResult(
            BaseId: 40,
            InstanceId: 4_201,
            Payload: payload,
            RarityId: 3,
            ContentVersion: 7,
            AffixCount: 4,
            RequestedAffixCount: 6);

        ItemGeneratedEvent generated = ItemGeneratedEvent.From(result, ItemGeneratedEvent.SourceDrop, sourceId: 12);

        Assert.Equal(40, generated.BaseId);
        Assert.Equal(4_201L, generated.InstanceId);
        Assert.Equal((byte)3, generated.RarityId);
        Assert.Equal(7, generated.ContentVersion);
        Assert.Equal(12, generated.SourceId);
        Assert.True(generated.Payload.Span.Overlaps(payload.AsSpan()));
    }

    [Fact]
    public void The_event_records_the_RESOLVED_item_and_carries_no_seed_state_or_draw_index()
    {
        // Contracts 14.3 read as a test. A replay returns the ORIGINAL receipt rather than re-running
        // anything, so an event carrying a seed would have to be re-rolled to mean anything.
        var names = new List<string>();
        foreach (System.Reflection.PropertyInfo property in typeof(ItemGeneratedEvent).GetProperties())
        {
            names.Add(property.Name.ToUpperInvariant());
        }

        Assert.DoesNotContain("SEED", names);
        Assert.DoesNotContain("STATE", names);
        Assert.DoesNotContain("DRAWINDEX", names);
        Assert.DoesNotContain("RANDOM", names);

        // The affix count and the requested count are the GENERATOR's telemetry and are deliberately not
        // durable: the payload says what the item is, and a count beside it would be a second answer.
        Assert.DoesNotContain("AFFIXCOUNT", names);
        Assert.DoesNotContain("REQUESTEDAFFIXCOUNT", names);
    }

    [Fact]
    public void A_rare_of_spec_3_8_is_about_72_bytes_on_the_wire()
    {
        // Spec 9.5 counts 58 payload bytes plus fourteen of header for the rare of 3.8. The header is what
        // this type owns, and against the 128 events per operation cap a drop burst of twenty rares is about
        // 1.4 KB of events, which is inside the one tick batch budget.
        var payload = new byte[58];
        var generated = new ItemGeneratedEvent(40, 4_201, 3, 7, ItemGeneratedEvent.SourceDrop, 12, payload);

        Assert.Equal(58 + 9, generated.ByteCount);

        // The fourteen byte header of 9.5's arithmetic is its WIDEST case, a five byte instance id and a two
        // byte content version and source id. This event's ids are smaller, so it is smaller.
        var widest = new ItemGeneratedEvent(40, 1L << 34, 3, 300, ItemGeneratedEvent.SourceDrop, 300, payload);
        Assert.Equal(58 + 14, widest.ByteCount);
    }
}
