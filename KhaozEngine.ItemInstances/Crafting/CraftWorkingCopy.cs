using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>One field as a craft holds it: either a slice of the stored bytes or a body the craft wrote.</summary>
/// <param name="Kind">The property kind id.</param>
/// <param name="SourceStart">Where the field's body begins in the STORED bytes, for a field nobody wrote.</param>
/// <param name="SourceLength">How long it is there.</param>
/// <param name="Body">The written body, or null for a field still pointing at the stored bytes.</param>
readonly record struct CraftField(ushort Kind, int SourceStart, int SourceLength, byte[]? Body)
{
    /// <summary>The body's byte count, whichever half of the pair holds it.</summary>
    internal int BodyLength => Body?.Length ?? SourceLength;
}

/// <summary>
/// The mutable half of a working copy, on the heap so a copy of the <c>ref struct</c> shares one craft
/// rather than forking it. That is what makes the all-or-nothing rule hold through
/// <c>ICraftOperation.Apply(ref CraftWorkingCopy, ...)</c> and through every primitive: a refusal recorded
/// anywhere is a refusal everywhere, and there is no second builder for a caller to encode instead.
/// </summary>
sealed class CraftFieldSet
{
    /// <summary>The fields, strictly ascending by kind, which is where they started and where they stay.</summary>
    internal List<CraftField> Fields { get; } = [];

    /// <summary>What the encode would write, kept running so the cap is checked before a body is built.</summary>
    internal int Length { get; set; }

    /// <summary>The FIRST refusal, which is the only one that means anything.</summary>
    internal CraftRefusal Refusal { get; set; }
}

/// <summary>
/// A craft in progress: the target's decoded payload, the edits a primitive makes, and the one re-encode at
/// the end. Spec 10.1's shape, made a property of a TYPE rather than a rule a reviewer enforces.
/// <para>
/// <b>A craft NEVER mutates in place.</b> It decodes the target, applies its steps into a builder and
/// re-encodes canonically. A refusal at any step discards the builder and the durable bytes are untouched,
/// so there is no partial craft and no rollback path to get wrong. That is the same all-or-nothing shape
/// the stacking fold and the bag clone already have in two of the four games.
/// </para>
/// <para>
/// <b>It is a <c>ref struct</c> because it is opened over the STORED SPAN.</b> A field nobody wrote is held
/// as a slice of those bytes rather than as a copy of them, so a craft that touches one field of six copies
/// one field, and "never patches a stored byte" is structural: the span is read only and the encode writes
/// somewhere else entirely.
/// </para>
/// <para>
/// <b>The four powers spec 10.5 says a game operation must not have are properties of this type.</b> An
/// unregistered kind has no door, because every write asks the registry first. The cap is checked at EVERY
/// write rather than at the encode. Canonical order is the builder's, so nothing here can produce a
/// non-canonical payload. And there is no <see cref="InstanceIdAllocator"/> anywhere in the type, so a
/// working copy provably cannot mint an instance id, which is the same argument spec 9.1 makes about the
/// generator's <c>IRandomSource</c> one level up.
/// </para>
/// <para>
/// <b>An unknown kind the decode preserved survives verbatim, INCLUDING its position in the ordering</b>
/// (contracts 9.4). It is held as the same opaque slice every other untouched field is, and the builder
/// re-seats it by kind, so a client built against content build N can craft an item carrying a field only
/// build N plus one knows and give it back whole.
/// </para>
/// <para>
/// <b>No primitive merges two stacks, and that is why the contracts 8.2 kind 4 stack cap is not this
/// type's problem.</b> A craft rewrites ONE payload in place and the currency consumption DECREMENTS a
/// stack, and a decrement can only shrink, which is the one direction the cap always permits. Issue 924
/// therefore neither blocks this work nor is closed by it.
/// </para>
/// </summary>
public ref struct CraftWorkingCopy
{
    readonly InstancePropertyRegistry _registry;
    readonly IContentSnapshot _snapshot;
    readonly ReadOnlySpan<byte> _source;
    readonly CraftFieldSet _set;
    readonly int _definitionId;

    CraftWorkingCopy(
        InstancePropertyRegistry registry,
        IContentSnapshot snapshot,
        int definitionId,
        ReadOnlySpan<byte> source,
        CraftFieldSet set)
    {
        _registry = registry;
        _snapshot = snapshot;
        _definitionId = definitionId;
        _source = source;
        _set = set;
    }

    /// <summary>The most entries kind 131 and kind 133 can hold, because their count is a byte.</summary>
    public const int MaxAffixes = byte.MaxValue;

    /// <summary>The property kinds this process knows, which is what decides an unregistered write.</summary>
    public readonly InstancePropertyRegistry Registry => _registry;

    /// <summary>The content version the craft reads, for a rarity rule, a socket type or a mod row.</summary>
    public readonly IContentSnapshot Snapshot => _snapshot;

    /// <summary>
    /// The target's own item definition, which the payload does not carry and two primitives need: a draw
    /// is keyed by the BASE's authored tags, and a tag guard asks the base rather than the instance.
    /// </summary>
    public readonly int DefinitionId => _definitionId;

    /// <summary>Whether a step has refused, after which every write is a no-op and no encode happens.</summary>
    public readonly bool IsRefused => _set.Refusal.IsRefusal;

    /// <summary>The FIRST refusal, or a <see cref="CraftRefusalKind.None"/> value while the craft is fine.</summary>
    public readonly CraftRefusal Refusal => _set.Refusal;

    /// <summary>What the encode would write right now, which is what the cap is checked against.</summary>
    public readonly int Length => _set.Length;

    /// <summary>How many fields the payload carries, unknown kinds included.</summary>
    public readonly int FieldCount => _set.Fields.Count;

    /// <summary>How many sockets kind 132 carries, or 0 when the item has none.</summary>
    public readonly int SocketCount
    {
        get
        {
            int index = IndexOf(InstancePropertyKind.Sockets);
            if (index < 0)
            {
                return 0;
            }

            int offset = 0;
            ReadOnlySpan<byte> body = BodyAt(index);
            return ContentVarint.TryRead(body, ref offset, out uint count, out _) ? (int)count : 0;
        }
    }

    /// <summary>
    /// Opens a craft over one stored payload. A payload that does not decode opens a copy that is ALREADY
    /// refused rather than throwing, because the bytes came from a page or a peer and the decoder never
    /// throws for a byte (contracts 9.7).
    /// </summary>
    /// <param name="registry">The property kinds this process knows.</param>
    /// <param name="snapshot">The content version the craft reads.</param>
    /// <param name="definitionId">The target's item definition, spec 3.1.</param>
    /// <param name="payload">The stored bytes, which may be empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="snapshot"/>
    /// is null.</exception>
    public static CraftWorkingCopy Open(
        InstancePropertyRegistry registry,
        IContentSnapshot snapshot,
        int definitionId,
        ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(snapshot);

        var set = new CraftFieldSet();
        Span<PayloadField> decoded = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        if (!ItemInstancePayload.TryDecode(registry, payload, decoded, out int count, out _))
        {
            set.Refusal = new CraftRefusal(CraftRefusalKind.PayloadMalformed, 0);
            return new CraftWorkingCopy(registry, snapshot, definitionId, payload, set);
        }

        for (int index = 0; index < count; index++)
        {
            PayloadField field = decoded[index];
            set.Fields.Add(new CraftField(field.Kind, field.BodyStart, field.BodyLength, null));
            set.Length += FieldSize(field.Kind, field.BodyLength);
        }

        return new CraftWorkingCopy(registry, snapshot, definitionId, payload, set);
    }

    /// <summary>
    /// Records a refusal, which the FIRST caller wins: a later step cannot overwrite what stopped the
    /// craft, and there is nothing to undo because nothing durable was written.
    /// </summary>
    /// <param name="refusal">Why the step refused.</param>
    /// <returns>The refusal that stands, which is the first one recorded.</returns>
    public CraftRefusal Refuse(CraftRefusal refusal)
    {
        if (!_set.Refusal.IsRefusal)
        {
            _set.Refusal = refusal;
        }

        return _set.Refusal;
    }

    /// <summary>Whether the item carries a field of that kind.</summary>
    /// <param name="kind">The property kind id.</param>
    public readonly bool Has(ushort kind) => IndexOf(kind) >= 0;

    /// <summary>Reads a field whose body is one unsigned varint, which is kinds 1, 2, 3, 6, 8 and 129.</summary>
    /// <param name="kind">The property kind id.</param>
    /// <param name="value">The value, when the item carries the field and it reads.</param>
    public readonly bool TryGetScalar(ushort kind, out ulong value)
    {
        value = 0;
        int index = IndexOf(kind);
        int offset = 0;
        return index >= 0 && ContentVarint.TryReadUInt64(BodyAt(index), ref offset, out value, out _);
    }

    /// <summary>Reads a field whose body is two unsigned varints, which is kinds 4 and 5.</summary>
    /// <param name="kind">The property kind id.</param>
    /// <param name="first">The first value, a current charge count or current durability.</param>
    /// <param name="second">The second, its maximum.</param>
    public readonly bool TryGetPair(ushort kind, out ulong first, out ulong second)
    {
        first = 0;
        second = 0;
        int index = IndexOf(kind);
        if (index < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> body = BodyAt(index);
        int offset = 0;
        return ContentVarint.TryReadUInt64(body, ref offset, out first, out _)
            && ContentVarint.TryReadUInt64(body, ref offset, out second, out _);
    }

    /// <summary>Reads a field whose body is one raw byte, which is kind 130's rarity ordinal.</summary>
    /// <param name="kind">The property kind id.</param>
    /// <param name="value">The byte, when the item carries the field.</param>
    public readonly bool TryGetByte(ushort kind, out byte value)
    {
        int index = IndexOf(kind);
        ReadOnlySpan<byte> body = index < 0 ? default : BodyAt(index);
        value = body.Length == 0 ? (byte)0 : body[0];
        return body.Length > 0;
    }

    /// <summary>Reads kind 128, the identification state and the revealed mask.</summary>
    /// <param name="identified">Whether the item is identified.</param>
    /// <param name="revealedMask">Which gated kinds have been revealed individually.</param>
    public readonly bool TryGetIdentification(out bool identified, out uint revealedMask)
    {
        identified = false;
        revealedMask = 0;
        int index = IndexOf(InstancePropertyKind.Identification);
        if (index < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> body = BodyAt(index);
        if (body.Length < 1)
        {
            return false;
        }

        identified = body[0] == 1;
        int offset = 1;
        return ContentVarint.TryRead(body, ref offset, out revealedMask, out _);
    }

    /// <summary>How many entries kind 131 or kind 133 carries.</summary>
    /// <param name="kind">131 for affixes, 133 for enchantments.</param>
    public readonly int AffixCount(ushort kind)
    {
        int index = IndexOf(kind);
        ReadOnlySpan<byte> body = index < 0 ? default : BodyAt(index);
        return body.Length == 0 ? 0 : body[0];
    }

    /// <summary>
    /// Reads kind 131 or kind 133 into a span, in the stored order, which is ASCENDING BY MOD ID because
    /// that is the only order the encoder ever writes.
    /// </summary>
    /// <param name="kind">131 for affixes, 133 for enchantments.</param>
    /// <param name="destination">Where to write, at least <see cref="AffixCount"/> long.</param>
    /// <returns>How many entries were written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short, which is a caller
    /// error rather than a fact about the item.</exception>
    public readonly int ReadAffixes(ushort kind, scoped Span<InstanceAffix> destination)
    {
        int count = AffixCount(kind);
        if (destination.Length < count)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"Kind {kind} carries {count} entries and the span holds {destination.Length}."),
                nameof(destination));
        }

        if (count == 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> body = BodyAt(IndexOf(kind));
        int offset = 1;
        for (int entry = 0; entry < count; entry++)
        {
            _ = ContentVarint.TryRead(body, ref offset, out uint modId, out _);
            byte tier = body[offset++];
            ushort position = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
            offset += 2;
            _ = ContentVarint.TryRead(body, ref offset, out uint flags, out _);
            destination[entry] = new InstanceAffix((int)modId, tier, position, flags);
        }

        return count;
    }

    /// <summary>
    /// Reads kind 132 into a span, in AUTHORED order, which is never sorted. Each nested payload is COPIED
    /// out, because a socket's contents leave this craft as an item of their own.
    /// </summary>
    /// <param name="destination">Where to write, at least <see cref="SocketCount"/> long.</param>
    /// <returns>How many sockets were written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public readonly int ReadSockets(scoped Span<InstanceSocket> destination)
    {
        int count = SocketCount;
        if (destination.Length < count)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"Kind 132 carries {count} sockets and the span holds {destination.Length}."),
                nameof(destination));
        }

        if (count == 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> body = BodyAt(IndexOf(InstancePropertyKind.Sockets));
        int offset = 0;
        _ = ContentVarint.TryRead(body, ref offset, out _, out _);
        for (int entry = 0; entry < count; entry++)
        {
            _ = ContentVarint.TryRead(body, ref offset, out uint socketType, out _);
            _ = ContentVarint.TryRead(body, ref offset, out uint definition, out _);
            _ = ContentVarint.TryReadUInt64(body, ref offset, out ulong instance, out _);
            _ = ContentVarint.TryRead(body, ref offset, out uint nested, out _);
            destination[entry] = new InstanceSocket(
                (int)socketType,
                (int)definition,
                instance,
                nested == 0 ? default : body.Slice(offset, (int)nested).ToArray());
            offset += (int)nested;
        }

        return count;
    }

    /// <summary>
    /// Whether one affix entry names a mod row this version marks <c>legacy</c>, which is the door the
    /// standing frozen-entry rule of spec 10.3 is written against. A mod row this version has no live copy
    /// of is not legacy: it is a drift the instance validator answers, not a craft rule.
    /// </summary>
    /// <param name="modId">The entry's mod id.</param>
    public readonly bool IsLegacyMod(int modId)
        => modId > 0
            && _snapshot.TryGetRow(new ContentTypeId(InstanceContentTypeIds.ModTypeId), modId, out ContentRow? row)
            && !row.IsRetired
            && InstanceContentChecks.Number(row, ModContentType.LegacyIndex) == 1;

    /// <summary>Writes a field whose body is one unsigned varint.</summary>
    /// <param name="kind">The property kind id.</param>
    /// <param name="value">The value.</param>
    /// <returns>Whether the craft is still fine.</returns>
    public bool SetScalar(ushort kind, ulong value)
    {
        Span<byte> body = stackalloc byte[10];
        int written = ContentVarint.WriteUInt64(body, value);
        return Write(kind, body[..written]);
    }

    /// <summary>Writes a field whose body is two unsigned varints.</summary>
    /// <param name="kind">The property kind id.</param>
    /// <param name="first">The first value.</param>
    /// <param name="second">The second.</param>
    /// <returns>Whether the craft is still fine.</returns>
    public bool SetPair(ushort kind, ulong first, ulong second)
    {
        Span<byte> body = stackalloc byte[20];
        int written = ContentVarint.WriteUInt64(body, first);
        written += ContentVarint.WriteUInt64(body[written..], second);
        return Write(kind, body[..written]);
    }

    /// <summary>Writes a field whose body is one raw byte.</summary>
    /// <param name="kind">The property kind id.</param>
    /// <param name="value">The byte.</param>
    /// <returns>Whether the craft is still fine.</returns>
    public bool SetByte(ushort kind, byte value)
    {
        Span<byte> body = stackalloc byte[1];
        body[0] = value;
        return Write(kind, body);
    }

    /// <summary>Writes kind 128, the identification state and the revealed mask.</summary>
    /// <param name="identified">Whether the item is identified.</param>
    /// <param name="revealedMask">Which gated kinds have been revealed individually.</param>
    /// <returns>Whether the craft is still fine.</returns>
    public bool SetIdentification(bool identified, uint revealedMask)
    {
        Span<byte> body = stackalloc byte[6];
        body[0] = identified ? (byte)1 : (byte)0;
        int written = 1 + ContentVarint.Write(body[1..], revealedMask);
        return Write(InstancePropertyKind.Identification, body[..written]);
    }

    /// <summary>
    /// Writes kind 131 or kind 133. An EMPTY list removes the field rather than writing a count of zero, so
    /// an item a craft stripped bare has the same bytes as one that never had affixes and the two stack.
    /// </summary>
    /// <param name="kind">131 for affixes, 133 for enchantments.</param>
    /// <param name="affixes">The entries, in any order. The encoder sorts them ascending by mod id.</param>
    /// <returns>Whether the craft is still fine.</returns>
    public bool SetAffixes(ushort kind, scoped ReadOnlySpan<InstanceAffix> affixes)
    {
        if (IsRefused)
        {
            return false;
        }

        if (affixes.Length > MaxAffixes)
        {
            _ = Refuse(new CraftRefusal(CraftRefusalKind.AffixListFull, kind));
            return false;
        }

        for (int outer = 0; outer < affixes.Length; outer++)
        {
            if (affixes[outer].ModId <= 0 || affixes[outer].Tier == 0 || affixes[outer].Flags != 0)
            {
                _ = Refuse(new CraftRefusal(CraftRefusalKind.ValueOutOfRange, affixes[outer].ModId));
                return false;
            }

            for (int inner = outer + 1; inner < affixes.Length; inner++)
            {
                if (affixes[outer].ModId == affixes[inner].ModId)
                {
                    _ = Refuse(new CraftRefusal(CraftRefusalKind.ValueOutOfRange, affixes[outer].ModId));
                    return false;
                }
            }
        }

        // The affix list is the ONE door both legacy standing rules sit at, so a frozen entry cannot be
        // rewritten and a legacy row cannot be added by any primitive or by an ICraftOperation writing its
        // own list. Spec 10.3 and contracts 5.4.
        if (CraftStandingRules.CheckAffixWrite(ref this, kind, affixes) is not null)
        {
            return false;
        }

        if (affixes.Length == 0)
        {
            return Remove(kind);
        }

        var builder = new ItemInstancePayloadBuilder();
        _ = builder.AddAffixes(kind, affixes);
        return Write(kind, builder.Fields[0].Body.Span);
    }

    /// <summary>
    /// Writes kind 132 in AUTHORED order, which is never sorted. An empty list removes the field, for the
    /// same reason an empty affix list does.
    /// </summary>
    /// <param name="sockets">The sockets, in the order the item carries them.</param>
    /// <returns>Whether the craft is still fine.</returns>
    public bool SetSockets(scoped ReadOnlySpan<InstanceSocket> sockets)
    {
        if (IsRefused)
        {
            return false;
        }

        foreach (InstanceSocket socket in sockets)
        {
            if (socket.SocketTypeId < 0 || socket.ContainedDefinitionId < 0
                || (socket.ContainedDefinitionId == 0
                    && (socket.ContainedInstanceId != 0 || !socket.Nested.IsEmpty))
                || (!socket.Nested.IsEmpty && !ItemInstancePayload.IsCanonical(socket.Nested)))
            {
                _ = Refuse(new CraftRefusal(CraftRefusalKind.ValueOutOfRange, socket.ContainedDefinitionId));
                return false;
            }
        }

        if (sockets.Length == 0)
        {
            return Remove(InstancePropertyKind.Sockets);
        }

        var builder = new ItemInstancePayloadBuilder();
        _ = builder.AddSockets(sockets);
        return Write(InstancePropertyKind.Sockets, builder.Fields[0].Body.Span);
    }

    /// <summary>
    /// Drops one field. A kind the item does not carry is a no-op rather than a refusal, and an
    /// UNREGISTERED kind is refused exactly as a write is, so a craft cannot strip a field only a later
    /// build understands.
    /// </summary>
    /// <param name="kind">The property kind id.</param>
    /// <returns>Whether the craft is still fine.</returns>
    public bool Remove(ushort kind)
    {
        if (IsRefused)
        {
            return false;
        }

        if (CraftStandingRules.RefuseCorrupted(ref this) is not null)
        {
            return false;
        }

        if (!_registry.TryGet(kind, out _))
        {
            _ = Refuse(new CraftRefusal(CraftRefusalKind.PropertyKindUnregistered, kind));
            return false;
        }

        int index = IndexOf(kind);
        if (index < 0)
        {
            return true;
        }

        _set.Length -= FieldSize(kind, _set.Fields[index].BodyLength);
        _set.Fields.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// The canonical bytes this craft produced, through the ONE encoder the format has. A refused craft
    /// answers false and an empty array, because the durable bytes are what the target keeps.
    /// </summary>
    /// <param name="payload">The re-encoded payload, or an empty array when the craft refused.</param>
    public readonly bool TryEncode(out byte[] payload)
    {
        if (IsRefused)
        {
            payload = [];
            return false;
        }

        var builder = new ItemInstancePayloadBuilder();
        for (int index = 0; index < _set.Fields.Count; index++)
        {
            _ = builder.Add(_set.Fields[index].Kind, BodyAt(index));
        }

        payload = builder.ToArray();
        return true;
    }

    /// <summary>
    /// The one write door, which is where all four of spec 10.5's powers are refused and where standing
    /// rule 1 sits.
    /// <para>
    /// <b>A corrupted item refuses HERE rather than in fourteen primitives</b>, because "every primitive
    /// that writes any part of the payload" IS this method, so the rule covers an <c>ICraftOperation</c>
    /// nobody has written yet and cannot be forgotten by a fifteenth primitive.
    /// </para>
    /// </summary>
    bool Write(ushort kind, scoped ReadOnlySpan<byte> body)
    {
        if (IsRefused)
        {
            return false;
        }

        if (CraftStandingRules.RefuseCorrupted(ref this) is not null)
        {
            return false;
        }

        if (!_registry.TryGet(kind, out _))
        {
            _ = Refuse(new CraftRefusal(CraftRefusalKind.PropertyKindUnregistered, kind));
            return false;
        }

        int index = IndexOf(kind);
        int length = _set.Length + FieldSize(kind, body.Length)
            - (index < 0 ? 0 : FieldSize(kind, _set.Fields[index].BodyLength));
        if (length > ItemInstancePayload.MaxInstancePayloadBytes)
        {
            _ = Refuse(new CraftRefusal(CraftRefusalKind.PayloadTooLong, kind));
            return false;
        }

        var written = new CraftField(kind, 0, 0, body.ToArray());
        if (index >= 0)
        {
            _set.Fields[index] = written;
        }
        else
        {
            _set.Fields.Insert(Seat(kind), written);
        }

        _set.Length = length;
        return true;
    }

    /// <summary>Where a kind the item does not carry belongs, which keeps the list ascending.</summary>
    readonly int Seat(ushort kind)
    {
        int seat = 0;
        while (seat < _set.Fields.Count && _set.Fields[seat].Kind < kind)
        {
            seat++;
        }

        return seat;
    }

    /// <summary>The index of one kind, or -1. The list is short and ascending, so a scan is the search.</summary>
    readonly int IndexOf(ushort kind)
    {
        for (int index = 0; index < _set.Fields.Count; index++)
        {
            if (_set.Fields[index].Kind == kind)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>One field's body, from the stored span when nobody wrote it and from the write when they did.</summary>
    readonly ReadOnlySpan<byte> BodyAt(int index)
    {
        CraftField field = _set.Fields[index];
        return field.Body ?? _source.Slice(field.SourceStart, field.SourceLength);
    }

    /// <summary>What one field costs on the wire, kind varint and length varint included.</summary>
    static int FieldSize(ushort kind, int bodyLength)
        => ContentVarint.Size(kind) + ContentVarint.Size((uint)bodyLength) + bodyLength;
}
