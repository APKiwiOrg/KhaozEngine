using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>One entry of kind 7's material list: an input item definition and how many parts of it went in.</summary>
/// <param name="MaterialId">An <c>item</c> content row, which is why a retired material is visible to the
/// remap pass and the validator.</param>
/// <param name="Parts">The parts of the whole this material contributed.</param>
public readonly record struct InstanceMaterial(int MaterialId, ushort Parts);

/// <summary>
/// One entry of kind 131 or kind 133, spec 3.4 over contracts 9.9:
/// <c>[ModId: varint][Tier: byte][Position: uint16 LE][Flags: varint]</c>.
/// </summary>
/// <param name="ModId">A <c>mod</c> content row, never 0.</param>
/// <param name="Tier">The mod's AUTHORED tier ordinal, 1 to 255, never 0, and never a <c>mod_tier</c>
/// content id: a payload that named one would break the moment an author reordered a mod's tiers.</param>
/// <param name="Position">The roll position, contracts 6.4. A FIXED two byte little endian value rather
/// than a varint, because positions are uniform over the whole range so a varint would cost more on
/// average.</param>
/// <param name="Flags">Reserved by contracts 9.9 and 0 in v1. It carries the per-instance facts about an
/// affix that arrive later, prefix or suffix, crafted, fractured, none of which can live on the mod row.</param>
public readonly record struct InstanceAffix(int ModId, byte Tier, ushort Position, uint Flags)
{
    /// <summary>An affix with the reserved flags field at its v1 value.</summary>
    /// <param name="modId">A <c>mod</c> content row, never 0.</param>
    /// <param name="tier">The mod's authored tier ordinal.</param>
    /// <param name="position">The roll position.</param>
    public InstanceAffix(int modId, byte tier, ushort position)
        : this(modId, tier, position, 0)
    {
    }
}

/// <summary>
/// One entry of kind 132, spec 3.5 over contracts 9.5. The socket carries the contained item's INSTANCE
/// id, so unsocketing RESTORES that identity rather than minting a new one, and an item duplicated by an
/// exploit stays traceable to the instance it was copied from.
/// </summary>
/// <param name="SocketTypeId">A <c>socket_type</c> content row, 0 for no restriction.</param>
/// <param name="ContainedDefinitionId">The contained item's definition, 0 when the socket is empty.</param>
/// <param name="ContainedInstanceId">The contained item's instance id, 0 when the socket is empty and also
/// 0 when the contained item has no instance of its own, which is the ordinary case for a plain gem.</param>
/// <param name="Nested">The contained item's own payload, in this same format and ONE level only.</param>
public readonly record struct InstanceSocket(
    int SocketTypeId,
    int ContainedDefinitionId,
    ulong ContainedInstanceId,
    ReadOnlyMemory<byte> Nested);

/// <summary>One field as the builder holds it: its kind and its body bytes, opaque either way.</summary>
readonly record struct PayloadFieldBytes(ushort Kind, ReadOnlyMemory<byte> Body);

/// <summary>
/// Builds a canonical payload. <b>The builder is the thing that makes the encoding canonical</b>: it holds
/// its fields strictly ascending by kind whatever order they were added in, refuses the same kind twice,
/// sorts an affix list ascending by mod id, and writes every varint minimally.
/// <para>
/// A field it does not recognise is held as an opaque <c>(kind, bytes)</c> pair in the same ordered list,
/// so a decode followed by a rebuild reproduces the input byte for byte and an unknown kind survives a
/// round trip through a client that never heard of it (contracts 9.4).
/// </para>
/// <para>
/// Every refusal here THROWS, because a builder is handed values by code rather than bytes by a peer. The
/// decoder is the total half of this pair and it never throws.
/// </para>
/// </summary>
public sealed class ItemInstancePayloadBuilder
{
    readonly List<PayloadFieldBytes> _fields = new();
    int _length;

    /// <summary>How many fields the payload will carry.</summary>
    public int FieldCount => _fields.Count;

    /// <summary>The bytes <see cref="ItemInstancePayload.Encode"/> will write, so a caller can size a buffer.</summary>
    public int Length => _length;

    /// <summary>The fields, ascending by kind.</summary>
    internal IReadOnlyList<PayloadFieldBytes> Fields => _fields;

    /// <summary>
    /// Adds one field as opaque bytes, which is how an unknown kind is carried and how a caller writes a
    /// body this builder has no helper for.
    /// </summary>
    /// <param name="kind">The property kind id. 0 is reserved and throws.</param>
    /// <param name="body">The field's bytes, without its kind and length prefix.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is 0.</exception>
    /// <exception cref="ArgumentException">The kind is already present.</exception>
    public ItemInstancePayloadBuilder Add(ushort kind, ReadOnlySpan<byte> body)
    {
        if (kind == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "Kind 0 is reserved by contracts 9.2 and is never a valid property kind.");
        }

        int index = _fields.Count;
        while (index > 0 && _fields[index - 1].Kind > kind)
        {
            index--;
        }

        if (index > 0 && _fields[index - 1].Kind == kind)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Kind {kind} is already in this payload. Rule 9.3.2 wants no kind twice, and two fields of one kind would give one set of properties two byte forms."),
                nameof(kind));
        }

        _fields.Insert(index, new PayloadFieldBytes(kind, body.ToArray()));
        _length += ContentVarint.Size(kind) + ContentVarint.Size((uint)body.Length) + body.Length;
        return this;
    }

    /// <summary>Adds a field whose body is one unsigned varint, which is kinds 1, 2, 3, 6, 8 and 129.</summary>
    /// <param name="kind">The property kind id.</param>
    /// <param name="value">The value.</param>
    public ItemInstancePayloadBuilder AddScalar(ushort kind, ulong value)
    {
        Span<byte> body = stackalloc byte[10];
        int written = ContentVarint.WriteUInt64(body, value);
        return Add(kind, body[..written]);
    }

    /// <summary>Adds a field whose body is two unsigned varints, which is kinds 4 and 5.</summary>
    /// <param name="kind">The property kind id.</param>
    /// <param name="first">The first value, a current charge count or current durability.</param>
    /// <param name="second">The second, its maximum.</param>
    public ItemInstancePayloadBuilder AddScalars(ushort kind, ulong first, ulong second)
    {
        Span<byte> body = stackalloc byte[20];
        int written = ContentVarint.WriteUInt64(body, first);
        written += ContentVarint.WriteUInt64(body[written..], second);
        return Add(kind, body[..written]);
    }

    /// <summary>Adds a field whose body is one raw byte, which is kind 130's rarity ordinal.</summary>
    /// <param name="kind">The property kind id.</param>
    /// <param name="value">The byte.</param>
    public ItemInstancePayloadBuilder AddByte(ushort kind, byte value)
    {
        Span<byte> body = stackalloc byte[1];
        body[0] = value;
        return Add(kind, body);
    }

    /// <summary>
    /// Adds kind 128, the identification state and the revealed mask the gated kinds index into. The mask
    /// bits are FIXED at registration and are never a kind's position in the list of gated kinds.
    /// </summary>
    /// <param name="identified">Whether the item is identified.</param>
    /// <param name="revealedMask">Which gated kinds have been revealed individually.</param>
    public ItemInstancePayloadBuilder AddIdentification(bool identified, uint revealedMask)
    {
        Span<byte> body = stackalloc byte[6];
        body[0] = identified ? (byte)1 : (byte)0;
        int written = 1 + ContentVarint.Write(body[1..], revealedMask);
        return Add(InstancePropertyKind.Identification, body[..written]);
    }

    /// <summary>
    /// Adds kind 7, the material inputs an item was made from, in AUTHORED order. The field stores the
    /// input ids and their parts rather than the stats derived from them, so a materials rebalance is a
    /// publish rather than a rewrite of every crafted item.
    /// </summary>
    /// <param name="materials">The inputs, in the order they were authored.</param>
    public ItemInstancePayloadBuilder AddMaterials(ReadOnlySpan<InstanceMaterial> materials)
    {
        int size = ContentVarint.Size((uint)materials.Length);
        foreach (InstanceMaterial material in materials)
        {
            if (material.MaterialId <= 0)
            {
                throw new ArgumentException(
                    "A material id is an item content row and is never 0 or negative.",
                    nameof(materials));
            }

            size += ContentVarint.Size((uint)material.MaterialId) + ContentVarint.Size(material.Parts);
        }

        byte[] body = new byte[size];
        int written = ContentVarint.Write(body, (uint)materials.Length);
        foreach (InstanceMaterial material in materials)
        {
            written += ContentVarint.Write(body.AsSpan(written), (uint)material.MaterialId);
            written += ContentVarint.Write(body.AsSpan(written), material.Parts);
        }

        return Add(InstancePropertyKind.Materials, body.AsSpan(0, written));
    }

    /// <summary>
    /// Adds kind 131 or kind 133, which share one entry layout. The list is written ASCENDING BY MOD ID
    /// whatever order it is handed in, which is what makes the field canonical and therefore what makes
    /// spec 4.6's byte comparison the stacking rule: two items carrying the same three affixes produce the
    /// same bytes regardless of the order they were rolled or crafted in.
    /// </summary>
    /// <param name="kind">131 for affixes, 133 for enchantments.</param>
    /// <param name="affixes">The entries, in any order.</param>
    /// <exception cref="ArgumentException">A mod id is 0 or appears twice, a tier is 0, the flags field is
    /// not 0, or there are more than 255 entries.</exception>
    public ItemInstancePayloadBuilder AddAffixes(ushort kind, ReadOnlySpan<InstanceAffix> affixes)
    {
        if (affixes.Length > byte.MaxValue)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"An affix list holds at most {byte.MaxValue} entries, because its count is a byte, and this one has {affixes.Length}."),
                nameof(affixes));
        }

        var sorted = new InstanceAffix[affixes.Length];
        affixes.CopyTo(sorted);
        Array.Sort(sorted, static (left, right) => left.ModId.CompareTo(right.ModId));

        int size = 1;
        long previousMod = -1;
        foreach (InstanceAffix affix in sorted)
        {
            if (affix.ModId <= 0)
            {
                throw new ArgumentException("A mod id is a content row and is never 0 or negative.", nameof(affixes));
            }

            if (affix.ModId == previousMod)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Mod {affix.ModId} appears twice. Contracts 9.9 allows a mod id at most once in one list, because without that the same three affixes encode several ways and byte equality stops being property equality."),
                    nameof(affixes));
            }

            if (affix.Tier == 0)
            {
                throw new ArgumentException("A tier ordinal runs 1 to 255 and is never 0.", nameof(affixes));
            }

            if (affix.Flags != 0)
            {
                throw new ArgumentException(
                    "The affix flags field is reserved by contracts 9.9 and is 0 in v1.",
                    nameof(affixes));
            }

            previousMod = affix.ModId;
            size += ContentVarint.Size((uint)affix.ModId) + 1 + 2 + ContentVarint.Size(affix.Flags);
        }

        byte[] body = new byte[size];
        body[0] = (byte)sorted.Length;
        int written = 1;
        foreach (InstanceAffix affix in sorted)
        {
            written += ContentVarint.Write(body.AsSpan(written), (uint)affix.ModId);
            body[written++] = affix.Tier;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(written), affix.Position);
            written += 2;
            written += ContentVarint.Write(body.AsSpan(written), affix.Flags);
        }

        return Add(kind, body.AsSpan(0, written));
    }

    /// <summary>
    /// Adds kind 132. Socket order is AUTHORED and never sorted, unlike the affix list, so two otherwise
    /// identical items whose gems sit in different sockets do NOT stack, which is correct because they are
    /// different items.
    /// </summary>
    /// <param name="sockets">The sockets, in the order the item carries them.</param>
    /// <exception cref="ArgumentException">An empty socket carries an instance id or a nested payload, or a
    /// nested payload is not canonical.</exception>
    public ItemInstancePayloadBuilder AddSockets(ReadOnlySpan<InstanceSocket> sockets)
    {
        int size = ContentVarint.Size((uint)sockets.Length);
        foreach (InstanceSocket socket in sockets)
        {
            if (socket.SocketTypeId < 0 || socket.ContainedDefinitionId < 0)
            {
                throw new ArgumentException("A socket type and a contained definition are never negative.", nameof(sockets));
            }

            if (socket.ContainedDefinitionId == 0
                && (socket.ContainedInstanceId != 0 || !socket.Nested.IsEmpty))
            {
                throw new ArgumentException(
                    "A contained definition of 0 MEANS the socket is empty (contracts 9.5), so an empty socket carries no instance id and no nested payload.",
                    nameof(sockets));
            }

            size += ContentVarint.Size((uint)socket.SocketTypeId)
                + ContentVarint.Size((uint)socket.ContainedDefinitionId)
                + ContentVarint.SizeUInt64(socket.ContainedInstanceId)
                + ContentVarint.Size((uint)socket.Nested.Length)
                + socket.Nested.Length;
        }

        byte[] body = new byte[size];
        int written = ContentVarint.Write(body, (uint)sockets.Length);
        foreach (InstanceSocket socket in sockets)
        {
            written += ContentVarint.Write(body.AsSpan(written), (uint)socket.SocketTypeId);
            written += ContentVarint.Write(body.AsSpan(written), (uint)socket.ContainedDefinitionId);
            written += ContentVarint.WriteUInt64(body.AsSpan(written), socket.ContainedInstanceId);
            written += ContentVarint.Write(body.AsSpan(written), (uint)socket.Nested.Length);
            socket.Nested.Span.CopyTo(body.AsSpan(written));
            written += socket.Nested.Length;
        }

        return Add(InstancePropertyKind.Sockets, body.AsSpan(0, written));
    }

    /// <summary>
    /// Adds kind 134, the composed rare name. The header slot is the <c>rarity_rule</c> row whose display
    /// format composed the name, not a <c>unique_template</c>, which is why a later rarity change does not
    /// strip the name off the item.
    /// </summary>
    /// <param name="rarityRuleId">The rarity rule that composed the name.</param>
    /// <param name="wordIds">The <c>rare_name_word</c> rows it used, in composed order.</param>
    public ItemInstancePayloadBuilder AddRareName(int rarityRuleId, ReadOnlySpan<int> wordIds)
    {
        if (rarityRuleId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rarityRuleId),
                rarityRuleId,
                "A rarity rule id is a content row and is never 0 or negative.");
        }

        if (wordIds.Length > byte.MaxValue)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"A rare name holds at most {byte.MaxValue} words, because its count is a byte."),
                nameof(wordIds));
        }

        int size = ContentVarint.Size((uint)rarityRuleId) + 1;
        foreach (int wordId in wordIds)
        {
            if (wordId <= 0)
            {
                throw new ArgumentException("A rare name word id is a content row and is never 0 or negative.", nameof(wordIds));
            }

            size += ContentVarint.Size((uint)wordId);
        }

        byte[] body = new byte[size];
        int written = ContentVarint.Write(body, (uint)rarityRuleId);
        body[written++] = (byte)wordIds.Length;
        foreach (int wordId in wordIds)
        {
            written += ContentVarint.Write(body.AsSpan(written), (uint)wordId);
        }

        return Add(InstancePropertyKind.RareName, body.AsSpan(0, written));
    }

    /// <summary>
    /// The encoded payload. An empty builder answers zero bytes, which is what a plain stack carries.
    /// </summary>
    /// <exception cref="InvalidOperationException">The fields total more than
    /// <see cref="ItemInstancePayload.MaxInstancePayloadBytes"/>.</exception>
    public byte[] ToArray()
    {
        // Ahead of the allocation, so a builder that ran away does not allocate what it cannot write.
        ItemInstancePayload.CheckCap(_length);
        if (_length == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] payload = new byte[_length];
        _ = ItemInstancePayload.Encode(this, payload);
        return payload;
    }
}
