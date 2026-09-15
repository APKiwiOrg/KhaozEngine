using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// One remap rule's own bytes, contracts 8.4's encoding and not one byte more:
/// <c>[Sequence varint][IntroducedIn varint][TypeId uint16 LE][Kind byte][FromId varint][ToId varint]
/// [PayloadLength byte 0 to 64][Payload]</c>. A typical rule between realistic ids is 9 to 12 bytes.
/// <para>
/// Every varint here is UNSIGNED and none is zig-zagged. Contracts 15 declares content ids, counts and
/// version numbers unsigned, and all four of these fields are one of those, so a small id costs one byte.
/// </para>
/// <para>
/// <see cref="TryRead"/> is total: a malformed rule is false plus a stable reason token, and the offset is
/// left exactly where it was found so the caller can report the position that failed.
/// </para>
/// </summary>
public static class RemapRuleCodec
{
    /// <summary>A rule's fields run past the end of the body.</summary>
    public const string ReasonTruncated = "rule-truncated";

    /// <summary>The kind byte is not one of the four v1 kinds, which fails CLOSED and is never skipped.</summary>
    public const string ReasonKind = "rule-kind";

    /// <summary>The payload is longer than 64 bytes or is not the shape its kind declares.</summary>
    public const string ReasonPayload = "rule-payload";

    /// <summary>A varint field carries a value no rule field can hold.</summary>
    public const string ReasonRange = "rule-range";

    /// <summary>The bytes this rule takes, so a caller can size a body without writing it.</summary>
    public static int Size(RemapRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return ContentVarint.Size((uint)rule.Sequence)
            + ContentVarint.Size((uint)rule.IntroducedIn)
            + sizeof(ushort)
            + 1
            + ContentVarint.Size((uint)rule.FromId)
            + ContentVarint.Size((uint)rule.ToId)
            + 1
            + rule.PayloadLength;
    }

    /// <summary>Writes one rule and returns the bytes written.</summary>
    public static int Write(Span<byte> destination, RemapRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        int written = ContentVarint.Write(destination, (uint)rule.Sequence);
        written += ContentVarint.Write(destination[written..], (uint)rule.IntroducedIn);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[written..], rule.Type.Value);
        written += sizeof(ushort);
        destination[written++] = (byte)rule.Kind;
        written += ContentVarint.Write(destination[written..], (uint)rule.FromId);
        written += ContentVarint.Write(destination[written..], (uint)rule.ToId);
        destination[written++] = (byte)rule.PayloadLength;
        rule.Payload.CopyTo(destination[written..]);
        return written + rule.PayloadLength;
    }

    /// <summary>
    /// Reads one rule and advances <paramref name="offset"/> past it. Returns false with a reason on
    /// anything malformed, leaving the offset unmoved.
    /// </summary>
    public static bool TryRead(
        ReadOnlySpan<byte> source,
        ref int offset,
        [MaybeNullWhen(false)] out RemapRule rule,
        out string? reason)
    {
        rule = null;
        int start = offset;

        if (!TryReadField(source, ref offset, out int sequence, out reason)) { offset = start; return false; }
        if (!TryReadField(source, ref offset, out int introducedIn, out reason)) { offset = start; return false; }

        if (offset + sizeof(ushort) + 1 > source.Length) { offset = start; reason = ReasonTruncated; return false; }
        ushort typeId = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
        offset += sizeof(ushort);
        byte kind = source[offset++];
        if (!RemapRule.IsKnownKind(kind)) { offset = start; reason = ReasonKind; return false; }

        if (!TryReadField(source, ref offset, out int fromId, out reason)) { offset = start; return false; }
        if (!TryReadField(source, ref offset, out int toId, out reason)) { offset = start; return false; }

        if (offset >= source.Length) { offset = start; reason = ReasonTruncated; return false; }
        int payloadLength = source[offset++];
        if (payloadLength > RemapRule.MaxPayloadBytes) { offset = start; reason = ReasonPayload; return false; }
        if (offset + payloadLength > source.Length) { offset = start; reason = ReasonTruncated; return false; }

        ReadOnlySpan<byte> payload = source.Slice(offset, payloadLength);
        if (!RemapRule.IsPayloadWellFormed((RemapRuleKind)kind, payload)) { offset = start; reason = ReasonPayload; return false; }
        offset += payloadLength;

        rule = new RemapRule(sequence, introducedIn, new ContentTypeId(typeId), (RemapRuleKind)kind, fromId, toId, payload);
        reason = null;
        return true;
    }

    static bool TryReadField(ReadOnlySpan<byte> source, ref int offset, out int value, out string? reason)
    {
        if (!ContentVarint.TryRead(source, ref offset, out uint raw, out reason))
        {
            value = 0;
            return false;
        }

        if (raw > int.MaxValue)
        {
            value = 0;
            reason = ReasonRange;
            return false;
        }

        value = (int)raw;
        reason = null;
        return true;
    }
}
