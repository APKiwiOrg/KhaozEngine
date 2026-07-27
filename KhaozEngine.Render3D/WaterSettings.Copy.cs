using System;

namespace KhaozEngine.Render3D
{
    // The field-wise copy lives in its own partial so the main WaterSettings.cs stays a documented list of knobs
    // and nothing else. The two files grow for different reasons (a new knob there, never here beyond one line),
    // and merging them would put a 56-line assignment block in the middle of the type every reader browses.
    public sealed partial class WaterSettings
    {
        /// <summary>
        /// Overwrite every field of this instance with <paramref name="source"/>'s. Used by
        /// <see cref="WaterLook.ResolveInto"/> to seed a scratch object with the scene-wide look before the
        /// per-plane overrides are written over the top, which is what lets a plane with no look pack from the
        /// caller's own settings object and stay byte-identical.
        /// <para>
        /// <b><see cref="SeaState"/> and <see cref="Bathymetry"/> are copied BY REFERENCE, on purpose.</b> Both
        /// back a once-per-frame GPU resource (the FFT bake and the depth texture), so there is exactly one of
        /// each per scene and a per-plane copy of either would be a lie: the scratch would carry values the
        /// producer never baked. Sharing the reference means a look cannot fork them even by accident, and it is
        /// what the "not forkable" test pins. Deep-copying them here would be the bug, not the fix.
        /// </para>
        /// </summary>
        /// <param name="source">The settings to copy from. Never modified.</param>
        public void CopyFrom(WaterSettings source)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            WaveSource = source.WaveSource;
            SeaState = source.SeaState;

            DeepColor = source.DeepColor;
            ShallowColor = source.ShallowColor;
            AbsorptionPerMetre = source.AbsorptionPerMetre;
            ShallowDepth = source.ShallowDepth;
            Opacity = source.Opacity;

            HorizonColor = source.HorizonColor;
            SkyReflectionStrength = source.SkyReflectionStrength;
            SkyReflectionSunStrength = source.SkyReflectionSunStrength;

            SwellAmplitude = source.SwellAmplitude;
            SwellWavelength = source.SwellWavelength;
            SwellDirectionDegrees = source.SwellDirectionDegrees;
            SwellSpreadDegrees = source.SwellSpreadDegrees;
            SwellSteepness = source.SwellSteepness;
            SwellSpeed = source.SwellSpeed;
            SwellSeed = source.SwellSeed;
            SwellComponents = source.SwellComponents;

            GridFocusBias = source.GridFocusBias;
            GridMode = source.GridMode;
            ClipmapCellSize = source.ClipmapCellSize;
            ClipmapRingCells = source.ClipmapRingCells;
            ClipmapLevels = source.ClipmapLevels;
            ClipmapBandLimitSamples = source.ClipmapBandLimitSamples;
            ClipmapGeomorphBand = source.ClipmapGeomorphBand;

            Bathymetry = source.Bathymetry;
            ShoalingStrength = source.ShoalingStrength;
            ShoalingDepthScale = source.ShoalingDepthScale;
            SurfStrength = source.SurfStrength;
            SurfBreakerIndex = source.SurfBreakerIndex;
            SurfBandWidth = source.SurfBandWidth;
            SurfCrestBias = source.SurfCrestBias;
            SurfTrailWidth = source.SurfTrailWidth;
            SurfAmplitudeCollapse = source.SurfAmplitudeCollapse;

            WaveScale = source.WaveScale;
            WaveSpeed = source.WaveSpeed;
            NormalStrength = source.NormalStrength;
            WaveWarpStrength = source.WaveWarpStrength;
            RippleComponents = source.RippleComponents;
            RippleLacunarity = source.RippleLacunarity;
            RippleGain = source.RippleGain;
            RippleSeed = source.RippleSeed;
            FootprintSamples = source.FootprintSamples;
            VarianceToRoughness = source.VarianceToRoughness;
            DetailFadeDistance = source.DetailFadeDistance;
            DistantDetailScale = source.DistantDetailScale;

            GlintStrength = source.GlintStrength;
            GlintRoughness = source.GlintRoughness;
            GlintDistantRoughness = source.GlintDistantRoughness;
            GlintExponent = source.GlintExponent;

            FoamColor = source.FoamColor;
            FoamStrength = source.FoamStrength;
            FoamCrestCoverage = source.FoamCrestCoverage;
            FoamShoreWidth = source.FoamShoreWidth;
            FoamPatternScale = source.FoamPatternScale;

            ShoreFadeDistance = source.ShoreFadeDistance;
        }
    }
}
