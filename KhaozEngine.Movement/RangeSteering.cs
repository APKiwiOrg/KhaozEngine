using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>Requested world XZ input with magnitude at most one. Following and Dropping can request movement.</summary>
public readonly record struct RangeSteering(Vector2 WorldDirection, RangeMoveStatus Status)
{
    /// <summary>A named Drop refusal accompanying <see cref="RangeMoveStatus.Blocked"/>.
    /// Missing retained evidence ends this attempt. It never grants sharp-route fallback.</summary>
    public ContactDropRuntimeRefusal DropRefusal { get; init; }
}

/// <summary>A runtime Drop refusal that the host can diagnose without parsing messages.</summary>
public enum ContactDropRuntimeRefusal { None, DependencyMissing }
