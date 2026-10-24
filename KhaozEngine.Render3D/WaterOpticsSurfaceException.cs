using System;

namespace KhaozEngine.Render3D;

/// <summary>Stable reasons an otherwise valid triangle surface cannot be certified.</summary>
public enum WaterOpticsSurfaceFailure
{
    IncompleteCoverage,
    AmbiguousSurface,
    CapacityExceeded,
    UnrepresentableSurface
}

/// <summary>A complete refusal of an unsupported optical surface, never a partial field.</summary>
public sealed class WaterOpticsSurfaceException : ArgumentException
{
    public WaterOpticsSurfaceException(WaterOpticsSurfaceFailure reason, string message) : base(message)
    {
        Reason = reason;
    }

    public WaterOpticsSurfaceFailure Reason { get; }
}
