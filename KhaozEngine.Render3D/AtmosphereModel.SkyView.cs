using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class AtmosphereModel
{
    /// <summary>CPU sky-view lookup for fog. Samples the sky half of the 192 by 108 LUT with
    /// periodic azimuth and texel-centred bilinear filtering, then applies the shader's controls.</summary>
    internal Vector3 SkyViewValue(float cameraHeightKm, Vector2 skyUv, in AtmosphereFrame frame)
    {
        ValidateLightingInputs(cameraHeightKm, frame);
        const int width = 192, horizonRow = AtmosphereLutMapping.SkyHeight / 2 - 1;
        double x = (skyUv.X - Math.Floor(skyUv.X)) * width - .5;
        double y = Math.Clamp((double)skyUv.Y * AtmosphereLutMapping.SkyHeight - .5, 0, horizonRow);
        int left = (int)Math.Floor(x), top = (int)Math.Floor(y);
        int bottom = Math.Min(top + 1, horizonRow);
        float fx = (float)(x - left), fy = (float)(y - top);
        double radius = _settings.PlanetRadiusKm + Math.Max((double)cameraHeightKm, 0);
        var sun = NormalizeDirection(frame.SunDirection);
        var moon = NormalizeDirection(frame.MoonDirection);
        float moonFraction = frame.MoonFraction;

        Vector3 Texel(int column, int row)
        {
            double azimuth = 2 * Math.PI * (column + .5) / width;
            double tangentY = -Math.Sqrt(Math.Max((radius - _settings.PlanetRadiusKm)
                * (radius + _settings.PlanetRadiusKm), 0)) / radius;
            double horizon = Math.Acos(tangentY);
            double v = row / (double)horizonRow;
            double theta = horizon * (1 - (1 - v) * (1 - v));
            double vertical = row == horizonRow ? tangentY : Math.Cos(theta);
            double horizontal = Math.Sqrt(Math.Max(1 - vertical * vertical, 0));
            var view = (horizontal * Math.Cos(azimuth), vertical, horizontal * Math.Sin(azimuth));
            // Sky texel membership already proves this ray is unoccluded. Retesting its rounded
            // tangent would reintroduce a ground branch that the sky-only lookup cannot select.
            var radiance = IntegrateScattering((0, radius, 0), view, sun, Tables, 32,
                isotropic: false, includeMultiScattering: true, skyOnly: true).Radiance;
            if (moonFraction > 0)
                radiance += IntegrateScattering((0, radius, 0), view, moon, Tables, 32,
                    isotropic: false, includeMultiScattering: true, skyOnly: true).Radiance * moonFraction;
            return radiance;
        }

        var upper = Vector3.Lerp(Texel(left, top), Texel(left + 1, top), fx);
        var lower = bottom == top ? upper : Vector3.Lerp(Texel(left, bottom), Texel(left + 1, bottom), fx);
        return ApplySkyControls(Vector3.Lerp(upper, lower, fy), frame.Tint);
    }
}
