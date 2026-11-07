using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of contract joint map input or access.</summary>
public sealed class ContractJointMapStateException : InvalidOperationException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public ContractJointMapStateException(ContractJointMapRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or access was refused.</summary>
    public ContractJointMapRefusal Reason { get; }

    /// <summary>The named skeleton node outside the skin.</summary>
    public string? JointName { get; init; }
}
