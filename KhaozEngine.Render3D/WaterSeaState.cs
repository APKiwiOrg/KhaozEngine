using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The sea state driving <see cref="WaterWaveSource.FftOcean"/>: an authorable wind sea (speed, fetch, depth,
    /// heading, spreading, swell) plus the cascade layout, the sampling frame and the foam model. Reached as
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

        /// <summary>
        /// World-space tile size in metres of the LARGEST (first) cascade, i.e. the distance over which its own
        /// contribution repeats. It also sets the longest wave the surface can carry. Default <c>250</c>.
        /// <para>
        /// <b>This trades against how much ocean the camera can see at once, and the default is sized for an
        /// eye-height view.</b> The first cascade is the only one whose repeat is big enough to read as a
        /// STRUCTURE rather than as texture, so the number that matters is how many times its tile fits across
        /// the visible water. At a standing eye height a few hundred metres of ocean is in frame and one 250
        /// metre tile spans most of it, which is why the repeat does not show. From an elevated vantage it does:
        /// at 35 metres up, with roughly 600 metres of water in frame, that same tile lays down two and a half
        /// copies of itself across the view and the eye finds them. Raising this is the direct fix and it costs
        /// nothing per frame (the transform is the same size either way), but it moves the whole cascade ladder
        /// with it via <see cref="CascadeTileRatio"/>, so the finest cascade coarsens too and near-field detail
        /// goes with it. Raise <see cref="CascadeCount"/> or <see cref="CascadeTileRatio"/> alongside it to get
        /// that detail back.
        /// </para>
        /// <para>
        /// The default is deliberately NOT moved, because the tile size a scene wants follows from its camera
        /// rather than from the sea. <see cref="DomainWarpMetres"/> is the other lever and does not coarsen
        /// anything: it bends cascade 0's lattice out of world-space regularity instead of enlarging it.
        /// </para>
        /// </summary>
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

        // ---- Sampling frame ------------------------------------------------------------------------------------
        //
        // Everything in this group rotates or bends the frame the cascade maps are SAMPLED in. None of it touches
        // the spectrum, the transform or the produced maps: a rotation preserves |k|, so the cascades' disjoint
        // wave-number bands and their energy are exactly what they were, and the whole group defaults to the
        // identity. What a rotation does change is DIRECTIONALITY - which way the spectrum's lobe points at a
        // given place, and how the cascades' lobes sit relative to each other.

        /// <summary>
        /// World-space XZ point the waves run TOWARD when <see cref="OnshoreFocusStrength"/> is above 0: the
        /// sampling frame is rotated per position so the spectrum's dominant heading points from that position at
        /// this point. Set it to the centre of the island (or the harbour mouth, or whatever the sea is supposed
        /// to be running at) and the surf converges on it from every azimuth instead of running past on one
        /// heading. Ignored entirely at strength 0, which is the default. Default <c>(0, 0)</c>.
        /// <para>
        /// <b>The rotation field's distortion piles up AT this point and the intended use assumes it is land.</b>
        /// Going once around it, the heading has to sweep a full turn, so the closer a sample is the faster the
        /// frame spins with position - the angular gradient goes as 1/distance and is unbounded at the point
        /// itself. Over water that would read as a smeared vortex; over an island it is under the terrain and
        /// never drawn. Aim it at land, not at a spot in open water the camera can reach.
        /// </para>
        /// </summary>
        public Vector2 OnshoreFocusPoint;

        /// <summary>
        /// How much of the way toward <see cref="OnshoreFocusPoint"/> the local wave heading is turned, 0..1.
        /// <c>0</c> (the default) is exactly the unfocused sea: one tap, no rotation, every wave running on
        /// <see cref="WindDirectionDegrees"/>, bit-identical to an ocean built before this knob existed.
        /// <c>1</c> points the heading straight at the focus point from wherever the surface is sampled, so the
        /// swell converges on it from every azimuth.
        /// <para>
        /// <b>1 is the value to use.</b> Not because a partial focus looks bad, but because it is the only value
        /// with no seam. A uniform heading field wraps zero times around the focus point and a converging one
        /// wraps once, so no continuous blend between them exists: partially turning the field HAS to leave a
        /// discontinuity somewhere. Here it is one ray, the one running from the focus point in the direction the
        /// wind blows, where the shortest-way-round turn flips between a half turn each way and the heading jumps
        /// across it. It closes at both ends of the range (0 turns nothing, 1 turns by a whole turn, which is the
        /// same heading again) and is widest at 0.5. If a partial focus is wanted anyway,
        /// <see cref="WindDirectionDegrees"/> aims the seam: put it behind the island or off the played map.
        /// </para>
        /// <para>
        /// <b>Cost:</b> above 0 the surface takes TWO cascade samples per stage instead of one (see
        /// <see cref="OnshoreFocusSectors"/> for why), in both the vertex and the fragment. At 0 the second tap
        /// is branched out on a uniform, so an unfocused ocean pays exactly what it paid before.
        /// </para>
        /// </summary>
        public float OnshoreFocusStrength;

        /// <summary>
        /// How many fixed lattice headings the onshore focus quantizes the turn to, clamped to 4..64. Default
        /// <c>12</c>, i.e. 30 degree sectors. Ignored when <see cref="OnshoreFocusStrength"/> is 0.
        /// <para>
        /// The focus cannot simply rotate the sampling coordinate: a rotation field that turns to face a point
        /// winds once around it, so in polar coordinates about that point its angle cancels the sample's own
        /// azimuth, the entire plane collapses onto one line of the map, and the sea renders as a bullseye of
        /// perfect circles. (Backing the strength off only scales the damage rather than removing it, and no
        /// non-constant rotation field is a valid coordinate map at all.) So the heading is carried by SAMPLING
        /// the two nearest fixed rotations and mixing them, each of which is a plain undistorted field.
        /// </para>
        /// <para>
        /// This is therefore a quality knob and not a cost one: only the two sectors either side of the wanted
        /// heading are ever non-zero, so the surface takes two samples per cascade at 4 sectors and two at 64.
        /// What it buys is how far apart those two headings are - the mix is two decorrelated realizations of the
        /// same spectrum a sector apart, which reads as directional SPREAD around the wanted heading. At the
        /// default that spread is about 15 degrees either side, which is narrower than
        /// <see cref="DirectionalSpread"/>'s own lobe and disappears into it. Coarse settings make the sea read as
        /// a few distinct wave trains meeting, which is a legitimate stylized look and is what the low end is for.
        /// </para>
        /// <para>
        /// <b>The blend's "conserves variance exactly" claim (see <see cref="Internal.OceanFocus.Sectors"/>) assumes the
        /// two taps are decorrelated, and that assumption fails close to <see cref="OnshoreFocusPoint"/>.</b> The
        /// two taps are two different ROTATIONS of the same world position, and near the focus point both
        /// rotations agree (in the limit, at the point itself, exactly), so the taps land on the same texel of
        /// each cascade and read the same value rather than two independent draws. Mixing one value with itself
        /// at L2 weights <c>(a, b)</c> with <c>a^2 + b^2 = 1</c> scales it by <c>a + b</c> rather than holding it,
        /// which peaks at <c>sqrt(2)</c> mid-sector (<c>T = 0.5</c>) instead of 1. By continuity the bias fades in
        /// rather than cutting off, over a radius on the order of
        /// <c>dominantWavelength / (2 * sin(halfSectorStep))</c> - roughly 80 metres at the default 12 sectors (a
        /// 15 degree half-step) and a 40 metre dominant wavelength. MORE sectors WIDEN that radius (a finer ring
        /// narrows the half-step, and the radius grows as its sine shrinks); fewer sectors shrink it. This is
        /// another reason <see cref="OnshoreFocusPoint"/> belongs on land: the intended island use puts the
        /// biased radius under the terrain, same as the unbounded angular gradient at the point itself.
        /// </para>
        /// </summary>
        public int OnshoreFocusSectors = 12;

        /// <summary>
        /// Fixed extra rotation of each cascade's sampling frame, in DEGREES, xyz = cascades 0/1/2. Applied on top
        /// of the onshore focus rotation, so the cascades stay locked to each other as the focus turns them all.
        /// Default <c>(0, 0, 0)</c>, which is exactly today's aligned frame.
        /// <para>
        /// This is a DE-TILING knob. The cascade tile sizes already avoid a shared repeat period (see
        /// <see cref="CascadeTileRatio"/>), but their lattices are all axis-aligned, so their repeats stack up
        /// along the same two world directions and reinforce each other into one readable grain. Turning each
        /// cascade a different way leaves three lattices with no common direction and nothing for the eye to lock
        /// onto. <c>(0, 19, 37)</c> is a good starting set: mutually non-square angles, far enough apart to
        /// decorrelate and small enough that the sea still reads as one weather system.
        /// </para>
        /// <para>
        /// What it costs is a little DIRECTIONALITY. Each cascade's spreading lobe turns with its frame, so the
        /// chop rides across the swell rather than with it, which is a mild crossed sea (real, and common, but it
        /// is a change). Nothing about the wave-number partition or the energy moves: a rotation preserves |k|,
        /// so each cascade owns exactly the band it owned and carries exactly the variance it carried.
        /// </para>
        /// </summary>
        public Vector3 CascadeRotationDegrees;

        /// <summary>
        /// Peak displacement, in metres, of a very-large-scale static warp of the sampling domain, applied BEFORE
        /// the rotations. <c>0</c> (the default) disables it and returns the sample position untouched.
        /// <para>
        /// This is the lever for the one repeat the other knobs cannot reach: cascade 0's OWN period. Rotating a
        /// lattice does not change how often it repeats and neither does decorrelating it from the other two, so
        /// from a vantage that sees several copies of <see cref="CascadeTileMetres"/> at once the largest cascade
        /// still lays down a grid. Bending the sample position first means world space no longer maps onto that
        /// grid regularly, and the repeat stops being a repeat. It is the same trick the procedural ripple field
        /// uses (<see cref="WaterSettings.WaveWarpStrength"/>), at ocean scale and without the drift: this warp
        /// is STATIC in world space, because a moving one at this wavelength reads as the whole sea sloshing.
        /// </para>
        /// <para>
        /// <b>Size it against the tile, not against a feeling.</b> Breaking a repeat needs a displacement that is
        /// a real fraction of the period being broken, so the useful band at the default
        /// <see cref="CascadeTileMetres"/> and <see cref="DomainWarpWavelengthMetres"/> is roughly <c>100</c> to
        /// <c>150</c>, i.e. 40 to 60 per cent of the tile. Baked from a 1500 metre overhead view (six tile
        /// periods in frame), <c>30</c> leaves the lattice plainly readable, <c>100</c> makes it wander, and
        /// <c>150</c> removes it. Anything much below a third of the tile is not doing this job.
        /// </para>
        /// <para>
        /// The cost of that is stated by <c>2 * pi * DomainWarpMetres / DomainWarpWavelengthMetres</c>, which is
        /// the local stretch the warp puts into the domain: the defaults at 150 metres of amplitude come to 0.75,
        /// so wavelengths vary by up to about three quarters either way across the field. That reads as the sea
        /// being livelier in some places than others, which is a fair description of a real one. What it must NOT
        /// exceed is 1: past that the domain folds back on itself and the surface tears.
        /// </para>
        /// </summary>
        public float DomainWarpMetres;

        /// <summary>Wavelength in metres of the <see cref="DomainWarpMetres"/> warp, i.e. the distance over which
        /// it bends one way and back. It has to be well ABOVE <see cref="CascadeTileMetres"/> or it stops being a
        /// large-scale reparametrization and becomes extra chop with the wrong dispersion. The warp is built from
        /// two incommensurate frequencies (this one and 0.57 of it) on both axes, so the warp does not simply
        /// tile at its own period either. Ignored when <see cref="DomainWarpMetres"/> is 0. Default
        /// <c>1250</c>, five times the default largest tile.</summary>
        public float DomainWarpWavelengthMetres = 1250f;

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
