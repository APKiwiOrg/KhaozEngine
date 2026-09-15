using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// One property kind's own rules over its field BODY, which is everything its
/// <see cref="InstanceFieldShape"/> cannot express: an ascending mod id, a reserved flags slot that must be
/// zero, a state byte of 0 or 1.
/// <para>
/// The STRUCTURAL walk is not here. Canonical ordering, the duplicate-kind refusal, minimal varints, every
/// declared length inside the payload, the one-level socket rule and the cap are the payload codec's, and
/// the per-kind slot walk is DERIVED from the registered shape. A codec is the last, narrow step after
/// both, so a kind that adds no rule of its own registers <see cref="InstancePropertyCodec.ShapeOnly"/> and
/// costs nothing.
/// </para>
/// <para>
/// It is TOTAL, like every decoder on this path. The bytes arrive from a remote peer or from a stored page,
/// so it answers false plus a stable reason token from the closed set of contracts 9.7 and never throws.
/// </para>
/// </summary>
public interface IInstancePropertyCodec
{
    /// <summary>Checks one field body. Returns false with a reason token rather than throwing.</summary>
    /// <param name="body">The field's bytes, without its kind and length prefix.</param>
    /// <param name="reason">The closed-set reason token when the answer is false, null when it is true.</param>
    bool TryValidate(ReadOnlySpan<byte> body, out string? reason);
}

/// <summary>
/// The codecs the engine ships: one that refuses nothing, and the three v1 kinds that carry a rule their
/// shape cannot express.
/// <para>
/// Each of the three runs AFTER the shape walk has succeeded, so it re-reads a body it already knows is
/// well formed and looks only at the values. It is total anyway, because it is public and a caller may
/// hand it anything.
/// </para>
/// </summary>
public static class InstancePropertyCodec
{
    /// <summary>
    /// The codec for a kind whose SHAPE is its whole contract. It refuses nothing, because the shape walk
    /// has already refused everything there is to refuse about the field.
    /// </summary>
    public static IInstancePropertyCodec ShapeOnly { get; } = new ShapeOnlyCodec();

    /// <summary>
    /// Kind 128: the state byte is 0 unidentified or 1 identified and nothing else (spec 3.3). A third
    /// value would be a state no rule in the tree knows how to apply.
    /// </summary>
    public static IInstancePropertyCodec Identification { get; } = new IdentificationCodec();

    /// <summary>
    /// Kinds 131 and 133: a mod id is never 0, the list is ASCENDING BY MOD ID with a mod id appearing at
    /// most once (contracts 9.9), a tier ordinal runs 1 to 255, and the reserved flags field is 0 in v1.
    /// <para>
    /// The ordering rule is the one that matters most. Rule 9.3.1 orders FIELDS and says nothing about
    /// entries inside one, so without this the same three affixes encode six ways and byte equality stops
    /// being property equality, which is what a stack check and a page digest both rest on.
    /// </para>
    /// </summary>
    public static IInstancePropertyCodec AffixList { get; } = new AffixListCodec();

    /// <summary>
    /// Kind 132: a contained definition of 0 MEANS the socket is empty (contracts 9.5), so an empty socket
    /// carries no instance id and no nested payload. The one level nesting limit is NOT here: it is derived
    /// from the registered shape by the payload walk, so a game kind that nests gets it too.
    /// </summary>
    public static IInstancePropertyCodec SocketList { get; } = new SocketListCodec();

    sealed class ShapeOnlyCodec : IInstancePropertyCodec
    {
        public bool TryValidate(ReadOnlySpan<byte> body, out string? reason)
        {
            reason = null;
            return true;
        }
    }

    sealed class IdentificationCodec : IInstancePropertyCodec
    {
        public bool TryValidate(ReadOnlySpan<byte> body, out string? reason)
        {
            if (body.Length >= 1 && body[0] <= 1)
            {
                reason = null;
                return true;
            }

            reason = InstancePayloadReason.FieldMalformed;
            return false;
        }
    }

    sealed class AffixListCodec : IInstancePropertyCodec
    {
        public bool TryValidate(ReadOnlySpan<byte> body, out string? reason)
        {
            reason = null;
            if (body.Length < 1)
            {
                reason = InstancePayloadReason.FieldMalformed;
                return false;
            }

            int count = body[0];
            int offset = 1;
            long previousMod = -1;
            for (int entry = 0; entry < count; entry++)
            {
                if (!ContentVarint.TryReadUInt64(body, ref offset, out ulong modId, out reason))
                {
                    return false;
                }

                // Zero, equal and descending are all refused: equal is what contracts 9.9's "at most once"
                // forbids and descending is what its ascending order forbids.
                if (modId == 0 || (long)modId <= previousMod)
                {
                    reason = InstancePayloadReason.FieldMalformed;
                    return false;
                }

                previousMod = (long)modId;
                if (offset >= body.Length || body[offset] == 0)
                {
                    reason = InstancePayloadReason.FieldMalformed;
                    return false;
                }

                offset++;
                if (body.Length - offset < 2)
                {
                    reason = InstancePayloadReason.FieldTruncated;
                    return false;
                }

                offset += 2;
                if (!ContentVarint.TryReadUInt64(body, ref offset, out ulong flags, out reason))
                {
                    return false;
                }

                if (flags != 0)
                {
                    reason = InstancePayloadReason.FieldMalformed;
                    return false;
                }
            }

            return true;
        }
    }

    sealed class SocketListCodec : IInstancePropertyCodec
    {
        public bool TryValidate(ReadOnlySpan<byte> body, out string? reason)
        {
            int offset = 0;
            if (!ContentVarint.TryRead(body, ref offset, out uint count, out reason))
            {
                return false;
            }

            for (uint entry = 0; entry < count; entry++)
            {
                if (!ContentVarint.TryReadUInt64(body, ref offset, out _, out reason)
                    || !ContentVarint.TryReadUInt64(body, ref offset, out ulong definitionId, out reason)
                    || !ContentVarint.TryReadUInt64(body, ref offset, out ulong instanceId, out reason)
                    || !ContentVarint.TryRead(body, ref offset, out uint nestedLength, out reason))
                {
                    return false;
                }

                if (nestedLength > (uint)(body.Length - offset))
                {
                    reason = InstancePayloadReason.FieldTruncated;
                    return false;
                }

                if (definitionId == 0 && (instanceId != 0 || nestedLength != 0))
                {
                    reason = InstancePayloadReason.FieldMalformed;
                    return false;
                }

                offset += (int)nestedLength;
            }

            return true;
        }
    }
}
