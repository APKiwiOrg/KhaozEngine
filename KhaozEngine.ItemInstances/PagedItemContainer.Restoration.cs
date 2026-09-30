using System;

namespace KhaozEngine.ItemInstances;

/// <summary>The constructor rules an external state copy needs to restore the same container doors.</summary>
public sealed partial class PagedItemContainer
{
    readonly Func<ReadOnlyMemory<byte>, bool>? _payloadCanonical;
    readonly Func<ReadOnlyMemory<byte>, bool>? _quarantineWellFormed;

    /// <summary>
    /// The rule this container uses to accept a non-empty instance payload. The original delegate is exposed
    /// so an external state copy sees the same live content changes. Null means the payload door is closed.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, bool>? PayloadCanonical => _payloadCanonical;

    /// <summary>
    /// The rule this container uses to accept quarantine wrapper bytes. The original delegate is exposed so
    /// an external state copy sees the same live validation changes. Null means the quarantine door is closed.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, bool>? QuarantineWellFormed => _quarantineWellFormed;
}
