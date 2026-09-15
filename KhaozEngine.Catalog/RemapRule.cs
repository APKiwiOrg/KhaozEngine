using System;
using System.Buffers.Binary;

namespace KhaozEngine.Catalog;

/// <summary>
/// The v1 remap rule kinds of contracts 8.2. The numbering is DURABLE: it is the <c>Kind</c> byte every
/// published rule chunk carries, and a rule list is the only record of how an old page becomes a current
/// one, so inserting a kind in the middle would restate the history of every durable page in the world.
/// <para>
/// There is deliberately NO delete kind and no delete policy. A definition is never deleted and is retired
/// instead, so a stored stack still decodes. A reader that meets a kind it does not know fails CLOSED and
/// never skips it, which is what <see cref="ContentPackFormat.Generation"/> exists to announce in advance.
/// </para>
/// </summary>
public enum RemapRuleKind : byte
{
    /// <summary>The ordinary rename or re-base: every reference to <c>FromId</c> becomes <c>ToId</c>.</summary>
    ReplacedBy = 1,

    /// <summary>The id leaves play. The payload's first byte is the POLICY.</summary>
    Retired = 2,

    /// <summary>A keep-legacy copy: existing items move to <c>ToId</c> and can never be generated again.</summary>
    MovedToLegacy = 3,

    /// <summary>A definition whose stack cap fell. The payload is the new cap and the id does not move.</summary>
    StackCapLowered = 4,
}

/// <summary>
/// One remap rule, contracts 8.1 and 8.4. Rules are APPEND ONLY: a rule is never edited and never deleted,
/// and the list is a permanent part of every published version.
/// <para>
/// This type is a producer-side value and its constructor THROWS on a shape a publisher should never build.
/// The decoder never reaches those throws, because <see cref="RemapRuleCodec"/> checks the same rules
/// against the bytes first and answers with a reason token instead.
/// </para>
/// </summary>
public sealed class RemapRule
{
    /// <summary>The widest payload a rule may carry, contracts 8.1.</summary>
    public const int MaxPayloadBytes = 64;

    /// <summary>
    /// A <see cref="RemapRuleKind.Retired"/> payload's first byte for the placeholder policy: the reference
    /// is kept as is and the item is shown through a placeholder, not usable, tradable or droppable.
    /// </summary>
    public const byte RetirePolicyPlaceholder = 0x01;

    /// <summary>
    /// A <see cref="RemapRuleKind.Retired"/> payload's first byte for the replacement policy, with bytes 1
    /// to 4 an int32 destination id. The behaviour is then <see cref="RemapRuleKind.ReplacedBy"/>'s.
    /// </summary>
    public const byte RetirePolicyReplacement = 0x02;

    readonly byte[] _payload;

    /// <summary>Builds one rule, copying the payload so the rule is immutable once it exists.</summary>
    /// <param name="sequence">The global apply order, monotonic across every type's rules.</param>
    /// <param name="introducedIn">The content version number the rule was published in.</param>
    /// <param name="type">The content type the rule operates on.</param>
    /// <param name="kind">Which of the four v1 kinds this is.</param>
    /// <param name="fromId">The id being remapped.</param>
    /// <param name="toId">The destination id, or 0 where the kind has none.</param>
    /// <param name="payload">The kind-specific payload, at most <see cref="MaxPayloadBytes"/> bytes.</param>
    public RemapRule(
        int sequence,
        int introducedIn,
        ContentTypeId type,
        RemapRuleKind kind,
        int fromId,
        int toId,
        ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(introducedIn);
        ArgumentOutOfRangeException.ThrowIfNegative(fromId);
        ArgumentOutOfRangeException.ThrowIfNegative(toId);

        if (!IsKnownKind((byte)kind))
        {
            throw new ArgumentException(FormattableString.Invariant(
                $"Remap rule kind {(byte)kind} is not one of the four v1 kinds, and an unknown kind fails closed."), nameof(kind));
        }

        if (!IsPayloadWellFormed(kind, payload))
        {
            throw new ArgumentException(FormattableString.Invariant(
                $"A {kind} rule's payload is {payload.Length} bytes, which is not the shape contracts 8.2 declares for it."), nameof(payload));
        }

        Sequence = sequence;
        IntroducedIn = introducedIn;
        Type = type;
        Kind = kind;
        FromId = fromId;
        ToId = toId;
        _payload = payload.ToArray();
        Destination = ComputeDestination(kind, toId, _payload);
    }

    /// <summary>The global apply order, monotonic across ALL rules of all types.</summary>
    public int Sequence { get; }

    /// <summary>The content version number the rule was published in, which a page stamp is compared against.</summary>
    public int IntroducedIn { get; }

    /// <summary>The content type this rule operates on. It is a no-op on every other type.</summary>
    public ContentTypeId Type { get; }

    /// <summary>Which of the four v1 kinds this is.</summary>
    public RemapRuleKind Kind { get; }

    /// <summary>The id being remapped.</summary>
    public int FromId { get; }

    /// <summary>The destination id AS ENCODED, which is 0 for a kind that carries none.</summary>
    public int ToId { get; }

    /// <summary>
    /// The id a page should carry after this rule, which is NOT always <see cref="ToId"/>: a retire under
    /// the replacement policy carries its destination in the payload, and a retire under the placeholder
    /// policy and a lowered stack cap move no id at all, so both answer 0.
    /// </summary>
    public int Destination { get; }

    /// <summary>The kind-specific payload.</summary>
    public ReadOnlySpan<byte> Payload => _payload;

    /// <summary>The payload's length, readable without touching the span.</summary>
    public int PayloadLength => _payload.Length;

    /// <summary>True for a retire the player sees as a placeholder rather than as a different item.</summary>
    public bool IsRetiredPlaceholder =>
        Kind == RemapRuleKind.Retired && _payload.Length > 0 && _payload[0] == RetirePolicyPlaceholder;

    /// <summary>The new stack cap a <see cref="RemapRuleKind.StackCapLowered"/> rule carries.</summary>
    public bool TryGetStackCap(out int newCap)
    {
        if (Kind != RemapRuleKind.StackCapLowered || _payload.Length != sizeof(int))
        {
            newCap = 0;
            return false;
        }

        newCap = BinaryPrimitives.ReadInt32LittleEndian(_payload);
        return true;
    }

    /// <summary>True for one of the four v1 kind bytes, which is the fail-closed check the decoder shares.</summary>
    public static bool IsKnownKind(byte kind) => kind is >= 1 and <= 4;

    /// <summary>
    /// True when the payload is the shape its kind declares, contracts 8.2. A retire carries a policy byte,
    /// and a replacement policy carries an int32 destination after it. A lowered stack cap is an int32 cap.
    /// The two plain id remaps declare no payload shape, so anything inside the length cap is accepted and
    /// the meaning of a future one is the reader's to learn through the format generation.
    /// </summary>
    public static bool IsPayloadWellFormed(RemapRuleKind kind, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
        {
            return false;
        }

        return kind switch
        {
            RemapRuleKind.Retired => payload.Length > 0 && payload[0] switch
            {
                RetirePolicyPlaceholder => payload.Length == 1,
                RetirePolicyReplacement => payload.Length == 1 + sizeof(int),
                _ => false,
            },
            RemapRuleKind.StackCapLowered => payload.Length == sizeof(int),
            _ => true,
        };
    }

    static int ComputeDestination(RemapRuleKind kind, int toId, ReadOnlySpan<byte> payload) => kind switch
    {
        RemapRuleKind.ReplacedBy or RemapRuleKind.MovedToLegacy => toId,
        RemapRuleKind.Retired when payload.Length == 1 + sizeof(int) && payload[0] == RetirePolicyReplacement
            => BinaryPrimitives.ReadInt32LittleEndian(payload[1..]),
        _ => 0,
    };
}
