using System;

namespace KhaozEngine.Items;

/// <summary>One slot's WHOLE state: the stack, the opaque instance payload, and whether that payload is a
/// quarantine wrapper rather than a readable instance. The engine never learns what any of those bytes
/// mean.</summary>
/// <param name="Stack">What the slot holds, instance id included.</param>
/// <param name="Payload">The instance's canonical property bytes, empty for a plain stack. A container
/// hands out a window over bytes it owns and never mutates in place, so handing one out costs nothing and
/// holding one cannot corrupt the container.</param>
/// <param name="Quarantined">Whether <see cref="Payload"/> is a quarantine wrapper. A quarantined slot
/// never merges with anything, and its bytes are preserved verbatim rather than read.</param>
/// <remarks>
/// Equality here is BYTE equality over the payload, not the reference equality a record struct generates
/// for a <see cref="ReadOnlyMemory{T}"/> field. That is load bearing rather than cosmetic: the stacking
/// rule is a span compare over a canonical payload, and a slot that has been through a codec holds a
/// different array with the same bytes.
/// </remarks>
public readonly record struct ItemSlot(ItemStack Stack, ReadOnlyMemory<byte> Payload, bool Quarantined)
{
    /// <summary>The largest payload a NON-quarantined slot may carry. A quarantine wrapper is bounded by
    /// the page it rides in instead, because it preserves bytes that may already have broken this cap.
    /// The number is the content contracts' MaxInstancePayloadBytes (section 9.6) and it moves ONE WAY:
    /// raising it is backward compatible, lowering it strands items already over it.</summary>
    public const int MaxPayloadBytes = 512;

    /// <summary>The empty slot. A cleared slot and a never-filled one are the same value.</summary>
    public static ItemSlot Empty => default;

    /// <summary>Whether this slot holds nothing.</summary>
    public bool IsEmpty => Stack.IsEmpty;

    /// <summary>Byte equality over the payload, so two slots holding the same bytes in different arrays
    /// are the same slot.</summary>
    /// <param name="other">The slot compared against.</param>
    public bool Equals(ItemSlot other) =>
        Stack == other.Stack
        && Quarantined == other.Quarantined
        && Payload.Span.SequenceEqual(other.Payload.Span);

    /// <summary>Hashes the payload's BYTES, so it agrees with <see cref="Equals(ItemSlot)"/>.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Stack);
        hash.Add(Quarantined);
        hash.AddBytes(Payload.Span);
        return hash.ToHashCode();
    }
}
