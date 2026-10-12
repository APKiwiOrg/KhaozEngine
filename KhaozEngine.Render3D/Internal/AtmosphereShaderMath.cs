using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Float32 mirrors of the atmosphere producer's ray setup and transmittance integration.
/// Keep the GLSL counterparts in ShaderSources.Atmosphere in sync, as with SkyMath.</summary>
internal static class AtmosphereShaderMath
{
    internal static Vector3 TransmittanceTexel(AtmosphereSettings settings, Vector2 uv)
    {
        var parameters = TransmittanceParameters(uv, settings.PlanetRadiusKm,
            settings.PlanetRadiusKm + settings.AtmosphereHeightKm);
        var direction = TransmittanceDirection(parameters.X, parameters.Y, settings.PlanetRadiusKm);
        return TransmittanceIntegral(settings, new Vector3(0f, parameters.X, 0f), direction);
    }

    internal static Vector2 TransmittanceParameters(Vector2 uv, float bottom, float top)
    {
        float x = Unsub(uv.X, 256f), y = Unsub(uv.Y, 64f);
        float h = MathF.Sqrt(MathF.Max((top - bottom) * (top + bottom), 0f));
        float rho = h * y, r = MathF.Sqrt(bottom * bottom + rho * rho);
        float minimum = top - r, d = minimum + x * (rho + h - minimum);
        float mu = d > 0f ? (h * h - rho * rho - d * d) / (2f * r * d) : 1f;
        return new Vector2(r, Math.Clamp(mu, -1f, 1f));
    }

    internal static Vector3 TransmittanceDirection(float r, float mu, float bottom)
    {
        float horizon = -MathF.Sqrt(MathF.Max((r - bottom) * (r + bottom), 0f)) / r;
        mu = MathF.Max(mu, horizon);
        var direction = new Vector3(MathF.Sqrt(MathF.Max(1f - mu * mu, 0f)), mu, 0f);
        return direction / Length(direction);
    }

    internal static bool PlanetOccludes(Vector3 p, Vector3 direction, float bottom)
    {
        float r = Length(p), projection = Dot(p, direction);
        float c = (r - bottom) * (r + bottom);
        // Eight float32 epsilons at radius-squared scale cover length and dot-product rounding.
        float epsilon = (8f * 1.1920929e-7f) * MathF.Max(r * r, bottom * bottom);
        return projection < 0f && projection * projection - c > epsilon;
    }

    static Vector3 TransmittanceIntegral(AtmosphereSettings settings, Vector3 p, Vector3 direction)
    {
        if (!SphereRoots(p, direction, settings.PlanetRadiusKm + settings.AtmosphereHeightKm, out var topRoots)
            || topRoots.Y <= 0f) return Vector3.One;
        float start = MathF.Max(topRoots.X, 0f), end = topRoots.Y;
        // LUT mapping guarantees an unoccluded ray. A rounded tangent must not be retested against ground.
        var depth = Vector3.Zero;
        float previous = start;
        for (int i = 0; i < 256; i++)
        {
            float fraction = (i + 1f) / 256f;
            float next = start + (end - start) * fraction * fraction;
            var point = p + direction * ((previous + next) * .5f);
            depth += Density(settings, Length(point) - settings.PlanetRadiusKm) * (next - previous);
            previous = next;
        }
        var extinction = settings.RayleighScatteringPerKm * depth.X
            + new Vector3(settings.MieExtinctionPerKm * depth.Y) + settings.OzoneAbsorptionPerKm * depth.Z;
        return new Vector3(MathF.Exp(-extinction.X), MathF.Exp(-extinction.Y), MathF.Exp(-extinction.Z));
    }

    static Vector3 Density(AtmosphereSettings settings, float height)
    {
        float h = MathF.Max(height, 0f);
        return new Vector3(MathF.Exp(-h / settings.RayleighScaleHeightKm),
            MathF.Exp(-h / settings.MieScaleHeightKm),
            MathF.Max(0f, 1f - MathF.Abs(h - settings.OzoneCenterKm) / (settings.OzoneWidthKm * .5f)));
    }

    static bool SphereRoots(Vector3 p, Vector3 direction, float radius, out Vector2 roots)
    {
        float projection = Dot(p, direction), r = Length(p);
        float c = (r - radius) * (r + radius);
        float discriminant = projection * projection - c;
        roots = Vector2.Zero;
        if (discriminant < 0f) return false;
        float q = -projection - (projection < 0f ? -1f : 1f) * MathF.Sqrt(discriminant);
        float other = q == 0f ? 0f : c / q;
        roots = new Vector2(MathF.Min(q, other), MathF.Max(q, other));
        return true;
    }

    static float Unsub(float value, float size) => Math.Clamp((value * size - .5f) / (size - 1f), 0f, 1f);
    static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static float Length(Vector3 value) => MathF.Sqrt(Dot(value, value));
}
