using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class AtmosphereModel
{
    AtmosphereLightingCache? _lightingCache;
    bool _hasLighting;
    float _lightingHeightKm;
    AtmosphereFrame _lightingFrame;
    AtmosphereLighting _lighting;

    /// <summary>Completed unit-source lighting table builds. Camera and frame changes do not rebuild it.</summary>
    internal int LightingBuildCount { get; private set; }

    AtmosphereLightingCache LightingCache
    {
        get
        {
            if (_lightingCache is not null) return _lightingCache;
            _lightingCache = new AtmosphereLightingCache(this, _settings);
            LightingBuildCount++;
            return _lightingCache;
        }
    }

    /// <summary>Builds a physical frame from <see cref="SunCycle.Evaluate"/>, retaining the true sun
    /// independently of the active key. The legacy anti-solar key uses an opposite-sun moon source.
    /// Keyless nights disable moon illumination. Palette colours do not enter the frame.</summary>
    /// <exception cref="InvalidOperationException">The state was not produced by SunCycle.Evaluate.</exception>
    public AtmosphereFrame CreateFrame(in SunCycleState state)
    {
        var sample = state.AtmosphereSample ?? throw new InvalidOperationException(
            "Physical atmosphere requires a state produced by SunCycle.Evaluate.");
        bool antiSolar = sample.NightKey == NightKeyMode.AntiSolarMoon;
        var moonDirection = antiSolar ? -sample.SunDirection : -state.MoonDirection;
        float moonFraction = sample.NightKey == NightKeyMode.None ? 0 : _settings.MoonIlluminanceFraction;
        var source = antiSolar && state.SunElevationDegrees <= 0 ? KeyLightSource.Moon : state.ActiveSource;
        return new AtmosphereFrame(sample.SunDirection, moonDirection, moonFraction, Tint(sample.PaletteWeights),
            source, sample.KeyVisibility, sample.SunOpacity, sample.MoonOpacity);
    }

    /// <summary>Evaluates both bodies in engine Lambert units, with floors before a single tint.
    /// Memoizes the most recent frame and altitude so scene application and GPU packing share the
    /// two source transmittance integrals. Negative camera altitude clamps to the planet surface.</summary>
    public AtmosphereLighting EvaluateLighting(float cameraHeightKm, in AtmosphereFrame frame)
    {
        ValidateLightingInputs(cameraHeightKm, frame);
        float height = Math.Max(cameraHeightKm, 0);
        if (_hasLighting && _lightingHeightKm == height && _lightingFrame == frame) return _lighting;

        var sunDirection = UnitDirection(frame.SunDirection);
        var moonDirection = UnitDirection(frame.MoonDirection);
        var position = new Vector3(0, _settings.PlanetRadiusKm + height, 0);
        var sunTransmittance = Transmittance(position, sunDirection);
        var moonTransmittance = Transmittance(position, moonDirection);
        var sun = LightingCache.Sample(height, sunDirection.Y);
        var moon = LightingCache.Sample(height, moonDirection.Y);
        float illuminance = _settings.SunIlluminance;
        var irradiance = (sun.Ambient + moon.Ambient * frame.MoonFraction) * illuminance;
        var direct = (sunTransmittance * Math.Max(sunDirection.Y, 0)
            + moonTransmittance * (frame.MoonFraction * Math.Max(moonDirection.Y, 0))) * illuminance;
        var bounce = (sun.Bounce + moon.Bounce * frame.MoonFraction) * illuminance;
        var horizon = (sun.Horizon + moon.Horizon * frame.MoonFraction) * (illuminance * MathF.PI);
        var key = frame.KeySource switch
        {
            KeyLightSource.Sun => sunTransmittance * illuminance,
            KeyLightSource.Moon => moonTransmittance * (illuminance * frame.MoonFraction),
            _ => Vector3.Zero,
        };
        _lighting = new AtmosphereLighting(key * frame.KeyVisibility * frame.Tint,
            Vector3.Max(irradiance * _settings.AmbientScale, new Vector3(_settings.NightAmbientFloor)) * frame.Tint,
            (direct * _settings.GroundAlbedo + bounce) * _settings.FillScale * frame.Tint,
            Vector3.Max(horizon, new Vector3(_settings.NightSkyFloor)) * frame.Tint,
            sunTransmittance, moonTransmittance);
        _lightingHeightKm = height;
        _lightingFrame = frame;
        _hasLighting = true;
        return _lighting;
    }

    /// <summary>Directional sky radiance from independent sun and moon sources, converted with pi
    /// into engine Lambert units. The untinted sky floor precedes tint. Lighting scales do not apply.</summary>
    public Vector3 SkyValue(float cameraHeightKm, Vector3 direction, in AtmosphereFrame frame)
    {
        ValidateLightingInputs(cameraHeightKm, frame);
        var view = UnitDirection(direction);
        var radiance = SkyRadiance(cameraHeightKm, view, frame.SunDirection);
        if (frame.MoonFraction > 0)
            radiance += SkyRadiance(cameraHeightKm, view, frame.MoonDirection) * frame.MoonFraction;
        return ApplySkyControls(radiance, frame.Tint);
    }

    /// <summary>Zenith and ground sky values from the physical-settings lighting table. Both bodies
    /// interpolate by elevation and altitude before source brightness, sky floor and tint are applied.</summary>
    internal (Vector3 Zenith, Vector3 Ground) EvaluateSkyBands(float cameraHeightKm, in AtmosphereFrame frame)
    {
        ValidateLightingInputs(cameraHeightKm, frame);
        var sun = LightingCache.Sample(cameraHeightKm, UnitDirection(frame.SunDirection).Y);
        var moon = LightingCache.Sample(cameraHeightKm, UnitDirection(frame.MoonDirection).Y);
        return (ApplySkyControls(sun.Zenith + moon.Zenith * frame.MoonFraction, frame.Tint),
            ApplySkyControls(sun.Ground + moon.Ground * frame.MoonFraction, frame.Tint));
    }

    Vector3 ApplySkyControls(Vector3 radiance, Vector3 tint)
        => Vector3.Max(radiance * (_settings.SunIlluminance * MathF.PI),
            new Vector3(_settings.NightSkyFloor)) * tint;

    /// <summary>Blends the atmosphere art tints using SunCycle's day, dusk and night elevation weights.</summary>
    internal Vector3 Tint(Vector3 dayDuskNightWeights)
        => _settings.DayTint * dayDuskNightWeights.X + _settings.DuskTint * dayDuskNightWeights.Y
            + _settings.NightTint * dayDuskNightWeights.Z;

    static Vector3 UnitDirection(Vector3 direction)
    {
        var normalized = NormalizeDirection(direction);
        return new Vector3((float)normalized.X, (float)normalized.Y, (float)normalized.Z);
    }

    static void ValidateLightingInputs(float heightKm, in AtmosphereFrame frame)
    {
        if (!float.IsFinite(heightKm))
            throw new ArgumentOutOfRangeException(nameof(heightKm), "Atmosphere altitude must be finite.");
        _ = NormalizeDirection(frame.SunDirection);
        _ = NormalizeDirection(frame.MoonDirection);
        NonNegativeLightingValue(frame.MoonFraction, nameof(frame.MoonFraction));
        NonNegativeLightingValue(frame.KeyVisibility, nameof(frame.KeyVisibility));
        NonNegativeLightingValue(frame.Tint.X, nameof(frame.Tint));
        NonNegativeLightingValue(frame.Tint.Y, nameof(frame.Tint));
        NonNegativeLightingValue(frame.Tint.Z, nameof(frame.Tint));
    }

    static void NonNegativeLightingValue(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(name, "Atmosphere frame controls must be finite and non-negative.");
    }
}
