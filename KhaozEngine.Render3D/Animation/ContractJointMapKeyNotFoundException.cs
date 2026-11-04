using System.Collections.Generic;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of contract joint map input or access.</summary>
public sealed class ContractJointMapKeyNotFoundException : KeyNotFoundException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public ContractJointMapKeyNotFoundException(ContractJointMapRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or access was refused.</summary>
    public ContractJointMapRefusal Reason { get; }

    /// <summary>The requested name absent from the joint map.</summary>
    public string? JointName { get; init; }
}
