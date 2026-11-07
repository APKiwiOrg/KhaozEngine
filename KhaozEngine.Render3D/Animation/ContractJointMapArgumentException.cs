using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of contract joint map input or access.</summary>
public sealed class ContractJointMapArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public ContractJointMapArgumentException(ContractJointMapRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or access was refused.</summary>
    public ContractJointMapRefusal Reason { get; }

    /// <summary>The number of nodes in the refused skeleton.</summary>
    public int? NodeCount { get; init; }

    /// <summary>The maximum number of skeleton nodes a skinned draw accepts.</summary>
    public int? MaximumNodeCount { get; init; }

    /// <summary>The duplicate name or the name of a node outside the contract.</summary>
    public string? NodeName { get; init; }

    /// <summary>The duplicate, non-contract, unskinned or singular-base node index.</summary>
    public int? NodeIndex { get; init; }

    /// <summary>The contract joint involved in the refusal.</summary>
    public string? JointName { get; init; }

    /// <summary>The invalid parent index.</summary>
    public int? ParentIndex { get; init; }

    /// <summary>The actual parent name, or null for a root.</summary>
    public string? ActualParentName { get; init; }

    /// <summary>The contract parent name, or null for a root.</summary>
    public string? ExpectedParentName { get; init; }

    /// <summary>The parent node whose base frame could not be inverted.</summary>
    public int? ParentNodeIndex { get; init; }

    /// <summary>The refused stance clip name.</summary>
    public string? ClipName { get; init; }

    /// <summary>The required stance clip name.</summary>
    public string? ExpectedClipName { get; init; }

    /// <summary>The glTF logical node targeted by the refused stance track.</summary>
    public int? TargetNode { get; init; }

    /// <summary>The number of keys in the refused stance channel.</summary>
    public int? KeyCount { get; init; }

    /// <summary>The required number of keys in a stance channel.</summary>
    public int? ExpectedKeyCount { get; init; }
}
