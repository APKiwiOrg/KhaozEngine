using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A per-plane override of the scene-wide water look. Attach one to a <see cref="WaterPlane"/> (the trailing
    /// constructor parameter) and that plane draws with these values in place of
    /// <see cref="PixelPostProcessSettings.Water"/>'s, leaving every other queued plane alone. Every field is
    /// nullable and <c>null</c> means INHERIT the scene's value, so a look states only what differs: the common
    /// case is a calm inland lake beside a rough sea, which is three or four fields against a scene look the game
    /// has already tuned.
    /// <para>
    /// <b>What it costs.</b> Nothing on the GPU. Each queued plane already owns its own slot in the water pass's
    /// uniform buffer, so an override is a different set of numbers written into a slot that was being written
    /// anyway: no extra bytes, no extra pipeline, no second shader. A plane with no look packs from the caller's
    /// own <see cref="WaterSettings"/> object, so a consumer that never opts in is byte-identical to before this
    /// type existed.
    /// </para>
    /// <para>
    /// <b>What it deliberately cannot change.</b> Anything produced once per frame for the whole scene, because a
    /// second value would mean a second GPU resource: <see cref="WaterSettings.SeaState"/> (one FFT bake),
    /// <see cref="WaterSettings.Bathymetry"/> (one depth texture), and the grid knobs
    /// (<see cref="WaterSettings.GridMode"/> and the <c>Clipmap*</c> group), which select the pass's pipeline and
    /// index buffer before the draw loop starts. Reflection weights stay scene-wide. Glint uses the shared
    /// sun direction and colour, with per-plane strength and roughness. What a plane CAN do is leave the shared ocean entirely by setting
    /// <see cref="WaveSource"/> to <see cref="WaterWaveSource.Procedural"/>, which is the inland-body case.
    /// </para>
    /// <example>
    /// A still lake queued in the same frame as an FFT sea:
    /// <code>
    /// scene.DrawWater(new WaterPlane(0f, 9.7f, 0f, 104f, 104f, new WaterLook
    /// {
    ///     WaveSource = WaterWaveSource.Procedural,
    ///     SwellAmplitude = 0.04f,
    ///     FoamStrength = 0f,
    ///     SurfStrength = 0f,
    /// }));
    /// </code>
    /// </example>
    /// </summary>
    public sealed class WaterLook
    {
        // ---- Wave source -------------------------------------------------------------------------------------

        /// <summary>Where THIS plane's displacement, normal and whitecap foam come from, overriding
        /// <see cref="WaterSettings.WaveSource"/>. <c>null</c> inherits the scene's source. This is the field an
        /// inland body usually wants: <see cref="WaterWaveSource.Procedural"/> takes the plane off the shared FFT
        /// ocean, so it loses the sea's swell, its whitecaps and (because shoaling and breaking surf ride the same
        /// gate) its breaking surf, and picks up the <c>Swell*</c> and <c>Ripple*</c> knobs instead.
        /// <para>
        /// One ocean is baked per frame either way and it is driven by DEMAND: the ocean runs when any queued
        /// plane's effective source is <see cref="WaterWaveSource.FftOcean"/>, so a scene defaulting to
        /// <see cref="WaterWaveSource.Procedural"/> with one plane overridden to the ocean gets a real ocean on
        /// that plane, and a scene whose planes all override away from it pays for none.
        /// </para></summary>
        public WaterWaveSource? WaveSource;

        // ---- Body colour -------------------------------------------------------------------------------------

        /// <summary>Overrides <see cref="WaterSettings.DeepColor"/> for this plane. <c>null</c> inherits. A lake or
        /// a river usually wants a warmer, browner deep colour than an open sea.</summary>
        public Color? DeepColor;

        /// <summary>Overrides <see cref="WaterSettings.ShallowColor"/> for this plane. <c>null</c> inherits.</summary>
        public Color? ShallowColor;

        /// <summary>Overrides <see cref="WaterSettings.AbsorptionPerMetre"/> for this plane. <c>null</c> inherits.
        /// An all-zero value still means "no absorption, use the two-stop <see cref="ShallowDepth"/> blend", so a
        /// look CAN turn absorption off for one body by overriding it with a zeroed colour.</summary>
        public Color? AbsorptionPerMetre;

        /// <summary>Overrides <see cref="WaterSettings.ShallowDepth"/> for this plane. <c>null</c> inherits. A pond
        /// that is two metres deep everywhere wants a much shorter blend than an ocean shelf.</summary>
        public float? ShallowDepth;

        /// <summary>Overrides <see cref="WaterSettings.Opacity"/> for this plane. <c>null</c> inherits. Useful when
        /// one body should read as shallower and clearer than the scene default.</summary>
        public float? Opacity;

        // ---- Swell (vertex displacement) ---------------------------------------------------------------------

        /// <summary>Overrides <see cref="WaterSettings.SwellAmplitude"/> for this plane. <c>null</c> inherits. This
        /// is the main dial for "calm inland body": a few centimetres reads as a lake, and <c>0</c> leaves the
        /// surface flat (which also removes whitecaps, since they are driven by the swell's fold). Inert while this
        /// plane's effective source is <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? SwellAmplitude;

        /// <summary>Overrides <see cref="WaterSettings.SwellWavelength"/> for this plane. <c>null</c> inherits. The
        /// scale knob: an ocean swell measured in tens of metres is wrong on a pond a few metres across. Inert
        /// under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? SwellWavelength;

        /// <summary>Overrides <see cref="WaterSettings.SwellDirectionDegrees"/> for this plane. <c>null</c>
        /// inherits. A sheltered body often runs across the prevailing wind rather than with it. Inert under
        /// <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? SwellDirectionDegrees;

        /// <summary>Overrides <see cref="WaterSettings.SwellSpreadDegrees"/> for this plane. <c>null</c> inherits.
        /// Inert under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? SwellSpreadDegrees;

        /// <summary>Overrides <see cref="WaterSettings.SwellSteepness"/> for this plane. <c>null</c> inherits. A
        /// small body reads better with rounder crests than the trochoidal pinch an ocean wants. Inert under
        /// <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? SwellSteepness;

        /// <summary>Overrides <see cref="WaterSettings.SwellSpeed"/> for this plane. <c>null</c> inherits. Inert
        /// under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? SwellSpeed;

        /// <summary>Overrides <see cref="WaterSettings.SwellSeed"/> for this plane. <c>null</c> inherits. This is
        /// the cheapest way to stop two bodies that share a look from marching in lockstep: it moves the crest
        /// positions and nothing else. Inert under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? SwellSeed;

        /// <summary>Overrides <see cref="WaterSettings.SwellComponents"/> for this plane. <c>null</c> inherits.
        /// Clamped to 1..8 where it is packed, as the scene value is. Inert under
        /// <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public int? SwellComponents;

        // ---- Shoaling and surf response ----------------------------------------------------------------------

        /// <summary>Overrides <see cref="WaterSettings.ShoalingStrength"/> for this plane. <c>null</c> inherits.
        /// Only the RESPONSE strength is per plane. The depth field itself
        /// (<see cref="WaterSettings.Bathymetry"/>) is one scene-wide texture, and a plane that is not on the FFT
        /// ocean loses the whole depth group anyway.</summary>
        public float? ShoalingStrength;

        /// <summary>Overrides <see cref="WaterSettings.SurfStrength"/> for this plane. <c>null</c> inherits.
        /// <c>0</c> is how a body says "no breaking surf" while the scene keeps it, which is the whole per-body
        /// need. HOW surf breaks (the <c>Surf*</c> shape knobs) stays scene-wide.</summary>
        public float? SurfStrength;

        // ---- Ripple detail (fragment normal field) -----------------------------------------------------------

        /// <summary>Overrides <see cref="WaterSettings.WaveScale"/> for this plane. <c>null</c> inherits. Note it
        /// keeps its second job under <see cref="WaterWaveSource.FftOcean"/>: it is the reference wavelength the
        /// sun glint's footprint-alias ramp measures against, so overriding it retunes that plane's glint even on
        /// the ocean.</summary>
        public float? WaveScale;

        /// <summary>Overrides <see cref="WaterSettings.WaveSpeed"/> for this plane. <c>null</c> inherits. Sheltered
        /// water reads as slower. Inert under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? WaveSpeed;

        /// <summary>Overrides <see cref="WaterSettings.NormalStrength"/> for this plane. <c>null</c> inherits. Turn
        /// it down for glassy inland water. Inert under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? NormalStrength;

        /// <summary>Overrides <see cref="WaterSettings.WaveWarpStrength"/> for this plane. <c>null</c> inherits.
        /// Inert under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? WaveWarpStrength;

        /// <summary>Overrides <see cref="WaterSettings.RippleComponents"/> for this plane. <c>null</c> inherits.
        /// Clamped to 1..12 where it is packed, as the scene value is. Inert under
        /// <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public int? RippleComponents;

        /// <summary>Overrides <see cref="WaterSettings.RippleLacunarity"/> for this plane. <c>null</c> inherits.
        /// Inert under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? RippleLacunarity;

        /// <summary>Overrides <see cref="WaterSettings.RippleGain"/> for this plane. <c>null</c> inherits. Inert
        /// under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? RippleGain;

        /// <summary>Overrides <see cref="WaterSettings.RippleSeed"/> for this plane. <c>null</c> inherits. The
        /// ripple half of the decorrelation pair (see <see cref="SwellSeed"/>): two bodies sharing a look stop
        /// rippling in step. Inert under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? RippleSeed;

        /// <summary>Overrides <see cref="WaterSettings.VarianceToRoughness"/> for this plane. <c>null</c>
        /// inherits.</summary>
        public float? VarianceToRoughness;

        /// <summary>Overrides <see cref="WaterSettings.DetailFadeDistance"/> for this plane. <c>null</c> inherits.
        /// Like <see cref="WaveScale"/> it keeps a second job under <see cref="WaterWaveSource.FftOcean"/>: it is
        /// the distance over which the glint lobe widens toward
        /// <see cref="WaterSettings.GlintDistantRoughness"/>. A small body the camera sees all of at once usually
        /// wants a shorter fade than an open sea.</summary>
        public float? DetailFadeDistance;

        /// <summary>Overrides <see cref="WaterSettings.DistantDetailScale"/> for this plane. <c>null</c> inherits.
        /// Inert under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? DistantDetailScale;

        // ---- Sun glint ---------------------------------------------------------------------------------------

        /// <summary>Overrides <see cref="WaterSettings.GlintStrength"/>. Null inherits the scene value.
        /// The sun direction and colour remain shared, only this plane's response changes.</summary>
        public float? GlintStrength;

        /// <summary>Overrides <see cref="WaterSettings.GlintRoughness"/>. Null inherits. Zero selects
        /// the legacy lobe controlled by <see cref="GlintExponent"/>.</summary>
        public float? GlintRoughness;

        /// <summary>Overrides <see cref="WaterSettings.GlintDistantRoughness"/>. Null inherits.</summary>
        public float? GlintDistantRoughness;

        /// <summary>Overrides <see cref="WaterSettings.GlintExponent"/> for the legacy glint lobe.
        /// Null inherits the scene value.</summary>
        public float? GlintExponent;

        // ---- Foam --------------------------------------------------------------------------------------------

        /// <summary>Overrides <see cref="WaterSettings.FoamColor"/> for this plane. <c>null</c> inherits.</summary>
        public Color? FoamColor;

        /// <summary>Overrides <see cref="WaterSettings.FoamStrength"/> for this plane. <c>null</c> inherits.
        /// <c>0</c> takes every foam source off this body (whitecaps, the shoreline band and the surf), which is
        /// what a still pond wants while the sea beside it keeps all three.</summary>
        public float? FoamStrength;

        /// <summary>Overrides <see cref="WaterSettings.FoamCrestCoverage"/> for this plane. <c>null</c> inherits.
        /// Inert under <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? FoamCrestCoverage;

        /// <summary>Overrides <see cref="WaterSettings.FoamShoreWidth"/> for this plane. <c>null</c> inherits. The
        /// shoreline BAND, which is live in both wave sources, so an inland body can keep a soft rim of foam at
        /// its edge without any of the sea's breaking surf.</summary>
        public float? FoamShoreWidth;

        /// <summary>Overrides <see cref="WaterSettings.FoamPatternScale"/> for this plane. <c>null</c> inherits.
        /// Smaller reads as finer, busier foam, which suits a smaller body. Inert under
        /// <see cref="WaterWaveSource.FftOcean"/>.</summary>
        public float? FoamPatternScale;

        // ---- Shore -------------------------------------------------------------------------------------------

        /// <summary>Overrides <see cref="WaterSettings.ShoreFadeDistance"/> for this plane. <c>null</c> inherits.
        /// The alpha feather at the waterline: a shallow inland body usually wants a tighter one than a coast.
        /// </summary>
        public float? ShoreFadeDistance;

        /// <summary>
        /// Resolve this look against the scene's settings, into a caller-owned scratch object. Copies
        /// <paramref name="scene"/> field by field into <paramref name="scratch"/> and then writes every non-null
        /// override over the top, so the result is a complete <see cref="WaterSettings"/> the packing code reads
        /// exactly as it reads the scene's.
        /// <para>
        /// The scratch is REUSED by the renderer, once per pass rather than once per plane, which is why this
        /// takes one rather than allocating. Its contents are only valid until the next call, so do not hold on to
        /// the returned reference. <see cref="WaterSettings.SeaState"/> and
        /// <see cref="WaterSettings.Bathymetry"/> come across by reference, deliberately: they are scene-wide and a
        /// look has no way to fork them.
        /// </para>
        /// </summary>
        /// <param name="scratch">The settings object to write the resolved look into. Overwritten wholesale.</param>
        /// <param name="scene">The scene-wide settings every null field inherits from. Never modified.</param>
        /// <returns><paramref name="scratch"/>, resolved.</returns>
        public WaterSettings ResolveInto(WaterSettings scratch, WaterSettings scene)
        {
            if (scratch is null) throw new ArgumentNullException(nameof(scratch));
            if (scene is null) throw new ArgumentNullException(nameof(scene));
            scratch.CopyFrom(scene);

            if (WaveSource.HasValue) scratch.WaveSource = WaveSource.Value;

            if (DeepColor.HasValue) scratch.DeepColor = DeepColor.Value;
            if (ShallowColor.HasValue) scratch.ShallowColor = ShallowColor.Value;
            if (AbsorptionPerMetre.HasValue) scratch.AbsorptionPerMetre = AbsorptionPerMetre.Value;
            if (ShallowDepth.HasValue) scratch.ShallowDepth = ShallowDepth.Value;
            if (Opacity.HasValue) scratch.Opacity = Opacity.Value;

            if (SwellAmplitude.HasValue) scratch.SwellAmplitude = SwellAmplitude.Value;
            if (SwellWavelength.HasValue) scratch.SwellWavelength = SwellWavelength.Value;
            if (SwellDirectionDegrees.HasValue) scratch.SwellDirectionDegrees = SwellDirectionDegrees.Value;
            if (SwellSpreadDegrees.HasValue) scratch.SwellSpreadDegrees = SwellSpreadDegrees.Value;
            if (SwellSteepness.HasValue) scratch.SwellSteepness = SwellSteepness.Value;
            if (SwellSpeed.HasValue) scratch.SwellSpeed = SwellSpeed.Value;
            if (SwellSeed.HasValue) scratch.SwellSeed = SwellSeed.Value;
            if (SwellComponents.HasValue) scratch.SwellComponents = SwellComponents.Value;

            if (ShoalingStrength.HasValue) scratch.ShoalingStrength = ShoalingStrength.Value;
            if (SurfStrength.HasValue) scratch.SurfStrength = SurfStrength.Value;

            if (WaveScale.HasValue) scratch.WaveScale = WaveScale.Value;
            if (WaveSpeed.HasValue) scratch.WaveSpeed = WaveSpeed.Value;
            if (NormalStrength.HasValue) scratch.NormalStrength = NormalStrength.Value;
            if (WaveWarpStrength.HasValue) scratch.WaveWarpStrength = WaveWarpStrength.Value;
            if (RippleComponents.HasValue) scratch.RippleComponents = RippleComponents.Value;
            if (RippleLacunarity.HasValue) scratch.RippleLacunarity = RippleLacunarity.Value;
            if (RippleGain.HasValue) scratch.RippleGain = RippleGain.Value;
            if (RippleSeed.HasValue) scratch.RippleSeed = RippleSeed.Value;
            if (VarianceToRoughness.HasValue) scratch.VarianceToRoughness = VarianceToRoughness.Value;
            if (DetailFadeDistance.HasValue) scratch.DetailFadeDistance = DetailFadeDistance.Value;
            if (DistantDetailScale.HasValue) scratch.DistantDetailScale = DistantDetailScale.Value;

            if (GlintStrength.HasValue) scratch.GlintStrength = GlintStrength.Value;
            if (GlintRoughness.HasValue) scratch.GlintRoughness = GlintRoughness.Value;
            if (GlintDistantRoughness.HasValue) scratch.GlintDistantRoughness = GlintDistantRoughness.Value;
            if (GlintExponent.HasValue) scratch.GlintExponent = GlintExponent.Value;

            if (FoamColor.HasValue) scratch.FoamColor = FoamColor.Value;
            if (FoamStrength.HasValue) scratch.FoamStrength = FoamStrength.Value;
            if (FoamCrestCoverage.HasValue) scratch.FoamCrestCoverage = FoamCrestCoverage.Value;
            if (FoamShoreWidth.HasValue) scratch.FoamShoreWidth = FoamShoreWidth.Value;
            if (FoamPatternScale.HasValue) scratch.FoamPatternScale = FoamPatternScale.Value;

            if (ShoreFadeDistance.HasValue) scratch.ShoreFadeDistance = ShoreFadeDistance.Value;

            return scratch;
        }
    }
}
