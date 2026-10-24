using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class AtmosphereModel
{
    AtmosphereIrradianceCache? _irradianceCache;
    AtmosphereSettings? _irradianceCacheSettings;
    AtmosphereSettings? _irradianceSettings;
    float _irradianceHeightKm;
    AtmosphereFrame _irradianceFrame;
    IrradianceSH _irradiance;

    /// <summary>Completed unit-source irradiance table builds. Tables are built only on demand.</summary>
    internal int IrradianceBuildCount { get; private set; }

    AtmosphereIrradianceCache IrradianceCache
    {
        get
        {
            if (_irradianceCache is not null && _settings.PhysicalValueEquals(_irradianceCacheSettings))
                return _irradianceCache;
            var lighting = LightingCache;
            _irradianceCache = new AtmosphereIrradianceCache(this, _settings, lighting);
            _irradianceCacheSettings = _settings;
            IrradianceBuildCount++;
            return _irradianceCache;
        }
    }

    /// <summary>Evaluates directional sky irradiance and uniform ground bounce in engine Lambert units.
    /// Each vertical pole receives its own hemispherical night-floor shortfall. Memoizes the most recent
    /// frame and clamped altitude independently of EvaluateLighting, without per-frame allocations.</summary>
    public IrradianceSH EvaluateIrradiance(float cameraHeightKm, in AtmosphereFrame frame)
    {
        ValidateLightingInputs(cameraHeightKm, frame);
        float height = Math.Max(cameraHeightKm, 0);
        if (_irradianceSettings == _settings && _irradianceHeightKm == height && _irradianceFrame == frame)
            return _irradiance;

        var sun = UnitDirection(frame.SunDirection);
        var moon = UnitDirection(frame.MoonDirection);
        var cache = IrradianceCache;
        var sky = (cache.Sample(height, sun.Y).RotateY(Azimuth(sun))
            + cache.Sample(height, moon.Y).RotateY(Azimuth(moon)).Scale(new Vector3(frame.MoonFraction)))
            .Scale((_settings.SunIlluminance * _settings.AmbientScale) * frame.Tint);
        var total = sky + IrradianceSH.SkyAndGround(Vector3.Zero, EvaluateLighting(height, frame).Fill);
        var floor = _settings.NightAmbientFloor * _settings.NightTint;
        var upward = total.EvaluateUnclamped(Vector3.UnitY);
        var downward = total.EvaluateUnclamped(-Vector3.UnitY);
        _irradiance = total + IrradianceSH.SkyAndGround(Vector3.Max(floor - upward, Vector3.Zero),
            Vector3.Max(floor - downward, Vector3.Zero));
        _irradianceHeightKm = height;
        _irradianceFrame = frame;
        _irradianceSettings = _settings;
        return _irradiance;
    }

    static float Azimuth(Vector3 direction)
        => direction.X == 0 && direction.Z == 0 ? 0 : MathF.Atan2(direction.Z, direction.X);
}
