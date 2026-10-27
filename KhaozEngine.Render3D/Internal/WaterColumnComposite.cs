using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>CPU reference for the four-stratum column transport and its composition in fogged space.</summary>
internal static class WaterColumnComposite
{
    internal static Vector3 Transmitted(in WaterColumnCoefficients c, Vector3 bed, Vector3 sun, Vector3 ambient,
        float viewCosine, float sunCosine, float distance, ReadOnlySpan<float> visibility, float jitter,
        float surfaceOffset = 0f)
    {
        if (visibility.Length != 4) throw new ArgumentException("Exactly four entry visibilities are required.", nameof(visibility));
        if (distance <= 0f) return bed;
        // Jitter selects the positions at which the caller obtains visibility, not the exact stratum weights.
        _ = jitter;
        float mu = MathF.Max(viewCosine, 0.0001f), mu0 = MathF.Max(sunCosine, 0.0001f);
        if (sunCosine <= 0f) sun = Vector3.Zero;
        Vector3 weights = Vector3.Zero;
        float meanVisibility = 0f, start = 0f;
        for (int i = 0; i < 4; i++)
        {
            float end = i == 3 ? distance : SampleDistance(c.Extinction, mu, mu0, distance, surfaceOffset, (i + 1f) / 4f);
            for (int channel = 0; channel < 3; channel++)
                weights[channel] += Integral(c.Extinction[channel], mu, mu0, surfaceOffset, start, end) * visibility[i];
            meanVisibility += visibility[i] * 0.25f;
            start = end;
        }
        Vector3 result = Vector3.Zero;
        for (int channel = 0; channel < 3; channel++)
        {
            float sigma = c.Extinction[channel], a = c.Albedo[channel];
            float singleSun = sun[channel] * c.Scattering[channel] * 0.25f * weights[channel];
            float singleAmbient = ambient[channel] * c.Scattering[channel] * 0.25f
                * Integral(sigma, mu, 1f, surfaceOffset, 0f, distance);
            float multiSun = sun[channel] * a * mu0 / (4f * (mu + mu0))
                * (WaterColumnOptics.H(a, mu) * WaterColumnOptics.H(a, mu0) - 1f)
                * FiniteDepth(sigma, mu, mu0, distance, surfaceOffset);
            float multiSky = ambient[channel] * a / (4f * (mu + 1f))
                * (WaterColumnOptics.H(a, mu) * WaterColumnOptics.H(a, 1f) - 1f)
                * FiniteDepth(sigma, mu, 1f, distance, surfaceOffset);
            result[channel] = bed[channel] * MathF.Exp(-sigma * distance) + singleSun + singleAmbient
                + (multiSun + multiSky) * meanVisibility;
        }
        return result;
    }

    /// <summary>Own radiance is already weighted by Fresnel and foam coverage. The bed copy is already fogged.</summary>
    internal static Vector3 ComposeFogged(Vector3 copiedBed, Vector3 transmittance, Vector3 ownRadiance,
        float fresnel, float foamCoverage, float shoreFade, Vector3 fogTransmittance, Vector3 fogColor)
    {
        if (shoreFade == 0f) return copiedBed;
        Vector3 weight = transmittance * ((1f - foamCoverage) * (1f - fresnel));
        Vector3 inside = copiedBed * weight + ownRadiance * fogTransmittance
            + fogColor * (Vector3.One - fogTransmittance) * (Vector3.One - weight);
        return Vector3.Lerp(copiedBed, inside, shoreFade);
    }

    // Remove the constant surface attenuation from the scalar CDF. It cancels during normalization and can
    // underflow at minimum clarity below a displaced trough, even though the normalized CDF is well defined.
    internal static float SampleDistance(Vector3 extinction, float mu, float mu0, float distance, float offset, float quantile)
    {
        if (quantile <= 0f) return 0f;
        if (quantile >= 1f) return distance;
        float sigma = (extinction.X + extinction.Y + extinction.Z) / 3f;
        float k = sigma * (1f + mu / mu0);
        if (MathF.Abs(k * distance) < 0.0001f) return quantile * distance;
        float split = Math.Clamp(-offset / mu, 0f, distance);
        float above = Q(sigma, split), belowScale = MathF.Exp(-sigma * split);
        float mass = quantile * (above + belowScale * Q(k, distance - split));
        float sample = mass <= above ? InvertQ(sigma, mass) : split + InvertQ(k, (mass - above) / belowScale);
        return Math.Clamp(sample, 0f, distance);
    }

    static float Integral(float sigma, float mu, float mu0, float offset, float start, float end)
    {
        float split = Math.Clamp(-offset / mu, start, end);
        float above = MathF.Exp(-sigma * start) * Q(sigma, split - start);
        float h = MathF.Max(0f, offset + mu * split);
        float below = MathF.Exp(-sigma * (split + h / mu0)) * Q(sigma * (1f + mu / mu0), end - split);
        return above + below;
    }

    static float Q(float k, float distance)
    {
        float x = k * distance;
        return MathF.Abs(x) < 0.0001f ? distance * (1f - x * 0.5f + x * x / 6f) : (1f - MathF.Exp(-x)) / k;
    }

    static float InvertQ(float k, float mass)
    {
        float x = k * mass;
        return MathF.Abs(x) < 0.0001f ? mass * (1f + x * 0.5f + x * x / 3f)
            : -MathF.Log(MathF.Max(1f - x, 1e-30f)) / k;
    }

    static float FiniteDepth(float sigma, float mu, float mu0, float distance, float offset)
    {
        float h0 = MathF.Max(0f, offset), hd = MathF.Max(0f, offset + mu * distance);
        float x = sigma * (distance + (hd - h0) / mu0);
        float finite = MathF.Abs(x) < 0.0001f ? x * (1f - x * 0.5f + x * x / 6f) : 1f - MathF.Exp(-x);
        return MathF.Exp(-sigma * h0 / mu0) * finite;
    }

    internal static Vector3 EntryPoint(Vector3 sample, Vector3 sunTravel, float stillSurfaceY)
    {
        float depth = MathF.Max(0f, stillSurfaceY - sample.Y);
        Vector3 entry = sample - sunTravel * (depth / MathF.Max(-sunTravel.Y, 0.0001f));
        entry.Y = MathF.Max(sample.Y, stillSurfaceY);
        return entry;
    }

    internal static bool TryIncidentDirection(Matrix4x4 inverseViewProjection, Vector2 ndc, out Vector3 direction)
    {
        direction = default;
        if (!TryUnproject(0f, out Vector3 near) || !TryUnproject(0.5f, out Vector3 far)) return false;
        Vector3 delta = far - near;
        float length = delta.Length();
        if (!float.IsFinite(length) || length <= 1e-8f) return false;
        direction = delta / length;
        return true;

        bool TryUnproject(float depth, out Vector3 point)
        {
            point = default;
            Vector4 p = Vector4.Transform(new Vector4(ndc, depth, 1f), inverseViewProjection);
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)
                || !float.IsFinite(p.W) || MathF.Abs(p.W) <= 1e-8f) return false;
            point = new Vector3(p.X, p.Y, p.Z) / p.W;
            return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
        }
    }
}
