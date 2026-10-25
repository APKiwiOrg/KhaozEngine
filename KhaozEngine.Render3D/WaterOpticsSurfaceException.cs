using System;

namespace KhaozEngine.Render3D;

/// <summary>Stable reasons an otherwise valid triangle surface cannot be certified.</summary>
public enum WaterOpticsSurfaceFailure
{
    /// <summary>The source triangles leave part of a certified rectangle uncovered, including a hole.</summary>
    IncompleteCoverage,
    /// <summary>Distinct triangles disagree where their projections overlap in area, or a shared boundary's
    /// depth discontinuity with sampler error exceeds 0.0001 metres.</summary>
    AmbiguousSurface,
    /// <summary>A bin needs more than <see cref="WaterOpticsTriangleField.MaxTrianglesPerBin"/> candidates, or
    /// the atlas exceeds <see cref="WaterOpticsTriangleField.MaxDimension"/> squared scalars.</summary>
    CapacityExceeded,
    /// <summary>A value cannot be represented by every backend, such as a subnormal atlas scalar, a non-normal
    /// inverse extent or a reconstruction bound above 0.0001 metres.</summary>
    UnrepresentableSurface,
    /// <summary>The effective water has no certified displacement envelope, such as FFT water or a swell whose
    /// reach is not finite.</summary>
    UncertifiedDisplacement
}

/// <summary>A complete refusal of an unsupported optical surface, never a partial field.</summary>
public sealed class WaterOpticsSurfaceException : ArgumentException
{
    /// <summary>Create a refusal with its stable <paramref name="reason"/> and a developer-facing
    /// <paramref name="message"/>.</summary>
    public WaterOpticsSurfaceException(WaterOpticsSurfaceFailure reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the surface was refused.</summary>
    public WaterOpticsSurfaceFailure Reason { get; }
}
