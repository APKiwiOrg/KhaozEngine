using System;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Payload;

/// <summary>V1 value bounds belong to registered kinds, while shape-only decoding stays unsigned 64 bit.</summary>
public class ScalarContractPayloadTests
{
    [Theory]
    [InlineData(new byte[] { 0x01, 0x01, 0x08 })] // Flags: first reserved bit.
    [InlineData(new byte[] { 0x01, 0x05, 0x80, 0x80, 0x80, 0x80, 0x10 })] // Flags: bit 32.
    [InlineData(new byte[] { 0x01, 0x06, 0x80, 0x80, 0x80, 0x80, 0x80, 0x20 })] // Flags: bit 40.
    [InlineData(new byte[] { 0x02, 0x01, 0x00 })] // ItemLevel: zero.
    [InlineData(new byte[] { 0x02, 0x03, 0x80, 0x80, 0x04 })] // ItemLevel: 65536.
    [InlineData(new byte[] { 0x02, 0x06, 0x80, 0x80, 0x80, 0x80, 0x80, 0x20 })] // ItemLevel: 2^40.
    [InlineData(new byte[] { 0x03, 0x03, 0x80, 0x80, 0x04 })] // Quality: 65536.
    [InlineData(new byte[] { 0x04, 0x06, 0x80, 0x80, 0x80, 0x80, 0x10, 0x00 })] // Charges Current: 2^32.
    [InlineData(new byte[] { 0x04, 0x06, 0x00, 0x80, 0x80, 0x80, 0x80, 0x10 })] // Charges Maximum: 2^32.
    [InlineData(new byte[] { 0x05, 0x04, 0x80, 0x80, 0x04, 0x00 })] // Durability Current: 65536.
    [InlineData(new byte[] { 0x05, 0x04, 0x00, 0x80, 0x80, 0x04 })] // Durability Maximum: 65536.
    [InlineData(new byte[] { 0x05, 0x0C, 0x80, 0x80, 0x80, 0x80, 0x80, 0x20, 0x80, 0x80, 0x80, 0x80, 0x80, 0x20 })]
    [InlineData(new byte[] { 0x06, 0x01, 0x00 })] // BoundTo: zero.
    [InlineData(new byte[] { 0x80, 0x01, 0x06, 0x01, 0x80, 0x80, 0x80, 0x80, 0x10 })] // Identification: mask 2^32.
    [InlineData(new byte[] { 0x80, 0x01, 0x0B, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })]
    [InlineData(new byte[] { 0x80, 0x01, 0x02, 0x02, 0x00 })] // Identification: state 2.
    public void A_registered_v1_kind_refuses_a_value_outside_its_contract(byte[] payload)
    {
        // Removing a kind's codec or widening one bound must make these literal fixtures fail.
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];

        Assert.False(ItemInstancePayload.TryDecode(registry, payload, fields, out _, out string? reason));
        Assert.Equal("field-malformed", reason);
        Assert.Equal("field-malformed", ItemInstancePayload.Validate(registry, payload));
        Assert.False(ItemInstancePayload.IsCanonical(registry, payload));

        // The envelope-only overload does not inspect a registered kind's shape or values.
        Assert.True(ItemInstancePayload.TryDecode(payload, fields, out int count, out reason));
        Assert.Equal(1, count);
        Assert.Null(reason);
        Assert.Null(ItemInstancePayload.Validate(payload));
        Assert.True(ItemInstancePayload.IsCanonical(payload));
        Assert.True(registry.TryGet(fields[0].Kind, out InstancePropertyRegistration? registration));
        Assert.NotNull(registration);
        Assert.False(registration.Codec.TryValidate(
            payload.AsSpan(fields[0].BodyStart, fields[0].BodyLength), out reason));
        Assert.Equal("field-malformed", reason);
    }

    [Theory]
    [InlineData(1, new byte[] { 0x01, 0x01, 0x00 })]
    [InlineData(1, new byte[] { 0x01, 0x01, 0x07 })]
    [InlineData(2, new byte[] { 0x02, 0x01, 0x01 })]
    [InlineData(2, new byte[] { 0x02, 0x03, 0xFF, 0xFF, 0x03 })] // ItemLevel upper boundary is 65535.
    [InlineData(3, new byte[] { 0x03, 0x01, 0x00 })]
    [InlineData(3, new byte[] { 0x03, 0x01, 0x65 })] // Quality is not capped at 100.
    [InlineData(3, new byte[] { 0x03, 0x03, 0xFF, 0xFF, 0x03 })]
    [InlineData(4, new byte[] { 0x04, 0x02, 0x00, 0x00 })]
    [InlineData(4, new byte[] { 0x04, 0x06, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x00 })] // Current may exceed Maximum.
    [InlineData(4, new byte[] { 0x04, 0x06, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]
    [InlineData(4, new byte[] { 0x04, 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]
    [InlineData(5, new byte[] { 0x05, 0x02, 0x00, 0x00 })]
    [InlineData(5, new byte[] { 0x05, 0x04, 0xFF, 0xFF, 0x03, 0x00 })] // Current may exceed Maximum.
    [InlineData(5, new byte[] { 0x05, 0x04, 0x00, 0xFF, 0xFF, 0x03 })]
    [InlineData(5, new byte[] { 0x05, 0x06, 0xFF, 0xFF, 0x03, 0xFF, 0xFF, 0x03 })]
    [InlineData(6, new byte[] { 0x06, 0x01, 0x01 })]
    [InlineData(6, new byte[] { 0x06, 0x05, 0x80, 0x80, 0x80, 0x80, 0x10 })] // Subject 2^32.
    [InlineData(6, new byte[] { 0x06, 0x0A, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })] // Subject 2^63.
    [InlineData(6, new byte[] { 0x06, 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })]
    [InlineData(128, new byte[] { 0x80, 0x01, 0x02, 0x00, 0x00 })]
    [InlineData(128, new byte[] { 0x80, 0x01, 0x02, 0x01, 0x00 })]
    [InlineData(128, new byte[] { 0x80, 0x01, 0x06, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]
    [InlineData(128, new byte[] { 0x80, 0x01, 0x06, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]
    public void Legal_v1_boundaries_decode_without_narrowing_or_extra_policy(int kind, byte[] payload)
    {
        // A narrower BoundTo, a 100 Quality cap or Current <= Maximum would refuse a legal fixture.
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];

        Assert.True(ItemInstancePayload.TryDecode(registry, payload, fields, out int count, out string? reason));
        Assert.Equal(1, count);
        Assert.Equal(kind, fields[0].Kind);
        Assert.Null(reason);
        Assert.Null(ItemInstancePayload.Validate(registry, payload));
        Assert.True(ItemInstancePayload.IsCanonical(registry, payload));
        Assert.True(registry.TryGet((ushort)kind, out InstancePropertyRegistration? registration));
        Assert.NotNull(registration);
        Assert.True(registration.Codec.TryValidate(
            payload.AsSpan(fields[0].BodyStart, fields[0].BodyLength), out reason));
        Assert.Null(reason);
    }

    [Fact]
    public void A_game_kinds_shape_only_varint_still_accepts_the_full_unsigned_64_bit_range()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        registry.Register(
            InstanceKindBand.Game,
            1024,
            InstancePropertyCodec.ShapeOnly,
            PropertyVisibility.Everyone,
            -1,
            new InstanceFieldShape(new[] { InstanceSlotKind.Varint }, InstanceCountWidth.None, default),
            ReadOnlySpan<InstanceReferenceTarget>.Empty);
        byte[] payload = [0x80, 0x08, 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];

        Assert.True(ItemInstancePayload.TryDecode(registry, payload, fields, out int count, out string? reason));
        Assert.Equal(1, count);
        Assert.Equal(1024, fields[0].Kind);
        Assert.Null(reason);
        Assert.Null(ItemInstancePayload.Validate(registry, payload));
        Assert.True(ItemInstancePayload.IsCanonical(registry, payload));
    }
}
