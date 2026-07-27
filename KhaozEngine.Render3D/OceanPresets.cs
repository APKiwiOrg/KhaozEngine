using System;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Ready-made ocean-surface bundles for <see cref="WaterSettings"/>. Each <see cref="OceanPresetKind"/> sets the
    /// Gerstner swell (amplitude, wavelength, steepness, speed), the ripple normal strength, the whitecap foam
    /// strength/coverage, and the sun glint as one coherent bundle, leaving <see cref="WaterSettings.GridMode"/>,
    /// the clipmap fields, <see cref="WaterSettings.Bathymetry"/>, and the surf fields untouched: those describe the
    /// water body's geometry and shoreline, not its weather, and stay whatever the consumer already configured.
    /// </summary>
    public static class OceanPresets
    {
        /// <summary>Applies <paramref name="kind"/>'s bundle to <paramref name="water"/>, overwriting the swell,
        /// ripple, foam, and glint fields it owns.</summary>
        public static void Apply(OceanPresetKind kind, WaterSettings water)
        {
            if (water is null) throw new ArgumentNullException(nameof(water));
            switch (kind)
            {
                case OceanPresetKind.Calm: ApplyCalm(water); break;
                case OceanPresetKind.Moderate: ApplyModerate(water); break;
                case OceanPresetKind.Rough: ApplyRough(water); break;
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown ocean preset.");
            }
        }

        static void ApplyCalm(WaterSettings water)
        {
            water.SwellAmplitude = 0.12f;
            water.SwellWavelength = 55f;
            water.SwellSteepness = 0.3f;
            water.SwellSpeed = 0.35f;
            water.NormalStrength = 0.15f;
            water.FoamStrength = 0.35f;
            water.FoamCrestCoverage = 0.3f;
            water.GlintStrength = 0.4f;
            water.GlintRoughness = 0.15f;
        }

        static void ApplyModerate(WaterSettings water)
        {
            // Close to WaterSettings' own defaults - the recommended map-editor default.
            water.SwellAmplitude = 0.45f;
            water.SwellWavelength = 42f;
            water.SwellSteepness = 0.6f;
            water.SwellSpeed = 0.6f;
            water.NormalStrength = 0.35f;
            water.FoamStrength = 0.85f;
            water.FoamCrestCoverage = 0.65f;
            water.GlintStrength = 0.6f;
            water.GlintRoughness = 0.22f;
        }

        static void ApplyRough(WaterSettings water)
        {
            water.SwellAmplitude = 1.2f;
            water.SwellWavelength = 50f;
            water.SwellSteepness = 0.95f;
            water.SwellSpeed = 0.9f;
            water.NormalStrength = 0.65f;
            water.FoamStrength = 1.0f;
            water.FoamCrestCoverage = 0.9f;
            water.GlintStrength = 0.75f;
            water.GlintRoughness = 0.35f;
        }
    }
}
