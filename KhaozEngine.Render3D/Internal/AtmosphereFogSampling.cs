using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Sky-only fog sampling. The final sky texel meets the geometric-horizon colour with zero
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

    /// <summary>For a view below the geometric horizon, returns the horizon direction the sky UV samples at the
    /// view's azimuth and the view's eased true direction. Depression eases in over one final sky texel span
    /// with zero value, slope and curvature, then joins the true depression with matching slope, so the
    /// downward colour keeps the horizon's zero-slope join. Views at or above the horizon return false.</summary>
    internal static bool BelowHorizon(Vector3 view, double radiusKm, float bottomKm, out Vector3 horizonView,
        out Vector3 easedView)
    {
        double horizon = HorizonAngle(radiusKm, bottomKm);
        double theta = Math.Acos(Math.Clamp(view.Y / Length(view), -1, 1));
        horizonView = easedView = default;
        if (theta <= horizon) return false;
        double cell = horizon / (HorizonRow * HorizonRow);
        double t = (theta - horizon) / cell;
        double zenith = t < 1 ? horizon + cell * t * t * t * (3 - 2 * t) : theta;
        double planar = Math.Sqrt((double)view.X * view.X + (double)view.Z * view.Z);
        double x = planar == 0 ? 1 : view.X / planar, z = planar == 0 ? 0 : view.Z / planar;
        var (horizonSin, horizonCos) = HorizonSinCos(radiusKm, bottomKm);
        horizonView = Direction(x, z, horizonSin, horizonCos);
        easedView = Direction(x, z, Math.Sin(zenith), Math.Cos(zenith));
        return true;
    }

    /// <summary>Sine and cosine of the geometric horizon's zenith angle. They are per-frame constants on the
    /// GPU, packed in the atmosphere uniforms' FogHorizon.</summary>
    internal static (double Sin, double Cos) HorizonSinCos(double radiusKm, float bottomKm)
    {
        double horizon = HorizonAngle(radiusKm, bottomKm);
        return (Math.Sin(horizon), Math.Cos(horizon));
    }

    static Vector3 Direction(double x, double z, double sin, double cos)
        => new((float)(x * sin), (float)cos, (float)(z * sin));

    // t is angular distance above the horizon in units of the final sky texel's angular span.
    // w(0)=w'(0)=w''(0)=0, w(1)=1 and w'(1)=1/2, matching sqrt(t) at the join.
    static double HorizonWeight(double t) => t < 1 ? t * t * t * (3.5 - 2.5 * t) : Math.Sqrt(t);

    static double HorizonAngle(double radius, double bottom)
        => Math.Acos(-Math.Sqrt(Math.Max((radius - bottom) * (radius + bottom), 0)) / radius);

    static double Length(Vector3 value)
        => Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);
}
