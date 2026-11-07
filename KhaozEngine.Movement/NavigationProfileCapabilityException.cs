using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>An unsupported navigation profile policy. Existing not-supported catches remain valid.</summary>
public sealed class NavigationProfileCapabilityException : NotSupportedException
{
    /// <summary>Creates a refusal with the unchanged developer message.</summary>
    public NavigationProfileCapabilityException(NavigationProfileRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>The unsupported policy.</summary>
    public NavigationProfileRefusal Reason { get; }
    /// <summary>The issue tracking the deferred profile support.</summary>
    public Uri? IssueUrl { get; init; }
    /// <summary>The named profile, or null for an unnamed profile build.</summary>
    public string? ProfileName { get; init; }
    /// <summary>The supplied movement binding kind.</summary>
    public EffectiveMovementBindingKind? ActualKind { get; init; }
    /// <summary>Whether the refused Drop profile requested aquatic movement.</summary>
    public bool? Aquatic { get; init; }
}
