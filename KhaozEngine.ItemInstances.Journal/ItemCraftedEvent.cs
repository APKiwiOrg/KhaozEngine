using System;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// Spec 10.6's <c>item-crafted</c> body, written on the commit that applies a craft.
/// <code>
/// [EventVersion: byte = 1]
/// [CurrencyId: varint int32][InstanceId: varint uint64]
/// [ContentVersion: varint int32]
/// [BeforeLength: varint int32][Before: bytes]     // the payload as it stood
/// [AfterLength: varint int32][After: bytes]       // the payload as it stands
/// </code>
/// <para>
/// <b>It carries BOTH payloads and that is the whole point.</b> A page is rewritten whole on every later
/// commit, so the bytes a craft replaced are not recoverable from anything except this event. On spec 3.8's
/// rare the body is about 127 bytes (<c>1 + 2 + 5 + 1 + 1 + 58 + 1 + 58</c>) of which the before costs 59,
/// and it is worth every one of them: without it the durable record says that an item changed and cannot say
/// what it changed from, which is the exact failure a consumer audit already has.
/// </para>
/// <para>
/// <b>A REFUSED craft writes nothing durable and never reaches the journal</b>, so there is no refusal field
/// here and never will be. The client is answered with the <c>CraftRefusal</c> naming the guard or the
/// primitive that refused, and the journal hears nothing at all.
/// </para>
/// <para>
/// <b><see cref="CurrencyId"/> is the <c>crafting_currency</c> ROW and not the item spent.</b> The row is
/// what ran, and the item base one run consumes is the operation's own <c>DefinitionId</c>, which a free
/// bench operation leaves at 0. The two are different questions and the event answers the first one.
/// </para>
/// <para>
/// <b>It lives here rather than in <c>KhaozEngine.ItemInstances</c>, and the crafting framework never writes
/// a journal byte.</b> The executor hands back a re-encoded payload and the caller that composes the commit
/// encodes it, which keeps <c>Foundation</c> free of event-shaped types, exactly as
/// <see cref="ItemGeneratedEvent"/> does for the generator.
/// </para>
/// </summary>
/// <param name="CurrencyId">The <c>crafting_currency</c> row that ran.</param>
/// <param name="InstanceId">The target's durable instance id, which a craft always has because a craft
/// rewrites an item that already exists.</param>
/// <param name="ContentVersion">The version the craft read, which is what makes "what did this craft do"
/// answerable against the right catalog rather than against today's.</param>
/// <param name="Before">The canonical payload as it stood before the craft.</param>
/// <param name="After">The canonical payload as it stands after it.</param>
public readonly record struct ItemCraftedEvent(
    int CurrencyId,
    long InstanceId,
    int ContentVersion,
    ReadOnlyMemory<byte> Before,
    ReadOnlyMemory<byte> After)
{
    /// <summary>The only body version this build writes, and the only one it reads.</summary>
    public const byte BodyVersion = 1;

    /// <summary>The body ended before a field it declared.</summary>
    public const string Truncated = "event-truncated";

    /// <summary>The body's first byte is a version this build does not know.</summary>
    public const string UnknownVersion = "event-version";

    /// <summary>A varint ran off the end of the body or was written non-minimally.</summary>
    public const string MalformedVarint = "event-varint";

    /// <summary>A declared payload length does not fit what is left of the body.</summary>
    public const string PayloadLength = "event-payload-length";

    /// <summary>A value the field's width cannot hold, or an id that names nothing, which is a writer from
    /// another build.</summary>
    public const string FieldRange = "event-field-range";

    /// <summary>The bytes <see cref="Write"/> writes.</summary>
    public int ByteCount
        => 1
            + ContentVarint.Size((uint)CurrencyId)
            + ContentVarint.SizeUInt64((ulong)InstanceId)
            + ContentVarint.Size((uint)ContentVersion)
            + ContentVarint.Size((uint)Before.Length)
            + Before.Length
            + ContentVarint.Size((uint)After.Length)
            + After.Length;

    /// <summary>
    /// The event one applied craft writes. Both payloads are carried by REFERENCE: the stored bytes and the
    /// working copy's re-encoded bytes already exist, and nothing on this path re-encodes either, which is
    /// what keeps the event and the page byte identical by construction rather than by inspection.
    /// </summary>
    /// <param name="plan">The currency that ran, which is the ONE place its row id lives.</param>
    /// <param name="instanceId">The target's instance id.</param>
    /// <param name="contentVersion">The version the craft read.</param>
    /// <param name="before">The payload as it stood.</param>
    /// <param name="after">The payload as it stands.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The instance id is 0, which is a plain stack and not a
    /// craftable item, the currency row id is not positive, or the content version is negative.</exception>
    public static ItemCraftedEvent From(
        CraftPlan plan,
        long instanceId,
        int contentVersion,
        ReadOnlyMemory<byte> before,
        ReadOnlyMemory<byte> after)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (instanceId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(instanceId),
                instanceId,
                "A craft targets an OWNED item, which always has an instance id. Instance 0 is a plain stack and there is nothing on it to craft.");
        }

        if (plan.CurrencyId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plan),
                plan.CurrencyId,
                "A craft runs a crafting_currency ROW, so the event names one. A free operation still has a row: what it spends nothing of is its consumed definition.");
        }

        if (contentVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentVersion),
                contentVersion,
                "A content version is written as an unsigned varint and is never negative.");
        }

        return new ItemCraftedEvent(plan.CurrencyId, instanceId, contentVersion, before, after);
    }

    /// <summary>Writes the body and answers the bytes written.</summary>
    /// <param name="destination">At least <see cref="ByteCount"/> bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short. That is a caller
    /// error rather than a fact about any bytes, which is why it throws while <see cref="TryRead"/> never
    /// does.</exception>
    public int Write(Span<byte> destination)
    {
        if (destination.Length < ByteCount)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"An item-crafted body needs {ByteCount} bytes and the destination holds {destination.Length}."),
                nameof(destination));
        }

        destination[0] = BodyVersion;
        int written = 1;
        written += ContentVarint.Write(destination[written..], (uint)CurrencyId);
        written += ContentVarint.WriteUInt64(destination[written..], (ulong)InstanceId);
        written += ContentVarint.Write(destination[written..], (uint)ContentVersion);
        written += ContentVarint.Write(destination[written..], (uint)Before.Length);
        Before.Span.CopyTo(destination[written..]);
        written += Before.Length;
        written += ContentVarint.Write(destination[written..], (uint)After.Length);
        After.Span.CopyTo(destination[written..]);
        return written + After.Length;
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
    /// a reason instead. The event is the only durable record of what a craft changed, and a record nothing
    /// can read back is not a record.
    /// </summary>
    /// <param name="source">The stored body.</param>
    /// <param name="value">The decoded event, whose payloads are SLICES of <paramref name="source"/> rather
    /// than copies. It is <c>default</c> when the answer is false.</param>
    /// <param name="reason">Why it was refused, null when the answer is true.</param>
    public static bool TryRead(ReadOnlyMemory<byte> source, out ItemCraftedEvent value, out string? reason)
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
        if (!ContentVarint.TryRead(bytes, ref offset, out uint currencyId, out _)
            || !ContentVarint.TryReadUInt64(bytes, ref offset, out ulong instanceId, out _)
            || !ContentVarint.TryRead(bytes, ref offset, out uint contentVersion, out _)
            || !ContentVarint.TryRead(bytes, ref offset, out uint beforeLength, out _))
        {
            reason = MalformedVarint;
            return false;
        }

        // The before has to leave room for the after's own length varint, so a length that merely fits what
        // is left is still a lie. Reading what fits would paper over a writer this build cannot read.
        if (beforeLength >= (uint)(bytes.Length - offset))
        {
            reason = PayloadLength;
            return false;
        }

        int beforeStart = offset;
        offset += (int)beforeLength;
        if (!ContentVarint.TryRead(bytes, ref offset, out uint afterLength, out _))
        {
            reason = MalformedVarint;
            return false;
        }

        // The after's declared length is exactly what is left: a short one leaves trailing bytes nothing
        // accounts for and a long one runs past the body.
        if (afterLength != (uint)(bytes.Length - offset))
        {
            reason = PayloadLength;
            return false;
        }

        // Instance 0 is a plain stack, which nothing crafts, and a currency id of 0 names no row. Both are a
        // body describing a craft that cannot have happened.
        if (currencyId is 0 or > int.MaxValue || contentVersion > int.MaxValue || instanceId == 0)
        {
            reason = FieldRange;
            return false;
        }

        value = new ItemCraftedEvent(
            (int)currencyId,
            (long)instanceId,
            (int)contentVersion,
            source.Slice(beforeStart, (int)beforeLength),
            source[offset..]);
        reason = null;
        return true;
    }
}
