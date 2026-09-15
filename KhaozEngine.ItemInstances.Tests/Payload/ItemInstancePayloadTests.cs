using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Payload;

/// <summary>
/// Contracts 9.3's three canonical rules and contracts 9.7's eight refusals, each as a fact. These are
/// spec 12.2's checks 1 to 5 arriving at the DOOR: the codec refuses a malformed payload outright, and task
/// 9's validator calls the same surface rather than writing a second copy of them.
/// <para>
/// The decoder never throws for a byte, so every fact here asserts a false plus a token rather than an
/// exception. Every fact builds its own registry, so nothing writes process-global state.
/// </para>
/// </summary>
public class ItemInstancePayloadTests
{
    [Fact]
    public void Fields_out_of_ascending_order_answer_kind_out_of_order()
    {
        // Durability then item level, which is 5 then 2. Rule 9.3.1 wants strictly ascending kinds.
        Assert.Equal(
            InstancePayloadReason.KindOutOfOrder,
            Refuse(0x05, 0x02, 0x5A, 0x64, 0x02, 0x01, 0x44));
    }

    [Fact]
    public void A_duplicate_kind_answers_kind_duplicate()
    {
        // Two item level fields. Rule 9.3.2 wants no kind twice, and a duplicate is told apart from plain
        // disorder so a counter can say which happened.
        Assert.Equal(
            InstancePayloadReason.KindDuplicate,
            Refuse(0x02, 0x01, 0x44, 0x02, 0x01, 0x44));
    }

    [Fact]
    public void A_non_minimal_varint_answers_varint_not_minimal()
    {
        // 0x81 0x00 is not a legal encoding of 1, which is rule 9.3.3 and the reason byte equality is
        // property equality at all.
        Assert.Equal(
            InstancePayloadReason.VarintNotMinimal,
            Refuse(0x81, 0x00, 0x01, 0x44));
    }

    [Fact]
    public void A_varint_that_does_not_terminate_answers_varint_overflow()
    {
        // A kind is a uint16, so its varint cannot carry value bits above the fifth byte.
        Assert.Equal(
            InstancePayloadReason.VarintOverflow,
            Refuse(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01));
    }

    [Fact]
    public void A_declared_length_past_the_end_answers_field_truncated()
    {
        // Kind 2 declares five body bytes and one follows. Contracts 9.7 refuses the WHOLE payload rather
        // than keeping the fields read so far, because a decoder that stopped here would silently strip
        // affixes off an item.
        Assert.Equal(
            InstancePayloadReason.FieldTruncated,
            Refuse(0x02, 0x05, 0x44));
    }

    [Fact]
    public void A_payload_above_the_cap_answers_payload_too_long()
    {
        byte[] overCap = new byte[ItemInstancePayload.MaxInstancePayloadBytes + 1];

        Assert.Equal(InstancePayloadReason.PayloadTooLong, Refuse(overCap));

        // The cap is contracts 9.6's 512 and it is the same number the slot carries, not a second copy.
        Assert.Equal(512, ItemInstancePayload.MaxInstancePayloadBytes);
        Assert.Equal(ItemSlot.MaxPayloadBytes, ItemInstancePayload.MaxInstancePayloadBytes);
    }

    [Fact]
    public void A_nested_payload_carrying_kind_132_answers_socket_nesting()
    {
        // One socket whose nested payload is itself a socket field. Contracts 9.5 makes the one level limit
        // STRUCTURAL, so the decoder refuses rather than recursing, which is what stops a 45 byte payload
        // becoming a denial of service.
        byte[] nested = { 0x84, 0x01, 0x01, 0x00 };
        byte[] payload = Field(
            InstancePropertyKind.Sockets,
            Concat(
                new byte[] { 0x01, 0x07, 0xC1, 0x06, 0x00, (byte)nested.Length },
                nested));

        Assert.Equal(InstancePayloadReason.SocketNesting, Refuse(payload));
    }

    [Fact]
    public void A_known_kinds_wrong_shape_answers_field_malformed()
    {
        // Kind 2's shape is one scalar and this body carries two, so the walk finishes with bytes left.
        Assert.Equal(
            InstancePayloadReason.FieldMalformed,
            Refuse(0x02, 0x02, 0x44, 0x44));
    }

    [Fact]
    public void A_non_zero_affix_flags_varint_answers_field_malformed()
    {
        // Contracts 9.9 reserves the flags varint and requires 0 in v1. A decoder meeting anything else
        // reports field-malformed rather than guessing what the bit meant.
        byte[] payload = Field(
            InstancePropertyKind.Affixes,
            new byte[] { 0x01, 0x5B, 0x01, 0x33, 0x33, 0x01 });

        Assert.Equal(InstancePayloadReason.FieldMalformed, Refuse(payload));
    }

    [Fact]
    public void Affix_entries_not_ascending_by_mod_id_answer_field_malformed()
    {
        // Mod 260 then mod 91. Contracts 9.9 orders the LIST, because rule 9.3.1 orders fields and says
        // nothing about entries inside one, so without it the same three affixes encode six ways.
        byte[] payload = Field(
            InstancePropertyKind.Affixes,
            new byte[] { 0x02, 0x84, 0x02, 0x02, 0xFF, 0xFF, 0x00, 0x5B, 0x01, 0x33, 0x33, 0x00 });

        Assert.Equal(InstancePayloadReason.FieldMalformed, Refuse(payload));
    }

    [Fact]
    public void A_mod_id_twice_in_one_affix_list_answers_field_malformed()
    {
        // Contracts 9.9 also says a mod id appears at most ONCE, so equal is refused as well as descending.
        byte[] payload = Field(
            InstancePropertyKind.Affixes,
            new byte[] { 0x02, 0x5B, 0x01, 0x33, 0x33, 0x00, 0x5B, 0x02, 0x33, 0x33, 0x00 });

        Assert.Equal(InstancePayloadReason.FieldMalformed, Refuse(payload));
    }

    [Fact]
    public void Kind_zero_is_never_a_valid_field()
    {
        // Contracts 9.2 reserves 0 and never assigns it, so a payload naming it is malformed rather than
        // an unknown kind kept verbatim.
        Assert.Equal(InstancePayloadReason.FieldMalformed, Refuse(0x00, 0x00));
    }

    [Fact]
    public void An_identification_state_above_one_answers_field_malformed()
    {
        // Kind 128's state byte is 0 unidentified or 1 identified, and nothing else (spec 3.3).
        byte[] payload = Field(InstancePropertyKind.Identification, new byte[] { 0x02, 0x00 });

        Assert.Equal(InstancePayloadReason.FieldMalformed, Refuse(payload));
    }

    [Fact]
    public void An_empty_socket_carrying_a_nested_payload_answers_field_malformed()
    {
        // Contracts 9.5: a contained definition id of 0 MEANS the socket is empty, so an empty socket
        // carrying an instance id or nested bytes is a contradiction rather than a shape error.
        byte[] payload = Field(
            InstancePropertyKind.Sockets,
            new byte[] { 0x01, 0x07, 0x00, 0x00, 0x03, 0x02, 0x01, 0x37 });

        Assert.Equal(InstancePayloadReason.FieldMalformed, Refuse(payload));
    }

    [Fact]
    public void An_empty_payload_is_zero_bytes_and_decodes_to_no_fields()
    {
        // Contracts 9.1: an EMPTY payload is zero bytes, which is what a plain stack has.
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];

        Assert.True(ItemInstancePayload.TryDecode(registry, default, fields, out int count, out string? reason));
        Assert.Equal(0, count);
        Assert.Null(reason);
        Assert.Null(ItemInstancePayload.Validate(registry, default));
        Assert.True(ItemInstancePayload.IsCanonical(registry, default));
        Assert.Equal(0, new ItemInstancePayloadBuilder().Length);
    }

    [Fact]
    public void An_unknown_kind_is_kept_verbatim_and_re_emitted_unchanged()
    {
        // Contracts 9.4. A game kind at 2000 the engine has never heard of rides through a decode and a
        // re-encode unchanged, and its bytes participate in byte equality.
        byte[] opaque = { 0xDE, 0xAD, 0xBE, 0xEF };
        byte[] payload = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .Add(2000, opaque)
            .ToArray();

        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        Assert.True(ItemInstancePayload.TryDecode(registry, payload, fields, out int count, out _));
        Assert.Equal(2, count);
        Assert.Equal(2000, fields[1].Kind);
        Assert.Equal(opaque, payload.AsSpan(fields[1].BodyStart, fields[1].BodyLength).ToArray());

        var rebuilt = new ItemInstancePayloadBuilder();
        for (int index = 0; index < count; index++)
        {
            rebuilt.Add(fields[index].Kind, payload.AsSpan(fields[index].BodyStart, fields[index].BodyLength));
        }

        Assert.Equal(payload, rebuilt.ToArray());
    }

    [Fact]
    public void The_encoder_never_produces_a_payload_the_decoder_refuses()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();

        foreach (ItemInstancePayloadBuilder builder in EveryShape())
        {
            byte[] payload = builder.ToArray();

            Assert.Null(ItemInstancePayload.Validate(registry, payload));
            Assert.True(ItemInstancePayload.IsCanonical(registry, payload));
            Assert.True(ItemInstancePayload.IsCanonical(payload));
            Assert.Equal(builder.Length, payload.Length);
        }
    }

    [Fact]
    public void Fields_added_out_of_order_are_encoded_ascending()
    {
        // The ENCODER is what makes the payload canonical, so the order of the Add calls does not reach
        // the bytes. Two callers building the same item agree, which is what spec 4.6's stack check needs.
        byte[] ascending = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .AddByte(InstancePropertyKind.Rarity, 3)
            .ToArray();

        byte[] descending = new ItemInstancePayloadBuilder()
            .AddByte(InstancePropertyKind.Rarity, 3)
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .ToArray();

        Assert.Equal(ascending, descending);
        Assert.Equal(new byte[] { 0x02, 0x01, 0x44, 0x82, 0x01, 0x01, 0x03 }, ascending);
    }

    [Fact]
    public void The_builder_refuses_the_same_kind_twice()
    {
        var builder = new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.ItemLevel, 68);

        // A duplicate kind is a programming error in the CALLER rather than a byte from a peer, so it
        // throws. Nothing about the BYTES ever throws.
        Assert.Throws<ArgumentException>(() => builder.AddScalar(InstancePropertyKind.ItemLevel, 70));
    }

    [Fact]
    public void The_builder_refuses_a_duplicate_mod_id_in_one_affix_list()
    {
        var builder = new ItemInstancePayloadBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddAffixes(
            InstancePropertyKind.Affixes,
            new[] { new InstanceAffix(91, 1, 13107), new InstanceAffix(91, 2, 100) }));
    }

    [Fact]
    public void The_encoder_refuses_a_payload_over_the_cap()
    {
        var builder = new ItemInstancePayloadBuilder();
        for (ushort kind = 1024; builder.Length <= ItemInstancePayload.MaxInstancePayloadBytes; kind++)
        {
            builder.Add(kind, new byte[32]);
        }

        // Spec 4.7 makes an over-cap payload a THROW at the door rather than a refusal token, because it
        // is an item that must not be written rather than bytes that were read.
        Assert.Throws<InvalidOperationException>(() => builder.ToArray());
    }

    [Fact]
    public void Sockets_keep_their_authored_order_and_two_orderings_do_not_stack()
    {
        // Spec 3.5: socket order is AUTHORED and never sorted, unlike the affix list. The consequence is
        // that two otherwise identical items whose gems sit in different sockets do not stack, which is
        // correct, because they are different items.
        var first = new InstanceSocket(7, 833, 4201, default);
        var second = new InstanceSocket(7, 834, 4202, default);

        byte[] authored = new ItemInstancePayloadBuilder().AddSockets(new[] { first, second }).ToArray();
        byte[] swapped = new ItemInstancePayloadBuilder().AddSockets(new[] { second, first }).ToArray();

        Assert.NotEqual(authored, swapped);
        Assert.False(ItemInstancePayload.SequenceEqual(authored, swapped));

        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        Assert.Null(ItemInstancePayload.Validate(registry, authored));
        Assert.Null(ItemInstancePayload.Validate(registry, swapped));
    }

    [Fact]
    public void The_builder_refuses_a_socket_whose_nested_payload_is_not_structurally_canonical()
    {
        // Kind 5 then kind 2 is DESCENDING, so the nested bytes break rule 9.3.1. The builder documented
        // this refusal and did not make it, which let a writer produce a socket carrying bytes the decoder
        // at the far end refuses. The check is the structural one of contracts 9.3, because the builder
        // holds no registry.
        byte[] descending = [0x05, 0x02, 0x5A, 0x64, 0x02, 0x01, 0x44];
        Assert.False(ItemInstancePayload.IsCanonical(descending));
        Assert.Throws<ArgumentException>(() => new ItemInstancePayloadBuilder()
            .AddSockets(new[] { new InstanceSocket(7, 833, 4201, descending) }));

        // The same socket carrying the same two fields ASCENDING still builds, and what it builds is
        // canonical under the full registry check as well.
        byte[] ascending = [0x02, 0x01, 0x44, 0x05, 0x02, 0x5A, 0x64];
        Assert.True(ItemInstancePayload.IsCanonical(ascending));
        byte[] built = new ItemInstancePayloadBuilder()
            .AddSockets(new[] { new InstanceSocket(7, 833, 4201, ascending) })
            .ToArray();
        Assert.Null(ItemInstancePayload.Validate(InstancePropertyRegistry.CreateV1(), built));
    }

    [Fact]
    public void Byte_equality_is_property_equality()
    {
        byte[] left = new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.ItemLevel, 68).ToArray();
        byte[] right = new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.ItemLevel, 68).ToArray();
        byte[] other = new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.ItemLevel, 69).ToArray();

        Assert.True(ItemInstancePayload.SequenceEqual(left, right));
        Assert.False(ItemInstancePayload.SequenceEqual(left, other));
        Assert.True(ItemInstancePayload.SequenceEqual(default, default));
    }

    [Fact]
    public void PublicView_is_the_whole_payload_until_the_visibility_task_finishes_it()
    {
        // Step 8 of task 4 ships this as a stub and the phase 2-3 plan's visibility task finishes it, which
        // is where the rule of spec 12.5 belongs. The fact exists so the stub is DELIBERATE rather than
        // forgotten, and so the day it stops returning everything is a red test rather than a surprise.
        byte[] payload = new ItemInstancePayloadBuilder()
            .AddScalars(InstancePropertyKind.Durability, 90, 100)
            .ToArray();

        byte[] view = new byte[payload.Length];
        int written = ItemInstancePayload.PublicView(
            payload,
            PropertyVisibility.Everyone,
            identified: false,
            revealedMask: 0,
            view);

        Assert.Equal(payload.Length, written);
        Assert.Equal(payload, view);
    }

    [Fact]
    public void The_reason_set_is_exactly_the_eight_tokens_of_contracts_9_7()
    {
        string[] expected =
        {
            "payload-too-long",
            "field-truncated",
            "kind-out-of-order",
            "kind-duplicate",
            "varint-not-minimal",
            "varint-overflow",
            "socket-nesting",
            "field-malformed",
        };

        // A counter is keyed on these (spec 12.6) and spec 12.4 assigns each a DURABLE ordinal, so a ninth
        // is a deliberate additive act. Reflection is the fence: a drive-by constant goes red here.
        string[] declared = typeof(InstancePayloadReason)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.Equal(expected.OrderBy(t => t, StringComparer.Ordinal), declared.OrderBy(t => t, StringComparer.Ordinal));
        Assert.Equal(expected, InstancePayloadReason.All);
        Assert.Equal(8, InstancePayloadReason.All.Count);
    }

    [Fact]
    public void The_varint_readers_reasons_are_the_payloads_own_tokens()
    {
        // One varint implementation in the tree (contracts 15), so its three refusals ARE three of the
        // eight tokens rather than a parallel vocabulary that has to be translated.
        Assert.Equal(InstancePayloadReason.VarintNotMinimal, ContentVarint.ReasonNotMinimal);
        Assert.Equal(InstancePayloadReason.VarintOverflow, ContentVarint.ReasonOverflow);
        Assert.Equal(InstancePayloadReason.FieldTruncated, ContentVarint.ReasonTruncated);
    }

    /// <summary>One builder per field shape the v1 kinds can take, which is what the encoder fact sweeps.</summary>
    static IEnumerable<ItemInstancePayloadBuilder> EveryShape()
    {
        yield return new ItemInstancePayloadBuilder();
        yield return new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.ItemLevel, 65535);
        yield return new ItemInstancePayloadBuilder().AddScalars(InstancePropertyKind.Charges, 0, 4294967295);
        yield return new ItemInstancePayloadBuilder().AddByte(InstancePropertyKind.Rarity, 255);
        yield return new ItemInstancePayloadBuilder().AddIdentification(identified: false, revealedMask: 0xFFFFFFFF);
        yield return new ItemInstancePayloadBuilder().AddMaterials(
            new[] { new InstanceMaterial(1, 1), new InstanceMaterial(2147483647, 65535) });
        yield return new ItemInstancePayloadBuilder().AddAffixes(
            InstancePropertyKind.Enchantments,
            new[] { new InstanceAffix(4210, 255, 0), new InstanceAffix(91, 1, 65535) });
        yield return new ItemInstancePayloadBuilder().AddSockets(
            new[]
            {
                new InstanceSocket(0, 0, 0, default),
                new InstanceSocket(7, 833, ulong.MaxValue, new byte[] { 0x02, 0x01, 0x37 }),
            });
        yield return new ItemInstancePayloadBuilder().AddRareName(7, new[] { 17, 34, 51 });
        yield return new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.Flags, 7)
            .Add(65535, new byte[] { 0x01, 0x02 });
    }

    /// <summary>Decodes with the v1 registry and returns the reason, asserting the answer was false.</summary>
    static string? Refuse(params byte[] payload)
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];

        Assert.False(ItemInstancePayload.TryDecode(registry, payload, fields, out _, out string? reason));
        Assert.NotNull(reason);

        // Validate answers the same token through the same implementation, which is what lets task 9's
        // validator call this surface for checks 1 to 5 rather than writing a second copy of them.
        Assert.Equal(reason, ItemInstancePayload.Validate(registry, payload));
        Assert.False(ItemInstancePayload.IsCanonical(registry, payload));
        return reason;
    }

    static byte[] Field(ushort kind, byte[] body)
    {
        var builder = new ItemInstancePayloadBuilder();
        builder.Add(kind, body);
        return builder.ToArray();
    }

    static byte[] Concat(byte[] first, byte[] second)
    {
        byte[] joined = new byte[first.Length + second.Length];
        first.CopyTo(joined, 0);
        second.CopyTo(joined, first.Length);
        return joined;
    }
}
