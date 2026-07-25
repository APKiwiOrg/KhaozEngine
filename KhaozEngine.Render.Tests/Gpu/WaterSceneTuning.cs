using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Shared water tuning for the GPU scenes that render a lake (the <c>scene3d_water</c> golden and the HDR
    /// showcase composite). Both draw a ~10-unit-wide plane about a unit deep, and the engine's
    /// <see cref="WaterSettings"/> defaults are tuned for an OCEAN at metre scale: a 42-unit swell over a plane up
    /// to 1200 across, absorption that grades over metres, a foam pattern sized in metres. Inherited unchanged
    /// those knobs render a doll-house lake as a flat, uniformly shallow sheet, which is exactly the look this
    /// release replaced, so each scene would otherwise carry its own copy of the same 20-line rescale.
    /// <para>
    /// Only the WORLD-SPACE knobs are rescaled here. Everything unit-less (steepness, foam coverage, the
    /// reflection strengths, the component count) keeps its shipped default on purpose, so these scenes still
    /// exercise the defaults for every knob whose default is scale-independent.
    /// </para>
    /// </summary>
    internal static class WaterSceneTuning
    {
        /// <summary>
        /// Apply the doll-house rescale to <paramref name="water"/>.
        /// </summary>
        /// <param name="water">The scene's water settings.</param>
        /// <param name="uniformGrid">Pass <c>true</c> when the whole plane is on screen at once (both these scenes
        /// frame it from a corner): camera-focused vertex density would then spend the entire budget in one corner
        /// for no gain, so the grid stays uniform.</param>
        public static void ApplyLakeScale(WaterSettings water, bool uniformGrid)
        {
            water.SwellWavelength = 3f;        // several crests across a ~10-unit plane
            water.SwellAmplitude = 0.16f;      // ~0.3 peak-to-trough: a clear silhouette even at 480x320
            water.SwellDirectionDegrees = 30f;
            water.SwellSpreadDegrees = 55f;
            water.SwellSteepness = 0.6f;
            water.SwellSpeed = 0.6f;
            water.SwellComponents = 4;
            water.GridFocusBias = uniformGrid ? 1f : water.GridFocusBias;

            water.SkyReflectionStrength = 1f;
            water.SkyReflectionSunStrength = 0.35f;

            // Wider than the default near-field roughness: the default is about a 2-degree lobe, which lands on a
            // handful of pixels at these bake resolutions, and the sun path would not read at all.
            water.GlintRoughness = 0.35f;
            water.GlintDistantRoughness = 0.6f;

            // ~8x the default coefficients, so the same turquoise -> deep-blue walk happens over these scenes'
            // sub-unit depths instead of over metres.
            water.AbsorptionPerMetre = new Color(4.4f, 1.9f, 1.1f, 0f);

            water.FoamStrength = 0.85f;
            water.FoamCrestCoverage = 0.65f;
            water.FoamShoreWidth = 0.35f;      // scene-sized: the shallow shelf sits about this far under
            water.FoamPatternScale = 0.45f;
        }

        /// <summary>
        /// The complete water configuration for the <c>scene3d_water</c> golden: the shared lake rescale above
        /// plus this scene's own palette and pre-14.23.0 knobs, every one pinned explicit so a change to an engine
        /// DEFAULT cannot silently move a golden that is supposed to be locking rendering behaviour. Kept out of
        /// GoldenSnapshotTests because that file is at its size ratchet and this is a self-contained scene
        /// description, not test logic.
        /// </summary>
        public static void ApplyGoldenLake(WaterSettings water)
        {
            water.DeepColor = new Color(0.04f, 0.16f, 0.26f, 0.92f);
            water.HorizonColor = new Color(0.60f, 0.70f, 0.80f, 0.75f);
            water.ShallowColor = new Color(0.20f, 0.46f, 0.44f, 0.78f);
            water.WaveScale = 0.9f;            // tight enough that several ripple crests fit across the plane
            water.WaveSpeed = 0.4f;
            water.NormalStrength = 0.45f;      // softer than 14.22.0: the ripples ride a swell now, they are not the surface
            water.ShoreFadeDistance = 0.7f;
            water.GlintStrength = 0.8f;
            water.GlintExponent = 100f;        // only consulted if GlintRoughness is turned off; pinned anyway
            water.ShallowDepth = 0.45f;        // the legacy two-stop depth; absorption below supersedes it
            water.WaveWarpStrength = 0.75f;    // the three 14.22.0 defaults, pinned explicit
            water.DetailFadeDistance = 40f;    // camera ~9 units out: detail near full, fade live
            water.DistantDetailScale = 0.18f;
            ApplyLakeScale(water, uniformGrid: true);
        }
    }
}
