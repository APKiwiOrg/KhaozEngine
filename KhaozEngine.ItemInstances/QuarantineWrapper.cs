using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The DURABLE reason ordinal table of spec 12.4: every reason a record is quarantined under, and the byte
/// each one is written as inside a <see cref="QuarantineWrapper"/>.
/// <para>
/// The ordinals are assigned in the spec and nowhere else, for one reason: the byte outlives the release
/// that wrote it, a tool reading an old page has only the number, and a set of tokens with no numbers
/// attached is a set every implementer numbers differently. Ordinal 0 is RESERVED and never assigned, so a
/// half written wrapper is detectable. <b>A new reason appends at the next free number and an assigned
/// number is never reused</b>, even when a reason is withdrawn, which is the never-reuse rule contracts 5.1
/// puts on a content id for the same cause.
/// </para>
/// <para>
/// One to eight are contracts 9.7's eight decoder tokens in the order 9.7 lists them, taken from
/// <see cref="InstancePayloadReason.All"/> rather than re-typed here so the two cannot drift. Nine to
/// thirteen are the drift and entry-level reasons the instance validator raises. Spec 12.2's checks 12 and
/// 13 have NO ordinal, deliberately: <c>over-cap</c> is tolerated and <c>definition-retired</c> produces
/// the Retired outcome, so neither ever writes a wrapper and giving them a number would invite one to be
/// written.
/// </para>
/// </summary>
public static class InstanceQuarantineReason
{
    /// <summary>Ordinal 9. The entry's definition id, at any depth, resolves in no active version.</summary>
    public const string UnknownDefinition = "unknown-definition";

    /// <summary>Ordinal 10. A content id a registered reference target names does not resolve, at any depth.</summary>
    public const string UnknownContentReference = "unknown-content-reference";

    /// <summary>Ordinal 11. A non-empty payload sits on an entry whose instance id is 0.</summary>
    public const string InstanceIdMissing = "instance-id-missing";

    /// <summary>Ordinal 12. Two entries in one page carry the same instance id.</summary>
    public const string InstanceIdDuplicate = "instance-id-duplicate";

    /// <summary>Ordinal 13. An entry carrying kind 5 or kind 132 has a count above one.</summary>
    public const string StackNotInstanceable = "stack-not-instanceable";

    /// <summary>The ordinal a zeroed byte carries, which is never a reason.</summary>
    public const byte ReservedOrdinal = 0;

    static readonly string[] Table = CreateTable();
    static readonly Dictionary<string, byte> ByReason = CreateOrdinals();

    /// <summary>
    /// Every assigned reason, in ORDINAL ORDER, so index 0 is ordinal 1. The order is the durable contract
    /// rather than presentation.
    /// </summary>
    public static IReadOnlyList<string> All => Table;

    /// <summary>The byte a reason is written as, or false when nothing assigns that token an ordinal.</summary>
    /// <param name="reason">A reason token, ordinally compared.</param>
    /// <param name="ordinal">The durable byte, when the token has one.</param>
    public static bool TryGetOrdinal(string reason, out byte ordinal)
    {
        if (reason is not null && ByReason.TryGetValue(reason, out ordinal))
        {
            return true;
        }

        ordinal = ReservedOrdinal;
        return false;
    }

    /// <summary>
    /// The reason a byte names, or false for the reserved 0 and for any ordinal this build has not been
    /// given. An unknown ordinal is a wrapper written by a LATER engine, and guessing at it would put the
    /// wrong meaning on a durable byte.
    /// </summary>
    /// <param name="ordinal">The byte read off a stored wrapper.</param>
    /// <param name="reason">The token it names, when it names one.</param>
    public static bool TryGetReason(byte ordinal, out string? reason)
    {
        if (ordinal >= 1 && ordinal <= Table.Length)
        {
            reason = Table[ordinal - 1];
            return true;
        }

        reason = null;
        return false;
    }

    static string[] CreateTable()
    {
        var table = new List<string>(InstancePayloadReason.All)
        {
            UnknownDefinition,
            UnknownContentReference,
            InstanceIdMissing,
            InstanceIdDuplicate,
            StackNotInstanceable,
        };

        return table.ToArray();
    }

    static Dictionary<string, byte> CreateOrdinals()
    {
        var ordinals = new Dictionary<string, byte>(Table.Length, StringComparer.Ordinal);
        for (int index = 0; index < Table.Length; index++)
        {
            ordinals.Add(Table[index], (byte)(index + 1));
        }

        return ordinals;
    }
}

/// <summary>
/// The <c>KECQ</c> envelope of spec 12.4 over contracts 10.2: the bytes of a failed record, kept VERBATIM,
/// with the reason it failed and the content version it was stamped with. Nothing is truncated, nothing is
/// normalized and nothing is re-encoded.
/// <code>
/// [Magic: 4 bytes 'K','E','C','Q']
/// [Version: uint16 LE]
/// [ReasonCode: byte]
/// [StampedVersion: varint]
/// [OriginalLength: varint]
/// [Original: OriginalLength bytes]
/// </code>
/// <para>
/// <b>A magic here and none on a payload, and the two are consistent.</b> Contracts 15 forbids a magic on a
/// format always embedded in a larger versioned record, and a payload is such a format. A wrapper is not:
/// it must be tellable from a payload at a glance in a hex dump of a page, and by a tool that never saw the
/// entry flag. Four bytes for that, once per quarantined entry, on a path that is rare by definition.
/// </para>
/// <para>
/// <b><see cref="Verify(ReadOnlySpan{byte})"/> and <see cref="TryUnwrap"/> are TOTAL.</b> The bytes come off a
/// stored page, so they answer false rather than throwing, at every length and for every byte.
/// <see cref="Wrap(string, int, ReadOnlySpan{byte})"/> is the
/// other half and throws, because it is handed values by code: an unknown reason token and a negative stamp
/// are caller bugs rather than data.
/// </para>
/// </summary>
public static class QuarantineWrapper
{
    /// <summary>
    /// The only version, and a decoder refuses anything else rather than guessing (contracts 10.5 and 15).
    /// A <c>ushort</c> rather than a byte, because a durable format outlives several content generations.
    /// </summary>
    public const ushort Version = 1;

    /// <summary>Bytes before the stamped version varint: the magic, the version and the reason code.</summary>
    public const int FixedHeaderBytes = 7;

    /// <summary>The shortest legal wrapper, which is the fixed header plus two one byte varints.</summary>
    public const int MinimumBytes = FixedHeaderBytes + 2;

    /// <summary>The four ASCII bytes a wrapper opens with, contracts 15's content magic for quarantine.</summary>
    public static ReadOnlySpan<byte> Magic => "KECQ"u8;

    /// <summary>
    /// The bytes a wrap will take, so a page codec can size its entry before writing one.
    /// </summary>
    /// <param name="reason">The reason token, which must carry an ordinal.</param>
    /// <param name="stampedVersion">The page stamp the record failed under, never negative.</param>
    /// <param name="originalLength">How many bytes are being preserved.</param>
    /// <exception cref="ArgumentOutOfRangeException">The reason has no ordinal, or a number is negative.</exception>
    public static int Size(string reason, int stampedVersion, int originalLength)
    {
        _ = Ordinal(reason);
        ArgumentOutOfRangeException.ThrowIfNegative(stampedVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
        return FixedHeaderBytes
            + ContentVarint.Size((uint)stampedVersion)
            + ContentVarint.Size((uint)originalLength)
            + originalLength;
    }

    /// <summary>
    /// Wraps a failed record's bytes. <paramref name="original"/> may be ANY length, including above
    /// <see cref="ItemInstancePayload.MaxInstancePayloadBytes"/>: <c>payload-too-long</c> is a reason, and
    /// refusing to wrap the thing that failed for being too big would destroy exactly the item the wrapper
    /// exists to keep. The page entry's own length check is what bounds it, at the 2 MiB section cap.
    /// </summary>
    /// <param name="reason">Why the record failed, a token carrying an ordinal.</param>
    /// <param name="stampedVersion">The page stamp the record failed under.</param>
    /// <param name="original">The bytes, preserved exactly.</param>
    /// <exception cref="ArgumentOutOfRangeException">The reason has no ordinal, or the stamp is negative.</exception>
    public static byte[] Wrap(string reason, int stampedVersion, ReadOnlySpan<byte> original)
    {
        byte[] wrapper = new byte[Size(reason, stampedVersion, original.Length)];
        _ = Wrap(reason, stampedVersion, original, wrapper);
        return wrapper;
    }

    /// <summary>Wraps into a caller's buffer. Returns the bytes written.</summary>
    /// <param name="reason">Why the record failed, a token carrying an ordinal.</param>
    /// <param name="stampedVersion">The page stamp the record failed under.</param>
    /// <param name="original">The bytes, preserved exactly.</param>
    /// <param name="destination">Where to write, at least <see cref="Size"/> long.</param>
    /// <exception cref="ArgumentOutOfRangeException">The reason has no ordinal, or the stamp is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public static int Wrap(string reason, int stampedVersion, ReadOnlySpan<byte> original, Span<byte> destination)
    {
        byte ordinal = Ordinal(reason);
        int size = Size(reason, stampedVersion, original.Length);
        if (destination.Length < size)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The wrapper needs {size} bytes and the destination holds {destination.Length}."),
                nameof(destination));
        }

        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[Magic.Length..], Version);
        destination[6] = ordinal;

        int written = FixedHeaderBytes;
        written += ContentVarint.Write(destination[written..], (uint)stampedVersion);
        written += ContentVarint.Write(destination[written..], (uint)original.Length);
        original.CopyTo(destination[written..]);
        return written + original.Length;
    }

    /// <summary>
    /// Whether these bytes are a well formed wrapper: the four magic bytes, the version, a reason ordinal
    /// this build knows, and a declared <c>OriginalLength</c> that accounts for exactly the bytes present.
    /// This is what stands in for spec 4.7's invariants 2 and 4 on a quarantined slot.
    /// </summary>
    /// <param name="wrapper">The bytes, which may be anything at all.</param>
    public static bool Verify(ReadOnlySpan<byte> wrapper) => TryUnwrap(wrapper, out _, out _, out _);

    /// <summary>
    /// The <see cref="ReadOnlyMemory{T}"/> shape of <see cref="Verify(ReadOnlySpan{byte})"/>, which is the
    /// shape <c>ItemContainer</c>'s predicate seam takes: <c>KhaozEngine.Items</c> sits BELOW this package
    /// and cannot call into it, so the check arrives as a <c>Func</c> the caller supplies.
    /// </summary>
    /// <param name="wrapper">The bytes, which may be anything at all.</param>
    public static bool Verify(ReadOnlyMemory<byte> wrapper) => Verify(wrapper.Span);

    /// <summary>
    /// Reads a wrapper back. The original is a WINDOW over the input rather than a copy, so unwrapping
    /// allocates nothing and the bytes cannot be changed on the way out.
    /// </summary>
    /// <param name="wrapper">The stored bytes.</param>
    /// <param name="original">The preserved bytes, empty when the answer is false.</param>
    /// <param name="reason">The reason the record was quarantined, null when the answer is false.</param>
    /// <param name="stampedVersion">The content version the record was stamped with.</param>
    public static bool TryUnwrap(
        ReadOnlySpan<byte> wrapper,
        out ReadOnlySpan<byte> original,
        out string? reason,
        out int stampedVersion)
    {
        original = default;
        reason = null;
        stampedVersion = 0;

        if (wrapper.Length < MinimumBytes
            || !wrapper[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt16LittleEndian(wrapper[Magic.Length..]) != Version
            || !InstanceQuarantineReason.TryGetReason(wrapper[6], out string? token))
        {
            return false;
        }

        int offset = FixedHeaderBytes;
        if (!ContentVarint.TryRead(wrapper, ref offset, out uint stamp, out _)
            || stamp > int.MaxValue
            || !ContentVarint.TryRead(wrapper, ref offset, out uint length, out _)
            || length != (uint)(wrapper.Length - offset))
        {
            return false;
        }

        original = wrapper.Slice(offset, (int)length);
        reason = token;
        stampedVersion = (int)stamp;
        return true;
    }

    /// <summary>The ordinal a reason is written as, throwing rather than inventing one.</summary>
    static byte Ordinal(string reason)
    {
        if (InstanceQuarantineReason.TryGetOrdinal(reason, out byte ordinal))
        {
            return ordinal;
        }

        throw new ArgumentOutOfRangeException(
            nameof(reason),
            reason,
            "Spec 12.4's reason table assigns every ordinal a wrapper may carry, and a token with no ordinal cannot be stored. A new reason appends at the next free number in the spec first.");
    }
}
