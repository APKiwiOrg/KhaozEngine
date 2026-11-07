using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>A navigation profile compatibility refusal. Existing argument catches remain valid.</summary>
public sealed class NavigationProfileException : ArgumentException
{
    /// <summary>Creates a refusal preserving the original parameter name.</summary>
    public NavigationProfileException(NavigationProfileRefusal reason, string message, string? paramName = null)
        : base(message, paramName) => Reason = reason;
    /// <summary>The rule that refused the profile.</summary>
    public NavigationProfileRefusal Reason { get; }
    /// <summary>The named profile, or null for an unnamed profile build.</summary>
    public string? ProfileName { get; init; }
    /// <summary>The baked binding kind.</summary>
    public EffectiveMovementBindingKind? ExpectedKind { get; init; }
    /// <summary>The supplied binding kind.</summary>
    public EffectiveMovementBindingKind? ActualKind { get; init; }
    /// <summary>The capture slope in radians.</summary>
    public float? ExpectedSlope { get; init; }
    /// <summary>The supplied profile slope in radians.</summary>
    public float? ActualSlope { get; init; }
    /// <summary>The retained tuning field that differs.</summary>
    public string? FieldName { get; init; }
    /// <summary>The baked geometry or fraction value.</summary>
    public double? ExpectedNumber { get; init; }
    /// <summary>The runtime geometry or fraction value.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The baked field bits, or its enum or Boolean value.</summary>
    public uint? ExpectedBits { get; init; }
    /// <summary>The runtime field bits, or its enum or Boolean value.</summary>
    public uint? ActualBits { get; init; }
    /// <summary>The supplied swim exit fraction.</summary>
    public float? ExitFraction { get; init; }
    /// <summary>The supplied swim submersion fraction.</summary>
    public float? SubmersionFraction { get; init; }
    /// <summary>The supplied swim enter fraction.</summary>
    public float? EnterFraction { get; init; }
    /// <summary>The supplied terrain descent permission.</summary>
    public bool? AllowTerrainDescents { get; init; }
    /// <summary>The supplied Drop permission in metres.</summary>
    public float? MaxDropMetres { get; init; }
}
