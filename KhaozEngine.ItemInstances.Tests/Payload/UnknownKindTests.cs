using System;
using System.IO;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Payload;

/// <summary>
/// Spec 17 row 2, which is two facts and the second is the one that matters. A decoder whose registry
/// OMITS a kind the encoder wrote reproduces the input byte for byte (contracts 9.4), and two items that
/// differ only in that omitted kind still do NOT merge (spec 15.4).
/// <para>
/// The second is why <see cref="InstancePropertyRegistry"/> permits no unregistration and no codec
/// replacement. Forgetting a kind IS the attack: an attacker who could make the server forget a field could
/// make two different items compare equal, merge them and keep the count of one while destroying the other.
/// Preserving an unknown field verbatim is therefore a security property before it is a compatibility one,
/// and the remaining surface is the honest one, a DEPLOY mismatch where one server was built without a kind
/// another server wrote.
/// </para>
/// <para>
/// Every fact builds its own registry, so nothing here writes process-global state.
/// </para>
/// </summary>
public class UnknownKindTests
{
    /// <summary>A game kind, which no engine registry ever holds and which spec 3.3 gives to the game.</summary>
    const ushort GameKind = InstancePropertyKind.FirstGameKind;

    [Fact]
    public void A_registry_missing_a_kind_reproduces_the_golden_byte_for_byte()
    {
        // The writer knew rarity and this reader was built without it. Every other v1 kind is registered,
        // so the golden's other four fields are shape checked and codec checked as usual.
        byte[] golden = Golden("contracts-9-8-worked-example.bin");
        InstancePropertyRegistry reader = WithoutKind(InstancePropertyKind.Rarity);

        Assert.False(reader.TryGet(InstancePropertyKind.Rarity, out _));
        Assert.Null(ItemInstancePayload.Validate(reader, golden));
        Assert.Equal(golden, Rebuild(reader, golden));
    }

    [Fact]
    public void An_unknown_kind_is_never_inspected_so_a_body_the_reader_could_not_parse_survives()
    {
        // Kind 1024 carrying bytes that are not a legal payload of any registered shape. A reader that
        // peeked would refuse it, and refusing is how a field gets dropped on the rewrite.
        var opaque = new byte[] { 0xFF, 0x00, 0x7F, 0x80 };
        byte[] written = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .Add(GameKind, opaque)
            .ToArray();

        InstancePropertyRegistry reader = InstancePropertyRegistry.CreateV1();
        Assert.False(reader.TryGet(GameKind, out _));
        Assert.Null(ItemInstancePayload.Validate(reader, written));

        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        Assert.True(ItemInstancePayload.TryDecode(reader, written, fields, out int count, out _));
        Assert.Equal(2, count);
        Assert.Equal(GameKind, fields[1].Kind);
        Assert.Equal(opaque, written.AsSpan(fields[1].BodyStart, fields[1].BodyLength).ToArray());
        Assert.Equal(written, Rebuild(reader, written));
    }

    [Fact]
    public void Two_items_differing_only_in_the_omitted_kind_do_not_merge()
    {
        // Spec 15.4. Byte equality is the stacking rule (spec 4.6), so the preserved bytes of a field this
        // build never heard of still separate two items. A decoder that dropped the field would merge them
        // and destroy one identity.
        InstancePropertyRegistry reader = WithoutKind(InstancePropertyKind.Rarity);

        byte[] rare = Item(rarity: 3);
        byte[] magic = Item(rarity: 2);

        Assert.Null(ItemInstancePayload.Validate(reader, rare));
        Assert.Null(ItemInstancePayload.Validate(reader, magic));
        Assert.False(ItemInstancePayload.SequenceEqual(rare, magic));
        Assert.False(ItemInstancePayload.SequenceEqual(Rebuild(reader, rare), Rebuild(reader, magic)));

        // And the same bytes through the same blind reader still ARE equal, so the refusal above is the
        // field's value rather than the round trip losing determinism.
        Assert.True(ItemInstancePayload.SequenceEqual(Rebuild(reader, rare), Rebuild(reader, rare)));
    }

    [Fact]
    public void Two_items_differing_only_in_an_unknown_game_kind_do_not_merge()
    {
        // The same rule one band up, and the case a consumer actually meets: a shard on build N reads an
        // item a shard on build N+1 wrote, and the two rolls of a game-owned field stay distinct.
        InstancePropertyRegistry reader = InstancePropertyRegistry.CreateV1();

        byte[] left = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .AddScalar(GameKind, 11)
            .ToArray();
        byte[] right = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .AddScalar(GameKind, 12)
            .ToArray();

        Assert.Null(ItemInstancePayload.Validate(reader, left));
        Assert.Null(ItemInstancePayload.Validate(reader, right));
        Assert.False(ItemInstancePayload.SequenceEqual(left, right));
        Assert.Equal(left, Rebuild(reader, left));
        Assert.Equal(right, Rebuild(reader, right));
    }

    [Fact]
    public void The_omitting_reader_and_the_full_reader_agree_on_every_field_position()
    {
        // The omitted kind changes what is CHECKED and never where anything sits, which is what makes the
        // rewrite above a copy rather than a re-encode.
        byte[] golden = Golden("spec-3-8-poe-greatsword.bin");
        InstancePropertyRegistry full = InstancePropertyRegistry.CreateV1();
        InstancePropertyRegistry partial = WithoutKind(InstancePropertyKind.Rarity);

        Span<PayloadField> left = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        Span<PayloadField> right = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        Assert.True(ItemInstancePayload.TryDecode(full, golden, left, out int leftCount, out _));
        Assert.True(ItemInstancePayload.TryDecode(partial, golden, right, out int rightCount, out _));

        Assert.Equal(leftCount, rightCount);
        for (int index = 0; index < leftCount; index++)
        {
            Assert.Equal(left[index], right[index]);
        }
    }

    /// <summary>
    /// The v1 registry MINUS one kind, rebuilt from its own registrations rather than from a second copy of
    /// spec 3.3's table, so it cannot drift from what <see cref="InstancePropertyRegistry.CreateV1"/> says.
    /// This is a server built without that kind, which is the only way the case arises: a live registry
    /// cannot unregister one.
    /// </summary>
    static InstancePropertyRegistry WithoutKind(ushort omitted)
    {
        var partial = new InstancePropertyRegistry();
        foreach (InstancePropertyRegistration registration in InstancePropertyRegistry.CreateV1().ByKind)
        {
            if (registration.Kind == omitted)
            {
                continue;
            }

            partial.Register(
                registration.Band,
                registration.Kind,
                registration.Codec,
                registration.Visibility,
                registration.IdentificationMaskBit,
                registration.Shape,
                registration.References.Span);
        }

        return partial;
    }

    /// <summary>
    /// Decodes and re-encodes through the PUBLIC builder, which is the round trip contracts 9.4 describes:
    /// every field goes back in as the opaque <c>(kind, bytes)</c> pair it came out as, known or not.
    /// </summary>
    static byte[] Rebuild(InstancePropertyRegistry registry, ReadOnlySpan<byte> payload)
    {
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        Assert.True(ItemInstancePayload.TryDecode(registry, payload, fields, out int count, out _));

        var builder = new ItemInstancePayloadBuilder();
        for (int index = 0; index < count; index++)
        {
            builder.Add(fields[index].Kind, payload.Slice(fields[index].BodyStart, fields[index].BodyLength));
        }

        return builder.ToArray();
    }

    /// <summary>The worked example's item at one rarity, which is the only byte that differs.</summary>
    static byte[] Item(byte rarity)
        => new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .AddScalars(InstancePropertyKind.Durability, 90, 100)
            .AddByte(InstancePropertyKind.Rarity, rarity)
            .ToArray();

    static byte[] Golden(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Payload", "Goldens", name));
}
