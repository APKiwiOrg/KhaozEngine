using System;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Validation;

/// <summary>
/// Spec 12.7's identification mechanic over the REGISTERED mask bit, and spec 15.8's accepted leak. Both
/// are durable facts rather than behaviour: the mask is stored inside kind 128 on every partially
/// identified item in the world, and the stacking answer is a byte compare nobody may soften.
/// </summary>
public class IdentificationMaskTests
{
    [Fact]
    public void RevealedMask_bit_N_is_the_bit_a_kind_REGISTERED_with()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();

        Assert.Equal(InstancePropertyKind.UniqueTemplate, Gated(registry, 0));
        Assert.Equal(InstancePropertyKind.Affixes, Gated(registry, 1));
        Assert.Equal(InstancePropertyKind.Enchantments, Gated(registry, 2));
        Assert.Equal(InstancePropertyKind.RareName, Gated(registry, 3));

        // Bits 4 to 31 are unassigned and zero in v1, and an ungated kind claims none of them.
        Assert.False(registry.TryGetByIdentificationMaskBit(4, out _));
        Assert.False(registry.TryGetByIdentificationMaskBit(InstancePropertyRegistry.MaxIdentificationMaskBit, out _));
        Assert.True(registry.TryGet(InstancePropertyKind.Identification, out InstancePropertyRegistration? identification));
        Assert.False(identification!.IsIdentificationGated);
    }

    [Fact]
    public void Registering_a_NEW_gated_engine_kind_at_9_does_not_move_bits_0_to_3()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();

        // The reachable hazard of spec 21's last row: an engine release adding a gated kind in its own
        // reserved 9 to 127 range lands BELOW 129, so a DERIVED ascending index would take bit 0 and shift
        // all four v1 assignments by one, on every stored payload, with no byte changing.
        registry.Register(
            InstanceKindBand.Engine,
            9,
            InstancePropertyCodec.ShapeOnly,
            PropertyVisibility.Everyone,
            4,
            new InstanceFieldShape(OneVarint, InstanceCountWidth.None, default),
            ReadOnlySpan<InstanceReferenceTarget>.Empty);

        Assert.Equal(InstancePropertyKind.UniqueTemplate, Gated(registry, 0));
        Assert.Equal(InstancePropertyKind.Affixes, Gated(registry, 1));
        Assert.Equal(InstancePropertyKind.Enchantments, Gated(registry, 2));
        Assert.Equal(InstancePropertyKind.RareName, Gated(registry, 3));
        Assert.Equal(9, Gated(registry, 4));

        // The new kind is FIRST in the ascending list of gated kinds and LAST in bit order, which is the
        // whole difference between a registered constant and an index.
        Assert.Equal(9, FirstGatedKind(registry));
    }

    [Fact]
    public void A_new_gated_kind_may_not_take_a_bit_a_released_engine_already_used()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => registry.Register(
            InstanceKindBand.Engine,
            9,
            InstancePropertyCodec.ShapeOnly,
            PropertyVisibility.Everyone,
            1,
            new InstanceFieldShape(OneVarint, InstanceCountWidth.None, default),
            ReadOnlySpan<InstanceReferenceTarget>.Empty));

        Assert.Contains("mask bit 1", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unidentified_item_still_stacks_by_byte_equality_and_different_hidden_rolls_do_not_merge()
    {
        byte[] first = Unidentified(new InstanceAffix(500, 3, 17));
        byte[] second = Unidentified(new InstanceAffix(500, 3, 900));
        byte[] twin = Unidentified(new InstanceAffix(500, 3, 17));

        // Spec 15.8's accepted leak, pinned so nobody closes it later without reading that section: two
        // unidentified items with different hidden affixes have different bytes, so they do not merge, and
        // a player who tries to stack them learns whether they are identical. Excluding the gated kinds
        // from the compare would make two genuinely different items merge and destroy one, which is
        // strictly worse than the one bit.
        Assert.False(ItemInstancePayload.SequenceEqual(first, second));
        Assert.True(ItemInstancePayload.SequenceEqual(first, twin));
    }

    static byte[] Unidentified(InstanceAffix affix)
    {
        var builder = new ItemInstancePayloadBuilder()
            .AddIdentification(identified: false, revealedMask: 0)
            .AddAffixes(InstancePropertyKind.Affixes, [affix]);
        byte[] bytes = new byte[builder.Length];
        ItemInstancePayload.Encode(builder, bytes);
        return bytes;
    }

    static ushort Gated(InstancePropertyRegistry registry, int bit)
    {
        Assert.True(
            registry.TryGetByIdentificationMaskBit(bit, out InstancePropertyRegistration? registration),
            FormattableString.Invariant($"No kind claims mask bit {bit}."));
        return registration!.Kind;
    }

    static ushort FirstGatedKind(InstancePropertyRegistry registry)
    {
        foreach (InstancePropertyRegistration registration in registry.ByKind)
        {
            if (registration.IsIdentificationGated)
            {
                return registration.Kind;
            }
        }

        Assert.Fail("No gated kind is registered.");
        return 0;
    }

    static readonly InstanceSlotKind[] OneVarint = { InstanceSlotKind.Varint };
}
