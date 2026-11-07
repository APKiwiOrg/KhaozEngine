using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of a skeleton contract joint table.</summary>
public sealed class SkeletonContractArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public SkeletonContractArgumentException(SkeletonContractRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public SkeletonContractRefusal Reason { get; }

    /// <summary>The number of joints in an empty table.</summary>
    public int? JointCount { get; init; }

    /// <summary>The declaration index of an unnamed joint.</summary>
    public int? JointIndex { get; init; }

    /// <summary>The refused joint name, null when the unnamed joint supplied null.</summary>
    public string? JointName { get; init; }

    /// <summary>The refused parent name.</summary>
    public string? ParentName { get; init; }

    /// <summary>The first root when another root is refused.</summary>
    public string? RootName { get; init; }
}
