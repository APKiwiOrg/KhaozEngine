using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Sky-only fog sampling. The final sky texel meets the constant downward colour with zero
/// slope, then rejoins the sky LUT's square-root mapping with matching value and first derivative.</summary>
internal static class AtmosphereFogSampling
{
    const int HorizonRow = AtmosphereLutMapping.SkyHeight / 2 - 1;

    internal static Vector2 SkyUv(Vector3 view, double radiusKm, float bottomKm)
    {
        double length = Length(view);
        double horizon = HorizonAngle(radiusKm, bottomKm);
        double theta = Math.Acos(Math.Clamp(view.Y / length, -1, 1));
        double weight = HorizonWeight(Math.Max(horizon - theta, 0) * HorizonRow * HorizonRow / horizon);
        double u = view.X == 0 && view.Z == 0 ? 0 : Math.Atan2(view.Z, view.X) / (2 * Math.PI);
        return new Vector2((float)(u - Math.Floor(u)),
            (float)((HorizonRow + .5 - weight) / AtmosphereLutMapping.SkyHeight));
    }

    // t is angular distance above the horizon in units of the final sky texel's angular span.
    // w(0)=w'(0)=w''(0)=0, w(1)=1 and w'(1)=1/2, matching sqrt(t) at the join.
    static double HorizonWeight(double t) => t < 1 ? t * t * t * (3.5 - 2.5 * t) : Math.Sqrt(t);

    static double HorizonAngle(double radius, double bottom)
        => Math.Acos(-Math.Sqrt(Math.Max((radius - bottom) * (radius + bottom), 0)) / radius);

    static double Length(Vector3 value)
        => Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);
}
