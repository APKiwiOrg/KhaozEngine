using System;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>The explicit binary64 to backend binary32 pose boundary. Semantic support keeps its precise transform.</summary>
public static class MapBackendPoseAdapter
{
    /// <summary>Rounds the origin relative to <paramref name="frame"/> and the yaw quaternion once. Scale belongs to
    /// the shape, rounded separately to binary32. Y follows WorldFrame's unframed convention.</summary>
    public static Pose Pose(MapEffectiveTransform transform, WorldFrame frame)
    {
        MapEffectiveTransform.Valid(transform);
        Vector3 p = new((float)(transform.X - frame.Anchor.X), (float)transform.Y, (float)(transform.Z - frame.Anchor.Z));
        var (s, c) = Math.SinCos(transform.YawRadians * 0.5);
        Quaternion q = new(0, (float)s, 0, (float)c);
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || !float.IsFinite((float)transform.Scale) || (float)transform.Scale <= 0)
            throw new MapDocumentException("Backend pose is not representable.");
        return new(p, q);
    }

    /// <summary>A conservative Euclidean vertex error in metres. Includes origin half-ULPs, source radius times
    /// scale error and angular chord error, quaternion norm rounding, and local-coordinate half-ULPs for the scaled
    /// vertex, quaternion arithmetic and final addition. Source radius is measured before instance scale.</summary>
    public static double ErrorMetres(MapEffectiveTransform transform, WorldFrame frame, double sourceRadius)
    {
        if (!double.IsFinite(sourceRadius) || sourceRadius < 0) throw new ArgumentOutOfRangeException(nameof(sourceRadius));
        Pose pose = Pose(transform, frame);
        double scale = (float)transform.Scale;
        Quaternion q = pose.Orientation;
        double angle = Math.IEEERemainder(2 * Math.Atan2(q.Y, q.W) - transform.YawRadians, 2 * Math.PI);
        double normError = Math.Abs((double)q.Y * q.Y + (double)q.W * q.W - 1);
        double radius = sourceRadius * scale;
        double origin = Norm(HalfUlp(pose.Position.X), HalfUlp(pose.Position.Y), HalfUlp(pose.Position.Z));
        double rotation = radius * (2 * Math.Sin(Math.Abs(angle) * 0.5) + 2 * normError);
        double local = Math.Sqrt(3) * 8 * HalfUlp(radius) + Norm(
            HalfUlp(Math.Abs(pose.Position.X) + radius), HalfUlp(Math.Abs(pose.Position.Y) + radius),
            HalfUlp(Math.Abs(pose.Position.Z) + radius));
        return origin + sourceRadius * Math.Abs(transform.Scale - scale) + rotation + local;
    }

    static double Norm(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);
    static double HalfUlp(double value)
    {
        float f = (float)Math.Abs(value);
        if (!float.IsFinite(f)) throw new MapDocumentException("Backend error bound is not representable.");
        return ((double)MathF.BitIncrement(f) - f) * 0.5;
    }
}
