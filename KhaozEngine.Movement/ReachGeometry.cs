using System;

namespace KhaozEngine.Movement;

/// <summary>Shortest 3D edge distance from an upright capsule, with caller-owned reach tolerance.</summary>
public static class ReachGeometry
{
    /// <summary>Returns zero for overlap. Refuses a distance greater than <see cref="float.MaxValue"/>.</summary>
    /// <exception cref="ArgumentException">The body or target is a default value.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The distance cannot be represented as a finite float.</exception>
    public static float Distance(in MovementBody body, in ReachTarget target)
    {
        double distance = Measure(body, target);
        if (distance > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(target), "Distance exceeds the finite float range.");
        return (float)distance;
    }

    /// <summary>Compares the wider distance directly to range plus tolerance, without a hidden epsilon.</summary>
    /// <exception cref="ArgumentException">The body or target is a default value.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Range or tolerance is invalid, or their sum exceeds the finite float range.</exception>
    public static bool Within(in MovementBody body, in ReachTarget target, float range, float tolerance = 0f)
    {
        if (!float.IsFinite(range) || range < 0f)
            throw new ArgumentOutOfRangeException(nameof(range), "Range must be finite and nonnegative.");
        if (!float.IsFinite(tolerance) || tolerance < 0f)
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be finite and nonnegative.");
        double threshold = (double)range + tolerance;
        if (threshold > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Range plus tolerance exceeds the finite float range.");
        return Measure(body, target) <= threshold;
    }

    static double Measure(in MovementBody body, in ReachTarget target)
    {
        body.Validate(nameof(body));
        double axis = Math.Max(0d, (double)body.HalfHeight - body.Radius);
        double x = (double)body.Centre.X - target.Centre.X;
        double y = (double)body.Centre.Y - target.Centre.Y;
        double z = (double)body.Centre.Z - target.Centre.Z;
        double radius = body.Radius;

        switch (target.Kind)
        {
            case ReachTargetKind.Capsule:
                double targetAxis = Math.Max(0d, (double)target.Body.HalfHeight - target.Body.Radius);
                y = IntervalGap(y, axis, targetAxis);
                radius += target.Body.Radius;
                break;
            case ReachTargetKind.Box:
                double cos = Math.Cos(target.YawRadians);
                double sin = Math.Sin(target.YawRadians);
                double localX = cos * x - sin * z;
                double localZ = sin * x + cos * z;
                x = IntervalGap(localX, 0d, target.HalfExtents.X);
                y = IntervalGap(y, axis, target.HalfExtents.Y);
                z = IntervalGap(localZ, 0d, target.HalfExtents.Z);
                break;
            case ReachTargetKind.Point:
                y = IntervalGap(y, axis, 0d);
                break;
            default:
                throw new ArgumentException("A default reach target is invalid.", nameof(target));
        }

        // Finite float inputs, interval arithmetic and squared distances fit safely in double.
        return Math.Max(0d, Math.Sqrt(x * x + y * y + z * z) - radius);
    }

    static double IntervalGap(double centreDelta, double firstExtent, double secondExtent) =>
        Math.Max(0d, Math.Abs(centreDelta) - firstExtent - secondExtent);
}
