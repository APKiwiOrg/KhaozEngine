using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>An immutable, physics-frame play-area certificate for capsule centres. Capture it before
/// a movement read and keep it unchanged through publication. A point clamp alone is not this contract.</summary>
public abstract class MovementBoundary
{
    public abstract string SemanticIdentity { get; }
    public abstract MovementAvailability Classify(Vector3 centre, out bool inside);
    /// <summary>Proposes a constrained displacement. The resolver still proves its complete path.</summary>
    public abstract MovementAvailability Constrain(Vector3 start, Vector3 displacement, out Vector3 constrained);
    /// <summary>Certifies the whole affine segment and its stored endpoint. A refusal exposes no prefix.</summary>
    public abstract MovementAvailability ProveSegment(Vector3 start, Vector3 displacement, Vector3 storedEnd, out bool admitted);

    public static MovementBoundary Rectangle(double minX, double minZ, double maxX, double maxZ, string? identity = null)
    {
        if (!double.IsFinite(minX) || !double.IsFinite(minZ) || !double.IsFinite(maxX) || !double.IsFinite(maxZ) ||
            minX > maxX || minZ > maxZ) throw new ArgumentOutOfRangeException(nameof(minX));
        return new RectangleBoundary(minX, minZ, maxX, maxZ,
            identity ?? $"rect-v1/{Bits(minX)}/{Bits(minZ)}/{Bits(maxX)}/{Bits(maxZ)}");
    }

    public static MovementBoundary Circle(double x, double z, double radius, string? identity = null)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z) || !double.IsFinite(radius) || radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        return new CircleBoundary(x, z, radius, identity ?? $"circle-v1/{Bits(x)}/{Bits(z)}/{Bits(radius)}");
    }

    static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16");
}
