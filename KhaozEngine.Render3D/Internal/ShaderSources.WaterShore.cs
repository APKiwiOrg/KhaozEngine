namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The GLSL blocks that make the water surface aware of how deep it is: the consumer-supplied bathymetry
    /// binding, the per-cascade shoaling taper, and the breaking-surf band. Part of the
    /// <see cref="ShaderSources"/> partial; ShaderSources.Water.cs owns the surface and splices these in, and
    /// ShaderSources.WaterFft.cs owns the cascade reader that multiplies by them.
    /// <para>
    /// Split into its own file for the same reason the FFT reader is: it is a separate concern riding one shading
    /// stack, it defaults to fully off, and keeping it here means the surface's own source still reads as the
    /// surface. Everything below mirrors <see cref="WaterShoaling"/> member for member.
    /// </para>
    /// <para>
    /// <b>The bathymetry is bound FIRST, ahead of the ocean maps, and that ordering is load-bearing.</b> Two
    /// constraints stack (both spelled out on <see cref="ShaderSources.WaterFftBindingsGlsl"/>'s class note):
    /// every stage's resources must be a PREFIX of the resource layout, and within a stage the Metal
    /// cross-compiler numbers them by FIRST REFERENCE across the emitted bodies. The depth field is read by BOTH
    /// stages, so it may sit anywhere ahead of the fragment-only scene depth and still satisfy the prefix rule -
    /// but the vertex stage needs the depth BEFORE it sums the cascades, because the taper is per cascade and is
    /// applied inside that loop. Putting the depth field ahead of the ocean is what lets both stages sample in
    /// plain top-of-main order; the alternative was reading it halfway through the cascade loop on a
    /// <c>cascade == 0</c> guard, which is correct and unreadable.
    /// </para>
    /// <para>
    /// <b>Every entry point returns its identity by an EARLY RETURN when no depth field is bound</b>, never by
    /// arithmetic that happens to land on it, and the multiplications downstream are all by a literal 1.0. That is
    /// what keeps an ocean with no bathymetry bit-identical to the one that shipped before this existed.
    /// </para>
    /// </summary>
    internal static partial class ShaderSources
    {
        // Declared IDENTICALLY in both stages and FIRST in the set (see the class note). A single-channel depth in
        // metres would do, but R32Float is documented as not linearly filterable on Metal and this field is read
        // bilinearly, so it rides the same rgba16f the ocean maps use, depth in .r.
        const string WaterShoreBindingsGlsl = @"layout(set=0, binding=0) uniform texture2D BathyTex;
layout(set=0, binding=1) uniform sampler BathySamp;   // CLAMPED bilinear: the rect has edges, unlike a cascade
";

        /// <summary>
        /// Shared by both stages, and by design touches no resource, so all of it may live in functions: only the
        /// SAMPLING has to stay inside <c>main</c>. Mirrors <see cref="WaterShoaling"/>.
        /// </summary>
        const string WaterShoreCommonGlsl = @"
const float KE_SHOAL_TANH_LIMIT = 20.0;   // mirrors WaterShoaling.TanhArgumentLimit
const float KE_SURF_BACK_GAIN = 6.0;      // mirrors WaterShoaling.BackFaceGain
const float KE_BATHY_DEEP = 1e4;          // mirrors WaterShoaling.DeepMetres
const float KE_SURF_MAX_BIAS = 0.95;      // mirrors WaterShoaling.MaxCrestBias
const float KE_SURF_WASH_START = 0.6;     // mirrors WaterShoaling.WashStart
const float KE_SURF_AMP_FLOOR = 0.1;      // mirrors WaterShoaling.AmplitudeFloor
const float KE_SURF_BAND_GATE = 0.25;     // mirrors WaterShoaling.BandGate

// A cascade's energy-weighted mean wave number. A switch rather than FftWave[i], same reason oceanTile is one:
// dynamic indexing into a vector cross-compiles to a scratch array on some backends and this is two selects.
float oceanWavenumber(int i) { return i == 0 ? FftWave.x : (i == 1 ? FftWave.y : FftWave.z); }

// World XZ -> the depth field's normalized coordinates.
vec2 oceanBathyUv(vec2 xz) { return (xz - BathyRect.xy) * BathyRect.zw; }

// Whether a world position is inside the field's rectangle at all. Outside it the surface reads as deep open
// water rather than as the clamped edge value, which is what makes it affordable to bake a coastal strip at a
// useful resolution instead of a whole ocean at a useless one.
bool oceanBathyInside(vec2 uv) { return uv.x >= 0.0 && uv.y >= 0.0 && uv.x <= 1.0 && uv.y <= 1.0; }

// How far inside the breaking-surf band a depth sits: 0 in water deeper than the break line, 1 at the waterline.
// Mirrors WaterShoaling.SurfBand, with the is-anything-bound check folded in.
float oceanSurfBand(float depth) {
    float db = SurfParams.y;
    if (BathyParams.x <= 0.5 || db <= 0.0) return 0.0;
    if (depth <= 0.0) return 1.0;
    float t = clamp((db - depth) / (max(SurfParams.z, 1e-3) * db), 0.0, 1.0);
    return smoothstep(0.0, 1.0, t);
}

// Per-cascade shoaling attenuation. Mirrors WaterShoaling.Attenuation.
//
// tanh(k d) is the textbook shoaling factor read BACKWARDS on purpose: linear theory says a wave entering the
// shallows grows, and a game wants the surface to settle down to meet the beach instead of piling up against it.
// Because k is per cascade, the long swell starts calming in metres of depth where the chop is still at full
// strength, which is what a lee shore looks like even though the mechanism is not the real one.
float oceanShoal(float depth, float band, int cascade) {
    float strength = BathyParams.y;
    if (BathyParams.x <= 0.5 || strength <= 0.0) return 1.0;
    float d = max(depth, 0.0) * max(BathyParams.z, 1e-4);
    float k = oceanWavenumber(cascade);
    float taper = k <= 0.0 ? 1.0 : tanh(min(k * d, KE_SHOAL_TANH_LIMIT));
    // The break's own collapse, flat across every cascade. NOT double counting the taper: the taper is per
    // wave number and barely touches the chop, while a broken wave is turbulent whitewater at every scale.
    taper *= 1.0 - clamp(SurfShape.y, 0.0, 1.0) * clamp(band, 0.0, 1.0);
    return mix(1.0, taper, clamp(strength, 0.0, 1.0));
}

// The surge: how white a point in the band is, before the band ramp and the intensity knob. Mirrors
// WaterShoaling.Surge. The CREST term is a gate on wave PHASE, so foam exists only on the upper part of the
// incoming wave and travels with it; the TRAIL term carries it back down the seaward face, so what the crest
// whitened does not vanish the instant the crest passes.
float oceanSurge(float riseN, float backFace) {
    float b = clamp(SurfParams.w, 0.0, KE_SURF_MAX_BIAS);
    float crest = smoothstep(b, 1.0, riseN);
    float trail = smoothstep(b - max(SurfShape.x, 1e-3), b, riseN) * clamp(backFace, 0.0, 1.0);
    return clamp(max(crest, trail), 0.0, 1.0);
}

// Where on its OWN wave a point sits. Mirrors WaterShoaling.NormalizedRise, and the LOCAL normalization is
// load-bearing rather than tidy: the taper has already flattened the sea by the break line, so measuring a crest
// against the open-water significant height reports almost nothing, the gate never opens, and the band renders as
// a bare pale line. That is not hypothetical - it is what the first probe render of this feature drew.
float oceanRiseN(float rise, float atten) {
    // `amp` rather than `half`, which is a RESERVED word in HLSL and MSL and rejected outright by the
    // cross-compiler - the same trap the ocean compute kernels' `mh` note records.
    float amp = 0.5 * max(BathyParams.w, 1e-3);
    return rise / max(amp * atten, KE_SURF_AMP_FLOOR * amp);
}

// The band's coverage: crest-locked out at the break line, handing over to a solid wash as the water runs out.
// Mirrors WaterShoaling.SurfFoam. A phase gate needs a wave to gate on and at the waterline there is none left,
// by construction - a beach is white there for a different reason, so past KE_SURF_WASH_START it stops asking.
float oceanSurfFoam(float band, float surge) {
    float b = clamp(band, 0.0, 1.0);
    // The band GATES (where surf can happen), the surge SCALES (how much of it there is). Multiplying the two
    // soft ramps together instead leaves a grey wash that never reaches white anywhere.
    float gate = smoothstep(0.0, KE_SURF_BAND_GATE, b);
    return clamp(gate * max(surge, smoothstep(KE_SURF_WASH_START, 1.0, b)), 0.0, 1.0);
}
";

        /// <summary>
        /// Vertex stage, spliced INTO main inside the tap loop and AHEAD of everything else there: read this tap's
        /// water depth into <c>tapDepth</c> and its surf-band position into <c>tapBand</c>. Per TAP rather than
        /// once, because a stitched or geomorphed clipmap vertex evaluates at up to three positions and each is
        /// somewhere different on the beach.
        /// <para>
        /// <c>textureLod</c>, not <c>texture</c>: a vertex stage has no derivatives, and an implicit-LOD sample is
        /// not even valid SPIR-V outside the fragment stage.
        /// </para>
        /// </summary>
        const string WaterShoreVertGlsl = @"
        float tapDepth = KE_BATHY_DEEP;
        if (BathyParams.x > 0.5) {
            vec2 buv = oceanBathyUv(aXz);
            tapDepth = oceanBathyInside(buv) ? textureLod(sampler2D(BathyTex, BathySamp), buv, 0.0).r : KE_BATHY_DEEP;
        }
        float tapBand = oceanSurfBand(tapDepth);
";

        /// <summary>
        /// Fragment stage, spliced INTO main straight after the absolute planar position is formed and BEFORE the
        /// ocean cascades are read: this fragment's water depth into <c>bathyDepth</c> and its band position into
        /// <c>surfBand</c>. Both are then read by the cascade reader (which attenuates by them) and by the foam
        /// block below.
        /// </summary>
        const string WaterShoreFragGlsl = @"
    float bathyDepth = KE_BATHY_DEEP;
    if (BathyParams.x > 0.5) {
        vec2 buv = oceanBathyUv(wpAbsXz);
        bathyDepth = oceanBathyInside(buv) ? textureLod(sampler2D(BathyTex, BathySamp), buv, 0.0).r : KE_BATHY_DEEP;
    }
    float surfBand = oceanSurfBand(bathyDepth);
";

        /// <summary>
        /// Fragment stage, spliced into the FOAM branch: the breaking-surf response, into <c>surf</c>. Zero
        /// whenever no depth field is bound or <see cref="WaterSettings.SurfStrength"/> is 0, and the caller
        /// folds it in with a <c>max</c>, so an ocean without it is arithmetically what it was.
        /// <para>
        /// The two extra depth taps are HERE, inside the band's own branch, rather than hoisted to the top of
        /// main with the first one: they are only wanted where the surf is, which is a thin strip of the frame.
        /// </para>
        /// </summary>
        const string WaterShoreSurfGlsl = @"
        float surf = 0.0;
        if (SurfParams.x > 0.0 && surfBand > 0.0) {
            // Which way the surge runs: UP the beach, i.e. DOWN the depth gradient, from a one-texel forward
            // difference of the depth field. Taking the direction from the bathymetry rather than from the wind
            // heading is what makes an isolated shallow - a rock, a bar - break AROUND itself for free: the
            // direction wraps it, so every side of it gets its own onshore.
            float e = max(SurfShape.w, 1e-3);
            vec2 uvx = oceanBathyUv(wpAbsXz + vec2(e, 0.0));
            vec2 uvz = oceanBathyUv(wpAbsXz + vec2(0.0, e));
            float dX = (oceanBathyInside(uvx) ? textureLod(sampler2D(BathyTex, BathySamp), uvx, 0.0).r : KE_BATHY_DEEP) - bathyDepth;
            float dZ = (oceanBathyInside(uvz) ? textureLod(sampler2D(BathyTex, BathySamp), uvz, 0.0).r : KE_BATHY_DEEP) - bathyDepth;
            vec2 upBeach = vec2(-dX, -dZ);
            float ul = length(upBeach);
            vec2 onshore = ul > 1e-6 ? upBeach / ul : vec2(0.0);
            // Crest PHASE, which is the whole difference between a wave crashing and a band glowing: the
            // DISPLACED surface's height above still water, against the amplitude the wave has HERE rather than
            // the one it had in open water. vWorldPos is render-relative and SurfShape.z is the plane's surface Y
            // in the same frame, so the origin cancels and no absolute is formed.
            float riseN = oceanRiseN(vWorldPos.y - SurfShape.z, oceanShoal(bathyDepth, surfBand, 0));
            float back = clamp(dot(oceanSlope, onshore) * KE_SURF_BACK_GAIN, 0.0, 1.0);
            surf = clamp(oceanSurfFoam(surfBand, oceanSurge(riseN, back)) * SurfParams.x, 0.0, 1.0);
        }
";
    }
}
