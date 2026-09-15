using System.Collections.Generic;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The CLOSED set of reasons a payload decode answers, contracts 9.7's eight tokens and no others.
/// <para>
/// The set is closed because things downstream are keyed on it: a counter is keyed on the token (spec
/// 12.6) and the quarantine wrapper stores a DURABLE ordinal per token (spec 12.4), so a ninth reason is a
/// deliberate additive act that assigns the next free ordinal and never reuses one. It is not something a
/// task does in passing, which is why the constants live here and nowhere else.
/// </para>
/// <para>
/// Three of the eight belong to the varint reader and are the same strings <c>ContentVarint</c> answers
/// (contracts 15 wants ONE varint implementation in the tree, so it answers this vocabulary rather than a
/// parallel one that has to be translated).
/// </para>
/// </summary>
public static class InstancePayloadReason
{
    /// <summary>The payload exceeds <see cref="ItemInstancePayload.MaxInstancePayloadBytes"/>.</summary>
    public const string PayloadTooLong = "payload-too-long";

    /// <summary>A field's declared length, or a value's bytes, run past the end of the payload.</summary>
    public const string FieldTruncated = "field-truncated";

    /// <summary>A kind is not strictly greater than its predecessor.</summary>
    public const string KindOutOfOrder = "kind-out-of-order";

    /// <summary>The same kind appears twice.</summary>
    public const string KindDuplicate = "kind-duplicate";

    /// <summary>A varint is longer than its value needs, which would give one value two byte forms.</summary>
    public const string VarintNotMinimal = "varint-not-minimal";

    /// <summary>A varint does not terminate within its declared width.</summary>
    public const string VarintOverflow = "varint-overflow";

    /// <summary>A nested payload carries a field that itself nests, which is the one-level limit.</summary>
    public const string SocketNesting = "socket-nesting";

    /// <summary>A known kind's bytes do not match its own shape or its own rules.</summary>
    public const string FieldMalformed = "field-malformed";

    /// <summary>
    /// All eight tokens in the order contracts 9.7 lists them, which is also the order spec 12.4 numbers
    /// their durable ordinals 1 to 8. The order is part of the contract rather than presentation.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        PayloadTooLong,
        FieldTruncated,
        KindOutOfOrder,
        KindDuplicate,
        VarintNotMinimal,
        VarintOverflow,
        SocketNesting,
        FieldMalformed,
    };
}
