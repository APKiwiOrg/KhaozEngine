using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.NetWorld;

/// <summary>A refused world configuration. Existing <see cref="ArgumentException"/> catches remain valid.
/// The reason and identifiers describe the rule. The message is developer prose.</summary>
public sealed class WorldConfigurationException : ArgumentException
{
    IReadOnlyList<string>? incompatibleMembers;

    /// <summary>Creates a refusal of the named parameter with its required reason.</summary>
    public WorldConfigurationException(WorldConfigurationRefusal reason, string message, string paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the configuration.</summary>
    public WorldConfigurationRefusal Reason { get; }
    /// <summary>The member the guard names: CellOrigin, CanRebase, ReplicationStream or optIn.</summary>
    public string? MemberName { get; init; }
    /// <summary>The required member, deltaReplication, when the guarded optIn is on without it.</summary>
    public string? RequiredMember { get; init; }
    /// <summary>The refused cell origin, in absolute XZ metres.</summary>
    public Vector2? ActualOrigin { get; init; }
    /// <summary>The cell size used to derive the refused planar magnitude, in metres.</summary>
    public float? CellSize { get; init; }
    /// <summary>The overlap margin for interest or frame-magnitude admission, in metres.</summary>
    public float? OverlapMargin { get; init; }
    /// <summary>The interest radius that does not fit the overlap, in metres.</summary>
    public float? InterestRadius { get; init; }
    /// <summary>The worst frame-local planar magnitude computed by the guard, in metres.</summary>
    public double? ActualPlanarMagnitude { get; init; }
    /// <summary>The inclusive float32 divergence ceiling, 512 metres, for a finite refused magnitude.</summary>
    public float? MaximumPlanarMagnitude { get; init; }
    /// <summary>The complete incompatible member pair, as an owned read-only snapshot.</summary>
    public IReadOnlyList<string>? IncompatibleMembers
    {
        get => incompatibleMembers;
        init => incompatibleMembers = value is null ? null : ImmutableArray.Create(value.ToArray());
    }
}
