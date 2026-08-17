using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Opt-in animated water surface: a Gerstner swell displacing the surface grid, a domain-warped three-layer
    /// procedural ripple normal on top, a camera-distance detail fade, per-channel depth absorption for the body
    /// colour, a fresnel blend toward the analytically reflected sky, a GGX sun glint, procedural whitecap and
    /// shoreline foam, and a depth-sampled shore fade at the waterline. Reachable as
    /// <see cref="PixelPostProcessSettings.Water"/> - the same home as <see cref="PixelPostProcessSettings.Sky"/>
    /// (both are scene-wide look-and-feel bags reached off <c>Post</c>, as opposed to <see cref="WaterPlane"/>,
    /// which is the per-frame WHERE-to-draw request via <see cref="Scene3D.DrawWater(in WaterPlane)"/>). Drawing
    /// nothing (no <see cref="Scene3D.DrawWater(in WaterPlane)"/> call this frame) means no water pass runs at all,
    /// regardless of these settings - existing scenes stay byte-stable. No refraction, no screen-space reflections,
    /// no caustics and no submerged view: the reflection is the analytic sky only (see
    /// <see cref="PixelPostProcessSettings.Sky"/>), and this is a stylized LDR surface, not a physically accurate
    /// one.
    /// <para>
    /// This is the scene's DEFAULT look rather than its only one: a queued <see cref="WaterPlane"/> may carry a
    /// <see cref="WaterLook"/> that overrides part of it for that plane alone, which is how a calm inland lake and
    /// a rough sea coexist in one frame. A plane with no look packs from this object unchanged. What a look may
    /// NOT override is anything backing a once-per-frame GPU resource or the pass's geometry, namely
    /// <see cref="SeaState"/>, <see cref="Bathymetry"/> and the grid group.
    /// </para>
    /// <para>
    /// <b>Reaching the 14.22.0 look.</b> Every 14.24.0 addition has an off value and they are independent, so the
    /// previous release's surface is reachable knob by knob:
    /// <c>SwellAmplitude = 0</c> (flat plane, no displacement, no whitecaps),
    /// <c>GridFocusBias = 1</c> (the uniform surface grid),
    /// <c>SkyReflectionStrength = 0</c> (fresnel blends toward the flat <see cref="HorizonColor"/> again),
    /// <c>GlintRoughness = 0</c> (the Blinn-Phong glint on <see cref="GlintExponent"/>),
    /// <c>AbsorptionPerMetre = default</c> i.e. all-zero (the two-stop <see cref="ShallowDepth"/> blend), and
    /// <c>FoamStrength = 0</c> (no foam at all). The pre-14.22.0 look is reachable on top of that per the same
    /// rule: <c>WaveWarpStrength = 0</c>, <c>DetailFadeDistance = 0</c>, <c>ShallowDepth = 0</c>. It does NOT
    /// restore the pre-14.22.0 two-octave ripple FIELD: those two octaves were axis-aligned and separable, and
    /// that is precisely the checkerboard tiling 14.22.0 replaced, so the three-layer field stays unconditional.
    /// </para>
    /// </summary>
    public sealed partial class WaterSettings
    {
        // ---- Wave source -------------------------------------------------------------------------------------

        /// <summary>
        /// Where the surface's displacement, normal and whitecap foam come from:
        /// <see cref="WaterWaveSource.Procedural"/> (the default, the closed-form Gerstner swell + cosine ripple
        /// spectrum, unchanged) or <see cref="WaterWaveSource.FftOcean"/> (a Tessendorf inverse-FFT ocean off the
        /// <see cref="SeaState"/>, computed on the GPU). The SHADING is identical either way - absorption,
        /// reflection, glint, foam colour and shore fade all read the same knobs and the same code.
        /// <para>
        /// <see cref="WaterWaveSource.FftOcean"/> needs
        /// <see cref="KhaozEngine.Gpu.GpuCapabilities.SupportsCompute"/>; on a device without it the surface
        /// silently renders as <see cref="WaterWaveSource.Procedural"/> rather than failing, so this is safe to
        /// set unconditionally.
        /// </para>
        /// <para>
        /// <b>These knobs go FULLY inert under <see cref="WaterWaveSource.FftOcean"/></b>, because the spectrum
        /// supplies what they described: every <c>Swell*</c> knob (<see cref="SwellAmplitude"/>,
        /// <see cref="SwellWavelength"/>, <see cref="SwellDirectionDegrees"/>, <see cref="SwellSpreadDegrees"/>,
        /// <see cref="SwellSteepness"/>, <see cref="SwellSpeed"/>, <see cref="SwellSeed"/>,
        /// <see cref="SwellComponents"/>), the ripple spectrum (<see cref="NormalStrength"/>,
        /// <see cref="WaveWarpStrength"/>, <see cref="RippleComponents"/>, <see cref="RippleLacunarity"/>,
        /// <see cref="RippleGain"/>, <see cref="RippleSeed"/>), <see cref="DistantDetailScale"/>, and
        /// <see cref="FoamCrestCoverage"/>. Their replacements are named in <see cref="WaterSeaState"/>.
        /// As of the fix for <c>KhaozEngine#343</c>, <see cref="FoamPatternScale"/> joins this list too: FFT-mode
        /// foam break-up is shaped by the wave field's own foam/Jacobian channel instead of the procedural
        /// world-space pattern <see cref="FoamPatternScale"/> sizes, because that fixed-period pattern re-tiled
        /// the FFT surface's own de-tiled cascades. <see cref="WaveSpeed"/> loses its former second job (below)
        /// for the same reason: nothing in FFT mode scrolls the procedural pattern any more.
        /// </para>
        /// <para>
        /// <b>Two keep a SECOND job and must NOT be deleted on adoption</b>, which is easy to miss because their
        /// primary job does go away. <see cref="WaveScale"/> still supplies the reference wavelength the sun
        /// glint's footprint-alias ramp measures against, and <see cref="DetailFadeDistance"/> still sets the
        /// distance over which the lobe widens toward <see cref="GlintDistantRoughness"/>, so dropping either
        /// retunes the glint even though neither shapes the surface any more.
        /// </para>
        /// <para>
        /// EVERYTHING ELSE stays live, including <see cref="GridFocusBias"/>, <see cref="FootprintSamples"/>,
        /// <see cref="VarianceToRoughness"/>, <see cref="FoamStrength"/> and <see cref="FoamShoreWidth"/>.
        /// </para>
        /// </summary>
        public WaterWaveSource WaveSource = WaterWaveSource.Procedural;

        /// <summary>The sea state driving <see cref="WaterWaveSource.FftOcean"/>: wind, fetch, depth, spreading,
        /// swell, choppiness, the cascade ladder, the sampling frame (onshore focus and de-tiling) and the foam
        /// model. Never read under <see cref="WaterWaveSource.Procedural"/>.</summary>
        public WaterSeaState SeaState = new();

        // ---- Body colour -------------------------------------------------------------------------------------

        /// <summary>Tint colour in deep water (view ray steep, far from shore). Default a deep teal-blue that
        /// reads as water under the engine's default lighting.</summary>
        public Color DeepColor = new(0.05f, 0.18f, 0.28f, 0.92f);

        /// <summary>Tint colour in SHALLOW water: the body colour at zero depth, which
        /// <see cref="AbsorptionPerMetre"/> (or, when that is off, <see cref="ShallowDepth"/>) grades down into
        /// <see cref="DeepColor"/> as the ground drops away. Applied to the BODY colour, before the fresnel blend
        /// toward the reflected sky, so a grazing view of the shallows still picks up the sky. Default a clean
        /// turquoise: with per-channel absorption the midtones stay graphic rather than going muddy, so the shallow
        /// end can be brighter and greener than the two-stop blend allowed.</summary>
        public Color ShallowColor = new(0.24f, 0.62f, 0.62f, 0.78f);

        /// <summary>
        /// Per-channel absorption coefficients, in inverse world units (1/metre), for the Beer-Lambert grading of
        /// the body colour: transmittance is <c>exp(-coefficient * depth)</c> per channel, and the body colour is
        /// <see cref="DeepColor"/> blended toward <see cref="ShallowColor"/> by it. Because red is absorbed several
        /// times faster than blue (the default ratios follow real water), the gradient walks turquoise -&gt; teal
        /// -&gt; deep blue along a curve instead of a straight line between two colours, which is what keeps the
        /// midtones clean.
        /// <para>
        /// The alpha channel is unused. An ALL-ZERO value (the <see cref="Color"/> default) disables absorption and
        /// restores the 14.22.0 two-stop smoothstep blend over <see cref="ShallowDepth"/>. Default
        /// <c>(0.55, 0.24, 0.14)</c>, which is fully deep by roughly 12 metres.
        /// </para>
        /// </summary>
        public Color AbsorptionPerMetre = new(0.55f, 0.24f, 0.14f, 0f);

        /// <summary>World-space depth below the surface over which <see cref="ShallowColor"/> blends into
        /// <see cref="DeepColor"/> in the LEGACY two-stop path. Only consulted when
        /// <see cref="AbsorptionPerMetre"/> is all-zero. Independent of <see cref="ShoreFadeDistance"/> (which is
        /// the much tighter ALPHA feather at the waterline itself). <c>0</c> or less disables the blend entirely
        /// (one body colour at every depth). Default <c>2.5</c>.</summary>
        public float ShallowDepth = 2.5f;

        /// <summary>Overall opacity multiplier applied on top of <see cref="DeepColor"/>/<see cref="ShallowColor"/>/
        /// <see cref="HorizonColor"/>'s own alpha (0 = invisible, 1 = full). Default <c>1</c>.</summary>
        public float Opacity = 1f;

        // ---- Reflection --------------------------------------------------------------------------------------

        /// <summary>Flat fallback tint for the fresnel term at grazing view angles, and the colour the reflection
        /// blends back toward as <see cref="SkyReflectionStrength"/> drops to 0. Default close to
        /// <see cref="SkySettings.HorizonColor"/>'s default so a scene that turns the sky reflection off still
        /// reads as one cohesive palette.</summary>
        public Color HorizonColor = new(0.62f, 0.70f, 0.80f, 0.75f);

        /// <summary>
        /// How much of the fresnel reflection comes from the ANALYTIC SKY evaluated along the reflected view ray,
        /// versus the flat <see cref="HorizonColor"/>. 1 = the sky's own gradient (and, scaled by
        /// <see cref="SkyReflectionSunStrength"/>, its sun) reflected per fragment; 0 = the 14.22.0 behaviour, one
        /// flat colour everywhere.
        /// <para>
        /// This is the knob that removes the two-tone banding: a single <see cref="HorizonColor"/> makes the whole
        /// surface ramp between exactly two colours, and the ramp shows as a hard band. Reflecting the sky gives
        /// every fragment its own colour from the gradient it is actually pointing at. The palette comes from
        /// <see cref="PixelPostProcessSettings.Sky"/> whether or not the sky PASS is enabled, so water and sky stay
        /// harmonized without the game hand-matching colours. Default <c>1</c>.
        /// </para>
        /// </summary>
        public float SkyReflectionStrength = 1f;

        /// <summary>How much of the sky's sun disc + halo the reflection carries, on top of its gradient (0..1).
        /// The reflected disc is the broad, soft bloom around the sun's mirror direction; the sharp glitter is
        /// <see cref="GlintStrength"/>'s GGX lobe, which is the statistically correct version of the same
        /// highlight. Carrying the full disc as well double-counts the sun and blows out the sun path, so the
        /// default keeps a fraction of it: enough to fill the space between glitter points, not enough to compete
        /// with them. <c>0</c> reflects the gradient only. Default <c>0.35</c>.</summary>
        public float SkyReflectionSunStrength = 0.35f;

        // ---- Swell (vertex displacement) ---------------------------------------------------------------------

        /// <summary>Summed vertical amplitude of the Gerstner swell, in world units: peak-to-trough is about twice
        /// this where components constructively interfere. This is the knob that gives the surface a silhouette at
        /// all - <c>0</c> leaves the grid flat and the surface is a normal-perturbed sheet exactly as it was in
        /// 14.22.0 (and whitecap foam, which is driven by the swell's fold, goes with it). Default <c>0.45</c>,
        /// i.e. a bit under a metre peak-to-trough: clearly readable from a standing eye height without turning a
        /// calm bay into a storm.</summary>
        public float SwellAmplitude = 0.45f;

        /// <summary>Wavelength of the LONGEST swell component, in world units; the rest of the stack ladders down
        /// from it geometrically. Sets the whole scale of the swell, so this is the knob to move when the water
        /// body is a pond rather than an ocean. Default <c>42</c>, an ocean-scale swell; the stack's shortest
        /// component at the default count lands near 14. Values below roughly four surface-grid cells will alias
        /// (see <see cref="GridFocusBias"/>).</summary>
        public float SwellWavelength = 42f;

        /// <summary>Wind/travel direction of the swell as an angle in DEGREES in the world XZ plane
        /// (0 = toward +X, 90 = toward +Z). Default <c>30</c>.</summary>
        public float SwellDirectionDegrees = 30f;

        /// <summary>Half-angle in DEGREES of the directional fan either side of
        /// <see cref="SwellDirectionDegrees"/>. 0 makes every component travel on exactly the same heading, which
        /// reads as a corrugated sheet; a wide fan reads as a confused sea. Default <c>55</c>.</summary>
        public float SwellSpreadDegrees = 55f;

        /// <summary>Gerstner Q, 0..1: how much each component pinches the surface HORIZONTALLY as well as
        /// vertically. 0 is a plain sum of sines (rounded crests, rounded troughs); higher sharpens crests and
        /// flattens troughs, which is the trochoidal shape a real swell has. Above 1 the surface folds back through
        /// itself and self-intersects, so treat 1 as the ceiling. Also drives whitecap foam, since the fold factor
        /// the foam reads is the horizontal compression this creates. Default <c>0.6</c>.</summary>
        public float SwellSteepness = 0.6f;

        /// <summary>Multiplier on the physical deep-water wave speed (omega = sqrt(g*k), so long components
        /// genuinely outrun short ones). <c>1</c> is the real speed, which at ocean wavelengths is brisk; the
        /// default slows it to a lazier, more stylized roll. <c>0</c> freezes the swell in place without flattening
        /// it. Default <c>0.6</c>.</summary>
        public float SwellSpeed = 0.6f;

        /// <summary>Decorrelates two water bodies that share the same wind: it offsets the component phases only,
        /// so the direction, wavelength ladder and shape are untouched and only the crest positions move. Any
        /// finite value works; nearby values give nearby (still correlated) surfaces. Default <c>0</c>.</summary>
        public float SwellSeed = 0f;

        /// <summary>How many Gerstner components make up the swell, clamped to 1..8. More components read as a
        /// less regular sea at a linear cost in the vertex shader (one sin/cos pair each). 1 is a single clean
        /// rolling wave train, which is a legitimate stylized look. Default <c>4</c>.</summary>
        public int SwellComponents = 4;

        /// <summary>
        /// How strongly the surface grid concentrates its vertices near the camera, as a power on the grid's
        /// parametric coordinate. <c>1</c> is a uniform grid (the 14.22.0 layout); higher packs more vertices into
        /// the near field, where the swell silhouette actually reads, and stretches the far cells out, where a
        /// crest is a couple of pixels tall anyway.
        /// <para>
        /// This exists because the grid is a fixed vertex budget spread over a plane the consumer sizes: at a 600
        /// unit half-extent a uniform grid puts its vertices more than a dozen units apart, which cannot carry a
        /// <see cref="SwellWavelength"/>-scale wave. The trade-off is that the mesh is then camera-relative, so
        /// vertices slide through the wave field as the camera moves. The slide is continuous (nothing pops) and
        /// the near field is dense enough that resampling a smooth long wave there is invisible. A consumer that
        /// already re-centres its <see cref="WaterPlane"/> on the camera every frame (the usual pattern for an
        /// open ocean) has a fully camera-locked mesh and gets the focus exactly centred, which is the best case
        /// for this warp: symmetric, and stable frame to frame.
        /// </para>
        /// <para>
        /// At the default, on a 600-unit half-extent plane with the camera in the middle, cells run from about half
        /// a unit at the focus through 10 units at 90 out to 22 at the far edge. The power warp has a wide dynamic
        /// range, so on a SMALL water body (a pond a few units across) the same bias crams the near cells down to
        /// millimetres, which is wasteful rather than wrong. Turn it down, or leave it at 1, for anything the
        /// camera can see all of at once. Default <c>1.8</c>.
        /// </para>
        /// </summary>
        public float GridFocusBias = 1.8f;

        /// <summary>
        /// How the surface grid is laid out. Default <see cref="WaterGridMode.CameraFocused"/>, the warped grid
        /// <see cref="GridFocusBias"/> shapes, which is what every consumer has been rendering and stays
        /// byte-identical. <see cref="WaterGridMode.Clipmap"/> opts into the world-locked ring grid instead, which
        /// is what removes the camera-motion boiling measured on
        /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/296">#296</see>.
        /// <para>
        /// <see cref="GridFocusBias"/> goes fully inert under <see cref="WaterGridMode.Clipmap"/> - the clipmap
        /// puts its detail where the camera is by construction, and the power warp is exactly the thing that has no
        /// snap quantum. <see cref="ClipmapCellSize"/>, <see cref="ClipmapRingCells"/> and
        /// <see cref="ClipmapLevels"/> replace it.
        /// </para>
        /// </summary>
        public WaterGridMode GridMode = WaterGridMode.CameraFocused;

        /// <summary>Cell size of the clipmap's INNERMOST ring, world units; each ring out doubles it. This is the
        /// finest geometry the surface ever carries, so it is the knob that decides whether a breaking crest at the
        /// player's feet has a silhouette. Default <c>0.5</c>. Inert unless
        /// <see cref="GridMode"/> is <see cref="WaterGridMode.Clipmap"/>.</summary>
        public float ClipmapCellSize = 0.5f;

        /// <summary>Cells per side per clipmap ring, rounded down to a multiple of 4 and clamped to 8..256. Drives
        /// the whole vertex budget: a level costs <c>(n+1)^2</c> vertices, and the rings past level 0 shade
        /// <c>n^2 - (n/2)^2</c> quads each. Default <c>32</c>, which at 7 levels is 6273 shaded vertices and 11264
        /// triangles - fewer than the 9409 / 18432 the camera-focused grid draws. Inert unless
        /// <see cref="GridMode"/> is <see cref="WaterGridMode.Clipmap"/>.</summary>
        public int ClipmapRingCells = 32;

        /// <summary>How many clipmap rings to build. <c>0</c> (the default, and the intended setting) sizes it from
        /// the <see cref="WaterPlane"/> so the outermost ring covers the plane from any camera position inside it;
        /// a positive value forces the count. Clamped to 1..10. Each extra level doubles the coverage for one more
        /// ring's worth of triangles, so this is a cheap dial. Inert unless <see cref="GridMode"/> is
        /// <see cref="WaterGridMode.Clipmap"/>.</summary>
        public int ClipmapLevels = 0;

        /// <summary>
        /// Samples per wavelength the clipmap's VERTEX stage band-limits the cascade maps to, against each ring's
        /// own cell size. <c>2</c> (the default) is plain Nyquist: a ring samples the mip whose texels match its
        /// cells, so it carries exactly what its geometry can represent and nothing shorter. Higher oversamples
        /// (softer, steadier); below 2 lets sub-Nyquist content back in, which is the artifact this whole mode
        /// exists to remove.
        /// <para>
        /// Needs the mipped cascade maps, so it does nothing under <see cref="WaterWaveSource.Procedural"/> (whose
        /// surface is closed-form and has no maps to low-pass) and nothing under
        /// <see cref="WaterGridMode.CameraFocused"/> (whose rings do not exist).
        /// </para>
        /// </summary>
        public float ClipmapBandLimitSamples = 2f;

        /// <summary>
        /// How far in from each clipmap ring's outer edge the geometry fades toward the NEXT ring out's
        /// evaluation, as a fraction of that ring's half-width. This is the LOD geomorph: over the band, a
        /// vertex's sampled displacement and its band-limit spacing both blend toward what the coarse ring would
        /// draw at the same place, reaching it exactly on the boundary, so a ring snapping in or out changes the
        /// surface continuously instead of swapping level in a one-cell annulus.
        /// <para>
        /// <c>0</c> restores 16.12.0's hard swap exactly: the boundary stitch and nothing else, byte for byte.
        /// <c>1</c> morphs a whole level. A RING's drawn extent starts at half its half-width (that is where its
        /// hole ends), so <c>0.5</c> already morphs an entire ring and only level 0 keeps an unmorphed core.
        /// </para>
        /// <para>
        /// <b>What it trades.</b> Near a boundary the surface is band-limited toward twice its own cell spacing,
        /// i.e. deliberately oversampled, so the band is a little softer than the ring's geometry could carry.
        /// That is the whole cost, and it buys the residual measured on
        /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/348">#348</see>. Inert unless
        /// <see cref="GridMode"/> is <see cref="WaterGridMode.Clipmap"/>.
        /// </para>
        /// </summary>
        public float ClipmapGeomorphBand = 0.5f;

        // ---- Bathymetry, shoaling and surf --------------------------------------------------------------------

        /// <summary>
        /// Consumer-supplied water depth over a world-space rectangle, and the switch for everything below it.
        /// <c>null</c> (the default) means the surface has no idea where the shallows are, which is exactly what
        /// it knew before 16.13.0 and renders bit-identically.
        /// <para>
        /// <b>It needs <see cref="WaterWaveSource.FftOcean"/>.</b> Under
        /// <see cref="WaterWaveSource.Procedural"/> the depth field and every knob in this group go fully inert:
        /// the shoaling taper is per-cascade against each cascade's own mean wave number, and the procedural swell
        /// has no cascades - it is a closed-form component stack whose whole point is that it needs no maps. The
        /// stylized path keeps <see cref="FoamShoreWidth"/>, which is a shoreline BAND rather than a breaking one.
        /// </para>
        /// <para>
        /// <b>Assigning a different field is supported and re-uploads</b>, whatever the incoming field's
        /// <see cref="WaterBathymetry.Revision"/> reads, so streaming a new region or moving between water bodies
        /// needs nothing but the assignment. Before 17.37.0 a replacement of the SAME resolution was silently
        /// ignored and the previous field's depths stayed on the GPU (#645).
        /// </para>
        /// </summary>
        public WaterBathymetry? Bathymetry;

        /// <summary>How much of the depth taper is applied, 0..1. At 1 each cascade is scaled by
        /// <c>tanh(k d)</c> against its own energy-weighted mean wave number, so the long swell starts calming in
        /// metres of depth while the chop rides in almost untouched; at 0 nothing is attenuated and the depth
        /// field only drives the surf band. Needs <see cref="Bathymetry"/>. Default <c>1</c>.</summary>
        public float ShoalingStrength = 1f;

        /// <summary>Multiplies the depth fed to the taper, so it moves where the calm starts without changing its
        /// shape: below 1 the calm shelf reaches further out into deeper water, above 1 it hugs the shore.
        /// <c>1</c> is the physical reading. Ignored when <see cref="ShoalingStrength"/> is 0. Default
        /// <c>1</c>.</summary>
        public float ShoalingDepthScale = 1f;

        /// <summary>
        /// Intensity of the breaking-surf band, 0..1. <c>0</c> disables the band entirely (and skips its branch,
        /// including its two extra depth taps). The band is foam gated on the incoming wave's CREST PHASE, so it
        /// surges up the beach with each wave rather than glowing in place, and it wraps anything shallow - a rock
        /// standing in shallow water breaks around itself with no extra authoring.
        /// <para>
        /// It rides <see cref="FoamStrength"/> like every other foam source, so a scene with foam turned off has
        /// no surf either. Needs <see cref="Bathymetry"/>. Default <c>1</c>.
        /// </para>
        /// </summary>
        public float SurfStrength = 1f;

        /// <summary>The breaker index <c>gamma</c> in <c>H / d = gamma</c>: waves of significant height <c>H</c>
        /// break where the depth falls below <c>H / gamma</c>. <c>0.78</c> is the classic shallow-water value and
        /// the default; raising it pulls the break line into shallower water (a narrower, later-breaking band),
        /// lowering it pushes the surf further out. Ignored when <see cref="SurfStrength"/> is 0.</summary>
        public float SurfBreakerIndex = 0.78f;

        /// <summary>How GRADUALLY the band builds once the water is shallow enough to break, as a fraction of the
        /// break depth. <c>1</c> (the default) ramps the whole way from the break line to the waterline, so the
        /// surf fades in over the entire zone; smaller values complete the ramp sooner below the break line, which
        /// reads as a harder-edged, more uniformly white surf zone. Where the band STARTS is
        /// <see cref="SurfBreakerIndex"/>'s job, not this one. Ignored when <see cref="SurfStrength"/>
        /// is 0.</summary>
        public float SurfBandWidth = 1f;

        /// <summary>Where on the incoming wave the foam starts, as a fraction of the crest height (0..1 in
        /// practice, hard-limited just under 1). <c>0</c> whitens everything from mean water level up, which reads
        /// as a wash; the default confines the surge to the upper part of the wave, so the band is visibly a
        /// travelling crest rather than a lit strip. Ignored when <see cref="SurfStrength"/> is 0. Default
        /// <c>0.25</c>.</summary>
        public float SurfCrestBias = 0.25f;

        /// <summary>How far below <see cref="SurfCrestBias"/> the foam trail reaches on the SEAWARD face behind
        /// the surge, in the same normalized wave height. This is what stops the foam vanishing the instant the
        /// crest passes: a break leaves its wash behind it. <c>0</c> leaves only the crest itself. Ignored when
        /// <see cref="SurfStrength"/> is 0. Default <c>0.8</c>.</summary>
        public float SurfTrailWidth = 0.8f;

        /// <summary>How much amplitude the break takes out, 0..1, on top of the depth taper and flat across every
        /// cascade: past the break line a wave is turbulent whitewater rather than a clean surface, at every
        /// scale. Rides <see cref="ShoalingStrength"/> (it is geometry, not foam), so a scene that wants the surf
        /// LOOK without the collapse turns this to 0 rather than turning the shoaling off. Default
        /// <c>0.6</c>.</summary>
        public float SurfAmplitudeCollapse = 0.6f;

        // ---- Ripple detail (fragment normal field) -----------------------------------------------------------

        /// <summary>World-space size of the LONGEST ripple component (larger = broader, slower-looking chop). The
        /// rest of the spectrum ladders down from it by <see cref="RippleLacunarity"/>. These ride ON TOP of the
        /// Gerstner swell as small-scale detail. Default <c>2.5</c>.</summary>
        public float WaveScale = 2.5f;

        /// <summary>How fast the scrolling ripple layers animate (world units / second-ish; drives the
        /// <see cref="Scene3D.EffectTimeSeconds"/>-scaled scroll). Also sets the drift rate of the procedural
        /// foam break-up pattern, so foam moves with the water rather than on its own clock. Ignored under
        /// <see cref="WaterWaveSource.FftOcean"/> (<c>KhaozEngine#343</c>): FFT-mode foam break-up is sourced
        /// from the wave field's own foam channel, which already moves with the real waves and needs no drift
        /// clock of its own. Default <c>0.35</c>.</summary>
        public float WaveSpeed = 0.35f;

        /// <summary>Strength of the procedural ripple normal perturbation (0 = the swell's own smooth normal with
        /// no chop on top, larger = choppier-looking ripples). The spectrum is normalized to a fixed slope
        /// variance, so this keeps the same meaning whatever <see cref="RippleComponents"/>,
        /// <see cref="RippleLacunarity"/> and <see cref="RippleGain"/> are set to. Default <c>0.35</c>.</summary>
        public float NormalStrength = 0.35f;

        /// <summary>How far a slow, large-scale domain warp displaces the ripple sample position before the three
        /// ripple layers are evaluated, in multiples of <see cref="WaveScale"/>. The warp's own wavelength is
        /// roughly five times the base layer's, so it bends the ripple field over a much longer distance than the
        /// ripples themselves repeat over, which is what stops a large surface reading as a repeating grid.
        /// <c>0</c> disables the warp. Default <c>0.75</c>.</summary>
        public float WaveWarpStrength = 0.75f;

        /// <summary>
        /// How many cosine components make up the ripple slope spectrum, clamped to 1..12. This is the knob that
        /// decides whether the surface reads as water or as a ruled pattern: three coherent cosines (what shipped
        /// through 14.24.0) have a slope that is constant along families of parallel lines, so they draw ribbons,
        /// and at distance those ribbons beat against the pixel grid into moire. Ten spreads the same slope energy
        /// over roughly five octaves and every heading, which is what a real wind sea does.
        /// <para>
        /// Cost is one cosine per component per pixel. Amplitudes are renormalized whenever this changes, so the
        /// surface keeps the same apparent chop at any count and this trades cost against richness only. Default
        /// <c>10</c>.
        /// </para>
        /// </summary>
        public int RippleComponents = 10;

        /// <summary>Wave-number ratio between successive ripple components: the spectrum's bandwidth per component.
        /// Deliberately not 2, which would make every component an exact octave harmonic of the one before it and
        /// reintroduce a shared repeat. Default <c>1.48</c>, giving about five octaves at the default component
        /// count.</summary>
        public float RippleLacunarity = 1.48f;

        /// <summary>Height-amplitude ratio between successive ripple components. Slope amplitude therefore scales
        /// as <c>RippleGain * RippleLacunarity</c> per step, so a product near 1 spreads slope energy evenly across
        /// the octaves (roughly a real wind-sea slope spectrum), below 1 biases toward the long ripples, above 1
        /// toward the fine ones, which is where sun glitter comes from. Default <c>0.66</c>, a hair under even.
        /// </summary>
        public float RippleGain = 0.66f;

        /// <summary>Rotates the ripple spectrum's heading fan and offsets its phases. Decorrelates two water bodies
        /// that share a look. Default <c>0</c>.</summary>
        public float RippleSeed = 0f;

        /// <summary>
        /// Pixel footprints per wavelength below which a ripple component (and, on the same measure, the swell's
        /// shading contrast) fades out of the normal. The Nyquist floor is 2; the default sits above it so a
        /// component fades BEFORE it starts aliasing rather than during.
        /// <para>
        /// This is the physics half of the anti-aliasing and it is why distance banding cannot come back through a
        /// knob: whatever the other settings say, a wave the pixel cannot resolve stops contributing a normal.
        /// What it contributes instead is lobe width, via <see cref="VarianceToRoughness"/>.
        /// <c>0</c> disables the band-limit entirely and restores the 14.24.0 behaviour of oscillating the normal
        /// at any frequency, moire included. Default <c>4</c>.
        /// </para>
        /// </summary>
        public float FootprintSamples = 4f;

        /// <summary>How much of the slope variance the footprint band-limit removes is transferred into the GGX
        /// glint lobe (Toksvig-style), so the surface conserves energy as detail is band-limited away rather than
        /// turning to glass at range. <c>0</c> disables the transfer, leaving distant water smooth and
        /// under-lit. Only the GGX path can receive it, since the legacy Blinn-Phong lobe has no roughness.
        /// Default <c>1</c>.</summary>
        public float VarianceToRoughness = 1f;

        /// <summary>Camera distance (world units) over which every ripple component ABOVE the longest fades
        /// toward <see cref="DistantDetailScale"/>. This is now an ARTISTIC extra layered on top of the footprint
        /// band-limit (<see cref="FootprintSamples"/>), which handles the physics on its own; it is
        /// also the distance over which <see cref="GlintRoughness"/> widens toward
        /// <see cref="GlintDistantRoughness"/>. <c>0</c> or less disables both fades, so the fine layers run at
        /// full strength to the horizon and the glint keeps its near-field roughness. Default <c>60</c>.</summary>
        public float DetailFadeDistance = 60f;

        /// <summary>Fraction of the fine ripple layers that survives at and beyond <see cref="DetailFadeDistance"/>
        /// (clamped to 0..1). <c>0</c> leaves the far field as the base ripple alone (glassiest); <c>1</c> is
        /// equivalent to no fade at all, which is now a reasonable choice because the GGX lobe band-limits the
        /// specular aliasing on its own (see <see cref="GlintRoughness"/>). Ignored when
        /// <see cref="DetailFadeDistance"/> is disabled. Default <c>0.18</c>.</summary>
        public float DistantDetailScale = 0.18f;

        // ---- Sun glint ---------------------------------------------------------------------------------------

        /// <summary>Strength of the key-light specular sun glint. 0 disables the glint entirely. The lobe is
        /// peak-normalized in both the GGX and the legacy Blinn-Phong path, so this number means the same
        /// brightness either way. Default <c>0.6</c>.</summary>
        public float GlintStrength = 0.6f;

        /// <summary>
        /// GGX/Trowbridge-Reitz roughness of the sun glint near the camera (the usual perceptual parameterization,
        /// <c>alpha = roughness * roughness</c>). Small values give a tight highlight that the ripple field breaks
        /// into individual glitter points, which is what a sun path on water actually is. <c>0</c> or less selects
        /// the LEGACY Blinn-Phong lobe on <see cref="GlintExponent"/> instead. Default <c>0.22</c>, roughly a 2
        /// degree half-width.
        /// </summary>
        public float GlintRoughness = 0.22f;

        /// <summary>
        /// Roughness the glint widens to where the surface is under-sampled: at and beyond
        /// <see cref="DetailFadeDistance"/>, or wherever one pixel already covers a large fraction of a ripple
        /// wavelength, whichever is worse. Widening the LOBE is the right answer to specular aliasing (the
        /// sub-pixel normal detail becomes lobe variance instead of being thrown away), and it is what stops the
        /// far field crawling with sparkle. Clamped up to at least <see cref="GlintRoughness"/>, and ignored
        /// entirely in the legacy Blinn-Phong path. Default <c>0.5</c>.
        /// </summary>
        public float GlintDistantRoughness = 0.5f;

        /// <summary>Specular exponent (tightness) of the LEGACY Blinn-Phong glint, used only when
        /// <see cref="GlintRoughness"/> is <c>0</c> or less: higher = a smaller, sharper highlight. Default
        /// <c>140</c>.</summary>
        public float GlintExponent = 140f;

        // ---- Foam --------------------------------------------------------------------------------------------

        /// <summary>Colour foam is painted in, and its own opacity in the alpha channel (foam is the one part of
        /// the surface that should read as nearly solid). Default a cool white.</summary>
        public Color FoamColor = new(0.94f, 0.97f, 1f, 1f);

        /// <summary>Overall foam intensity multiplier for BOTH sources (whitecaps and the shoreline band).
        /// <c>0</c> disables foam entirely and skips its whole branch. Default <c>0.85</c>.</summary>
        public float FoamStrength = 0.85f;

        /// <summary>How much of the swell carries whitecaps, 0..1: it is the threshold on the Gerstner fold factor,
        /// inverted, so 0 means only a perfectly folded crest foams (in practice never) and 1 foams the whole
        /// surface. The fold factor is normalized by <see cref="SwellSteepness"/>, so this coverage means the same
        /// thing at any steepness. Needs <see cref="SwellAmplitude"/> above 0 to have anything to read. Default
        /// <c>0.65</c>: with the default swell that puts strong foam on about 5% of the surface and a trace of it
        /// on about 8%, so whitecaps read as scattered breaks rather than either a clean sea or a wash. (A real
        /// ocean at moderate wind is nearer 1-3%; the stylized read wants a little more than the truth.)</summary>
        public float FoamCrestCoverage = 0.65f;

        /// <summary>World-space depth below the surface over which the SHORELINE foam band fades out: full foam
        /// where the ground touches the surface, none at this depth. Because the band is measured under the
        /// DISPLACED surface, the swell carries the foam line up and down the beach on its own. <c>0</c> or less
        /// disables the shoreline band and leaves only whitecaps. Default <c>1.6</c>.</summary>
        public float FoamShoreWidth = 1.6f;

        /// <summary>World-space scale of the procedural pattern that breaks the foam into shapes: three
        /// non-axis-aligned scrolling layers at mutually irrational frequencies, thresholded hard so the result is
        /// clean graphic lobes rather than a soft photoreal scum. Smaller = finer, busier foam. The pattern drifts
        /// at <see cref="WaveSpeed"/>. Ignored under <see cref="WaterWaveSource.FftOcean"/> (<c>KhaozEngine#343</c>):
        /// a fixed world-space pattern re-tiles the FFT surface's own de-tiled cascades, so FFT-mode break-up is
        /// shaped by the wave field's own foam/Jacobian channel instead (see <c>WaveSource</c>'s doc). Default
        /// <c>2.2</c>.</summary>
        public float FoamPatternScale = 2.2f;

        // ---- Shore -------------------------------------------------------------------------------------------

        /// <summary>World-space distance over which the surface fades out near the shore (where the resolved
        /// scene depth shows the ground is close beneath the water), softening the waterline instead of a hard
        /// clip. Default <c>0.6</c>.</summary>
        public float ShoreFadeDistance = 0.6f;
    }
}
