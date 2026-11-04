using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of ambiguous skeleton name lookup.</summary>
public sealed class SkeletonStateException : InvalidOperationException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public SkeletonStateException(SkeletonRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public SkeletonRefusal Reason { get; }

    /// <summary>The ambiguous node name.</summary>
    public string? NodeName { get; init; }

    /// <summary>The first node carrying the ambiguous name.</summary>
    public int? FirstNodeIndex { get; init; }

    /// <summary>The later node carrying the ambiguous name.</summary>
    public int? DuplicateNodeIndex { get; init; }
}
