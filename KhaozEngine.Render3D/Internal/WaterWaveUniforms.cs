using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The uniform vectors the swell and ripple shader blocks read by name. The water pass and the caustic
/// surface field pack them here, so the bands track the surface the water pass animates.</summary>
internal readonly record struct WaterWaveUniforms(Vector4 WaveParams, Vector4 DetailParams, Vector4 SwellParams,
    Vector4 SwellShape, Vector4 RippleSpectrum, Vector4 FootprintParams)
{
    internal static WaterWaveUniforms From(WaterSettings settings, float timeSeconds) => new(
        new Vector4(settings.WaveScale, settings.WaveSpeed, settings.NormalStrength, timeSeconds),
        new Vector4(settings.WaveWarpStrength, settings.DetailFadeDistance,
            settings.DistantDetailScale, settings.ShallowDepth),
        new Vector4(settings.SwellAmplitude, settings.SwellWavelength,
            GerstnerWaves.DegreesToRadians(settings.SwellDirectionDegrees),
            GerstnerWaves.DegreesToRadians(settings.SwellSpreadDegrees)),
        new Vector4(settings.SwellSteepness, settings.SwellSpeed,
            Math.Clamp(settings.SwellComponents, 1, GerstnerWaves.MaxComponents), settings.SwellSeed),
        new Vector4(Math.Clamp(settings.RippleComponents, 1, Internal.RippleSpectrum.MaxComponents),
            settings.RippleLacunarity, settings.RippleGain, settings.RippleSeed),
        new Vector4(settings.FootprintSamples, settings.VarianceToRoughness,
            MathF.Max(settings.ClipmapBandLimitSamples, 1f), 0f));
}
