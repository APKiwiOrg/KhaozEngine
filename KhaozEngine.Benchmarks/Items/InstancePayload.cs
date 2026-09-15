using System;
using System.Buffers.Binary;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>Property kind ids, spec 3.3. Only the kinds this spike encodes are named.</summary>
internal static class InstanceKinds
{
    internal const ushort Flags = 1;
    internal const ushort ItemLevel = 2;
    internal const ushort Quality = 3;
    internal const ushort Charges = 4;
    internal const ushort Durability = 5;
    internal const ushort BoundTo = 6;
    internal const ushort Materials = 7;
    internal const ushort Tier = 8;
    internal const ushort Identification = 128;
    internal const ushort UniqueTemplate = 129;
    internal const ushort Rarity = 130;
    internal const ushort Affixes = 131;
    internal const ushort Sockets = 132;
    internal const ushort Enchantments = 133;
    internal const ushort RareName = 134;

    /// <summary>Kinds 4, 5 and 6 are OwnerOnly, spec 3.3. Everything else this spike writes is Everyone.</summary>
    internal static bool IsOwnerOnly(ushort kind) => kind is Charges or Durability or BoundTo;
}

internal readonly record struct PayloadField(ushort Kind, int BodyStart, int BodyLength)
{
    internal int FieldStart => BodyStart - Varint.Size((ulong)Kind) - Varint.Size((ulong)(uint)BodyLength);
    internal int FieldLength => BodyStart + BodyLength - FieldStart;
}

/// <summary>
/// The tagged field sequence of contracts 9.1 and spec 3.2. The encoder is canonical by construction
/// (ascending kind, no duplicate, minimal varints) and the decoder NEVER throws: it answers false plus
/// one of the closed reason tokens of contracts 9.7.
/// </summary>
internal static class InstancePayload
{
    internal const int MaximumPayloadBytes = 512;
    internal const int MaximumFields = 24;

    internal static int WriteField(Span<byte> destination, ushort kind, ReadOnlySpan<byte> body)
    {
        int written = Varint.Write(destination, kind);
        written += Varint.Write(destination[written..], (ulong)(uint)body.Length);
        body.CopyTo(destination[written..]);
        return written + body.Length;
    }

    internal static int FieldSize(ushort kind, int bodyLength)
        => Varint.Size((ulong)kind) + Varint.Size((ulong)(uint)bodyLength) + bodyLength;

    internal static bool TryDecode(
        ReadOnlySpan<byte> payload,
        Span<PayloadField> fields,
        out int fieldCount,
        out string? reason)
        => TryDecode(payload, fields, nested: false, out fieldCount, out reason);

    private static bool TryDecode(
        ReadOnlySpan<byte> payload,
        Span<PayloadField> fields,
        bool nested,
        out int fieldCount,
        out string? reason)
    {
        fieldCount = 0;
        reason = null;
        if (payload.Length > MaximumPayloadBytes)
        {
            reason = "payload-too-long";
            return false;
        }

        int offset = 0;
        long previousKind = -1;
        while (offset < payload.Length)
        {
            if (!Varint.TryRead(payload, ref offset, Varint.MaximumBytes32, out ulong rawKind, out reason)) return false;
            if (rawKind == 0 || rawKind > ushort.MaxValue)
            {
                reason = "field-malformed";
                return false;
            }

            if ((long)rawKind == previousKind)
            {
                reason = "kind-duplicate";
                return false;
            }

            if ((long)rawKind < previousKind)
            {
                reason = "kind-out-of-order";
                return false;
            }

            previousKind = (long)rawKind;
            if (!Varint.TryRead(payload, ref offset, Varint.MaximumBytes32, out ulong rawLength, out reason)) return false;
            if (rawLength > (ulong)(payload.Length - offset))
            {
                reason = "field-truncated";
                return false;
            }

            var kind = (ushort)rawKind;
            int bodyLength = (int)rawLength;
            if (nested && kind == InstanceKinds.Sockets)
            {
                reason = "socket-nesting";
                return false;
            }

            if (!CheckShape(kind, payload.Slice(offset, bodyLength), out reason)) return false;
            if (fieldCount == fields.Length)
            {
                reason = "field-malformed";
                return false;
            }

            fields[fieldCount++] = new PayloadField(kind, offset, bodyLength);
            offset += bodyLength;
        }

        return true;
    }

    /// <summary>
    /// The known-kind shape check of contracts 9.7's <c>field-malformed</c>. An unknown kind is kept
    /// verbatim and is never inspected, which is contracts 9.4.
    /// </summary>
    private static bool CheckShape(ushort kind, ReadOnlySpan<byte> body, out string? reason)
    {
        reason = null;
        int offset = 0;
        switch (kind)
        {
            case InstanceKinds.Flags:
            case InstanceKinds.ItemLevel:
            case InstanceKinds.Quality:
            case InstanceKinds.BoundTo:
            case InstanceKinds.UniqueTemplate:
                return ReadScalars(body, ref offset, 1, out reason) && AtEnd(body, offset, out reason);
            case InstanceKinds.Charges:
            case InstanceKinds.Durability:
                return ReadScalars(body, ref offset, 2, out reason) && AtEnd(body, offset, out reason);
            case InstanceKinds.Rarity:
                if (body.Length != 1) reason = "field-malformed";
                return reason is null;
            case InstanceKinds.Identification:
                if (body.Length < 2 || body[0] > 1)
                {
                    reason = "field-malformed";
                    return false;
                }

                offset = 1;
                return ReadScalars(body, ref offset, 1, out reason) && AtEnd(body, offset, out reason);
            case InstanceKinds.Affixes:
            case InstanceKinds.Enchantments:
                return CheckAffixes(body, out reason);
            case InstanceKinds.Sockets:
                return CheckSockets(body, out reason);
            case InstanceKinds.RareName:
                return CheckRareName(body, out reason);
            default:
                return true;
        }
    }

    private static bool CheckAffixes(ReadOnlySpan<byte> body, out string? reason)
    {
        reason = null;
        if (body.Length < 1)
        {
            reason = "field-malformed";
            return false;
        }

        int count = body[0];
        int offset = 1;
        long previousMod = -1;
        for (int entry = 0; entry < count; entry++)
        {
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong modId, out reason)) return false;
            if (modId == 0 || (long)modId < previousMod)
            {
                reason = "field-malformed";
                return false;
            }

            previousMod = (long)modId;
            if (offset >= body.Length || body[offset] == 0)
            {
                reason = "field-malformed";
                return false;
            }

            offset++;
            if (offset + 2 > body.Length)
            {
                reason = "field-truncated";
                return false;
            }

            offset += 2;
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong flags, out reason)) return false;
            if (flags != 0)
            {
                reason = "field-malformed";
                return false;
            }
        }

        return AtEnd(body, offset, out reason);
    }

    private static bool CheckSockets(ReadOnlySpan<byte> body, out string? reason)
    {
        reason = null;
        int offset = 0;
        Span<PayloadField> nestedFields = stackalloc PayloadField[MaximumFields];
        if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong count, out reason)) return false;
        for (ulong entry = 0; entry < count; entry++)
        {
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out _, out reason)) return false;
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out _, out reason)) return false;
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes64, out _, out reason)) return false;
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong nestedLength, out reason)) return false;
            if (nestedLength > (ulong)(body.Length - offset))
            {
                reason = "field-truncated";
                return false;
            }

            if (!TryDecode(body.Slice(offset, (int)nestedLength), nestedFields, nested: true, out _, out reason)) return false;
            offset += (int)nestedLength;
        }

        return AtEnd(body, offset, out reason);
    }

    private static bool CheckRareName(ReadOnlySpan<byte> body, out string? reason)
    {
        reason = null;
        int offset = 0;
        if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out _, out reason)) return false;
        if (offset >= body.Length)
        {
            reason = "field-truncated";
            return false;
        }

        int words = body[offset++];
        for (int word = 0; word < words; word++)
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out _, out reason)) return false;
        return AtEnd(body, offset, out reason);
    }

    private static bool ReadScalars(ReadOnlySpan<byte> body, ref int offset, int count, out string? reason)
    {
        reason = null;
        for (int scalar = 0; scalar < count; scalar++)
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes64, out _, out reason)) return false;
        return true;
    }

    private static bool AtEnd(ReadOnlySpan<byte> body, int offset, out string? reason)
    {
        reason = offset == body.Length ? null : "field-malformed";
        return reason is null;
    }

    /// <summary>
    /// Spec 7.4's one visibility function, as the forward pass over retained runs it describes: fields
    /// are already ascending and each is length prefixed, so the public view is a copy of contiguous
    /// ranges with no decode and no re-sort.
    /// </summary>
    internal static int PublicView(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        Span<PayloadField> fields = stackalloc PayloadField[MaximumFields];
        if (!TryDecode(payload, fields, out int fieldCount, out _)) return -1;
        int written = 0;
        int runStart = -1;
        int runEnd = -1;
        for (int index = 0; index < fieldCount; index++)
        {
            PayloadField field = fields[index];
            if (InstanceKinds.IsOwnerOnly(field.Kind))
            {
                written += Flush(payload, destination, written, ref runStart, ref runEnd);
                continue;
            }

            if (runStart < 0) runStart = field.FieldStart;
            runEnd = field.BodyStart + field.BodyLength;
        }

        written += Flush(payload, destination, written, ref runStart, ref runEnd);
        return written;
    }

    private static int Flush(ReadOnlySpan<byte> payload, Span<byte> destination, int written, ref int runStart, ref int runEnd)
    {
        if (runStart < 0) return 0;
        int length = runEnd - runStart;
        payload.Slice(runStart, length).CopyTo(destination[written..]);
        runStart = -1;
        runEnd = -1;
        return length;
    }

    internal static int WriteAffixEntry(Span<byte> destination, int modId, byte tier, ushort position)
    {
        int written = Varint.Write(destination, (ulong)(uint)modId);
        destination[written++] = tier;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[written..], position);
        written += 2;
        destination[written++] = 0;
        return written;
    }

    internal static int AffixEntrySize(int modId) => Varint.Size(modId) + 4;
}
