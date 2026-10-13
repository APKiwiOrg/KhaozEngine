using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Immutable CPU atmosphere LUTs belonging to one retained settings snapshot. The
/// transmittance table is completed before the zero-recursion multi-scattering build starts.</summary>
internal sealed class AtmosphereTables
{
    internal const int TransmittanceWidth = 256;
    internal const int TransmittanceHeight = 64;
    internal const int MultiSize = 32;

    readonly Vector3[] _transmittance = new Vector3[TransmittanceWidth * TransmittanceHeight];
    readonly Vector3[] _multiScattering = new Vector3[MultiSize * MultiSize];
    readonly float _bottomKm;
    readonly float _topKm;
    readonly float _heightKm;

    internal ReadOnlySpan<Vector3> Transmittance => _transmittance;
    internal ReadOnlySpan<Vector3> MultiScattering => _multiScattering;

    internal AtmosphereTables(AtmosphereModel model, AtmosphereSettings settings)
    {
        _bottomKm = settings.PlanetRadiusKm;
        _heightKm = settings.AtmosphereHeightKm;
        _topKm = _bottomKm + _heightKm;
        BuildTransmittance(model);
        BuildMultiScattering(model);
    }

    /// <summary>Clamped bilinear transmission for a source path not occluded by the planet.
    /// The ray marcher evaluates planet visibility before sampling this table.</summary>
    internal Vector3 SampleTransmittance(float radiusKm, float mu)
        => Bilinear(_transmittance, TransmittanceWidth, TransmittanceHeight,
            AtmosphereLutMapping.TransmittanceUv(radiusKm, mu, _bottomKm, _topKm));

    /// <summary>Clamped bilinear isotropic multiple-scattering source radiance for unit illuminance.</summary>
    internal Vector3 SampleMulti(float heightKm, float mu)
        => Bilinear(_multiScattering, MultiSize, MultiSize, AtmosphereLutMapping.MultiUv(heightKm, mu, _heightKm));

    void BuildTransmittance(AtmosphereModel model)
    {
        for (int y = 0; y < TransmittanceHeight; y++)
            for (int x = 0; x < TransmittanceWidth; x++)
            {
                var uv = new Vector2((x + .5f) / TransmittanceWidth, (y + .5f) / TransmittanceHeight);
                var parameters = AtmosphereLutMapping.TransmittanceParameters(uv, _bottomKm, _topKm);
                var direction = UnoccludedTransmittanceDirection(parameters.X, parameters.Y);
                _transmittance[y * TransmittanceWidth + x] = model.Transmittance(
                    new Vector3(0, parameters.X, 0), direction);
            }
    }

    Vector3 UnoccludedTransmittanceDirection(float radiusKm, float mu)
    {
        double radius = radiusKm, bottom = _bottomKm;
        double horizon = -Math.Sqrt(Math.Max(1 - bottom * bottom / (radius * radius), 0));
        double cosine = Math.Max(mu, horizon);
        float roundedCosine = (float)cosine;
        // Mapping parameters round to floats. Keep a tangent endpoint on the unblocked side
        // after rounding both direction components and normalizing them in Transmittance.
        if (cosine <= horizon + 1e-7 && horizon < 0) roundedCosine = MathF.BitIncrement(roundedCosine);
        return new Vector3((float)Math.Sqrt(Math.Max(1 - (double)roundedCosine * roundedCosine, 0)), roundedCosine, 0);
    }

    void BuildMultiScattering(AtmosphereModel model)
    {
        for (int y = 0; y < MultiSize; y++)
            for (int x = 0; x < MultiSize; x++)
            {
                var uv = new Vector2((x + .5f) / MultiSize, (y + .5f) / MultiSize);
                var parameters = AtmosphereLutMapping.MultiParameters(uv, _heightKm);
                double lightMu = parameters.Y;
                var light = (X: Math.Sqrt(Math.Max(1 - lightMu * lightMu, 0)), Y: lightMu, Z: 0.0);
                var position = (X: 0.0, Y: (double)_bottomKm + parameters.X, Z: 0.0);
                var radiance = Vector3.Zero;
                var response = Vector3.Zero;
                for (int z = 0; z < 8; z++)
                    for (int azimuth = 0; azimuth < 8; azimuth++)
                    {
                        var direction = EqualSolidAngleDirection(z, azimuth);
                        var result = model.IntegrateScattering(position, direction, light, this, 20,
                            isotropic: true, includeMultiScattering: false);
                        radiance += result.Radiance;
                        response += result.Response;
                    }
                _multiScattering[y * MultiSize + x] = MultiScatteringSeries(radiance / 64, response / 64);
            }
    }

    static (double X, double Y, double Z) EqualSolidAngleDirection(int zCell, int azimuthCell)
    {
        double z = -1 + 2 * (zCell + .5) / 8;
        double azimuth = 2 * Math.PI * (azimuthCell + .5) / 8;
        double horizontal = Math.Sqrt(1 - z * z);
        return (horizontal * Math.Cos(azimuth), z, horizontal * Math.Sin(azimuth));
    }

    // Hillaire's PsiMS = mean(L2) / (1 - mean(fMS)), independently for each colour channel.
    static Vector3 MultiScatteringSeries(Vector3 meanRadiance, Vector3 meanResponse)
        => meanRadiance / (Vector3.One - Vector3.Min(meanResponse, new Vector3(.999f)));

    static Vector3 Bilinear(Vector3[] values, int width, int height, Vector2 uv)
    {
        float x = Math.Clamp(uv.X * width - .5f, 0, width - 1);
        float y = Math.Clamp(uv.Y * height - .5f, 0, height - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
        var lower = Vector3.Lerp(values[y0 * width + x0], values[y0 * width + x1], x - x0);
        var upper = Vector3.Lerp(values[y1 * width + x0], values[y1 * width + x1], x - x0);
        return Vector3.Lerp(lower, upper, y - y0);
    }
}
