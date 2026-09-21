using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// The typed view over the engine <c>item</c> type of spec 9.1, and the ONLY typed view in the catalog. The
/// generic read side is <see cref="ContentRow"/>, which is right for an editor, a diff and a game type the
/// engine has never heard of, and wrong for the hot item fields. <c>stackable</c> and <c>max_stack</c> are
/// read on every merge test, <c>value</c> on every price read, <c>durability_max</c> at every generation and
/// <c>socket_max</c> in the container validator. A field-by-name walk over a value list is not what those
/// paths budget for.
/// <para>
/// A <c>ref struct</c> over the body span, with the five hot fields decoded at construction. It is the ENGINE
/// item type only, which is what makes a fixed walk legal: the schema is spec 3.3's, and a game cannot add a
/// field to it. Nothing else gets a typed view, because a typed view over a schema the engine does not own
/// could not be written.
/// </para>
/// <para>
/// <b>Spec 9.1 says "known offsets", and in a positional varint row the offsets are NOT constant.</b> So this
/// walks the preceding fields with length skips instead: one pass, no branch on a name, no allocation. The
/// next reader will otherwise try to hard-code an offset, which the first row with a two-byte <c>max_stack</c>
/// would break.
/// </para>
/// <para>
/// The body arrives as its backing ARRAY plus a range rather than as a span, because <see cref="Key"/> is a
/// <see cref="ContentKey"/> over those same bytes (spec 9.1: every row body opens with its own key, so an
/// id-to-key read is the row read one varint further in) and a span cannot produce one without copying.
/// </para>
/// </summary>
public readonly ref struct ItemRow
{
    ItemRow(
        bool stackable,
        int maxStack,
        int value,
        int durabilityMax,
        int socketMax,
        ContentKey key,
        bool isRetired)
    {
        Stackable = stackable;
        MaxStack = maxStack;
        Value = value;
        DurabilityMax = durabilityMax;
        SocketMax = socketMax;
        Key = key;
        IsRetired = isRetired;
    }

    /// <summary>Whether several of the item occupy one slot.</summary>
    public bool Stackable { get; }

    /// <summary>The largest stack one slot holds.</summary>
    public int MaxStack { get; }

    /// <summary>The item's authored value in the item schema's scaled units.</summary>
    public int Value { get; }

    /// <summary>The durability a fresh instance starts at. 0 means the base has no durability.</summary>
    public int DurabilityMax { get; }

    /// <summary>The socket CAP, not the authored set. 0 means the base takes no sockets.</summary>
    public int SocketMax { get; }

    /// <summary>The row's key, a slice of the loaded bytes rather than a materialised string.</summary>
    public ContentKey Key { get; }

    /// <summary>The retired bit, which the row TABLE owns rather than the body (spec 7.3).</summary>
    public bool IsRetired { get; }

    /// <summary>Decodes a whole buffer as one item body.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    public static bool TryDecode(byte[] body, bool isRetired, out ItemRow row)
    {
        ArgumentNullException.ThrowIfNull(body);
        return TryDecode(body, 0, body.Length, isRetired, out row);
    }

    /// <summary>
    /// Decodes one item body out of a larger buffer, which is how the loaded bodies blob holds it.
    /// <para>
    /// TOTAL, like every decoder here: a malformed body answers false rather than throwing, because the bytes
    /// arrived from a remote peer. The RANGE is a different matter and throws, because an offset outside the
    /// buffer comes from the caller's own table rather than from the wire. The reason token belongs to the
    /// generic <see cref="IContentRowCodec.TryDecode"/> path, which is what reports WHY a row is malformed.
    /// </para>
    /// </summary>
    /// <param name="body">The buffer holding the body, usually a whole type's concatenated bodies.</param>
    /// <param name="start">The body's offset into that buffer.</param>
    /// <param name="length">The body's length.</param>
    /// <param name="isRetired">The retired bit, which comes from the row table rather than the body.</param>
    /// <param name="row">The decoded view, or the default on a malformed body.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside <paramref name="body"/>.</exception>
    public static bool TryDecode(byte[] body, int start, int length, bool isRetired, out ItemRow row)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + (long)length, body.Length);

        row = default;
        ReadOnlySpan<byte> span = body.AsSpan(start, length);
        int offset = 0;

        if (!ContentVarint.TryRead(span, ref offset, out uint keyLength, out _))
        {
            return false;
        }

        if (keyLength > (uint)(span.Length - offset))
        {
            return false;
        }

        int keyStart = offset;
        offset += (int)keyLength;

        if (!SkipTagList(span, ref offset)
            || !TryReadFlag(span, ref offset, out bool stackable)
            || !ContentVarint.TryRead(span, ref offset, out uint maxStack, out _)
            || !TryReadFlag(span, ref offset, out _)                            // tradable
            || !ContentVarint.TryRead(span, ref offset, out uint value, out _)
            || !SkipLengthPrefixed(span, ref offset)                            // icon
            || !SkipLengthPrefixed(span, ref offset)                            // mesh
            || !SkipLengthPrefixed(span, ref offset)                            // held_mesh
            || !ContentVarint.TryRead(span, ref offset, out _, out _)           // ground_pose
            || !ContentVarint.TryRead(span, ref offset, out _, out _)           // icon_tilt
            || !ContentVarint.TryRead(span, ref offset, out _, out _)           // icon_spin
            || !ContentVarint.TryRead(span, ref offset, out uint durabilityMax, out _)
            || !ContentVarint.TryRead(span, ref offset, out uint socketMax, out _)
            || !ContentVarint.TryRead(span, ref offset, out _, out _))          // equip_profile
        {
            return false;
        }

        // The walk above is exactly ItemContentType.BaselineFieldCount fields, the list the type first
        // shipped with, and everything past this point is ContentRowTailRule's tail. A body that ends HERE
        // is a row written before the schema gained its appended fields, or one written after it that sets
        // none of them, which the canonical short encode makes the same bytes. A body that carries more reads
        // them, and one that has not landed exactly on its end after that is not an item row, whatever the
        // fields before it read as. Ending anywhere EARLIER is still refused above, field by field, which is
        // the half of the rule that keeps a corrupt body from passing as a short one.
        if (offset != span.Length
            && (!ContentVarint.TryRead(span, ref offset, out _, out _)          // category
                || offset != span.Length))
        {
            return false;
        }

        row = new ItemRow(
            stackable,
            unchecked((int)maxStack),
            unchecked((int)value),
            unchecked((int)durabilityMax),
            unchecked((int)socketMax),
            new ContentKey(body, start + keyStart, (int)keyLength),
            isRetired);
        return true;
    }

    /// <summary>A bool field, one byte, 0 or 1 and nothing else, which is the canonical form.</summary>
    static bool TryReadFlag(ReadOnlySpan<byte> body, ref int offset, out bool value)
    {
        if (offset >= body.Length || body[offset] > 1)
        {
            value = false;
            return false;
        }

        value = body[offset++] != 0;
        return true;
    }

    /// <summary>A tag list: a varint count then that many varint ids, none of which this view reads.</summary>
    static bool SkipTagList(ReadOnlySpan<byte> body, ref int offset)
    {
        if (!ContentVarint.TryRead(body, ref offset, out uint count, out _))
        {
            return false;
        }

        for (uint i = 0; i < count; i++)
        {
            if (!ContentVarint.TryRead(body, ref offset, out _, out _))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An opaque field: a varint length then that many bytes.</summary>
    static bool SkipLengthPrefixed(ReadOnlySpan<byte> body, ref int offset)
    {
        if (!ContentVarint.TryRead(body, ref offset, out uint length, out _))
        {
            return false;
        }

        if (length > (uint)(body.Length - offset))
        {
            return false;
        }

        offset += (int)length;
        return true;
    }
}
