using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Unit-source hemisphere irradiance, its ground bounce, and mean geometric-horizon radiance.
/// Only physical settings are retained. Elevations span [-90,90] at one degree intervals, with eight
/// quadratic altitude slices spanning the atmosphere. Samples interpolate in physical altitude.</summary>
internal sealed class AtmosphereLightingCache
{
    const int ElevationCount = 181;
    const int HeightCount = 8;
    readonly UnitLighting[] _values = new UnitLighting[ElevationCount * HeightCount];
    readonly float[] _heights = new float[HeightCount];

    internal AtmosphereLightingCache(AtmosphereModel model, AtmosphereSettings settings)
    {
        for (int h = 0; h < HeightCount; h++)
        {
            double fraction = h / (double)(HeightCount - 1);
            float heightKm = (float)(settings.AtmosphereHeightKm * fraction * fraction);
            _heights[h] = heightKm;
            double radius = settings.PlanetRadiusKm + (double)heightKm;
            double bottom = settings.PlanetRadiusKm;
            float horizonMu = (float)(-Math.Sqrt(Math.Max(radius * radius - bottom * bottom, 0)) / radius);
            // The tangent is the sky-side limit. Stay above it after float direction normalization.
            horizonMu += 1e-7f;
            for (int e = 0; e < ElevationCount; e++)
            {
                double angle = (e - 90) * Math.PI / 180;
                var source = new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0);
                var irradiance = Vector3.Zero;
                for (int cosine = 0; cosine < 4; cosine++)
                {
                    float mu = (cosine + .5f) / 4;
                    for (int azimuth = 0; azimuth < 8; azimuth++)
                    {
                        var direction = Direction(mu, (azimuth + .5) * 2 * Math.PI / 8);
                        irradiance += model.SkyRadiance(heightKm, direction, source) * (mu * MathF.PI / 16);
                    }
                }

                var horizon = Vector3.Zero;
                for (int azimuth = 0; azimuth < 16; azimuth++)
                    horizon += model.SkyRadiance(heightKm,
                        Direction(horizonMu, (azimuth + .5) * 2 * Math.PI / 16), source);
                _values[h * ElevationCount + e] = new UnitLighting(irradiance,
                    irradiance * settings.GroundAlbedo, horizon / 16);
            }
        }
    }

    internal UnitLighting Sample(float heightKm, float sourceMu)
    {
        float height = Math.Clamp(heightKm, 0, _heights[HeightCount - 1]);
        int h0 = 0;
        while (h0 < HeightCount - 2 && height > _heights[h0 + 1]) h0++;
        int h1 = h0 + 1;
        float heightWeight = (height - _heights[h0]) / (_heights[h1] - _heights[h0]);
        float elevation = Math.Clamp(MathF.Asin(Math.Clamp(sourceMu, -1, 1)) * 180 / MathF.PI + 90,
            0, ElevationCount - 1);
        int e0 = (int)elevation, e1 = Math.Min(e0 + 1, ElevationCount - 1);
        var lower = Lerp(_values[h0 * ElevationCount + e0], _values[h0 * ElevationCount + e1], elevation - e0);
        var upper = Lerp(_values[h1 * ElevationCount + e0], _values[h1 * ElevationCount + e1], elevation - e0);
        return Lerp(lower, upper, heightWeight);
    }

    static Vector3 Direction(float mu, double azimuth)
    {
        double horizontal = Math.Sqrt(Math.Max(1 - (double)mu * mu, 0));
        return new Vector3((float)(horizontal * Math.Cos(azimuth)), mu, (float)(horizontal * Math.Sin(azimuth)));
    }

    static UnitLighting Lerp(UnitLighting a, UnitLighting b, float weight)
        => new(Vector3.Lerp(a.Ambient, b.Ambient, weight), Vector3.Lerp(a.Bounce, b.Bounce, weight),
            Vector3.Lerp(a.Horizon, b.Horizon, weight));

    internal readonly record struct UnitLighting(Vector3 Ambient, Vector3 Bounce, Vector3 Horizon);
}
