using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Unit-source sky irradiance at zero source azimuth. The elevation and quadratic altitude
/// grid, including physical-altitude interpolation, matches AtmosphereLightingCache.</summary>
internal sealed class AtmosphereIrradianceCache
{
    const int ElevationCount = 181;
    const int HeightCount = 8;
    readonly Coefficients[] _values = new Coefficients[ElevationCount * HeightCount];
    readonly float[] _heights = new float[HeightCount];

    internal AtmosphereIrradianceCache(AtmosphereModel model, AtmosphereSettings settings,
        AtmosphereLightingCache lighting)
    {
        for (int h = 0; h < HeightCount; h++)
        {
            double fraction = h / (double)(HeightCount - 1);
            float heightKm = (float)(settings.AtmosphereHeightKm * fraction * fraction);
            _heights[h] = heightKm;
            for (int e = 0; e < ElevationCount; e++)
            {
                double angle = (e - 90) * Math.PI / 180;
                var source = new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0);
                var projection = new IrradianceProjection();
                for (int cosine = 0; cosine < 8; cosine++)
                {
                    float mu = (cosine + .5f) / 8;
                    double horizontal = Math.Sqrt(Math.Max(1 - (double)mu * mu, 0));
                    // Mirrored azimuths share radiance and all six retained, even coefficients.
                    for (int azimuth = 0; azimuth < 8; azimuth++)
                    {
                        double phi = (azimuth + .5) * 2 * Math.PI / 16;
                        var direction = new Vector3((float)(horizontal * Math.Cos(phi)), mu,
                            (float)(horizontal * Math.Sin(phi)));
                        projection.Add(direction, model.SkyRadiance(heightKm, direction, source), MathF.PI / 32);
                    }
                }

                var irradiance = projection.ToIrradiance();
                // Cancel L2 sky leakage at the downward pole without changing the upward pole.
                var downward = irradiance.EvaluateUnclamped(-Vector3.UnitY);
                irradiance += IrradianceSH.SkyAndGround(Vector3.Zero, -downward);
                var upward = irradiance.EvaluateUnclamped(Vector3.UnitY);
                var ambient = lighting.Sample(heightKm, source.Y).Ambient;
                var scale = new Vector3(upward.X == 0 ? 1 : ambient.X / upward.X,
                    upward.Y == 0 ? 1 : ambient.Y / upward.Y,
                    upward.Z == 0 ? 1 : ambient.Z / upward.Z);
                _values[h * ElevationCount + e] = Coefficients.From(irradiance.Scale(scale));
            }
        }
    }

    internal IrradianceSH Sample(float heightKm, float sourceMu)
    {
        float height = Math.Clamp(heightKm, 0, _heights[HeightCount - 1]);
        int h0 = 0;
        while (h0 < HeightCount - 2 && height > _heights[h0 + 1]) h0++;
        int h1 = h0 + 1;
        float heightWeight = (height - _heights[h0]) / (_heights[h1] - _heights[h0]);
        float elevation = Math.Clamp(MathF.Asin(Math.Clamp(sourceMu, -1, 1)) * 180 / MathF.PI + 90,
            0, ElevationCount - 1);
        int e0 = (int)elevation, e1 = Math.Min(e0 + 1, ElevationCount - 1);
        var lower = Coefficients.Lerp(_values[h0 * ElevationCount + e0], _values[h0 * ElevationCount + e1], elevation - e0);
        var upper = Coefficients.Lerp(_values[h1 * ElevationCount + e0], _values[h1 * ElevationCount + e1], elevation - e0);
        return Coefficients.Lerp(lower, upper, heightWeight).Expand();
    }

    // Reflection in the source's vertical plane removes coefficients 2, 4 and 5.
    readonly record struct Coefficients(Vector3 E0, Vector3 E1, Vector3 E3, Vector3 E6, Vector3 E7, Vector3 E8)
    {
        internal static Coefficients From(IrradianceSH value)
            => new(value.Coefficient(0), value.Coefficient(1), value.Coefficient(3),
                value.Coefficient(6), value.Coefficient(7), value.Coefficient(8));

        internal static Coefficients Lerp(Coefficients a, Coefficients b, float weight)
            => new(Vector3.Lerp(a.E0, b.E0, weight), Vector3.Lerp(a.E1, b.E1, weight),
                Vector3.Lerp(a.E3, b.E3, weight), Vector3.Lerp(a.E6, b.E6, weight),
                Vector3.Lerp(a.E7, b.E7, weight), Vector3.Lerp(a.E8, b.E8, weight));

        internal IrradianceSH Expand()
        {
            Span<Vector3> coefficients = stackalloc Vector3[IrradianceSH.CoefficientCount]
            {
                E0, E1, Vector3.Zero, E3, Vector3.Zero, Vector3.Zero, E6, E7, E8,
            };
            return new IrradianceSH(coefficients);
        }
    }
}
