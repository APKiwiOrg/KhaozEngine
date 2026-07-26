namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The stylized ocean surface (2 of the renderer's shader sources: <see cref="WaterVert"/> +
    /// <see cref="WaterFrag"/>). Part of the <see cref="ShaderSources"/> partial: see ShaderSources.cs for the
    /// shared contract (GLSL #version 450, cross-compiled at load via the GPU seam's SPIR-V path). Split out of
    /// ShaderSources.Sky.cs in 14.24.0, when the swell/reflection/foam work grew the pair past the size at which
    /// it was reasonable to keep it inside the sky file.
    /// </summary>
    internal static partial class ShaderSources
    {
        // The Water UBO block, declared IDENTICALLY in both stages (one buffer, read by both, per the
        // one-UBO-per-set rule). UboLayoutTests asserts every member appears in both, in both directions, so a
        // rename on one side cannot silently reinterpret the other side's bytes. Packing note: every member is
        // 16-byte aligned, so std140 needs no explicit padding, and WaterRenderer.WaterUbo is the C# mirror.
        const string WaterUboGlsl = @"layout(set=0, binding=4) uniform Water {
    mat4 ViewProj;
    mat4 InvViewProj;   // RAW (not clip-corrected) inverse, for the fragment's depth reconstruction
    vec4 LightDir;      // xyz = key light travel direction
    vec4 LightColor;
    vec4 CameraPos;     // xyz = eye position
    vec4 DeepColor;     // rgb + alpha, the body colour at depth
    vec4 ShallowColor;  // rgb + alpha, the body colour at the waterline
    vec4 HorizonColor;  // rgb + alpha, the flat reflection fallback
    vec4 WaveParams;    // x=waveScale, y=waveSpeed, z=normalStrength, w=time
    vec4 ShoreGlint;    // x=shoreFadeDistance, y=glintStrength, z=glintExponent, w=opacity
    vec4 DetailParams;  // x=warpStrength, y=detailFadeDistance, z=distantDetailScale, w=shallowDepth
    vec4 SkyHorizon;    // rgb, the reflected sky's horizon colour
    vec4 SkyZenith;     // rgb, the reflected sky's zenith colour
    vec4 SkySunColor;   // rgb, the reflected sun disc + halo colour
    vec4 SkyParams;     // x=sunEnabled, y=sunRadius, z=haloStrength, w=haloFalloff
    vec4 ReflectGlint;  // x=skyReflectionStrength, y=skyReflectionSunStrength, z=glintRoughness, w=glintDistantRoughness
    vec4 SwellParams;   // x=amplitude, y=wavelength, z=directionRadians, w=spreadRadians
    vec4 SwellShape;    // x=steepness, y=speedScale, z=componentCount, w=seed
    vec4 Absorption;    // rgb = per-metre absorption coefficients (all-zero = legacy two-stop blend), w unused
    vec4 FoamColor;     // rgba
    vec4 FoamParams;    // x=strength, y=crestCoverage, z=shoreWidth, w=patternScale
    vec4 RippleSpectrum;   // x=componentCount, y=lacunarity, z=gain, w=seed
    vec4 FootprintParams;  // x=samplesPerWavelength (0 = band-limit off), y=varianceToRoughness, z/w reserved
    vec4 FftParams;        // x=1 when the FFT ocean maps are live (0 = procedural), y=cascadeCount, z=resolution, w reserved
    vec4 FftTiles;         // xyz = per-cascade tile size in world metres, w reserved
    vec4 FftVariance;      // xyz = per-cascade slope variance of the baked spectrum (Toksvig input), w reserved
};";

        // ---- Stylized ocean surface. Drawn AFTER the sky and the ground decals into ColorDepthFB (lit colour +
        //      read-only scene depth). Depth test ON (Less, standard, so terrain/props above the surface occlude
        //      it, matching the textured-billboard/beam depth-interleave convention) but depth WRITE OFF: the
        //      outline pass reads the resolved normal/linear-depth MRT (ColorTex's siblings), and those are
        //      captured by the OPAQUE model pass alone (see RenderResources.ResolveDepthNormal/ResolveColor, which
        //      run BEFORE this pass in Scene3D.RenderInternal) - a water depth WRITE would need its own MRT write
        //      to keep that buffer meaningful, which reflections/probes (out of scope, issue #54) would want but
        //      this LDR pass does not attempt. No-write keeps the edge outline tracing the solid geometry's
        //      silhouette (a shore-line water edge is desirable; a corrupted normal/depth buffer that broke the
        //      outline pass for EVERYTHING behind the water is not).
        //
        //      The surface is a CPU-tessellated grid (WaterMath.GridResolution, laid out by
        //      WaterMath.BuildGridPositions with its vertices concentrated near the camera) displaced in the VERTEX
        //      stage by a Gerstner swell, with three domain-warped scrolling ripple layers perturbing the normal
        //      per pixel on top - or, under WaterWaveSource.FftOcean, by a Tessendorf FFT surface read out of the
        //      ocean map array (see ShaderSources.WaterFft). Two textures bound: the ocean map array and the
        //      resolved scene depth, IN THAT ORDER, and both stages sample the ocean first - the Metal
        //      first-sample-order rule, plus the harder constraint that each stage's resources must be a prefix of
        //      the resource layout (WaterRenderer's layout note has the full reasoning). Vertex inputs are Position
        //      only (no gap-free-signature hazard: everything declared is read). One UBO (read by both stages, the
        //      vertex needing ViewProj + the swell block). ----

        /// <summary>
        /// Water vertex stage: displaces the still-water grid by the Gerstner swell and hands the fragment the
        /// displaced world position, the swell's analytic normal, and its fold factor for whitecaps.
        /// <para>
        /// The component stack is REGENERATED here from the seven scalars in <c>SwellParams</c>/<c>SwellShape</c>
        /// rather than uploaded per component, which is why the whole swell costs two vec4s of UBO instead of one
        /// per wave. Mirrors <see cref="GerstnerWaves"/> exactly (same generator, same op order, same constants);
        /// the loop is bounded by a compile-time constant with an early break on the runtime count, the form every
        /// backend's cross-compiler handles without an unroll hazard.
        /// </para>
        /// </summary>
        public const string WaterVert = @"#version 450
" + WaterFftBindingsGlsl + WaterUboGlsl + WaterFftCommonGlsl + @"
layout(location=0) in vec3 Position;
layout(location=0) out vec3 vWorldPos;
layout(location=1) out vec3 vSwellNormal;
layout(location=2) out float vFold;
layout(location=3) out vec2 vRefXz;   // the STILL-water XZ, i.e. where this vertex samples the ocean maps

const float KE_GRAVITY = 9.81;
const float KE_LAMBDA_DECAY = 0.685;
const float KE_TWO_PI = 6.28318531;
const float KE_SEED_STRIDE = 1.61803399;
const int   KE_MAX_COMPONENTS = 8;

void main() {
    float amplitude = SwellParams.x, wavelength = SwellParams.y;
    float dirRad = SwellParams.z, spreadRad = SwellParams.w;
    float steepness = SwellShape.x, speedScale = SwellShape.y, seed = SwellShape.w;
    float time = WaveParams.w;

    vec3 p = Position;
    vec3 swellNormal = vec3(0.0, 1.0, 0.0);
    float fold = 0.0;

    // FFT ocean: the displacement is a texture lookup per cascade, and the normal + the fold both come out of the
    // derivative map in the fragment, so the whole Gerstner block below is skipped rather than added to. The two
    // sources are alternatives, never a sum - summing them would double-count the same sea twice over.
    if (FftParams.x > 0.5) {
" + WaterFftVertGlsl + @"        p = Position + oceanDisp;
    } else if (amplitude > 0.0 && wavelength > 0.0) {
        int n = clamp(int(SwellShape.z + 0.5), 1, KE_MAX_COMPONENTS);
        // Closed-form geometric sum (NOT an accumulated loop), matching GerstnerWaves.BuildComponents so the two
        // round identically instead of drifting by however each happened to accumulate.
        float lambdaSum = wavelength * (1.0 - pow(KE_LAMBDA_DECAY, float(n))) / (1.0 - KE_LAMBDA_DECAY);

        vec3 offset = vec3(0.0);
        float nx = 0.0, nz = 0.0, nyLoss = 0.0;   // analytic normal accumulators
        float jxx = 0.0, jzz = 0.0, jxz = 0.0;    // horizontal Jacobian accumulators
        for (int i = 0; i < KE_MAX_COMPONENTS; i++) {
            if (i >= n) break;
            float fi = n > 1 ? float(i) / float(n - 1) : 0.5;
            float fan = fi * 2.0 - 1.0;
            fan *= 0.55 + 0.45 * abs(fan);        // s-curve: cluster the middle, push the edges out
            float angle = dirRad + spreadRad * fan;
            float lambda = wavelength * pow(KE_LAMBDA_DECAY, float(i));
            float k = KE_TWO_PI / lambda;
            float a = amplitude * lambda / lambdaSum;
            float omega = sqrt(KE_GRAVITY * k) * speedScale;
            float q = a > 1e-6 ? steepness / (k * a * float(n)) : 0.0;
            float ph = seed * float(i + 1) * KE_SEED_STRIDE;
            vec2 d = vec2(cos(angle), sin(angle));

            float phase = k * (d.x * Position.x + d.y * Position.z) - omega * time + ph;
            float s = sin(phase), cs = cos(phase);

            float qa = q * a;                      // horizontal orbital radius
            offset.x += qa * d.x * cs;
            offset.z += qa * d.y * cs;
            offset.y += a * s;

            float wa = k * a;                      // slope magnitude
            nx += d.x * wa * cs;
            nz += d.y * wa * cs;
            nyLoss += q * wa * s;

            float qka = q * k * a;                 // == steepness / n, by construction
            jxx += qka * d.x * d.x * s;
            jzz += qka * d.y * d.y * s;
            jxz += qka * d.x * d.y * s;
        }
        p += offset;

        vec3 nv = vec3(-nx, 1.0 - nyLoss, -nz);
        float nl = length(nv);
        swellNormal = nl > 1e-8 ? nv / nl : vec3(0.0, 1.0, 0.0);

        // Determinant of the horizontal Jacobian: 1 where undeformed, > 1 in stretched troughs, dropping toward 0
        // at compressed crests. 1 - determinant is therefore a physical whitecap driver, and dividing by the
        // steepness normalizes it so the foam coverage knob means the same thing at any steepness.
        float jXX = 1.0 - jxx, jZZ = 1.0 - jzz, jXZ = -jxz;
        float determinant = jXX * jZZ - jXZ * jXZ;
        fold = max(0.0, 1.0 - determinant) / max(steepness, 1e-4);
    }

    gl_Position = ViewProj * vec4(p, 1.0);
    vWorldPos = p;
    vSwellNormal = swellNormal;
    vFold = fold;
    vRefXz = Position.xz;
}";

        /// <summary>
        /// Water fragment stage. Mirrors <see cref="WaterMath"/> (ripple normals, depth grading, reflection blend,
        /// glint, foam, shore fade) and <see cref="SkyMath.ShadeDirection"/> (the reflected sky) exactly.
        /// </summary>
        public const string WaterFrag = @"#version 450
" + WaterFftBindingsGlsl + @"
layout(set=0, binding=2) uniform texture2D DepthTex;   // .r = resolved scene linear depth (single-channel R32F)
layout(set=0, binding=3) uniform sampler Samp;
" + WaterUboGlsl + WaterFftCommonGlsl + @"
layout(location=0) in vec3 vWorldPos;
layout(location=1) in vec3 vSwellNormal;
layout(location=2) in float vFold;
layout(location=3) in vec2 vRefXz;
layout(location=0) out vec4 oColor;

const float KE_WHITECAP_SOFTNESS = 0.18;   // mirrors WaterMath.WhitecapSoftness
const float KE_TWO_PI = 6.28318531;
const int   KE_MAX_RIPPLES = 12;           // mirrors RippleSpectrum.MaxComponents
const int   KE_MAX_SWELL = 8;              // mirrors GerstnerWaves.MaxComponents
const float KE_GOLDEN_ANGLE = 2.39996323;  // mirrors RippleSpectrum.GoldenAngle
const float KE_PHASE_STRIDE = 4.74311;     // mirrors RippleSpectrum.PhaseStride
const float KE_LEGACY_SLOPE_VARIANCE = 2.72317;   // mirrors RippleSpectrum.LegacySlopeVariance
const float KE_LAMBDA_DECAY = 0.685;       // mirrors GerstnerWaves.LambdaDecay

// Mirrors WaterMath.DomainWarp exactly: a slow, large-scale displacement of the sample position applied BEFORE the
// ripple layers, so their pattern is bent over a distance several times their own wavelength. Its Jacobian is
// deliberately NOT folded into the analytic slope below (see the WaterMath.DomainWarp note).
vec2 domainWarp(vec2 xz, float scrollTime, float waveScale, float warpStrength) {
    if (warpStrength <= 0.0) return xz;
    float scale = max(waveScale, 1e-4);
    float k = 0.21 / scale;
    float wt = scrollTime * 0.23;
    float ax = sin(xz.y * k + wt) + 0.7 * sin(xz.x * k * 0.57 - wt * 1.31);
    float az = cos(xz.x * k - wt * 0.79) + 0.7 * cos(xz.y * k * 0.57 + wt * 1.17);
    return xz + vec2(ax, az) * (warpStrength * scale);
}

// Mirrors WaterMath.DetailScale exactly: 1 at the camera, falling to distantScale at/after fadeDistance, so the
// fine ripple layers stop aliasing into a crawling moire in the far field.
float detailScaleFor(float camDist, float fadeDistance, float distantScale) {
    if (fadeDistance <= 0.0) return 1.0;
    float s = smoothstep(0.0, fadeDistance, camDist);
    return mix(1.0, clamp(distantScale, 0.0, 1.0), s);
}

// Mirrors RippleSpectrum.Resolve exactly: how much of a component of this wavelength survives at this pixel.
// 1 while the wavelength is comfortably wider than `samples` footprints, smoothly to 0 as it drops below. This is
// the half of band-limiting 14.24.0 left out: that release widened the specular LOBE by footprint, but left the
// normal FIELD oscillating at frequencies the pixel cannot resolve, and that is precisely what moire is.
float rippleResolve(float wavelength, float footprint, float samples) {
    if (footprint <= 0.0 || samples <= 0.0) return 1.0;
    float need = footprint * samples;
    if (need <= 1e-8) return 1.0;
    return smoothstep(0.0, 1.0, clamp(wavelength / need, 0.0, 1.0));
}

// Mirrors RippleSpectrum.Build + Slope exactly: generate the ripple spectrum from four scalars and evaluate its
// band-limited slope. Returns (dH/dx, dH/dz, removed slope variance).
//
// This replaced three fixed cosines. Three coherent cosines do not make a surface, they make a RULED pattern:
// their summed slope is constant along a family of parallel lines, the domain warp only bends those lines rather
// than breaking them, and at distance they beat against the pixel grid into moire. Headings here step by the
// golden angle so no two are parallel and no subset lines up at any count, wave numbers climb geometrically over
// several octaves, and amplitudes are normalized so the whole set carries the same slope variance the old field
// did - which is what keeps NormalStrength meaning what it meant.
vec3 waterSlope(vec2 xz, float time, float waveScale, float waveSpeed, float warpStrength, float detail,
                float footprint, float samples) {
    float scale = max(waveScale, 1e-4);
    float k0 = 1.0 / scale;
    float t = time * waveSpeed;
    vec2 p = domainWarp(xz, t, waveScale, warpStrength);

    int n = clamp(int(RippleSpectrum.x + 0.5), 1, KE_MAX_RIPPLES);
    float lac = max(RippleSpectrum.y, 1.01);
    float g = clamp(RippleSpectrum.z, 0.05, 1.5);
    float seed = RippleSpectrum.w;

    // Closed-form geometric sum, matched literally by the CPU mirror so the two round alike.
    float r = g * lac;
    float rr = r * r;
    float sumSq = abs(1.0 - rr) < 1e-6 ? float(n) : (1.0 - pow(rr, float(n))) / (1.0 - rr);
    float norm = sqrt(KE_LEGACY_SLOPE_VARIANCE / max(sumSq, 1e-6));

    float dhdx = 0.0, dhdz = 0.0, lost = 0.0;
    for (int i = 0; i < KE_MAX_RIPPLES; i++) {
        if (i >= n) break;
        float fi = float(i);
        float angle = seed + fi * KE_GOLDEN_ANGLE;
        float k = k0 * pow(lac, fi);
        float slopeAmp = norm * k0 * pow(r, fi);
        float scroll = sqrt(pow(lac, fi));             // omega ~ sqrt(k), normalized to 1 at i = 0
        float phase = fi * KE_PHASE_STRIDE + seed * (fi + 1.0) * 1.61803399;
        vec2 d = vec2(cos(angle), sin(angle));

        float keep = rippleResolve(KE_TWO_PI / max(k, 1e-8), footprint, samples);
        if (i > 0) keep *= detail;                     // DetailFadeDistance stays an artistic extra, on top

        float gg = slopeAmp * keep * cos(dot(d, p) * k + t * scroll + phase);
        dhdx += gg * d.x;
        dhdz += gg * d.y;
        lost += slopeAmp * slopeAmp * (1.0 - keep * keep) * 0.5;   // variance of a cosine is A^2/2
    }
    return vec3(dhdx, dhdz, lost);
}

// Mirrors WaterMath.SlopeToNormal exactly.
vec3 slopeToNormal(float dhdx, float dhdz, float normalStrength) {
    vec3 n = vec3(-dhdx * normalStrength, 1.0, -dhdz * normalStrength);
    float len = length(n);
    return len > 1e-8 ? n / len : vec3(0.0, 1.0, 0.0);
}

// Mirrors SkyMath.ShadeDirection exactly: the SAME sky the background pass paints, evaluated along a world
// direction instead of a screen pixel, because the reflected view ray has no screen position. The gradient runs
// off the direction's elevation and the sun distance is the chord between unit directions, so SkySettings'
// radius/falloff read as angular sizes here. This is what replaces the flat HorizonColor and kills the two-tone
// banding: every fragment gets the colour of the sky it is actually pointing at.
vec3 skyAlongDirection(vec3 dir, vec3 sunDir, float sunStrength) {
    float up = clamp(dir.y, 0.0, 1.0);
    float t = smoothstep(0.0, 1.0, up);
    vec3 col = mix(SkyHorizon.rgb, SkyZenith.rgb, t);
    float strength = clamp(sunStrength, 0.0, 1.0);
    if (SkyParams.x > 0.5 && strength > 0.0) {
        float sunRadius = SkyParams.y, haloStrength = SkyParams.z, haloFalloff = SkyParams.w;
        float d = length(dir - sunDir);
        float feather = max(haloFalloff * 0.25, 1e-4);
        float disc = 1.0 - smoothstep(sunRadius, sunRadius + feather, d);
        float halo = 0.0;
        if (haloStrength > 0.0 && haloFalloff > 0.0) {
            float beyond = max(0.0, d - sunRadius);
            halo = haloStrength * exp(-beyond / haloFalloff);
        }
        float sun = clamp((disc + halo) * strength, 0.0, 1.0);
        col = mix(col, SkySunColor.rgb, sun);
    }
    return col;
}

// Mirrors WaterMath.GlintRoughnessAt exactly: widen the specular lobe wherever the surface is under-sampled, by
// camera distance OR by the pixel's world footprint against the ripple wavelength, whichever is worse. The
// footprint is the measure that is actually right (what aliases is a wave narrower than a pixel, and distance is
// only a proxy that breaks under a wide FOV, an ortho camera, or a different resolution).
float glintRoughnessAt(float nearR, float farR, float camDist, float fadeDistance, float footprint, float rippleLambda) {
    float wide = max(farR, nearR);
    float distanceT = fadeDistance <= 0.0 ? 0.0 : smoothstep(0.0, fadeDistance, camDist);
    float aliasT = 0.0;
    if (footprint > 0.0 && rippleLambda > 1e-6) aliasT = clamp(footprint / (rippleLambda * 0.5), 0.0, 1.0);
    float t = max(distanceT, aliasT);
    return nearR + (wide - nearR) * t;
}

// Mirrors WaterMath.FoamPattern exactly: three non-axis-aligned scrolling layers at mutually irrational
// frequencies, thresholded tightly into clean graphic lobes rather than soft photoreal scum. Both foam sources are
// multiplied by this, which is what stops the shore band reading as a painted-on ring.
float foamPattern(vec2 xz, float scrollTime, float scale) {
    float s = max(scale, 1e-4);
    vec2 p = xz / s;
    vec2 d1 = vec2(0.86602540, 0.5);            // 30 deg
    vec2 d2 = vec2(-0.34202014, 0.93969262);    // 110 deg
    vec2 d3 = vec2(0.76604444, -0.64278761);    // -40 deg
    float a = 1.00 * sin(dot(d1, p) * 1.0 + scrollTime * 0.90);
    float b = 0.75 * sin(dot(d2, p) * 1.41421356 + scrollTime * -1.27);
    float c = 0.55 * sin(dot(d3, p) * 2.23606798 + scrollTime * 0.61);
    return smoothstep(-0.30, 0.42, (a + b + c) * 0.43478261);
}

void main() {
    float waveScale = WaveParams.x, waveSpeed = WaveParams.y, normalStrength = WaveParams.z, time = WaveParams.w;
    float warpStrength = DetailParams.x, detailFadeDist = DetailParams.y;
    float distantDetail = DetailParams.z, shallowDepth = DetailParams.w;

    // Screen-space footprint of this pixel on the surface, taken at the TOP of main so the derivative is in
    // uniform control flow on every backend (a derivative inside a per-fragment branch is undefined).
    float footprint = max(fwidth(vWorldPos.x), fwidth(vWorldPos.z));

    // One eye vector, used for the view direction, the fresnel term, the reflected ray AND the detail fade's
    // camera distance.
    vec3 toEye = CameraPos.xyz - vWorldPos;
    float camDist = length(toEye);
    vec3 V = camDist > 1e-8 ? toEye / camDist : vec3(0.0, 1.0, 0.0);
    float detail = detailScaleFor(camDist, detailFadeDist, distantDetail);

    float footprintSamples = FootprintParams.x, varianceGain = max(FootprintParams.y, 0.0);

    // FFT ocean cascades, sampled FIRST. Two reasons, both Metal-only and both invisible here: the ocean map is
    // binding 0 and the scene depth binding 2, and the cross-compiler numbers a stage's textures by first
    // reference, so sampling the depth first would swap them. Inside the branch, so the procedural surface still
    // pays nothing at runtime - emission order is static and a not-taken branch is still emitted.
    vec2 oceanSlope = vec2(0.0);
    float oceanFoam = 0.0;
    float oceanLost = 0.0;
    if (FftParams.x > 0.5) {
" + WaterFftFragGlsl + @"    }

    // Ground reconstruction: recover the world position of whatever the opaque pass left under this pixel, from
    // the resolved scene depth (the ground-decal pass's gl_FragCoord + raw-inverse-view-projection convention -
    // backend-independent, unlike an interpolated UV, because render-target texture SAMPLING has a
    // backend-dependent Y origin while gl_FragCoord is upper-left on every backend). depthBelowSurface is this
    // water fragment's own world Y minus the ground's world Y - and that world Y is the DISPLACED one, so a
    // passing crest deepens the water under it and both the waterline and the shore foam run up the beach with
    // the swell at no extra cost. The depth grading, the shore foam and the waterline alpha feather all key off
    // it, so it is computed once, up front.
    //
    // It sits HERE, ahead of the surface normal, because DepthTex is binding 0 and must be the first texture this
    // stage samples once the FFT derivative map (binding 4) is in play - the Metal SPIRV-Cross first-sample-order
    // rule, same as ModelFrag's ShadowMap. It has no dependency on the normal, so the move is a pure reorder and
    // the procedural surface renders exactly as before. The ocean sampling above is earlier still, for the same
    // reason one binding further up.
    ivec2 sz = textureSize(sampler2D(DepthTex, Samp), 0);
    float groundDepth = texelFetch(sampler2D(DepthTex, Samp), ivec2(gl_FragCoord.xy), 0).r;
    vec4 ndc = vec4(gl_FragCoord.x / float(sz.x) * 2.0 - 1.0, 1.0 - gl_FragCoord.y / float(sz.y) * 2.0, groundDepth, 1.0);
    vec4 wp = InvViewProj * ndc;
    vec3 groundWorld = wp.xyz / wp.w;
    float depthBelowSurface = vWorldPos.y - groundWorld.y;

    vec3 N;
    float lostSlopeVariance;
    float fftFoam = 0.0;
    if (FftParams.x > 0.5) {
        // FFT ocean. The cascades' ANALYTIC slope is the whole surface normal: there is no separate swell to
        // attenuate, because the swell scale is just the coarsest cascade. Foam has already been accumulated per
        // texel by the compute pass, so it is a lookup rather than a per-fragment fold test. Both were read above
        // at the STILL-water XZ handed down from the vertex stage, never the displaced position: the maps are
        // indexed by the reference grid the transform is defined over, and sampling them at the displaced point
        // would fold the horizontal displacement in a second time.
        N = slopeToNormal(oceanSlope.x, oceanSlope.y, 1.0);
        fftFoam = oceanFoam;
        lostSlopeVariance = oceanLost;
    } else {
    // Ripple spectrum, band-limited to this pixel. slope.xy is the surviving slope, slope.z the variance the
    // band-limit removed (handed to the glint lobe below rather than discarded).
    vec3 slope = waterSlope(vWorldPos.xz, time, waveScale, waveSpeed, warpStrength, detail,
                            footprint, footprintSamples);
    vec3 ripple = slopeToNormal(slope.x, slope.y, normalStrength);

    // Swell shading attenuation (mirrors RippleSpectrum.SwellAttenuation). The crest GEOMETRY is untouched - the
    // silhouette is the point of the swell - but once a crest is narrower than the pixels drawing it, its shading
    // contrast is what reads as parallel rules ruled across the horizon, so that contrast fades with the same
    // footprint measure and its variance goes to the lobe too. Every swell component carries the same slope
    // amplitude by construction (height amplitude is proportional to wavelength), so a plain mean is correct.
    float swellAtten = 1.0;
    float swellLostVar = 0.0;
    float swAmp = SwellParams.x, swLambda = SwellParams.y;
    if (swAmp > 0.0 && swLambda > 0.0) {
        int sn = clamp(int(SwellShape.z + 0.5), 1, KE_MAX_SWELL);
        float lambdaSum = swLambda * (1.0 - pow(KE_LAMBDA_DECAY, float(sn))) / (1.0 - KE_LAMBDA_DECAY);
        float swSlopeAmp = KE_TWO_PI * swAmp / max(lambdaSum, 1e-6);
        float keepSum = 0.0;
        for (int i = 0; i < KE_MAX_SWELL; i++) {
            if (i >= sn) break;
            keepSum += rippleResolve(swLambda * pow(KE_LAMBDA_DECAY, float(i)), footprint, footprintSamples);
        }
        swellAtten = keepSum / float(sn);
        swellLostVar = float(sn) * swSlopeAmp * swSlopeAmp * 0.5 * (1.0 - swellAtten * swellAtten);
    }

    // Shading normal: the attenuated swell normal with the ripple field's horizontal tilt added in.
    // Mirrors WaterMath.CombineNormals over the attenuated swell.
    vec3 nSwell = normalize(vSwellNormal);
    vec3 nSum = vec3(nSwell.x * swellAtten + ripple.x, nSwell.y, nSwell.z * swellAtten + ripple.z);
    float nLen = length(nSum);
    N = nLen > 1e-8 ? nSum / nLen : vec3(0.0, 1.0, 0.0);

    // Total slope variance the band-limit removed. The ripple half scales with NormalStrength squared (it is a
    // slope that NormalStrength multiplies); the swell half is geometry and does not.
    lostSlopeVariance = slope.z * normalStrength * normalStrength + swellLostVar;
    }

    // Body colour. Per-channel Beer-Lambert absorption (mirrors WaterMath.AbsorbTint/AbsorbWeight) grades shallow
    // to deep along an exponential PER CHANNEL, so the ramp bends through green-teal instead of running straight
    // down the line between two colours - that curve is what keeps the midtones clean. An all-zero coefficient
    // falls back to the legacy two-stop smoothstep over ShallowDepth (mirrors WaterMath.ShallowWeight/ShallowTint).
    // Applied BEFORE the fresnel blend so a grazing view of the shallows still picks up the sky on top.
    vec3 absorb = Absorption.rgb;
    vec3 body;
    float bodyAlpha;
    if (absorb.r + absorb.g + absorb.b > 0.0) {
        vec3 tr = exp(-absorb * max(depthBelowSurface, 0.0));
        body = DeepColor.rgb + (ShallowColor.rgb - DeepColor.rgb) * tr;
        bodyAlpha = mix(DeepColor.a, ShallowColor.a, (tr.r + tr.g + tr.b) * (1.0 / 3.0));
    } else {
        float shallowW = shallowDepth <= 0.0 ? 0.0
            : 1.0 - smoothstep(0.0, 1.0, clamp(depthBelowSurface / shallowDepth, 0.0, 1.0));
        body = mix(DeepColor.rgb, ShallowColor.rgb, shallowW);
        bodyAlpha = mix(DeepColor.a, ShallowColor.a, shallowW);
    }

    vec3 Lsun = -normalize(LightDir.xyz);   // mirrors SkyMath.SunDirectionFromLight

    // Reflection colour: the analytic sky along the reflected view ray, blended back toward the flat
    // HorizonColor by SkyReflectionStrength (0 = exactly the 14.22.0 single-colour behaviour).
    // Mirrors WaterMath.ReflectionColor.
    float skyReflStrength = ReflectGlint.x, skyReflSun = ReflectGlint.y;
    vec3 reflectColor = HorizonColor.rgb;
    if (skyReflStrength > 0.0) {
        vec3 R = reflect(-V, N);
        reflectColor = mix(HorizonColor.rgb, skyAlongDirection(R, Lsun, skyReflSun), clamp(skyReflStrength, 0.0, 1.0));
    }

    float ndotv = clamp(dot(N, V), 0.0, 1.0);
    // Schlick-style fresnel: (1-ndotv)^5, mirrors WaterMath.Fresnel.
    float fx = clamp(1.0 - ndotv, 0.0, 1.0);
    float fresnel = fx * fx * fx * fx * fx;
    vec3 tint = mix(body, reflectColor, fresnel);
    float tintAlpha = mix(bodyAlpha, HorizonColor.a, fresnel);

    // Key-light specular sun glint. GGX (mirrors WaterMath.GgxGlint), peak-normalized so GlintStrength means the
    // same brightness as the legacy lobe, with the roughness widened wherever the surface is under-sampled. A
    // GlintRoughness of 0 or less selects the legacy Blinn-Phong lobe on GlintExponent (mirrors
    // WaterMath.SunGlint) so the 14.22.0 highlight stays one knob away. Deliberately NOT routed through the
    // shared computeLighting block: water wants its own tight lobe, distinct from any mesh material.
    float glintStrength = ShoreGlint.y, glintExponent = ShoreGlint.z;
    float glintRough = ReflectGlint.z, glintDistantRough = ReflectGlint.w;
    float glint = 0.0;
    if (glintStrength > 0.0) {
        vec3 H = V + Lsun;
        float hLen = length(H);
        if (hLen > 1e-8) {
            H /= hLen;
            float ndoth = max(dot(N, H), 0.0);
            if (glintRough > 0.0) {
                float rough = glintRoughnessAt(glintRough, glintDistantRough, camDist, detailFadeDist,
                                               footprint, max(waveScale, 1e-4) * KE_TWO_PI);
                float a = max(rough, 1e-3);
                a *= a;                                // alpha = roughness^2
                // Toksvig-style transfer: detail the pixel cannot resolve becomes lobe width, not lost energy.
                // Without it, band-limited distant water goes to glass instead of to a believable sheen.
                a = min(sqrt(a * a + 2.0 * max(lostSlopeVariance, 0.0) * varianceGain), 1.0);
                float a2 = a * a;
                float denom = ndoth * ndoth * (a2 - 1.0) + 1.0;
                float lobe = a2 / max(denom, 1e-6);
                lobe *= lobe;                          // peak 1 at ndoth == 1
                float ndotl = max(dot(N, Lsun), 0.0);
                float k = a * 0.5;                     // Smith-Schlick visibility
                float gv = ndotv / (ndotv * (1.0 - k) + k);
                float gl = ndotl / (ndotl * (1.0 - k) + k);
                glint = lobe * gv * gl * ndotl * glintStrength;
            } else {
                glint = pow(ndoth, max(glintExponent, 1.0)) * glintStrength;
            }
        }
    }

    // Foam: whitecaps where the swell folds (mirrors WaterMath.Whitecap over the vertex stage's fold factor) and a
    // shoreline band off the reconstructed depth (mirrors WaterMath.ShoreFoam), both broken up by the same
    // scrolling pattern (mirrors WaterMath.FoamAmount). A max, not a sum, so a whitecap inside the shore band does
    // not add past white.
    float foamStrength = FoamParams.x;
    float foam = 0.0;
    if (foamStrength > 0.0) {
        float threshold = 1.0 - clamp(FoamParams.y, 0.0, 1.0);
        // Whitecaps. In FFT mode the compute pass has already turned the displacement Jacobian into an
        // accumulating, dissipating foam value per texel, so this is that value; in procedural mode it is the
        // per-fragment threshold on the Gerstner fold factor. Both then go through the SAME break-up pattern and
        // the same shore-band max below, which is what keeps the graphic foam look identical across the two.
        float crest = FftParams.x > 0.5
            ? fftFoam
            : smoothstep(threshold, threshold + KE_WHITECAP_SOFTNESS, vFold);
        float shoreWidth = FoamParams.z;
        float band = shoreWidth <= 0.0 ? 0.0
            : 1.0 - smoothstep(0.0, 1.0, clamp(depthBelowSurface / shoreWidth, 0.0, 1.0));
        float mask = foamPattern(vWorldPos.xz, time * waveSpeed, FoamParams.w);
        foam = clamp(max(crest, band) * mask * foamStrength, 0.0, 1.0);
    }

    // Waterline alpha feather (mirrors WaterMath.ShoreFade): a much tighter distance than the depth grading above,
    // so the very edge softens out instead of clipping against the seabed.
    float shoreFadeDist = ShoreGlint.x;
    float shoreFade = shoreFadeDist <= 0.0 ? 1.0 : smoothstep(0.0, 1.0, clamp(depthBelowSurface / shoreFadeDist, 0.0, 1.0));

    float opacity = ShoreGlint.w;
    vec3 rgb = tint + LightColor.rgb * glint;
    float alpha = tintAlpha * opacity * shoreFade;
    // Foam paints over the surface and pushes it toward its own (near-solid) opacity, but still inside the
    // waterline feather, so the very edge does not become a hard white line.
    rgb = mix(rgb, FoamColor.rgb, foam);
    alpha = mix(alpha, max(alpha, FoamColor.a * opacity * shoreFade), foam);
    if (alpha <= 0.001) discard;
    oColor = vec4(rgb, alpha);
}";
    }
}
