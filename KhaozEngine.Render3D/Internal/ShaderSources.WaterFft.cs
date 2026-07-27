namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The GLSL blocks that turn the water pair into an FFT-ocean reader (<see cref="WaterWaveSource.FftOcean"/>):
    /// the two map bindings, and the cascade summation each stage splices into its own <c>main</c>. Part of the
    /// <see cref="ShaderSources"/> partial; ShaderSources.Water.cs owns the surface itself and splices these in.
    /// <para>
    /// Split out because the two wave sources are genuinely separate concerns sharing one shading stack. Keeping
    /// the FFT reader here means the procedural surface's source reads exactly as it did in 14.28.0, and the FFT
    /// branch is one clearly-bounded block in each stage rather than a diff smeared through both.
    /// </para>
    /// <para>
    /// <b>Two Metal-only landmines shape everything below.</b> Neither is visible in the GLSL, and between them
    /// they are why the ocean is ONE texture, declared in both stages, bound ahead of the fragment-only scene
    /// depth rather than as two textures bound last.
    /// </para>
    /// <para>
    /// First, Veldrid numbers a backend's resource slots with one counter PER KIND across the whole resource
    /// layout, and binds each element to the stages in its mask - while the cross-compiler numbers each stage
    /// DENSELY over only the bindings that stage declares. The two agree only when every stage's resources are a
    /// PREFIX of the layout. A vertex-only texture sitting after a fragment-only one therefore cannot line up at
    /// any binding number: the vertex sees dense index 0 and Veldrid binds it at global index 1, so the vertex
    /// samples an unbound slot and gets zero, silently. Hence one ocean map array, declared identically in both
    /// stages, ahead of the fragment-only scene depth - the vertex's resources are then a prefix of each kind, and
    /// the fragment's are the whole list. (16.13.0's bathymetry field is read by both stages too, and sits ahead
    /// of the ocean for a reason of its own: see <see cref="WaterShoreBindingsGlsl"/>'s file.)
    /// </para>
    /// <para>
    /// Second, within a stage that dense numbering follows FIRST REFERENCE across the emitted function bodies, and
    /// a function is emitted before <c>main</c>. A tidy <c>oceanSurface()</c> helper reached the ocean map before
    /// <c>main</c> touched the scene depth and swapped the two, so the water read its own derivative layer as the
    /// depth buffer. Both stages therefore sample INSIDE <c>main</c>, ocean first, which is the same
    /// first-sample-order rule ModelFrag and SplatFrag already carry with the extra twist that a helper function
    /// jumps the queue. The fragment's ocean block sits inside its <c>if</c> so Procedural mode still pays nothing
    /// at runtime; emission order is a static property and the branch does not affect it.
    /// </para>
    /// </summary>
    internal static partial class ShaderSources
    {
        // Declared IDENTICALLY in both stages, and ahead of the fragment-only scene depth, both deliberately (see
        // the class note). Layers [0, cascadeCount) are displacement, [cascadeCount, 2*cascadeCount) are
        // derivatives. Bindings 0/1 are the bathymetry pair, which both stages also declare and which both sample
        // FIRST (ShaderSources.WaterShore.cs).
        const string WaterFftBindingsGlsl = @"layout(set=0, binding=2) uniform texture2DArray OceanMap;
layout(set=0, binding=3) uniform sampler OceanSamp;   // WRAPPING bilinear: each cascade tiles at its own period
";

        // Shared by both stages. Touches no resource, so it may safely live in a function: only the SAMPLING has to
        // stay inside main. A switch rather than FftTiles[i], because dynamic indexing into a vector cross-compiles
        // to a scratch array on some backends and this is two selects.
        //
        // The sampling-frame block below mirrors Internal/OceanFocus.cs member for member, in the same op order
        // and with the same literals. Every one of it defaults to the identity, and the identity is reached by an
        // EARLY RETURN rather than by computing cos(0): GLSL allows sin/cos a couple of ULP, so a backend that
        // answered 0.99999994 for cos(0) would scale every sample position by that, and an ocean with no focus
        // set would stop being bit-identical to the one that shipped before this existed.
        const string WaterFftCommonGlsl = @"
const int KE_MAX_CASCADES = 3;   // mirrors OceanSpectrum.MaxCascades and the compute kernels' Cascade[3]
const float KE_OCEAN_TWO_PI = 6.28318531;
const float KE_FOCUS_MIN_D2 = 1e-8;   // mirrors OceanFocus.MinFocusDistanceSquared
const float KE_FOCUS_UNIT_TOL = 1e-6; // mirrors OceanFocus.UnitTolerance
const float KE_WARP_PEAK = 1.7;       // mirrors OceanFocus.WarpPeak
const float KE_WARP_FREQ_B = 0.57;    // mirrors OceanFocus.WarpFrequencyB

float oceanTile(int i) { return i == 0 ? FftTiles.x : (i == 1 ? FftTiles.y : FftTiles.z); }
float oceanVariance(int i) { return i == 0 ? FftVariance.x : (i == 1 ? FftVariance.y : FftVariance.z); }
float oceanRotCos(int i) { return i == 0 ? FftRotCos.x : (i == 1 ? FftRotCos.y : FftRotCos.z); }
float oceanRotSin(int i) { return i == 0 ? FftRotSin.x : (i == 1 ? FftRotSin.y : FftRotSin.z); }

// Half-texel offset so a world position lands on a texel CENTRE rather than on the boundary between two: the
// compute kernel writes texel (px, pz) for world (px, pz) * tile / resolution.
vec2 oceanUv(vec2 xz, float tile, float halfTexel) { return xz / tile + halfTexel; }

// A cascade's world-space texel size: its tile over the FFT resolution.
float oceanTexel(float tile) { return tile / max(FftParams.z, 1.0); }

// Mip level that low-passes a cascade to `spacing`'s Nyquist. Mirrors WaterClipmap.MipLevel exactly, and is the
// per-ring band limit KhaozEngine#296 is about: mip m carries texels of texel * 2^m and so a shortest wavelength
// of twice that, and solving 2 * texel * 2^m >= samples * spacing gives the log2 below.
//
// FftParams.w is the TOP MIP INDEX of the live maps and is 0 unless the clipmap grid asked for a chain, so the
// whole thing early-returns 0 on the camera-focused path - deliberately by an early return rather than by
// arithmetic that happens to land on 0, so the pre-mip surface samples a literal 0.0 exactly as it always did.
// A `samples` of 0 is FootprintSamples' documented band-limit-off switch and turns this off with it, so the two
// mechanisms cannot disagree about whether the surface is being low-passed.
// Touches no resource, so it is safe in a function: only the SAMPLING has to stay inside main.
float oceanMip(float spacing, float texel, float samples) {
    float maxMip = FftParams.w;
    if (maxMip <= 0.0 || spacing <= 0.0 || texel <= 1e-9 || samples <= 0.0) return 0.0;
    float want = spacing * max(samples, 1.0) / (2.0 * texel);
    if (want <= 1.0) return 0.0;
    return min(log2(want), maxMip);
}

// ---- Sampling frame (mirrors Internal/OceanFocus.cs) ----

// R(a + b) from the two (cos, sin) pairs. Composing two identities is exactly the identity again.
vec2 oceanRotAdd(vec2 a, vec2 b) { return vec2(a.x * b.x - a.y * b.y, a.y * b.x + a.x * b.y); }

// World XZ -> a cascade's sampling frame: R(-theta). The POSITION turns this way, so a wave the map carries at
// wave vector k lands in the world at R(theta) k - which is how turning the frame turns the sea.
vec2 oceanToSample(vec2 xz, vec2 cs) { return vec2(cs.x * xz.x + cs.y * xz.y, cs.x * xz.y - cs.y * xz.x); }

// A sampled VECTOR (horizontal displacement, height slope) back into the world frame: R(+theta). Scalars the
// maps carry (height, foam, jacobian) are rotation-invariant and pass through untouched.
vec2 oceanToWorld(vec2 v, vec2 cs) { return vec2(cs.x * v.x - cs.y * v.y, cs.y * v.x + cs.x * v.y); }

// Rescale an INTERPOLATED pair back to unit. Across a triangle the varying interpolates to the chord, which is
// exactly the wanted behaviour (it never sweeps the long way round the way an interpolated ANGLE would wherever
// the angle wraps, which near the focus point is every triangle) but is a hair short, and a short pair scales
// the sample position as well as turning it. Pairs already unit are returned UNCHANGED rather than divided by a
// computed 1.0: hardware interpolation of a constant attribute is not promised to be exact, and neither is
// inversesqrt(1.0), so the unrotated case must not go through either.
vec2 oceanUnitRot(vec2 cs) {
    float l2 = dot(cs, cs);
    if (l2 <= KE_FOCUS_MIN_D2) return vec2(1.0, 0.0);
    if (abs(l2 - 1.0) <= KE_FOCUS_UNIT_TOL) return cs;
    return cs * inversesqrt(l2);
}

// The onshore-focus rotation WANTED at a world XZ: turn the frame so the spectrum's heading (FftFocus.w) points
// from here at FftFocus.xy, scaled by FftFocus.z. The difference is wrapped to the short way round, which is
// what puts a PARTIAL focus's unavoidable seam on the ray running downwind from the focus point.
vec2 oceanFocusRot(vec2 xz) {
    if (FftFocus.z <= 0.0) return vec2(1.0, 0.0);
    vec2 toFocus = FftFocus.xy - xz;
    float d2 = dot(toFocus, toFocus);
    if (d2 <= KE_FOCUS_MIN_D2) return vec2(1.0, 0.0);
    float delta = atan(toFocus.y, toFocus.x) - FftFocus.w;
    delta = atan(sin(delta), cos(delta));
    float phi = FftFocus.z * delta;
    return vec2(cos(phi), sin(phi));
}

// Quantize that wanted rotation onto the ring of FftSector.x fixed lattice rotations and return the blend:
// xy = the LOWER tap's (cos, sin), z = the position between the two taps, w = the L2 weight normalizer.
//
// It has to be a blend rather than a rotation of the sampling coordinate, and that is not a preference. A
// rotation field that turns to face a point winds once around it, so about that point its angle cancels the
// sample's own azimuth and the whole plane maps onto ONE RAY of the map - every crest becomes a circle and the
// sea renders as a bullseye. (No non-constant rotation field is a valid coordinate map at all: equate the mixed
// partials and grad phi falls out zero.) Each tap here is a plain CONSTANT-rotation sample, so each is
// undistorted, and only the two either side are ever non-zero - the sector count is free.
vec4 oceanSectors(vec2 focusRot) {
    if (FftFocus.z <= 0.0) return vec4(1.0, 0.0, 0.0, 1.0);   // one tap, no rotation, full weight: exact
    float phi = atan(focusRot.y, focusRot.x);
    float m = phi * FftSector.x / KE_OCEAN_TWO_PI;
    float m0 = floor(m);
    float t = m - m0;
    float a0 = m0 * KE_OCEAN_TWO_PI / FftSector.x;
    float norm = inversesqrt(max((1.0 - t) * (1.0 - t) + t * t, 1e-8));
    return vec4(cos(a0), sin(a0), t, norm);
}

// The very-large-scale STATIC warp of the sampling domain, applied BEFORE the rotations. Two incommensurate
// frequencies per axis so the warp does not itself tile at its own wavelength. Static, unlike the ripple field's
// warp: at several times the largest cascade tile, a drifting one reads as the whole sea sloshing.
vec2 oceanWarp(vec2 xz) {
    float amp = FftRotCos.w;
    if (amp <= 0.0) return xz;
    float k = KE_OCEAN_TWO_PI / max(FftRotSin.w, 1.0);
    float ax = sin(xz.y * k) + 0.7 * sin(xz.x * k * KE_WARP_FREQ_B);
    float az = cos(xz.x * k) + 0.7 * cos(xz.y * k * KE_WARP_FREQ_B);
    return xz + vec2(ax, az) * (amp / KE_WARP_PEAK);
}
";

        /// <summary>
        /// Vertex stage, spliced INTO main (inside the caller's tap loop, so <c>sxz</c> is THIS tap's sample XZ and
        /// <c>bandCell</c> its grid spacing): sum every cascade's displacement into <c>oceanDisp</c>. Sampled with
        /// <c>textureLod</c> because a vertex shader has no derivatives to pick a mip with, so the level comes from
        /// the grid instead - <c>bandCell</c> through <c>oceanMip</c>, which is 0 on the camera-focused grid and
        /// the ring's own cell size on the clipmap.
        /// <para>
        /// <b>This stage OWNS the sampling frame, and hands it down.</b> The focus rotation is computed here, from
        /// the UNDISPLACED grid position, and written to <c>tapRot</c> / <c>tapRef</c> which the caller averages
        /// across taps and passes on as varyings. The fragment never re-derives it. That is what keeps the shading
        /// attached to the geometry:
        /// deriving it a second time from the DISPLACED position (the only position the fragment has of its own)
        /// would rotate the normals in a frame the displacement was never computed in, and the surface's lighting
        /// would detach from its silhouette wherever the two disagreed.
        /// </para>
        /// <para>
        /// The sampled horizontal displacement is a VECTOR in the cascade's own frame, so it comes back through
        /// <c>oceanToWorld</c>. The height is a scalar and does not.
        /// </para>
        /// </summary>
        const string WaterFftVertGlsl = @"
        int nc = clamp(int(FftParams.y + 0.5), 1, KE_MAX_CASCADES);
        float halfTexel = 0.5 / max(FftParams.z, 1.0);
        tapRot = oceanFocusRot(aXz);
        tapRef = oceanWarp(aXz);
        vec4 sec = oceanSectors(tapRot);
        vec2 csHi = oceanRotAdd(sec.xy, FftSector.yz);
        float wLo = (1.0 - sec.z) * sec.w, wHi = sec.z * sec.w;
        vec3 oceanDisp = vec3(0.0);
        for (int i = 0; i < KE_MAX_CASCADES; i++) {
            if (i >= nc) break;
            vec2 off = vec2(oceanRotCos(i), oceanRotSin(i));
            vec2 cs = oceanRotAdd(sec.xy, off);
            // Shoaling: exactly 1.0 with no depth field bound (early return), so the weights below are multiplied
            // by a literal 1 and an ocean without bathymetry displaces bit for bit what it always did.
            float shoal = oceanShoal(tapDepth, tapBand, i);
            // The per-ring band limit. bandCell is this vertex's own grid spacing under WaterGridMode.Clipmap and
            // a literal 0 under WaterGridMode.CameraFocused, where oceanMip returns 0 and this is the LOD-0 sample
            // it always was. A vertex shader has no derivatives, so the level has to come from the geometry.
            float lod = oceanMip(bandCell, oceanTexel(oceanTile(i)), FootprintParams.z);
            vec2 uv = oceanUv(oceanToSample(tapRef, cs), oceanTile(i), halfTexel);
            vec4 dm = textureLod(sampler2DArray(OceanMap, OceanSamp), vec3(uv, float(i)), lod);
            vec2 dxz = oceanToWorld(dm.xz, cs);
            oceanDisp += vec3(dxz.x, dm.y, dxz.y) * (wLo * shoal);
            // The second tap only exists mid-sector. Skipping it is what keeps an unfocused ocean at ONE sample
            // per cascade, and the branch is uniform there because the weight comes from a uniform.
            if (wHi > 0.0) {
                vec2 cs2 = oceanRotAdd(csHi, off);
                vec2 uv2 = oceanUv(oceanToSample(tapRef, cs2), oceanTile(i), halfTexel);
                vec4 dm2 = textureLod(sampler2DArray(OceanMap, OceanSamp), vec3(uv2, float(i)), lod);
                vec2 dxz2 = oceanToWorld(dm2.xz, cs2);
                oceanDisp += vec3(dxz2.x, dm2.y, dxz2.y) * (wHi * shoal);
            }
        }
";

        /// <summary>
        /// Fragment stage, spliced INTO main ahead of the scene-depth fetch and inside the FFT branch: sum the
        /// cascades' slope into <c>oceanSlope</c>, take the strongest foam into <c>oceanFoam</c>, and accumulate
        /// into <c>oceanLost</c> the slope variance the footprint band-limit removed, so the glint lobe can absorb
        /// it. All three are declared by the caller, so this block can live inside the branch.
        /// <para>
        /// The band-limit is the SAME measure the procedural spectrum uses (<c>rippleResolve</c> against the pixel
        /// footprint), applied per cascade against twice its texel size, which is the shortest wave that cascade
        /// can carry. It has to be here: a 128-texel cascade over a 14 metre tile is 11 cm of detail, and past the
        /// distance where a pixel covers that, it is noise that crawls. Where a mip chain exists
        /// (<see cref="WaterGridMode.Clipmap"/>) the hardware filter does the removal properly and
        /// <c>rippleResolve</c> only supplies what the chain ran out of; where it does not, this is unchanged.
        /// </para>
        /// <para>
        /// Foam is still not SCALED by the band limit - it is the one channel whose far-field read should survive,
        /// and a whitecap two kilometres out is still white. It does ride the same mip, which is not the same
        /// thing and is the right answer for it: foam is a bounded coverage, so box-averaging it over the pixel
        /// footprint preserves its mean while a point sample at LOD 0 just aliases whether a given whitecap
        /// happened to land on this texel.
        /// </para>
        /// <para>
        /// The removed variance comes from the per-cascade slope variance baked with the spectrum, not from the
        /// sampled slope: the sampled value is one realization at one texel, and Toksvig wants the statistic.
        /// </para>
        /// <para>
        /// The sampling frame arrives from the VERTEX stage - <c>vRefXz</c> already warped, <c>vFocusRot</c> the
        /// focus rotation as an interpolated <c>(cos, sin)</c> pair - so both stages read the maps in one frame
        /// rather than each deriving its own. The sampled SLOPE is a vector in that frame and comes back through
        /// <c>oceanToWorld</c>; foam and the Jacobian are scalars and do not. Rotating the slope back matters more
        /// than it looks: a rotation preserves its LENGTH, so the normal stays unit and the Toksvig variance the
        /// glint lobe receives is unchanged, but skipping the rotation would leave the whole surface lit as though
        /// the waves ran on the unrotated heading while their geometry ran on the rotated one.
        /// </para>
        /// </summary>
        const string WaterFftFragGlsl = @"
        int nc = clamp(int(FftParams.y + 0.5), 1, KE_MAX_CASCADES);
        float res = max(FftParams.z, 1.0);
        float halfTexel = 0.5 / res;
        vec2 focusRot = FftFocus.z > 0.0 ? oceanUnitRot(vFocusRot) : vec2(1.0, 0.0);
        vec4 sec = oceanSectors(focusRot);
        vec2 csHi = oceanRotAdd(sec.xy, FftSector.yz);
        // L2 weights for the displacement/slope maps (zero-mean Gaussian fields, so this conserves the
        // spectrum's variance); plain linear ones for foam, which is a bounded coverage and wants its mean.
        float wLo = (1.0 - sec.z) * sec.w, wHi = sec.z * sec.w;
        for (int i = 0; i < KE_MAX_CASCADES; i++) {
            if (i >= nc) break;
            float tile = oceanTile(i);
            float texel = tile / res;
            vec2 off = vec2(oceanRotCos(i), oceanRotSin(i));
            vec2 cs = oceanRotAdd(sec.xy, off);
            // How much of THIS CASCADE'S finest content the pixel can resolve at all. Unchanged, and it is what
            // the Toksvig transfer below wants: the total attenuation from full detail down to the pixel's
            // resolving power, which is the variance that has to reappear as lobe width however it was removed.
            float keepAll = rippleResolve(2.0 * texel, footprint, footprintSamples);
            // With a mip chain the hardware filter does most of that removal properly instead of the shader
            // scaling an aliased sample down, so `keep` is only the residual attenuation still owed on top of the
            // mip. It collapses to keepAll at lod 0, which is the whole camera-focused path.
            float lod = oceanMip(footprint, texel, footprintSamples);
            float keep = lod > 0.0 ? rippleResolve(2.0 * texel * exp2(lod), footprint, footprintSamples) : keepAll;
            // The SAME shoaling factor the vertex stage applied to this cascade's displacement, so the shading
            // stays attached to the geometry: a crest the vertex flattened must not still be lit as a crest.
            // Exactly 1.0 with no depth field bound.
            float shoal = oceanShoal(bathyDepth, surfBand, i);
            // Derivative layers follow the displacement layers, so cascade i is at nc + i.
            vec4 d = textureLod(sampler2DArray(OceanMap, OceanSamp),
                                vec3(oceanUv(oceanToSample(vRefXz, cs), tile, halfTexel), float(nc + i)), lod);
            oceanSlope += oceanToWorld(d.xy, cs) * (keep * wLo * shoal);
            float foam = d.z * (1.0 - sec.z);
            if (wHi > 0.0) {
                vec2 cs2 = oceanRotAdd(csHi, off);
                vec4 d2 = textureLod(sampler2DArray(OceanMap, OceanSamp),
                                     vec3(oceanUv(oceanToSample(vRefXz, cs2), tile, halfTexel), float(nc + i)), lod);
                oceanSlope += oceanToWorld(d2.xy, cs2) * (keep * wHi * shoal);
                foam += d2.z * sec.z;
            }
            // Whitecaps ride the taper too: a crest the shoaling flattened is not breaking, and leaving its foam
            // behind would leave a wash of white sitting on calm shallows. The BREAKING foam that replaces it
            // comes from the surf band in the fragment's foam block, which is a max() over this.
            oceanFoam = max(oceanFoam, foam * shoal);
            oceanLost += oceanVariance(i) * shoal * shoal * (1.0 - keepAll * keepAll);
        }
";
    }
}
