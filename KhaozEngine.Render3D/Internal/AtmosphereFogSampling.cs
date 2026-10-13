using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Sky-only fog sampling. The final sky texel meets the constant downward colour with zero
/// slope, then rejoins the sky LUT's square-root mapping with matching value and first derivative.</summary>
internal static class AtmosphereFogSampling
{
    const int HorizonRow = AtmosphereLutMapping.SkyHeight / 2 - 1;

    internal static Vector2 SkyUv(Vector3 view, float radiusKm, float bottomKm)
    {
        double length = Length(view);
        double horizon = HorizonAngle(radiusKm, bottomKm);
        double theta = Math.Acos(Math.Clamp(view.Y / length, -1, 1));
        double weight = HorizonWeight(Math.Max(horizon - theta, 0) * HorizonRow * HorizonRow / horizon);
        double u = view.X == 0 && view.Z == 0 ? 0 : Math.Atan2(view.Z, view.X) / (2 * Math.PI);
        return new Vector2((float)(u - Math.Floor(u)),
            (float)((HorizonRow + .5 - weight) / AtmosphereLutMapping.SkyHeight));
    }

    internal static Vector3 SkyDirection(Vector3 view, float heightKm, float bottomKm)
    {
        double radius = bottomKm + (double)heightKm;
        double horizon = HorizonAngle(radius, bottomKm);
        double theta = Math.Acos(Math.Clamp(view.Y / Length(view), -1, 1));
        double t = Math.Max(horizon - theta, 0) * HorizonRow * HorizonRow / horizon;
        if (t >= 1) return view;
        double weight = HorizonWeight(t);
        double elevation = horizon * weight * weight / (HorizonRow * HorizonRow);
        double tangentY = -Math.Sqrt(Math.Max((radius - bottomKm) * (radius + bottomKm), 0)) / radius;
        double y = tangentY * Math.Cos(elevation) + Math.Sqrt(1 - tangentY * tangentY) * Math.Sin(elevation);
        double horizontal = Math.Sqrt(Math.Max(1 - y * y, 0));
        double azimuthLength = Math.Sqrt((double)view.X * view.X + (double)view.Z * view.Z);
        var direction = azimuthLength == 0 ? new Vector3((float)horizontal, (float)y, 0)
            : new Vector3((float)(view.X / azimuthLength * horizontal), (float)y,
                (float)(view.Z / azimuthLength * horizontal));
        // Round the tangent toward the sky so float direction conversion cannot shorten the CPU ray
        // at a spurious planet intersection. No physical ground-hit tolerance is changed.
        if (direction.Y < 0) direction.Y = MathF.BitIncrement(direction.Y);
        return direction;
    }

    // t is angular distance above the horizon in units of the final sky texel's angular span.
    // w(0)=w'(0)=w''(0)=0, w(1)=1 and w'(1)=1/2, matching sqrt(t) at the join.
    static double HorizonWeight(double t) => t < 1 ? t * t * t * (3.5 - 2.5 * t) : Math.Sqrt(t);

    static double HorizonAngle(double radius, double bottom)
        => Math.Acos(-Math.Sqrt(Math.Max((radius - bottom) * (radius + bottom), 0)) / radius);

    static double Length(Vector3 value)
        => Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);
}
