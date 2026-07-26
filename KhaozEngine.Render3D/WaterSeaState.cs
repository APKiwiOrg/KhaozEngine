namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The sea state driving <see cref="WaterWaveSource.FftOcean"/>: an authorable wind sea (speed, fetch, depth,
    /// heading, spreading, swell) plus the cascade layout and the foam model. Reached as
    /// <see cref="WaterSettings.SeaState"/>. Entirely inert under <see cref="WaterWaveSource.Procedural"/>, which
    /// keeps its own <c>Swell*</c> / <c>Ripple*</c> knobs.
    /// <para>
    /// These are OCEANOGRAPHIC parameters, not wave-table entries: the spectrum is TMA (JONSWAP shaped by fetch,
    /// attenuated for depth by the Kitaigorodskii factor), so a sea state is described the way a forecast
    /// describes one, and every wave in the surface follows from it. Changing any of them (or
    /// <see cref="Seed"/>) rebuilds the initial spectrum once, on the CPU; the per-frame cost does not depend on
    /// them at all.
    /// </para>
    /// </summary>
    public sealed class WaterSeaState
    {
        // ---- Wind sea ------------------------------------------------------------------------------------------

        /// <summary>Wind speed at the standard 10 metre reference height, in metres per second. The dominant knob:
        /// the peak wavelength grows with the square of it, so 5 reads as a light breeze on a sheltered bay and 20
        /// as a gale. Default <c>11</c>, a fresh breeze (Beaufort 5-6) with a peak wavelength near 45 metres at the
        /// default fetch.</summary>
        public float WindSpeed = 11f;

        /// <summary>Wind direction as an angle in DEGREES in the world XZ plane (0 = toward +X, 90 = toward +Z),
        /// matching <see cref="WaterSettings.SwellDirectionDegrees"/>'s convention so switching wave source keeps
        /// the sea running the same way. Default <c>30</c>.</summary>
        public float WindDirectionDegrees = 30f;

        /// <summary>Fetch, in KILOMETRES: how far the wind has blown over open water before reaching here. Short
        /// fetch gives a young, steep, short-wavelength sea (a lake, a lee shore); long fetch gives a mature swell
        /// that has had room to grow. This is the knob that makes a sea state authorable rather than a magic
        /// amplitude - it and <see cref="WindSpeed"/> together fix the peak frequency and the JONSWAP scale.
        /// Default <c>120</c>.</summary>
        public float FetchKilometres = 120f;

        /// <summary>Water depth in metres, used by the Kitaigorodskii depth attenuation and by the finite-depth
        /// dispersion relation. Shallow water slows and steepens the long components and cuts their energy, which
        /// is what makes a coastal shelf read differently from open ocean. Values at or below 0 are treated as very
        /// deep (the attenuation becomes 1 and the dispersion reduces to the deep-water
        /// <c>omega = sqrt(g k)</c>). Default <c>60</c>.</summary>
        public float DepthMetres = 60f;

        // ---- Directionality ------------------------------------------------------------------------------------

        /// <summary>
        /// How directional the wind sea is, 0..1: a blend between a FLAT (isotropic) spreading function and the
        /// Hasselmann <c>cos^2s</c> lobe about <see cref="WindDirectionDegrees"/>. <c>0</c> is a fully confused
        /// sea with equal energy on every heading; <c>1</c> is the full empirical lobe, which is long-crested and
        /// clearly running downwind.
        /// <para>
        /// Both ends are normalized to the same total energy, so this changes the SHAPE of the sea and never its
        /// height. Default <c>0.75</c>: clearly directional, still with enough cross-sea to avoid the corrugated
        /// read a single heading gives.
        /// </para>
        /// </summary>
        public float DirectionalSpread = 0.75f;

        /// <summary>How much long-period swell rides through the wind sea, 0..1. Swell is narrow-banded and
        /// long-crested, so it sharpens the directional lobe of the LOW frequencies only (the sharpening is scaled
        /// by <c>tanh(omega_peak / omega)</c>, which falls away for the short waves). <c>0</c> is a pure wind sea.
        /// Default <c>0.4</c>.</summary>
        public float SwellAmount = 0.4f;

        /// <summary>Heading the swell runs on, in DEGREES in the world XZ plane, same convention as
        /// <see cref="WindDirectionDegrees"/>. A swell heading that differs from the wind gives the crossed sea
        /// that reads as weather having changed. Ignored when <see cref="SwellAmount"/> is 0. Default <c>30</c>,
        /// i.e. running with the default wind.</summary>
        public float SwellDirectionDegrees = 30f;

        // ---- Surface shape -------------------------------------------------------------------------------------

        /// <summary>Horizontal displacement scale (Tessendorf's lambda), the FFT counterpart of
        /// <see cref="WaterSettings.SwellSteepness"/>: 0 gives a pure height field with rounded crests, higher
        /// pinches crests and broadens troughs into the trochoidal shape a real wave has, and it is what drives the
        /// Jacobian foam. Above roughly 1.5 the surface folds through itself at the crests. Default
        /// <c>1.1</c>.</summary>
        public float Choppiness = 1.1f;

        /// <summary>Suppression length in metres for waves too small to matter, applied as Tessendorf's
        /// <c>exp(-k^2 l^2)</c> factor on the spectrum. It removes the ripple-scale energy that the finest cascade
        /// cannot resolve without aliasing, which is cheaper and more stable than letting it in and band-limiting
        /// it in the shader. <c>0</c> or less disables it. Default <c>0.02</c> (2 cm).</summary>
        public float SmallWaveCutoffMetres = 0.02f;

        /// <summary>Decorrelates two oceans that share a sea state, and re-rolls the random phases of one. Any
        /// finite value works and the surface is fully deterministic in it: the same seed and the same elapsed time
        /// always produce the same maps. Default <c>0</c>.</summary>
        public int Seed;

        // ---- Cascades ------------------------------------------------------------------------------------------

        /// <summary>How many octave-separated cascades the surface is summed from, clamped to
        /// 1..<see cref="Internal.OceanSpectrum.MaxCascades"/> (3). Each cascade is its own FFT over its own tile
        /// size and carries its own DISJOINT band of wave numbers, so adding one extends the surface's detail
        /// downward in scale instead of adding energy on top of what is already there. Default <c>3</c>.</summary>
        public int CascadeCount = 3;

        /// <summary>World-space tile size in metres of the LARGEST (first) cascade, i.e. the distance over which
        /// its own contribution repeats. It has to be big enough that the repeat is not a visible structure at
        /// horizon distance, and it sets the longest wave the surface can carry. Default <c>250</c>.</summary>
        public float CascadeTileMetres = 250f;

        /// <summary>Ratio between successive cascade tile sizes: each cascade's tile is the previous one divided by
        /// this. Deliberately not a power of two, so no two cascades share a repeat period and their tilings never
        /// line up. Default <c>4.2</c>, which ladders the default 250 metre tile down through roughly 60 and 14
        /// metres.</summary>
        public float CascadeTileRatio = 4.2f;

        /// <summary>FFT resolution per cascade per axis; must be a power of two in 32..256, and 128 or 256 are the
        /// intended values. 256 quadruples the transform work and the map memory for detail that mostly matters
        /// close to the camera. Default <c>128</c>.</summary>
        public int CascadeResolution = 128;

        // ---- Foam ----------------------------------------------------------------------------------------------

        /// <summary>How much foam a folding crest injects per second. The injection is driven by the Jacobian of
        /// the horizontal displacement: where it drops below <see cref="FoamJacobianBias"/> the surface has
        /// compressed enough to curl back on itself, which is what a breaking crest is. <c>0</c> disables FFT
        /// whitecaps (the shoreline band on <see cref="WaterSettings.FoamShoreWidth"/> is unaffected). Default
        /// <c>1.6</c>.</summary>
        public float FoamGain = 1.6f;

        /// <summary>Jacobian value below which foam starts being injected. 1 is an undeformed surface and 0 is a
        /// full fold, so lowering this foams only the hardest breaks and raising it toward 1 washes the whole
        /// surface. The FFT counterpart of <see cref="WaterSettings.FoamCrestCoverage"/>. Default
        /// <c>0.55</c>.</summary>
        public float FoamJacobianBias = 0.55f;

        /// <summary>Exponential dissipation rate of accumulated foam, per second: foam decays by
        /// <c>exp(-rate * dt)</c> every frame, so this is roughly the reciprocal of how long a patch lingers after
        /// the crest that made it has passed. Foam ACCUMULATING over time (rather than being a per-frame function
        /// of the current fold) is what gives it a wake that trails the break. <c>0</c> makes foam permanent.
        /// Default <c>0.5</c>, i.e. a couple of seconds of visible trail.</summary>
        public float FoamDissipationPerSecond = 0.5f;
    }
}
