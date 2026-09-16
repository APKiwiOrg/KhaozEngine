using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// Spec 9.5's <c>item-generated</c> body, written on the commit that first seats a generated item somewhere
/// durable.
/// <code>
/// [EventVersion: byte = 1]
/// [BaseId: varint int32]
/// [InstanceId: varint uint64]
/// [RarityId: byte]
/// [ContentVersion: varint int32]
/// [SourceKind: byte]              // 1 drop, 2 craft, 3 admin grant, 4 migration, 5 to 255 game
/// [SourceId: varint int32]        // the loot table, currency or migration id, 0 when none
/// [PayloadLength: varint int32]
/// [Payload: PayloadLength bytes]  // the FULL payload, exactly as encoded
/// </code>
/// <para>
/// <b>It records the RESOLVED item and never a seed, a state or a draw index</b>, which is contracts 14.3
/// verbatim and is also the only shape that survives the journal's replay model: a replay returns the
/// original receipt rather than re-running anything, so an event carrying a seed would have to be re-rolled
/// to mean anything and a re-roll on replay is a duplication bug wearing a hat.
/// </para>
/// <para>
/// <b>The FULL payload rather than a reference to the page.</b> A page is rewritten whole on every later
/// commit, so the page bytes at the moment of generation are not recoverable from anything except this
/// event. A rare of spec 3.8 is 58 payload bytes plus fourteen of header, so the event is about 72 bytes and
/// a drop burst of twenty rares is about 1.4 KB against the 128 events per operation cap.
/// </para>
/// <para>
/// <b>It lives here rather than in <c>KhaozEngine.ItemInstances</c>, and the generator never writes a
/// journal byte.</b> The generator hands back a <see cref="GenerationResult"/> and the caller that composes
/// the commit encodes it, which keeps <c>Foundation</c> free of event-shaped types. This package is where
/// <see cref="ItemInstanceEvents"/> already names the event and where every other body lives.
/// </para>
/// </summary>
/// <param name="BaseId">The <c>item</c> row the item was made from.</param>
/// <param name="InstanceId">The durable instance id, 0 for a plain stack.</param>
/// <param name="RarityId">The <c>rarity_rule</c> row seated, 0 for an item with no rarity.</param>
/// <param name="ContentVersion">The version the roll read, which is what makes "what did this item look
/// like when it dropped" answerable against the right catalog rather than against today's.</param>
/// <param name="SourceKind">Why it exists: a drop, a craft, an admin grant, a migration, or a game's own
/// kind at 5 and above.</param>
/// <param name="SourceId">The loot table, currency or migration row that caused it, 0 when none.</param>
/// <param name="Payload">The full canonical payload, exactly as encoded.</param>
public readonly record struct ItemGeneratedEvent(
    int BaseId,
    long InstanceId,
    byte RarityId,
    int ContentVersion,
    byte SourceKind,
    int SourceId,
    ReadOnlyMemory<byte> Payload)
{
    /// <summary>The only body version this build writes, and the only one it reads.</summary>
    public const byte BodyVersion = 1;

    /// <summary>An item that dropped.</summary>
    public const byte SourceDrop = 1;

    /// <summary>An item a craft produced.</summary>
    public const byte SourceCraft = 2;

    /// <summary>An item an operator granted.</summary>
    public const byte SourceAdminGrant = 3;

    /// <summary>An item a migration minted.</summary>
    public const byte SourceMigration = 4;

    /// <summary>The first source kind a GAME may define. The engine owns 1 to 4 and never takes another.</summary>
    public const byte FirstGameSourceKind = 5;

    /// <summary>The body ended before a field it declared.</summary>
    public const string Truncated = "event-truncated";

    /// <summary>The body's first byte is a version this build does not know.</summary>
    public const string UnknownVersion = "event-version";

    /// <summary>A varint ran off the end of the body or was written non-minimally.</summary>
    public const string MalformedVarint = "event-varint";

    /// <summary>The declared payload length does not match what is left of the body.</summary>
    public const string PayloadLength = "event-payload-length";

    /// <summary>A value the field's width cannot hold, which is a writer from another build.</summary>
    public const string FieldRange = "event-field-range";

    /// <summary>The bytes <see cref="Write"/> writes.</summary>
    public int ByteCount
        => 1
            + ContentVarint.Size((uint)BaseId)
            + ContentVarint.SizeUInt64((ulong)InstanceId)
            + 1
            + ContentVarint.Size((uint)ContentVersion)
            + 1
            + ContentVarint.Size((uint)SourceId)
            + ContentVarint.Size((uint)Payload.Length)
            + Payload.Length;

    /// <summary>
    /// The event one rolled item writes. The payload is carried by REFERENCE: the generator already owns
    /// those bytes and nothing on this path re-encodes them, which is what keeps the event and the stored
    /// page byte identical by construction rather than by inspection.
    /// </summary>
    /// <param name="result">The rolled item.</param>
    /// <param name="sourceKind">Why it exists.</param>
    /// <param name="sourceId">The loot table, currency or migration row that caused it, 0 when none.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rarity id does not fit kind 130's byte, the source
    /// kind is 0, or an id is negative.</exception>
    public static ItemGeneratedEvent From(in GenerationResult result, byte sourceKind, int sourceId = 0)
    {
        if (result.RarityId is < 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(result),
                result.RarityId,
                "A rarity rule id runs 0 to 255, because kind 130 stores it as a single byte and this event copies that width.");
        }

        if (sourceKind == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceKind),
                sourceKind,
                "Source kind 0 is reserved and names nothing. The engine owns 1 to 4 and a game takes 5 and above.");
        }

        if (result.BaseId < 0 || sourceId < 0 || result.ContentVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(result),
                "A base id, a source id and a content version are written as unsigned varints and are never negative.");
        }

        return new ItemGeneratedEvent(
            result.BaseId,
            result.InstanceId,
            (byte)result.RarityId,
            result.ContentVersion,
            sourceKind,
            sourceId,
            result.Payload);
    }

    /// <summary>Writes the body and answers the bytes written.</summary>
    /// <param name="destination">At least <see cref="ByteCount"/> bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short. That is a caller
    /// error rather than a fact about any bytes, which is why it throws while
    /// <see cref="TryRead"/> never does.</exception>
    public int Write(Span<byte> destination)
    {
        if (destination.Length < ByteCount)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"An item-generated body needs {ByteCount} bytes and the destination holds {destination.Length}."),
                nameof(destination));
        }

        destination[0] = BodyVersion;
        int written = 1;
        written += ContentVarint.Write(destination[written..], (uint)BaseId);
        written += ContentVarint.WriteUInt64(destination[written..], (ulong)InstanceId);
        destination[written++] = RarityId;
        written += ContentVarint.Write(destination[written..], (uint)ContentVersion);
        destination[written++] = SourceKind;
        written += ContentVarint.Write(destination[written..], (uint)SourceId);
        written += ContentVarint.Write(destination[written..], (uint)Payload.Length);
        Payload.Span.CopyTo(destination[written..]);
        return written + Payload.Length;
    }

    /// <summary>The body as a fresh array, which is what a <c>JournalEvent</c> takes.</summary>
    public byte[] ToArray()
    {
        byte[] body = new byte[ByteCount];
        _ = Write(body);
        return body;
    }

    /// <summary>
    /// Reads a body back. <b>It NEVER throws</b>, because the bytes come from a store: it answers false plus
    /// a reason instead. Spec 9.5's whole argument for carrying the full payload is that the page is
    /// rewritten whole on every later commit, so this event is the only durable record of what the item
    /// looked like when it dropped, and a record nothing can read back is not a record.
    /// </summary>
    /// <param name="source">The stored body.</param>
    /// <param name="value">The decoded event, whose payload is a SLICE of <paramref name="source"/> rather
    /// than a copy. It is <c>default</c> when the answer is false.</param>
    /// <param name="reason">Why it was refused, null when the answer is true.</param>
    public static bool TryRead(ReadOnlyMemory<byte> source, out ItemGeneratedEvent value, out string? reason)
    {
        value = default;
        ReadOnlySpan<byte> bytes = source.Span;
        if (bytes.Length == 0)
        {
            reason = Truncated;
            return false;
        }

        if (bytes[0] != BodyVersion)
        {
            reason = UnknownVersion;
            return false;
        }

        int offset = 1;
        if (!ContentVarint.TryRead(bytes, ref offset, out uint baseId, out _)
            || !ContentVarint.TryReadUInt64(bytes, ref offset, out ulong instanceId, out _))
        {
            reason = MalformedVarint;
            return false;
        }

        if (offset >= bytes.Length)
        {
            reason = Truncated;
            return false;
        }

        byte rarityId = bytes[offset++];
        if (!ContentVarint.TryRead(bytes, ref offset, out uint contentVersion, out _))
        {
            reason = MalformedVarint;
            return false;
        }

        if (offset >= bytes.Length)
        {
            reason = Truncated;
            return false;
        }

        byte sourceKind = bytes[offset++];
        if (!ContentVarint.TryRead(bytes, ref offset, out uint sourceId, out _)
            || !ContentVarint.TryRead(bytes, ref offset, out uint payloadLength, out _))
        {
            reason = MalformedVarint;
            return false;
        }

        // The declared length has to be exactly what is left: a short one leaves trailing bytes nothing
        // accounts for and a long one runs past the body, and both are a writer this build cannot read.
        if (payloadLength != (uint)(bytes.Length - offset))
        {
            reason = PayloadLength;
            return false;
        }

        if (baseId > int.MaxValue || contentVersion > int.MaxValue || sourceId > int.MaxValue
            || sourceKind == 0)
        {
            reason = FieldRange;
            return false;
        }

        value = new ItemGeneratedEvent(
            (int)baseId,
            (long)instanceId,
            rarityId,
            (int)contentVersion,
            sourceKind,
            (int)sourceId,
            source[offset..]);
        reason = null;
        return true;
    }
}
