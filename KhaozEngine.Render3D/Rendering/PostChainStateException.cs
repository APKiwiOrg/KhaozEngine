using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of PostChain input or state.</summary>
public sealed class PostChainStateException : InvalidOperationException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public PostChainStateException(PostChainRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public PostChainStateException(PostChainRefusal reason, string message, Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or state was refused.</summary>
    public PostChainRefusal Reason { get; }

    /// <summary>The generation last bound, or null when no targets have been bound.</summary>
    public int? BoundGeneration { get; init; }

    /// <summary>The generation of the refused targets, when generation admission failed.</summary>
    public int? ActualGeneration { get; init; }

    /// <summary>Whether the refused targets are the ones last bound, when generation admission failed.</summary>
    public bool? TargetsMatch { get; init; }

    /// <summary>The selected source slot, when its resource sets were not built.</summary>
    public int? SourceSlot { get; init; }

    /// <summary>Whether the internal ping pair was allocated, when the temporal outline was refused.</summary>
    public bool? PingsAllocated { get; init; }
}
