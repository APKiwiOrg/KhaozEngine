using System;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;

namespace KhaozEngine.Tests.ItemInstances.Visibility;

/// <summary>
/// The registry and the payloads every visibility fact reads, in one place so each fact states a rule
/// rather than rebuilding a world.
/// <para>
/// The v1 kinds of spec 3.3 hold no <c>ServerOnly</c> field and no gated <c>OwnerOnly</c> one, so the two
/// clauses of spec 12.5 that matter most would otherwise go untested. Four GAME kinds are registered here
/// to supply them, in the 1024 and above band the band rule reserves to a game, which is also the shape a
/// game uses when it registers its own.
/// </para>
/// <para>
/// Every helper builds its own registry, so nothing here writes process-global state and no collection
/// attribute is needed.
/// </para>
/// </summary>
static class VisibilityFixtures
{
    /// <summary>A game kind the server alone ever holds, which is the clause no v1 kind exercises.</summary>
    internal const ushort ServerSecret = 1024;

    /// <summary>A game kind that is BOTH owner-only and identification gated, so the two rules compose.</summary>
    internal const ushort OwnerSecret = 1025;

    /// <summary>A game kind that is public and identification gated, which is the shape kinds 129 to 134 use.</summary>
    internal const ushort PublicGated = 1026;

    /// <summary>A game kind gated on the HIGHEST legal bit, which is where a narrowed mask would show.</summary>
    internal const ushort HighBitGated = 1027;

    /// <summary>The bit <see cref="OwnerSecret"/> is gated behind.</summary>
    internal const int OwnerSecretBit = 4;

    /// <summary>The bit <see cref="PublicGated"/> is gated behind.</summary>
    internal const int PublicGatedBit = 5;

    /// <summary>The bit <see cref="HighBitGated"/> is gated behind, which is the registry's ceiling.</summary>
    internal const int HighBit = InstancePropertyRegistry.MaxIdentificationMaskBit;

    /// <summary>A kind no registry here holds, which is contracts 9.4's unknown kind.</summary>
    internal const ushort UnregisteredKind = 2048;

    /// <summary>The mask bit an ungated kind declares.</summary>
    internal const int NotGated = -1;

    /// <summary>The v1 registry plus the four game kinds above, unfrozen.</summary>
    internal static InstancePropertyRegistry Registry()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        Scalar(registry, ServerSecret, PropertyVisibility.ServerOnly, NotGated);
        Scalar(registry, OwnerSecret, PropertyVisibility.OwnerOnly, OwnerSecretBit);
        Scalar(registry, PublicGated, PropertyVisibility.Everyone, PublicGatedBit);
        Scalar(registry, HighBitGated, PropertyVisibility.Everyone, HighBit);
        return registry;
    }

    /// <summary>
    /// A payload carrying EVERY kind <see cref="Registry"/> holds, one field each, which is what lets the
    /// table-driven fact read a projection once per viewer and ask about each kind in it.
    /// </summary>
    internal static byte[] EveryKind(bool identified, uint revealedMask)
        => new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.Flags, 1)
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .AddScalar(InstancePropertyKind.Quality, 87)
            .AddScalars(InstancePropertyKind.Charges, 18, 20)
            .AddScalars(InstancePropertyKind.Durability, 90, 100)
            .AddScalar(InstancePropertyKind.BoundTo, 990_001)
            .AddMaterials(new[] { new InstanceMaterial(12, 60), new InstanceMaterial(200, 40) })
            .AddScalar(InstancePropertyKind.Tier, 2)
            .AddIdentification(identified, revealedMask)
            .AddScalar(InstancePropertyKind.UniqueTemplate, 77)
            .AddByte(InstancePropertyKind.Rarity, 3)
            .AddAffixes(InstancePropertyKind.Affixes, new[] { new InstanceAffix(91, 1, 13107) })
            .AddSockets(new[] { new InstanceSocket(7, 833, 4201, SocketedGem) })
            .AddAffixes(InstancePropertyKind.Enchantments, new[] { new InstanceAffix(260, 2, 65535) })
            .AddRareName(7, new[] { 17, 34 })
            .AddScalar(ServerSecret, 5)
            .AddScalar(OwnerSecret, 6)
            .AddScalar(PublicGated, 7)
            .AddScalar(HighBitGated, 8)
            .ToArray();

    /// <summary>Whether a projection carries a field of one kind, which is the replication filter's answer.</summary>
    internal static bool Carries(InstancePropertyRegistry registry, ReadOnlySpan<byte> view, ushort kind)
    {
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        if (!ItemInstancePayload.TryDecode(registry, view, fields, out int count, out _))
        {
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            if (fields[index].Kind == kind)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every kind <see cref="Registry"/> holds, ascending, which the table-driven fact walks.</summary>
    internal static ushort[] RegisteredKinds()
    {
        InstancePropertyRegistry registry = Registry();
        var kinds = new ushort[registry.ByKind.Count];
        for (int index = 0; index < kinds.Length; index++)
        {
            kinds[index] = registry.ByKind[index].Kind;
        }

        return kinds;
    }

    /// <summary>
    /// The socket's contained item, which is the one level of nesting the format allows. It carries two
    /// OWNER-ONLY kinds beside its public one, because kind 132 is <c>Everyone</c> and a projection that
    /// kept or dropped whole top-level fields shipped whatever a kept field held inside it.
    /// </summary>
    internal static byte[] SocketedGem { get; } =
        new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 55)
            .AddScalars(InstancePropertyKind.Durability, 90, 100)
            .AddScalar(InstancePropertyKind.BoundTo, 990_001)
            .ToArray();

    /// <summary>
    /// The nested payload of a projection's ONE socket entry, which is where a projection that only kept
    /// or dropped whole top-level fields leaks. It walks kind 132's body directly rather than through a
    /// helper the projection also uses, so a fact reading it cannot be satisfied by the same mistake.
    /// </summary>
    internal static byte[] SocketNested(InstancePropertyRegistry registry, ReadOnlySpan<byte> view)
    {
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        if (!ItemInstancePayload.TryDecode(registry, view, fields, out int count, out _))
        {
            return Array.Empty<byte>();
        }

        for (int index = 0; index < count; index++)
        {
            if (fields[index].Kind != InstancePropertyKind.Sockets)
            {
                continue;
            }

            ReadOnlySpan<byte> body = view.Slice(fields[index].BodyStart, fields[index].BodyLength);
            int offset = 0;

            // The entry count, then the socket type, the contained definition and the contained instance
            // id, then the length prefixed payload of the item in the socket.
            if (!ContentVarint.TryRead(body, ref offset, out uint entries, out _) || entries == 0)
            {
                return Array.Empty<byte>();
            }

            for (int slot = 0; slot < 3; slot++)
            {
                if (!ContentVarint.TryReadUInt64(body, ref offset, out _, out _))
                {
                    return Array.Empty<byte>();
                }
            }

            return ContentVarint.TryRead(body, ref offset, out uint nested, out _)
                ? body.Slice(offset, (int)nested).ToArray()
                : Array.Empty<byte>();
        }

        return Array.Empty<byte>();
    }

    static void Scalar(
        InstancePropertyRegistry registry,
        ushort kind,
        PropertyVisibility visibility,
        int identificationMaskBit)
        => registry.Register(
            InstanceKindBand.Game,
            kind,
            InstancePropertyCodec.ShapeOnly,
            visibility,
            identificationMaskBit,
            new InstanceFieldShape(OneVarint, InstanceCountWidth.None, default),
            ReadOnlySpan<InstanceReferenceTarget>.Empty);

    static readonly InstanceSlotKind[] OneVarint = { InstanceSlotKind.Varint };
}
