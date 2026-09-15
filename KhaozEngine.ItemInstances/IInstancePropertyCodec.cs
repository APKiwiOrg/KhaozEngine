using System;

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

/// <summary>The codecs the engine ships for kinds that need no rule beyond their shape.</summary>
public static class InstancePropertyCodec
{
    /// <summary>
    /// The codec for a kind whose SHAPE is its whole contract. It refuses nothing, because the shape walk
    /// has already refused everything there is to refuse about the field.
    /// </summary>
    public static IInstancePropertyCodec ShapeOnly { get; } = new ShapeOnlyCodec();

    sealed class ShapeOnlyCodec : IInstancePropertyCodec
    {
        public bool TryValidate(ReadOnlySpan<byte> body, out string? reason)
        {
            reason = null;
            return true;
        }
    }
}
