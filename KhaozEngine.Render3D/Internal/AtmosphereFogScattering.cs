using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Per-frame constants that rescale the fog's sky-view sample to a downward view's true scattering
/// angle. The sample below the horizon is the horizon texel at the view's azimuth, so its Mie aureole belongs to
/// the wrong angle. Each source's in-scatter of a slab of the air at the camera's scaled altitude is evaluated at
/// both angles, and their per-channel ratio scales the sample. Views at and above the horizon keep it unchanged.
/// Mirrors atmosphereFogPhaseRatio in ShaderSources.AtmosphereFog.</summary>
internal readonly record struct AtmosphereFogScattering(Vector3 RayleighScattering, float MieScattering,
    Vector3 MultipleScattering, float MieAnisotropy, Vector3 SunDirection, Vector3 SunTransmittance,
    Vector3 MoonDirection, Vector3 MoonTransmittance, float MoonFraction)
{
    /// <summary>Evaluates both source transmittances along their directions at the camera altitude, as
    /// <see cref="AtmosphereModel.EvaluateLighting"/> does.</summary>
    internal static AtmosphereFogScattering Evaluate(AtmosphereSettings settings, AtmosphereModel model,
        in AtmosphereFrame frame, float cameraHeightKm)
    {
        var position = new Vector3(0, settings.PlanetRadiusKm + MathF.Max(cameraHeightKm, 0), 0);
        return Evaluate(settings, model, frame, cameraHeightKm, model.Transmittance(position, frame.SunDirection),
            model.Transmittance(position, frame.MoonDirection));
    }

    /// <summary>Uses source transmittances already evaluated for the frame's lighting. Scattering coefficients
    /// use the camera altitude through the aerial scale, like the fog's optical depth. The multiple-scattering
    /// source uses the physical altitude, like the sky-view LUT it corrects.</summary>
    internal static AtmosphereFogScattering Evaluate(AtmosphereSettings settings, AtmosphereModel model,
        in AtmosphereFrame frame, float cameraHeightKm, Vector3 sunTransmittance, Vector3 moonTransmittance)
    {
        float height = MathF.Max(cameraHeightKm, 0);
        var density = model.Density(height * settings.AerialDistanceScale);
        var rayleigh = settings.RayleighScatteringPerKm * density.X;
        float mie = settings.MieScatteringPerKm * density.Y;
        var sun = Vector3.Normalize(frame.SunDirection);
        var moon = Vector3.Normalize(frame.MoonDirection);
        var multiple = model.MultiScattering(height, sun.Y);
        if (frame.MoonFraction > 0) multiple += model.MultiScattering(height, moon.Y) * frame.MoonFraction;
        return new AtmosphereFogScattering(rayleigh, mie, multiple * (rayleigh + new Vector3(mie)),
            settings.MieAnisotropy, sun, sunTransmittance, moon, moonTransmittance, frame.MoonFraction);
    }

    /// <summary>Per-channel in-scatter at the view's eased true angle over in-scatter at the sample's angle.
    /// Exactly one for views at or above the geometric horizon and in vacuum. A nadir view uses the +X horizon
    /// sample, matching the sky UV's azimuth.</summary>
    internal Vector3 Ratio(Vector3 view, double radiusKm, float bottomKm)
    {
        if (!AtmosphereFogSampling.BelowHorizon(view, radiusKm, bottomKm, out var horizonView, out var easedView))
            return Vector3.One;
        var actual = Source(easedView);
        var sample = Source(horizonView);
        return new Vector3(Divide(actual.X, sample.X), Divide(actual.Y, sample.Y), Divide(actual.Z, sample.Z));
    }

    Vector3 Source(Vector3 direction)
    {
        float sunMu = Vector3.Dot(direction, SunDirection), moonMu = Vector3.Dot(direction, MoonDirection);
        return SunTransmittance * Phase(sunMu) + MoonTransmittance * (Phase(moonMu) * MoonFraction) + MultipleScattering;
    }

    Vector3 Phase(float mu) => RayleighScattering * AtmosphereModel.RayleighPhase(mu)
        + new Vector3(MieScattering * AtmosphereModel.MiePhase(mu, MieAnisotropy));

    static float Divide(float actual, float sample) => sample > 0 ? actual / sample : 1;
}
