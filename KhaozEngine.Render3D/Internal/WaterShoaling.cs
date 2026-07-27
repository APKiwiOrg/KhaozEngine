using System;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure, GPU-free mirror of the depth-driven half of the water surface: the per-cascade shoaling taper, the
    /// breaker criterion, the surf band's depth ramp and the crest-phase surge. The GLSL in
    /// <see cref="ShaderSources"/> (the <c>WaterShore</c> partial) MUST mirror this exactly, the same contract
    /// <see cref="WaterMath"/> holds against <c>WaterFrag</c> and <see cref="GerstnerWaves"/> against the swell.
    /// <para>
    /// All of it is keyed on ONE input the surface never had: the local water depth, supplied by the consumer as
    /// <see cref="WaterBathymetry"/>. With no depth field bound every function here returns its identity by an
    /// EARLY RETURN rather than by arithmetic that happens to land on it, so an ocean with no bathymetry is
    /// bit-identical to one built before this existed.
    /// </para>
    /// <para>
    /// <b>The taper is stylized, and deliberately the opposite of textbook shoaling.</b> Linear theory says a wave
    /// coming into shallow water GROWS: the group velocity falls, energy flux is conserved, and the amplitude goes
    /// up by <c>sqrt(cg_deep / cg_local)</c> until it breaks. That is true and it is not what a game wants at the
    /// waterline, where the surface has to settle down to meet the beach instead of piling up against it. So the
    /// factor here is <c>tanh(k d)</c>, which is the same quantity read the other way round: it is 1 wherever the
    /// wave cannot feel the bottom and falls toward <c>k d</c> as the bottom comes up. Because <c>k</c> is
    /// per-cascade, the long swell (small <c>k</c>) starts calming in metres of depth where the chop (large
    /// <c>k</c>) is still at full strength, which is what a real lee shore looks like even though the mechanism
    /// is not the real one.
    /// </para>
    /// </summary>
    internal static class WaterShoaling
    {
        /// <summary>Ceiling on the <c>tanh</c> argument, shared with <see cref="OceanSpectrum.TanhArgumentLimit"/>
        /// and mirrored by <c>KE_SHOAL_TANH_LIMIT</c> in the shaders. A hardware <c>tanh</c> is commonly
        /// <c>(exp(2x) - 1) / (exp(2x) + 1)</c>, which overflows to <c>inf/inf</c> = NaN well before the argument
        /// does, and the finest cascade against a 60 metre depth reaches an argument in the hundreds.</summary>
        public const float TanhArgumentLimit = OceanSpectrum.TanhArgumentLimit;

        /// <summary>How steeply the surface slope along the up-beach direction gates the foam TRAIL: the slope is
        /// a dimensionless gradient, and a breaking face runs at a few tenths, so this is the gain that turns
        /// "sloping back toward the sea at all" into full coverage. Mirrored by <c>KE_SURF_BACK_GAIN</c>.</summary>
        public const float BackFaceGain = 6f;

        /// <summary>Depth reported outside the bathymetry rectangle, in metres: far past anything
        /// <see cref="Attenuation"/> or <see cref="SurfBand"/> reacts to, so a field that covers only the coast
        /// leaves open water exactly as it was. Mirrored by <c>KE_BATHY_DEEP</c>.</summary>
        public const float DeepMetres = 1e4f;

        /// <summary>Highest <see cref="WaterSettings.SurfCrestBias"/> the surge gate will take: at 1 the crest
        /// smoothstep would be degenerate (its two edges equal) and the band would go black.</summary>
        public const float MaxCrestBias = 0.95f;

        /// <summary>Where in the band the phase gate hands over to a solid waterline wash: below this the foam is
        /// purely crest-locked, above it the surf goes unconditionally white. Mirrored by
        /// <c>KE_SURF_WASH_START</c>.</summary>
        public const float WashStart = 0.6f;

        /// <summary>How far into the band the foam reaches FULL coverage, so the band decides WHERE the surf can
        /// happen and the surge decides how much of it there is. Multiplying two soft ramps together instead
        /// leaves a grey wash that never reaches white anywhere - which is what the first probe render of this
        /// feature drew. Mirrored by <c>KE_SURF_BAND_GATE</c>.</summary>
        public const float BandGate = 0.25f;

        /// <summary>Floor on the local wave amplitude the crest gate normalizes against, as a fraction of half the
        /// significant wave height. Without it the ratio is 0/0 at the waterline, where the taper has taken the
        /// amplitude to nothing. Mirrored by <c>KE_SURF_AMP_FLOOR</c>.</summary>
        public const float AmplitudeFloor = 0.1f;

        /// <summary>
        /// Significant wave height of a sea state, metres: the usual <c>Hs = 4 sqrt(m0)</c> over the total height
        /// variance of every cascade. It is what the breaker criterion measures the depth against, and it comes
        /// out of the same bake loop that already produces the slope variance, so it costs nothing.
        /// </summary>
        public static float SignificantHeight(float totalHeightVariance)
            => 4f * MathF.Sqrt(MathF.Max(totalHeightVariance, 0f));

        /// <summary>
        /// Depth at which a sea of <paramref name="significantHeight"/> starts breaking, metres. The classic
        /// shallow-water criterion is <c>H / d = gamma</c> with <c>gamma</c> near 0.78, so the break line sits at
        /// <c>d = H / gamma</c>.
        /// <para>
        /// It is measured against the UNSHOALED height on purpose. Feeding the shoaled height back in makes the
        /// criterion degenerate: in the shallow limit <c>tanh(k d) -&gt; k d</c>, so <c>H(d) / d -&gt; Hs k</c>,
        /// a constant, and the sea either breaks everywhere or nowhere depending on a number the consumer never
        /// set. The stylized reading keeps the break line where the sea state says it should be and lets
        /// <see cref="WaterSettings.SurfBreakerIndex"/> move it.
        /// </para>
        /// </summary>
        public static float BreakDepth(float significantHeight, float breakerIndex)
            => MathF.Max(significantHeight, 0f) / MathF.Max(breakerIndex, 1e-3f);

        /// <summary>
        /// Per-cascade shoaling attenuation at a depth, 0..1. 1 is untouched open water.
        /// </summary>
        /// <param name="depthMetres">Local water depth. Clamped at 0, so land is fully flat.</param>
        /// <param name="wavenumber">The cascade's energy-weighted mean wave number, rad/m
        /// (<see cref="OceanSpectrum.CascadeStatistics.MeanWavenumber"/>). 0 or less means "no content", which
        /// attenuates nothing.</param>
        /// <param name="depthScale"><see cref="WaterSettings.ShoalingDepthScale"/>: multiplies the depth fed to
        /// the taper, so below 1 the calm shelf reaches further out and above 1 it hugs the shore.</param>
        /// <param name="strength"><see cref="WaterSettings.ShoalingStrength"/>, 0..1. 0 returns exactly 1.</param>
        /// <param name="surfBand"><see cref="SurfBand"/> at the same depth: how far inside the breaking band this
        /// is.</param>
        /// <param name="collapse"><see cref="WaterSettings.SurfAmplitudeCollapse"/>, 0..1: how much of the
        /// remaining amplitude the break takes out on top of the taper. This is the part that is NOT double
        /// counting - the taper is per cascade and barely touches the chop, while a broken wave is turbulent
        /// whitewater at every scale, so the collapse is applied flat across all of them.</param>
        public static float Attenuation(float depthMetres, float wavenumber, float depthScale, float strength,
            float surfBand, float collapse)
        {
            if (strength <= 0f) return 1f;
            float d = MathF.Max(depthMetres, 0f) * MathF.Max(depthScale, 1e-4f);
            float taper = wavenumber <= 0f ? 1f : MathF.Tanh(MathF.Min(wavenumber * d, TanhArgumentLimit));
            taper *= 1f - Math.Clamp(collapse, 0f, 1f) * Math.Clamp(surfBand, 0f, 1f);
            float s = Math.Clamp(strength, 0f, 1f);
            return 1f * (1f - s) + taper * s;   // GLSL mix(1.0, taper, s), literally
        }

        /// <summary>
        /// How far inside the breaking-surf band a depth sits: 0 in water deeper than
        /// <paramref name="breakDepth"/>, 1 at the waterline and on land, smoothstepped between.
        /// </summary>
        /// <param name="depthMetres">Local water depth.</param>
        /// <param name="breakDepth">The break line, from <see cref="BreakDepth"/>. 0 or less disables the
        /// band.</param>
        /// <param name="width"><see cref="WaterSettings.SurfBandWidth"/>: the ramp's depth span as a fraction of
        /// <paramref name="breakDepth"/>. 1 ramps the whole way from the break line to the waterline; smaller
        /// values finish the ramp sooner below the break line, i.e. a harder-edged band.</param>
        public static float SurfBand(float depthMetres, float breakDepth, float width)
        {
            if (breakDepth <= 0f) return 0f;
            if (depthMetres <= 0f) return 1f;
            float w = MathF.Max(width, 1e-3f) * breakDepth;
            float t = Math.Clamp((breakDepth - depthMetres) / w, 0f, 1f);
            return t * t * (3f - 2f * t);   // GLSL smoothstep(0, 1, t) on an already-clamped t
        }

        /// <summary>
        /// The surge itself: how white this point is, before the band ramp and the intensity knob.
        /// <para>
        /// Two terms, and the split is what makes the band read as a moving crash rather than a painted ring. The
        /// CREST term is a plain gate on wave phase, so foam only exists on the upper part of the incoming wave
        /// and travels with it. The TRAIL term extends that backward down the seaward face
        /// (<paramref name="backFace"/>), so what the crest whitened does not vanish the instant the crest passes
        /// - a real break leaves its foam behind, and this is the cheapest honest reading of that on a surface
        /// with no world-space foam accumulator to write into.
        /// </para>
        /// </summary>
        /// <param name="normalizedRise">Surface height above still water divided by half the significant wave
        /// height, so roughly +1 at a crest and -1 in a trough.</param>
        /// <param name="crestBias"><see cref="WaterSettings.SurfCrestBias"/>: where on the wave foam starts. 0
        /// whitens everything from mean water level up, higher confines it to the crest.</param>
        /// <param name="trailWidth"><see cref="WaterSettings.SurfTrailWidth"/>: how far below
        /// <paramref name="crestBias"/> the trail reaches, in the same normalized rise.</param>
        /// <param name="backFace">0..1, how much this point faces back toward the sea: the surface slope along the
        /// up-beach direction, gained by <see cref="BackFaceGain"/> and clamped.</param>
        public static float Surge(float normalizedRise, float crestBias, float trailWidth, float backFace)
        {
            float b = Math.Clamp(crestBias, 0f, MaxCrestBias);
            float crest = SmoothStep(b, 1f, normalizedRise);
            float trail = SmoothStep(b - MathF.Max(trailWidth, 1e-3f), b, normalizedRise)
                          * Math.Clamp(backFace, 0f, 1f);
            return Math.Clamp(MathF.Max(crest, trail), 0f, 1f);
        }

        /// <summary>
        /// Where on its OWN wave a point sits, from the surface's height above still water. Roughly +1 on a crest
        /// and -1 in a trough, whatever the depth.
        /// <para>
        /// <b>Normalizing against the LOCAL amplitude is what makes the surf band work at all, and it is not a
        /// refinement.</b> The shoaling taper has already flattened the sea by the time it reaches the break line -
        /// that is its job - so measuring the crest against the OPEN-WATER significant height reports a wave that
        /// is barely off the mean surface, the crest gate never opens, and the band renders as nothing. Measured
        /// against the amplitude the wave actually has HERE, a crest is a crest at any depth, which is the reading
        /// the gate needs. Found by probe render: the first implementation normalized against Hs and drew a bare
        /// pale line where the surf should have been.
        /// </para>
        /// </summary>
        /// <param name="riseMetres">Displaced surface height above the plane's still water.</param>
        /// <param name="significantHeight">The sea state's open-water <see cref="SignificantHeight"/>.</param>
        /// <param name="attenuation">The dominant cascade's <see cref="Attenuation"/> at this depth.</param>
        public static float NormalizedRise(float riseMetres, float significantHeight, float attenuation)
        {
            float half = 0.5f * MathF.Max(significantHeight, 1e-3f);
            return riseMetres / MathF.Max(half * attenuation, AmplitudeFloor * half);
        }

        /// <summary>
        /// The band's final coverage: the crest-locked <see cref="Surge"/> out at the break line, handing over to
        /// a solid wash as the water runs out.
        /// <para>
        /// The handover is not decoration. A phase gate needs a wave to gate on, and at the waterline there is
        /// none left - the surface is flat there by construction, which is exactly what the calm-shallows half of
        /// this release is for. A real beach is white at the sand for a different reason (the wash that a broken
        /// wave leaves running up it), so past <see cref="WashStart"/> the band stops asking about phase.
        /// </para>
        /// </summary>
        public static float SurfFoam(float surfBand, float surge)
        {
            float band = Math.Clamp(surfBand, 0f, 1f);
            float gate = SmoothStep(0f, BandGate, band);
            return Math.Clamp(gate * MathF.Max(surge, SmoothStep(WashStart, 1f, band)), 0f, 1f);
        }

        /// <summary>How much this point faces back toward the sea, from the surface slope and the direction the
        /// surge runs. Positive where the surface climbs along the up-beach direction, i.e. behind the crest.
        /// Mirrors the shader's <c>clamp(dot(slope, up) * KE_SURF_BACK_GAIN, 0, 1)</c>.</summary>
        public static float BackFace(float slopeAlongUpBeach)
            => Math.Clamp(slopeAlongUpBeach * BackFaceGain, 0f, 1f);

        /// <summary>GLSL <c>smoothstep</c>, literally, including its degenerate-edge behaviour.</summary>
        static float SmoothStep(float edge0, float edge1, float x)
        {
            float span = edge1 - edge0;
            if (span <= 0f) return x < edge0 ? 0f : 1f;
            float t = Math.Clamp((x - edge0) / span, 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
