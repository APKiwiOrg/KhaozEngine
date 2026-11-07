using System;

namespace KhaozEngine.Locomotion;

/// <summary>A refused movement configuration. Existing <see cref="ArgumentException"/> catches remain valid.
/// The reason and identifiers describe the rule. The message is developer prose.</summary>
public sealed class MovementConfigurationException : ArgumentException
{
    /// <summary>Creates a refusal, preserving the original parameter name when present.</summary>
    public MovementConfigurationException(MovementConfigurationRefusal reason, string message, string? paramName = null)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the configuration.</summary>
    public MovementConfigurationRefusal Reason { get; }
    /// <summary>The tuning field whose value was refused.</summary>
    public string? FieldName { get; init; }
    /// <summary>The nonfinite or out-of-domain tuning value.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The required legacy foot fraction, 0.5.</summary>
    public double? ExpectedNumber { get; init; }
    /// <summary>The inclusive lower limit, zero, for a nonnegative kernel field.</summary>
    public double? MinimumNumber { get; init; }
    /// <summary>The exclusive lower limit, zero, for a positive kernel field.</summary>
    public double? MinimumExclusive { get; init; }
    /// <summary>The inclusive upper limit, one, for a kernel fraction.</summary>
    public double? MaximumNumber { get; init; }
    /// <summary>The refused capsule radius.</summary>
    public float? CapsuleRadius { get; init; }
    /// <summary>The refused capsule half height.</summary>
    public float? CapsuleHalfHeight { get; init; }
    /// <summary>The refused maximum slope in radians.</summary>
    public float? ActualSlope { get; init; }
    /// <summary>The exclusive maximum slope, half pi radians.</summary>
    public float? MaximumExclusive { get; init; }
    /// <summary>The refused wade start fraction.</summary>
    public float? StartFraction { get; init; }
    /// <summary>The refused wade end fraction.</summary>
    public float? EndFraction { get; init; }
    /// <summary>The refused swim exit fraction.</summary>
    public float? ExitFraction { get; init; }
    /// <summary>The refused swim enter fraction.</summary>
    public float? EnterFraction { get; init; }
    /// <summary>The supplied policy's Legacy water mode.</summary>
    public WaterTraversalMode? ActualMode { get; init; }
    /// <summary>The required default or supplied explicit water policy.</summary>
    public WaterTraversalPolicy? ExpectedWater { get; init; }
    /// <summary>The refused tuning water policy.</summary>
    public WaterTraversalPolicy? ActualWater { get; init; }
    /// <summary>The controller the refused tuning selects.</summary>
    public MovementControllerKind? ActualController { get; init; }
    /// <summary>The controller required by a Legacy or ordinary contact binding.</summary>
    public MovementControllerKind? ExpectedController { get; init; }
    /// <summary>The PublicExplicit binding that conflicts with the Contact selector.</summary>
    public EffectiveMovementBindingKind? RequestedKind { get; init; }
    /// <summary>The required cell factory member on the sharded world configuration.</summary>
    public string? MemberName { get; init; }
    /// <summary>The missing physics, registration, boundary or water type.</summary>
    public Type? RequiredCapability { get; init; }
}
