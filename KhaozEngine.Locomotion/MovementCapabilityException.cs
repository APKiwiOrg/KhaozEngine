using System;

namespace KhaozEngine.Locomotion;

/// <summary>A refused movement capability. Existing <see cref="NotSupportedException"/> catches remain valid.
/// The reason and identifiers describe the refusal. The message is developer prose.</summary>
public sealed class MovementCapabilityException : NotSupportedException
{
    /// <summary>Creates a refusal with its required reason.</summary>
    public MovementCapabilityException(MovementConfigurationRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The rule that refused movement.</summary>
    public MovementConfigurationRefusal Reason { get; }
    /// <summary>The missing physics or certified water interface.</summary>
    public Type? RequiredCapability { get; init; }
    /// <summary>The required stateful overload, CharacterMovement.Step(in MoveState, ...).</summary>
    public string? RequiredOverload { get; init; }
    /// <summary>The state type the required overload carries, MoveState.</summary>
    public Type? RequiredStateType { get; init; }
}
