using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>A ground context contact refusal. Existing not-supported catches remain valid.</summary>
public sealed class GroundContactCapabilityException : NotSupportedException
{
    /// <summary>Creates a refusal with the unchanged developer message.</summary>
    public GroundContactCapabilityException(GroundContactCapabilityRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The capability rule that refused contact movement.</summary>
    public GroundContactCapabilityRefusal Reason { get; }
    /// <summary>The producer-supplied restriction text, when contact is restricted at construction.</summary>
    public string? Restriction { get; init; }
    /// <summary>The known engine producer, when the producer supplied its kind.</summary>
    public GroundContactProducerKind? ProducerKind { get; init; }
    /// <summary>The known engine capability the producer requires, when supplied.</summary>
    public GroundContactCapability? RequiredCapability { get; init; }
    /// <summary>The required binding kind for ordinary context or prediction refusals.</summary>
    public EffectiveMovementBindingKind? RequiredKind { get; init; }
}
