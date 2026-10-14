using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Invertible coordinates for the physical atmosphere LUTs. Bounded axes use texel
/// centres, while sky azimuth remains periodic. Shader mappings must mirror these methods.</summary>
internal static class AtmosphereLutMapping
{
    const int TransmittanceWidth = 256;
    const int TransmittanceHeight = 64;
    const int MultiSize = 32;
    internal const int SkyHeight = 108;

    /// <summary>Maps radius and zenith cosine for a ray that is not blocked by the planet.</summary>
    public static Vector2 TransmittanceUv(float radiusKm, float mu, float bottomKm, float topKm)
    {
        double bottom = bottomKm, top = topKm;
        double r = Math.Clamp((double)radiusKm, bottom, top);
        double cosine = Math.Clamp((double)mu, -1, 1);
        double h = Math.Sqrt(Math.Max(top * top - bottom * bottom, 0));
        double rho = Math.Sqrt(Math.Max(r * r - bottom * bottom, 0));
        double d = -r * cosine + Math.Sqrt(Math.Max(r * r * (cosine * cosine - 1) + top * top, 0));
        double minimum = top - r;
        double range = rho + h - minimum;
        double x = range > 0 ? (d - minimum) / range : 0;
        double y = h > 0 ? rho / h : 0;
        return new Vector2(Sub(x, TransmittanceWidth), Sub(y, TransmittanceHeight));
    }

    /// <summary>Returns radius and zenith cosine. The zero-length top endpoint uses the outward pole.</summary>
    public static Vector2 TransmittanceParameters(Vector2 uv, float bottomKm, float topKm)
    {
        double bottom = bottomKm, top = topKm;
        double x = Unsub(uv.X, TransmittanceWidth), y = Unsub(uv.Y, TransmittanceHeight);
        double h = Math.Sqrt(Math.Max(top * top - bottom * bottom, 0));
        double rho = h * y;
        double r = Math.Sqrt(bottom * bottom + rho * rho);
        double minimum = top - r;
        double d = minimum + x * (rho + h - minimum);
        double mu = d > 0 ? (h * h - rho * rho - d * d) / (2 * r * d) : 1;
        return new Vector2((float)r, (float)Math.Clamp(mu, -1, 1));
    }

    /// <summary>Maps altitude and source zenith cosine into the 32 by 32 multi-scattering LUT.</summary>
    public static Vector2 MultiUv(float heightKm, float sunMu, float heightLimitKm)
        => new(Sub(((double)sunMu + 1) * .5, MultiSize), Sub(heightKm / (double)heightLimitKm, MultiSize));

    /// <summary>Returns altitude in kilometres and source zenith cosine.</summary>
    public static Vector2 MultiParameters(Vector2 uv, float heightLimitKm)
        => new((float)(Unsub(uv.Y, MultiSize) * heightLimitKm), (float)(Unsub(uv.X, MultiSize) * 2 - 1));

    /// <summary>Maps a finite nonzero direction with full azimuth and increased resolution at the
    /// planet horizon. Each V hemisphere has its own horizon texel, so filtering never mixes sky
    /// with ground radiance at the horizon.</summary>
    public static Vector2 SkyUv(Vector3 direction, float radiusKm, float bottomKm)
    {
        double x = direction.X, y = direction.Y, z = direction.Z;
        double length = Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(length) || length == 0)
            throw new ArgumentException("Sky direction must be finite and nonzero.", nameof(direction));
        double u = Wrap(Math.Atan2(z, x) / (2 * Math.PI));
        double theta = Math.Acos(Math.Clamp(y / length, -1, 1));
        double horizon = HorizonAngle(radiusKm, bottomKm);
        float v = theta <= horizon
            ? .5f * Sub(1 - Math.Sqrt(Math.Max(1 - theta / horizon, 0)), SkyHeight / 2)
            : .5f + .5f * Sub(Math.Sqrt(Math.Clamp((theta - horizon) / (Math.PI - horizon), 0, 1)), SkyHeight / 2);
        return new Vector2((float)u, v);
    }

    /// <summary>Returns a unit direction for periodic azimuth and bounded sky V.</summary>
    public static Vector3 SkyDirection(Vector2 uv, float radiusKm, float bottomKm)
    {
        double u = Wrap(uv.X);
        bool sky = uv.Y <= .5f;
        double v = Unsub(sky ? uv.Y * 2 : (uv.Y - .5f) * 2, SkyHeight / 2);
        // Use the producer's analytic float32 tangent for both duplicated horizon endpoints.
        if ((sky && v >= 1 - AtmosphereShaderMath.SkyHorizonEndpointEpsilon)
            || (!sky && v <= AtmosphereShaderMath.SkyHorizonEndpointEpsilon))
            return AtmosphereShaderMath.SkyDirection(uv, radiusKm, bottomKm);
        double horizon = HorizonAngle(radiusKm, bottomKm);
        double theta = sky ? horizon * (1 - (1 - v) * (1 - v))
            : horizon + (Math.PI - horizon) * v * v;
        double azimuth = 2 * Math.PI * u;
        double sine = Math.Sin(theta);
        return new Vector3((float)(sine * Math.Cos(azimuth)), (float)Math.Cos(theta), (float)(sine * Math.Sin(azimuth)));
    }

    static double HorizonAngle(float radiusKm, float bottomKm)
    {
        double r = Math.Max(radiusKm, bottomKm), bottom = bottomKm;
        return Math.Acos(Math.Clamp(-Math.Sqrt(Math.Max(r * r - bottom * bottom, 0)) / r, -1, 1));
    }

    static float Sub(double value, int size) => (float)((Math.Clamp(value, 0, 1) * (size - 1) + .5) / size);
    static double Unsub(float value, int size) => Math.Clamp(((double)value * size - .5) / (size - 1), 0, 1);
    static double Wrap(double value) => value - Math.Floor(value);
}
