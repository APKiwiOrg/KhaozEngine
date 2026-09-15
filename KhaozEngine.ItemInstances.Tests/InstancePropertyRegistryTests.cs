using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances;

/// <summary>
/// Spec 3.3's registration rules, stated as facts. The band and the mask bit are the two that stop a
/// DURABLE FORMAT bug rather than a compile error: a band mismatch is an engine release landing on a kind
/// a game already took, and a duplicate mask bit is two gated kinds sharing one revealed bit, which
/// re-points a partially identified item with no byte changing.
/// <para>
/// The registry is per instance, so every fact here builds its own and nothing writes process-global
/// state. No collection attribute is needed and none should be added.
/// </para>
/// </summary>
public class InstancePropertyRegistryTests
{
    static readonly InstanceFieldShape Scalar =
        new(new[] { InstanceSlotKind.Varint }, InstanceCountWidth.None, default);

    static void Register(
        InstancePropertyRegistry registry,
        InstanceKindBand band,
        ushort kind,
        int maskBit = -1)
        => registry.Register(
            band,
            kind,
            InstancePropertyCodec.ShapeOnly,
            PropertyVisibility.Everyone,
            maskBit,
            Scalar,
            ReadOnlySpan<InstanceReferenceTarget>.Empty);

    [Fact]
    public void A_game_band_registration_below_1024_throws()
    {
        var registry = new InstancePropertyRegistry();

        Assert.Throws<ArgumentOutOfRangeException>(() => Register(registry, InstanceKindBand.Game, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Register(registry, InstanceKindBand.Game, 127));
        Assert.Throws<ArgumentOutOfRangeException>(() => Register(registry, InstanceKindBand.Game, 1023));

        Register(registry, InstanceKindBand.Game, 1024);
        Register(registry, InstanceKindBand.Game, 65535);
        Assert.True(registry.TryGet(1024, out _));
        Assert.True(registry.TryGet(65535, out _));
    }

    [Fact]
    public void A_ScopeB_band_registration_outside_128_to_1023_throws()
    {
        var registry = new InstancePropertyRegistry();

        Assert.Throws<ArgumentOutOfRangeException>(() => Register(registry, InstanceKindBand.ScopeB, 127));
        Assert.Throws<ArgumentOutOfRangeException>(() => Register(registry, InstanceKindBand.ScopeB, 1024));

        Register(registry, InstanceKindBand.ScopeB, 128);
        Register(registry, InstanceKindBand.ScopeB, 1023);
        Assert.True(registry.TryGet(128, out _));
        Assert.True(registry.TryGet(1023, out _));
    }

    [Fact]
    public void An_Engine_band_registration_above_127_throws()
    {
        var registry = new InstancePropertyRegistry();

        Assert.Throws<ArgumentOutOfRangeException>(() => Register(registry, InstanceKindBand.Engine, 128));
        Assert.Throws<ArgumentOutOfRangeException>(() => Register(registry, InstanceKindBand.Engine, 1024));

        Register(registry, InstanceKindBand.Engine, 1);
        Register(registry, InstanceKindBand.Engine, 127);
        Assert.True(registry.TryGet(1, out _));
        Assert.True(registry.TryGet(127, out _));
    }

    [Fact]
    public void Kind_zero_is_never_registrable()
    {
        var registry = new InstancePropertyRegistry();

        Assert.Throws<ArgumentOutOfRangeException>(() => Register(registry, InstanceKindBand.Engine, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Register(registry, InstanceKindBand.ScopeB, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Register(registry, InstanceKindBand.Game, 0));
        Assert.Empty(registry.ByKind);
    }

    [Fact]
    public void A_duplicate_kind_throws()
    {
        var registry = new InstancePropertyRegistry();
        Register(registry, InstanceKindBand.Engine, 5);

        Assert.Throws<ArgumentException>(() => Register(registry, InstanceKindBand.Engine, 5));
        Assert.Single(registry.ByKind);
    }

    [Fact]
    public void A_duplicate_identification_mask_bit_throws()
    {
        var registry = new InstancePropertyRegistry();
        Register(registry, InstanceKindBand.ScopeB, 200, maskBit: 3);

        Assert.Throws<ArgumentException>(() => Register(registry, InstanceKindBand.ScopeB, 201, maskBit: 3));

        // A free bit is fine, and an ungated kind claims no bit at all, so any number of them coexist.
        Register(registry, InstanceKindBand.ScopeB, 201, maskBit: 4);
        Register(registry, InstanceKindBand.ScopeB, 202);
        Register(registry, InstanceKindBand.ScopeB, 203);
        Assert.Equal(4, registry.ByKind.Count);
    }

    [Fact]
    public void A_registration_after_the_first_pack_load_throws()
    {
        var registry = new InstancePropertyRegistry();
        Register(registry, InstanceKindBand.Engine, 2);

        Assert.False(registry.IsFrozen);
        registry.Freeze();
        Assert.True(registry.IsFrozen);

        Assert.Throws<InvalidOperationException>(() => Register(registry, InstanceKindBand.Engine, 3));

        // Freezing twice is not an error, and lookup keeps answering afterwards.
        registry.Freeze();
        Assert.True(registry.TryGet(2, out _));
    }

    [Fact]
    public void Unregistering_and_replacing_a_codec_are_both_refused()
    {
        var registry = new InstancePropertyRegistry();
        IInstancePropertyCodec first = InstancePropertyCodec.ShapeOnly;
        registry.Register(
            InstanceKindBand.ScopeB,
            300,
            first,
            PropertyVisibility.Everyone,
            -1,
            Scalar,
            ReadOnlySpan<InstanceReferenceTarget>.Empty);

        // Replacing is refused by the SAME rule that refuses a duplicate kind, and the first codec stays.
        var second = new ReplacementCodec();
        Assert.Throws<ArgumentException>(() => registry.Register(
            InstanceKindBand.ScopeB,
            300,
            second,
            PropertyVisibility.ServerOnly,
            -1,
            Scalar,
            ReadOnlySpan<InstanceReferenceTarget>.Empty));

        Assert.True(registry.TryGet(300, out InstancePropertyRegistration? held));
        Assert.Same(first, held.Codec);

        // Unregistering is refused by there being no door at all (spec 15.4): removing a kind would make
        // two previously distinct items stack and destroy one identity.
        string[] removers = typeof(InstancePropertyRegistry)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => n.Contains("Unregister", StringComparison.Ordinal)
                || n.Contains("Remove", StringComparison.Ordinal)
                || n.Contains("Replace", StringComparison.Ordinal)
                || n.Contains("Clear", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(removers);
    }

    [Fact]
    public void The_v1_engine_and_ScopeB_kinds_register_with_the_shapes_and_targets_of_3_3()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();

        Assert.Equal(V1Rows.Length, registry.ByKind.Count);
        Assert.Equal(
            V1Rows.Select(r => r.Kind).OrderBy(k => k).ToArray(),
            registry.ByKind.Select(r => r.Kind).ToArray());

        foreach (V1Row row in V1Rows)
        {
            Assert.True(registry.TryGet(row.Kind, out InstancePropertyRegistration? actual));
            Assert.Equal(row.Band, actual.Band);
            Assert.Equal(row.Visibility, actual.Visibility);
            Assert.Equal(row.MaskBit, actual.IdentificationMaskBit);
            Assert.Equal(row.Header, actual.Shape.Header.ToArray());
            Assert.Equal(row.Count, actual.Shape.Count);
            Assert.Equal(row.Entry, actual.Shape.Entry.ToArray());
            Assert.Equal(row.References, actual.References.ToArray());
        }

        // The registry starts unfrozen: a game registers its own kinds above 1023 before the first pack.
        Assert.False(registry.IsFrozen);
    }

    [Fact]
    public void The_four_v1_identification_bits_are_129_to_0_131_to_1_133_to_2_134_to_3()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();

        var gated = registry.ByKind
            .Where(r => r.IsIdentificationGated)
            .ToDictionary(r => r.Kind, r => r.IdentificationMaskBit);

        Assert.Equal(
            new Dictionary<ushort, int> { [129] = 0, [131] = 1, [133] = 2, [134] = 3 },
            gated);

        // Every other v1 kind claims no bit, kind 128 included: the mask LIVES in 128 and is not gated by it.
        Assert.All(
            registry.ByKind.Where(r => !gated.ContainsKey(r.Kind)),
            r => Assert.Equal(-1, r.IdentificationMaskBit));
    }

    sealed record V1Row(
        ushort Kind,
        InstanceKindBand Band,
        PropertyVisibility Visibility,
        int MaskBit,
        InstanceSlotKind[] Header,
        InstanceCountWidth Count,
        InstanceSlotKind[] Entry,
        InstanceReferenceTarget[] References);

    // Spec 3.3's two tables, transcribed. The shape says WHERE a value sits and the target says WHICH
    // content type it belongs to, so this is the whole of what the remap pass and the validator walk.
    static readonly V1Row[] V1Rows =
    {
        Header(1, InstanceKindBand.Engine, PropertyVisibility.Everyone, 1),
        Header(2, InstanceKindBand.Engine, PropertyVisibility.Everyone, 1),
        Header(3, InstanceKindBand.Engine, PropertyVisibility.Everyone, 1),
        Header(4, InstanceKindBand.Engine, PropertyVisibility.OwnerOnly, 2),
        Header(5, InstanceKindBand.Engine, PropertyVisibility.OwnerOnly, 2),
        Header(6, InstanceKindBand.Engine, PropertyVisibility.OwnerOnly, 1),
        new V1Row(
            7,
            InstanceKindBand.Engine,
            PropertyVisibility.Everyone,
            -1,
            Array.Empty<InstanceSlotKind>(),
            InstanceCountWidth.Varint,
            new[] { InstanceSlotKind.Varint, InstanceSlotKind.Varint },
            new[] { new InstanceReferenceTarget("item", InstanceReferenceSite.Entry, 0) }),
        Header(8, InstanceKindBand.Engine, PropertyVisibility.Everyone, 1),
        new V1Row(
            128,
            InstanceKindBand.ScopeB,
            PropertyVisibility.Everyone,
            -1,
            new[] { InstanceSlotKind.Byte, InstanceSlotKind.Varint },
            InstanceCountWidth.None,
            Array.Empty<InstanceSlotKind>(),
            Array.Empty<InstanceReferenceTarget>()),
        new V1Row(
            129,
            InstanceKindBand.ScopeB,
            PropertyVisibility.Everyone,
            0,
            new[] { InstanceSlotKind.Varint },
            InstanceCountWidth.None,
            Array.Empty<InstanceSlotKind>(),
            new[] { new InstanceReferenceTarget("unique_template", InstanceReferenceSite.Header, 0) }),
        new V1Row(
            130,
            InstanceKindBand.ScopeB,
            PropertyVisibility.Everyone,
            -1,
            new[] { InstanceSlotKind.Byte },
            InstanceCountWidth.None,
            Array.Empty<InstanceSlotKind>(),
            new[] { new InstanceReferenceTarget("rarity_rule", InstanceReferenceSite.Header, 0) }),
        Entries(131, 1),
        new V1Row(
            132,
            InstanceKindBand.ScopeB,
            PropertyVisibility.Everyone,
            -1,
            Array.Empty<InstanceSlotKind>(),
            InstanceCountWidth.Varint,
            new[]
            {
                InstanceSlotKind.Varint, InstanceSlotKind.Varint, InstanceSlotKind.Varint,
                InstanceSlotKind.NestedPayload,
            },
            new[]
            {
                new InstanceReferenceTarget("socket_type", InstanceReferenceSite.Entry, 0),
                new InstanceReferenceTarget("item", InstanceReferenceSite.Entry, 1),
            }),
        Entries(133, 2),
        new V1Row(
            134,
            InstanceKindBand.ScopeB,
            PropertyVisibility.Everyone,
            3,
            new[] { InstanceSlotKind.Varint },
            InstanceCountWidth.Byte,
            new[] { InstanceSlotKind.Varint },
            new[]
            {
                new InstanceReferenceTarget("rarity_rule", InstanceReferenceSite.Header, 0),
                new InstanceReferenceTarget("rare_name_word", InstanceReferenceSite.Entry, 0),
            }),
    };

    static V1Row Header(ushort kind, InstanceKindBand band, PropertyVisibility visibility, int scalars)
        => new(
            kind,
            band,
            visibility,
            -1,
            Enumerable.Repeat(InstanceSlotKind.Varint, scalars).ToArray(),
            InstanceCountWidth.None,
            Array.Empty<InstanceSlotKind>(),
            Array.Empty<InstanceReferenceTarget>());

    // Kinds 131 and 133 share one entry layout, and 131's count is a BYTE where 132's is a varint.
    static V1Row Entries(ushort kind, int maskBit)
        => new(
            kind,
            InstanceKindBand.ScopeB,
            PropertyVisibility.Everyone,
            maskBit,
            Array.Empty<InstanceSlotKind>(),
            InstanceCountWidth.Byte,
            new[]
            {
                InstanceSlotKind.Varint, InstanceSlotKind.Byte, InstanceSlotKind.Fixed2,
                InstanceSlotKind.Varint,
            },
            new[] { new InstanceReferenceTarget("mod", InstanceReferenceSite.Entry, 0) });

    sealed class ReplacementCodec : IInstancePropertyCodec
    {
        public bool TryValidate(ReadOnlySpan<byte> body, out string? reason)
        {
            reason = null;
            return true;
        }
    }
}
