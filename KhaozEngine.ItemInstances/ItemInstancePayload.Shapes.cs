using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The per-kind half of the decode, DERIVED from the registered <see cref="InstanceFieldShape"/> rather
/// than from a switch over the kinds this release happens to know.
/// <para>
/// That is the whole point of the shape. A walker that reads the registration finds every value in a field
/// without knowing what the kind MEANS, so a game kind at or above 1024 gets shape checking, the one level
/// nesting limit, remap and quarantine by declaring its shape and nothing else. The spike this was lifted
/// from hard-coded the kinds, and a hard-coded switch gives a game kind none of the four, silently.
/// </para>
/// </summary>
public static partial class ItemInstancePayload
{
    /// <summary>
    /// Checks one field's body against its registered shape, then against the kind's own codec. An
    /// unregistered kind is kept VERBATIM and never inspected, which is contracts 9.4 and what lets a
    /// client built against content build N read and re-save an item carrying a field only build N+1 knows.
    /// </summary>
    static bool CheckBody(
        InstancePropertyRegistry registry,
        ushort kind,
        ReadOnlySpan<byte> body,
        bool nested,
        out string? reason)
    {
        reason = null;
        if (!registry.TryGet(kind, out InstancePropertyRegistration? registration))
        {
            return true;
        }

        InstanceFieldShape shape = registration.Shape;

        // The one level limit of contracts 9.5, derived: a field that CARRIES a nested payload may not sit
        // INSIDE one. Keying it on the shape rather than on kind 132 is what makes the limit structural for
        // a game kind too, and recursion is the one way a 45 byte payload becomes a denial of service.
        if (nested && shape.Nests)
        {
            reason = InstancePayloadReason.SocketNesting;
            return false;
        }

        int offset = 0;
        if (!ReadSlots(registry, shape.Header.Span, body, ref offset, out reason))
        {
            return false;
        }

        if (shape.Count != InstanceCountWidth.None)
        {
            if (!ReadCount(shape.Count, body, ref offset, out uint count, out reason))
            {
                return false;
            }

            ReadOnlySpan<InstanceSlotKind> entry = shape.Entry.Span;
            for (uint index = 0; index < count; index++)
            {
                if (!ReadSlots(registry, entry, body, ref offset, out reason))
                {
                    return false;
                }
            }
        }

        // Bytes left over after the shape is spent are a field that does not match its own shape, which is
        // contracts 9.7's field-malformed rather than something to skip.
        if (offset != body.Length)
        {
            reason = InstancePayloadReason.FieldMalformed;
            return false;
        }

        return registration.Codec.TryValidate(body, out reason);
    }

    /// <summary>
    /// Walks one run of slots. A <see cref="InstanceSlotKind.Varint"/> is read at the FULL unsigned 64 bit
    /// width, because the shape declares a value's POSITION and not its width: kind 6's bound-to subject
    /// and a socket's contained instance id are both <c>uint64</c> (contracts 9.5), and a node prefixed
    /// instance id sets the high bit, so narrowing the read here would refuse a legal payload. Narrowing a
    /// particular kind's value is that kind's own codec's business.
    /// <para>
    /// <b>This walk, <c>InstanceValidator.ReadRun</c> and <c>InstanceRemapPass.ReadRun</c> are a SET of
    /// three.</b> They read the same four <see cref="InstanceSlotKind"/> members off the same shapes and
    /// differ only in what they do with what they find: this one discards the values and recurses
    /// STRUCTURALLY, the validator's keeps the values, because resolving a reference needs them, and
    /// answers <c>field-malformed</c> for every failure, because the bytes have already decoded here and a
    /// refusal there is defensive, and the pass's keeps them and writes them back. A fifth slot kind is
    /// added to ALL THREE or to none.
    /// </para>
    /// </summary>
    static bool ReadSlots(
        InstancePropertyRegistry registry,
        ReadOnlySpan<InstanceSlotKind> slots,
        ReadOnlySpan<byte> body,
        ref int offset,
        out string? reason)
    {
        reason = null;
        foreach (InstanceSlotKind slot in slots)
        {
            switch (slot)
            {
                case InstanceSlotKind.Varint:
                    if (!ContentVarint.TryReadUInt64(body, ref offset, out _, out reason))
                    {
                        return false;
                    }

                    break;

                case InstanceSlotKind.Byte:
                    if (offset >= body.Length)
                    {
                        reason = InstancePayloadReason.FieldTruncated;
                        return false;
                    }

                    offset++;
                    break;

                case InstanceSlotKind.Fixed2:
                    if (body.Length - offset < 2)
                    {
                        reason = InstancePayloadReason.FieldTruncated;
                        return false;
                    }

                    offset += 2;
                    break;

                case InstanceSlotKind.NestedPayload:
                    if (!ContentVarint.TryRead(body, ref offset, out uint nestedLength, out reason))
                    {
                        return false;
                    }

                    if (nestedLength > (uint)(body.Length - offset))
                    {
                        reason = InstancePayloadReason.FieldTruncated;
                        return false;
                    }

                    if (!Nested(registry, body.Slice(offset, (int)nestedLength), out reason))
                    {
                        return false;
                    }

                    offset += (int)nestedLength;
                    break;

                default:
                    reason = InstancePayloadReason.FieldMalformed;
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decodes a nested payload, which is a payload in this same format one level down. Its own span lives
    /// here rather than in the slot loop, so the allocation happens once per nested payload rather than
    /// once per slot.
    /// </summary>
    static bool Nested(InstancePropertyRegistry registry, ReadOnlySpan<byte> nested, out string? reason)
    {
        Span<PayloadField> fields = stackalloc PayloadField[MaxFields];
        return Walk(registry, nested, fields, nested: true, out _, out reason);
    }

    /// <summary>
    /// Reads a field's repeat count. Kind 132's is a VARINT and kind 131's is a BYTE and the difference is
    /// not an oversight: contracts 9.5 writes the socket count as a varint, so narrowing it would be a
    /// width change, and it would be invisible in the golden file because the worked example writes
    /// <c>01</c> and that is both.
    /// </summary>
    static bool ReadCount(
        InstanceCountWidth width,
        ReadOnlySpan<byte> body,
        ref int offset,
        out uint count,
        out string? reason)
    {
        switch (width)
        {
            case InstanceCountWidth.Byte:
                if (offset >= body.Length)
                {
                    count = 0;
                    reason = InstancePayloadReason.FieldTruncated;
                    return false;
                }

                count = body[offset++];
                reason = null;
                return true;

            case InstanceCountWidth.Varint:
                return ContentVarint.TryRead(body, ref offset, out count, out reason);

            default:
                count = 0;
                reason = InstancePayloadReason.FieldMalformed;
                return false;
        }
    }
}
