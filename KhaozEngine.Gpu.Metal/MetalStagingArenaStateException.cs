using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalStagingArena refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class MetalStagingArenaStateException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original BCL diagnostic data.</summary>
    public MetalStagingArenaStateException(MetalStagingArenaRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public MetalStagingArenaRefusal Reason { get; }

    /// <summary>The size of the block the native allocator refused.</summary>
    public ulong BlockBytes { get; init; }
}
