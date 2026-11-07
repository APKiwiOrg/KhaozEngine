using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of PostChain input or state.</summary>
public sealed class PostChainArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public PostChainArgumentException(PostChainRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public PostChainArgumentException(PostChainRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or state was refused.</summary>
    public PostChainRefusal Reason { get; }

    /// <summary>The source-slot count the targets reported.</summary>
    public int SourceSlotCount { get; init; }

    /// <summary>The minimum supported source-slot count.</summary>
    public int MinimumSourceSlotCount { get; init; }

    /// <summary>The maximum supported source-slot count.</summary>
    public int MaximumSourceSlotCount { get; init; }
}
