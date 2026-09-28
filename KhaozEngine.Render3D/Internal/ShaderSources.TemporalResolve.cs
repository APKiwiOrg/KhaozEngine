namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The temporal resolve and its depth store (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3), two fullscreen fragment
    /// programs paired with <see cref="FullscreenVert"/> and recorded by <c>TemporalResolveRenderer</c>.
    /// <para><b>ORIENTATION.</b> Both address pixels by <c>gl_FragCoord</c>, which is upper-left on every backend (the
    /// convention <c>DecalFrag</c> and <c>StarfieldFrag</c> rely on), and read every input by <c>texelFetch</c> or at the
    /// UV of a pixel centre. So neither flips: the resolve's output is upright like <c>ColorTex</c>, and the post
    /// chain's flip parities stay what they were.</para>
    /// <para><b>THE CORE IS SHARED.</b> <see cref="TemporalResolveCoreGlsl"/> holds the bindings, the uniform block and
    /// the per-pixel resolve as a function with no stage inputs or outputs, so the temporal debug views
    /// (<see cref="TemporalDebugFrag"/>) and the sampled temporal counts (<see cref="TemporalProbeFrag"/>) can
    /// re-evaluate exactly what the resolve decided, through the resource set it bound.</para>
    /// <para><b>THE UNIFORM BLOCKS ARE THE C# STRUCTS.</b> Each block's members are spliced from
    /// <see cref="TemporalResolveUniforms.GlslMembers"/> and <see cref="TemporalDepthStoreUniforms.GlslMembers"/>, so the
    /// text cannot drift from the fields, and the fields carry the meaning.</para>
    /// <para><b>A MOVING SURFACE SKIPS THE DEPTH TEST.</b> <see cref="TemporalResolveUniforms.CurrentToPrevious"/> maps a
    /// static point, so the expected depth is only meaningful where the motion of the texel that reprojects the pixel,
    /// the dilated nearest or, beside a fast edge, the centre texel, carries its own sample within
    /// <see cref="TemporalResolveTuning.MovingSurfaceInternalPixels"/>, plus
    /// <see cref="TemporalResolveTuning.MovingSurfaceMotionFraction"/> of that motion, of that sample's static previous
    /// UV. The motion target wrote the motion for exactly that surface point, so a static surface agrees up to float
    /// precision and the target's half-float rounding. Elsewhere the surface moved and neighbourhood clipping handles
    /// it. At a moving edge the stored depths are still read, and there they decide only whether the thin-feature lock
    /// is kept. The motion target never reads as background off screen: an x channel past
    /// <see cref="TemporalResolveTuning.MotionSentinel"/> is background, and a previous UV outside [0, 1], which every
    /// clamped or behind-the-camera motion gives, rejects history as off screen.</para>
    /// <para><b>D3D11 SIGNATURE.</b> Each <c>main</c> reads <c>vUv</c> with a <c>1e-30</c> weight, which changes no
    /// output, so the pixel-input signature is TEXCOORD0 then SV_Position, the shape <c>PaletteFrag</c> ships on every
    /// backend, rather than SV_Position alone after a vertex stage that writes TEXCOORD0.</para>
    /// <para>The constants mirror <see cref="TemporalResolveTuning"/> one for one, and the helpers mirror
    /// <see cref="TemporalResolveMath"/>. Change both together.</para>
    /// </summary>
    internal static partial class ShaderSources
    {
        // ---- Shared by both temporal programs: the background sentinel and the depth linearisation ----
        internal const string TemporalCommonGlsl = @"
const float MotionSentinel = 60000.0;
const float BackgroundLinearDepth = 1.0e30;
// The largest finite half float. A history texel outside it is not finite, and a scene colour is held inside it before
// the luma weighting, where an infinity would become NaN.
const float HalfMax = 65504.0;
// View distance from NDC depth: perspective as OutlineMath.LinearizeDepth, orthographic linear between near and far.
// depthParams = (1 for perspective else 0, near, far, 0).
float temporalLinearDepth(float ndcDepth, vec4 depthParams) {
    return depthParams.x > 0.5
        ? (depthParams.y * depthParams.z) / (depthParams.z - ndcDepth * (depthParams.z - depthParams.y))
        : depthParams.y + ndcDepth * (depthParams.z - depthParams.y);
}
";

        // ---- The resolve's tuning constants, one for one with TemporalResolveTuning ----
        internal const string TemporalResolveTuningGlsl = @"
const float DisocclusionTolerance = 0.02;
const float MovingSurfaceInternalPixels = 0.5;
const float MovingSurfaceMotionFraction = 0.0009765625;
const float MaxAccumulation = 15.0;
const float MovingAccumulation = 8.0;
const float MotionAccumulationPixels = 16.0;
const float GammaStill = 1.5;
const float GammaMoving = 0.75;
const float GammaMotionPixels = 3.0;
const float ReactiveGain = 4.0;
const float ReactiveStrength = 0.9;
const float LockRidgeAbsolute = 0.02;
const float LockRidgeRelative = 0.1;
const float LockMotionStartPixels = 1.0;
const float LockMotionEndPixels = 4.0;
const float LockReactiveRelease = 2.0;
const float LockDecay = 0.125;
const float LockHoldGain = 2.0;
const float LockEdgeRelease = 1.0;
const float LockEdgeMotionFraction = 0.001953125;
const float LockEdgeFloorInternalPixels = 0.001;
const float ClipFlagMinimumMove = 0.0009765625;
const float DilationReachInternalPixels = 1.25;
const float WorldMotionMetres = 0.001;
const float FollowedHistoryMotionFraction = 0.5;
const float DisocclusionVisibleShare = 0.5;
const float MovingShareConfidence = 0.0;
";

        // ---- The resolve's core: bindings, uniforms and the per-pixel resolve, no stage inputs or outputs ----
        public const string TemporalResolveCoreGlsl = @"
layout(set=0, binding=0) uniform texture2D SceneColor;
layout(set=0, binding=1) uniform texture2D OpaqueColor;
layout(set=0, binding=2) uniform texture2D SceneDepth;
layout(set=0, binding=3) uniform texture2D MotionTex;
layout(set=0, binding=4) uniform texture2D PrevDepth;
layout(set=0, binding=5) uniform texture2D HistoryColor;
layout(set=0, binding=6) uniform texture2D HistoryConfidence;
layout(set=0, binding=7) uniform sampler LinearClamp;
// The members are TemporalResolveUniforms.GlslMembers, documented on the struct's fields.
layout(set=0, binding=8) uniform Resolve {" + TemporalResolveUniforms.GlslMembers + @"};
" + TemporalCommonGlsl + TemporalResolveTuningGlsl + @"
struct TemporalPixel {
    vec3 color; float confidence; float stability; float moved; float disocclusion; float reactive; float clip;
    float alpha;
};

float temporalLuma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }
vec3 toWeighted(vec3 c) { return c / (1.0 + temporalLuma(c)); }
vec3 fromWeighted(vec3 w) { return w / max(1.0 - temporalLuma(w), 1.0e-4); }
vec3 rgbToYCoCg(vec3 c) {
    return vec3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b, 0.5 * c.r - 0.5 * c.b, -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}
vec3 yCoCgToRgb(vec3 c) { return vec3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z); }

// sinc(x) * sinc(x / 2) on |x| < 2, mirrored by TemporalResolveMath.Lanczos2.
float lanczos2(float x) {
    float ax = abs(x);
    if (ax < 1.0e-5) return 1.0;
    if (ax >= 2.0) return 0.0;
    float px = 3.14159265 * ax;
    return 2.0 * sin(px) * sin(0.5 * px) / (px * px);
}

bool isRidge(float centreLuma, float a, float b, float threshold) {
    return centreLuma - max(a, b) > threshold || min(a, b) - centreLuma > threshold;
}

// Step 2: Catmull-Rom from five bilinear taps (the corner taps dropped), weights renormalised over the five. At a
// texel centre it returns that texel exactly, so a still history is never softened. The outer weights go negative, so
// the result is clamped to the range of the four history texels in the position's bilinear footprint, colour and
// alpha. Unclamped, a history texel about 14 times brighter than its neighbour drives the fetch below zero, and the
// clamp to zero after it leaves a black halo that the variance box accepts. The range is the texels' own, not the
// taps': each tap is a bilinear blend, so the taps of a thin line all sit below its peak, and clamping to them shaved
// the peak on every fractional resample. The footprint holds the peak texel whenever the position is beside it, and
// still bounds undershoot and overshoot to values the history holds. Four texel fetches.
vec4 sampleHistoryCatmullRom(vec2 uv, vec2 size) {
    vec2 samplePos = uv * size;
    vec2 texPos1 = floor(samplePos - 0.5) + 0.5;
    vec2 f = samplePos - texPos1;
    vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    vec2 w3 = f * f * (-0.5 + 0.5 * f);
    vec2 w12 = w1 + w2;
    vec2 tc0 = (texPos1 - 1.0) / size;
    vec2 tc3 = (texPos1 + 2.0) / size;
    vec2 tc12 = (texPos1 + w2 / w12) / size;
    vec4 top = textureLod(sampler2D(HistoryColor, LinearClamp), vec2(tc12.x, tc0.y), 0.0);
    vec4 left = textureLod(sampler2D(HistoryColor, LinearClamp), vec2(tc0.x, tc12.y), 0.0);
    vec4 middle = textureLod(sampler2D(HistoryColor, LinearClamp), tc12, 0.0);
    vec4 right = textureLod(sampler2D(HistoryColor, LinearClamp), vec2(tc3.x, tc12.y), 0.0);
    vec4 bottom = textureLod(sampler2D(HistoryColor, LinearClamp), vec2(tc12.x, tc3.y), 0.0);
    vec4 sum = top * (w12.x * w0.y) + left * (w0.x * w12.y) + middle * (w12.x * w12.y) + right * (w3.x * w12.y)
        + bottom * (w12.x * w3.y);
    float weight = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
    ivec2 base = ivec2(floor(samplePos - 0.5));
    ivec2 lastTexel = ivec2(size) - ivec2(1);
    vec4 t00 = texelFetch(sampler2D(HistoryColor, LinearClamp), clamp(base, ivec2(0), lastTexel), 0);
    vec4 t10 = texelFetch(sampler2D(HistoryColor, LinearClamp), clamp(base + ivec2(1, 0), ivec2(0), lastTexel), 0);
    vec4 t01 = texelFetch(sampler2D(HistoryColor, LinearClamp), clamp(base + ivec2(0, 1), ivec2(0), lastTexel), 0);
    vec4 t11 = texelFetch(sampler2D(HistoryColor, LinearClamp), clamp(base + ivec2(1, 1), ivec2(0), lastTexel), 0);
    vec4 texelMin = min(min(t00, t10), min(t01, t11));
    vec4 texelMax = max(max(t00, t10), max(t01, t11));
    return clamp(sum / weight, texelMin, texelMax);
}

// Steps 1 and 3 for one texel's surface point: where this display pixel was last frame if it moved as that point did,
// and the depth the point had there if static. Background reprojects from camera rotation alone. A surface reads
// history where its motion carries the display pixel. The expected depth and the moving-surface test use the texel's
// own point, its unjittered sample at its own depth, which is the point the motion target wrote that motion for.
// CurrentToPrevious takes it to last frame's view space as if static, and PreviousProjection gives the UV it had
// there. On a static surface that UV and the texel's own previous position agree up to float precision and the motion
// target's half-float rounding. More than MovingSurfaceInternalPixels apart, plus MovingSurfaceMotionFraction of the
// motion for that rounding, the surface moved, and skips the depth test. Under perspective so does one whose static
// point sat on or behind last frame's camera plane. Under orthographic that point keeps the test, which rejects it as
// not in front. travel is how far apart the two lie, in internal pixels, how far the surface moved against a static
// point where it stood, and it counts as beyond every bound for a point on or behind last frame's camera plane.
void temporalReproject(vec2 uv, vec2 sampleInternal, vec2 motion, float depth, bool isBackground, vec2 internalSize,
    out vec2 previousUv, out float expectedDepth, out bool depthTested, out float travel) {
    vec2 ndcXY = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    previousUv = vec2(-1.0);
    expectedDepth = BackgroundLinearDepth;
    depthTested = true;
    travel = 0.0;
    if (isBackground) {
        vec4 previousClip = BackgroundToPrevious * vec4(ndcXY, 1.0, 1.0);
        if (previousClip.w > 1.0e-6)
            previousUv = vec2(previousClip.x / previousClip.w * 0.5 + 0.5, 0.5 - previousClip.y / previousClip.w * 0.5);
    } else {
        previousUv = uv - motion;
        vec2 sampleUv = sampleInternal / internalSize;
        vec2 sampleNdc = vec2(sampleUv.x * 2.0 - 1.0, 1.0 - sampleUv.y * 2.0);
        float clipW = CurrentDepth.x > 0.5 ? depth : 1.0;
        vec4 previousView = CurrentToPrevious * vec4(sampleNdc * clipW, depth, 1.0);
        expectedDepth = -previousView.z;
        vec4 staticClip = PreviousProjection * previousView;
        depthTested = false;
        travel = 1.0e30;   // beyond every bound, in internal pixels
        if (staticClip.w > 1.0e-6) {
            vec2 staticUv = vec2(staticClip.x / staticClip.w * 0.5 + 0.5, 0.5 - staticClip.y / staticClip.w * 0.5);
            vec2 surfaceMotion = (staticUv - (sampleUv - motion)) * internalSize;
            float movingThreshold = MovingSurfaceInternalPixels
                + length(motion * internalSize) * MovingSurfaceMotionFraction;
            travel = length(surfaceMotion);
            depthTested = travel <= movingThreshold;
        }
    }
}

// Step 3's four stored depths around a previous position: the farthest, the nearest and where it lies, and the share
// of the bilinear weight on those not nearer than the expected depth by the disocclusion tolerance.
struct DepthFootprint { float farthest; float nearest; ivec2 nearestTexel; float visibleShare; };

DepthFootprint temporalDepthFootprint(vec2 previousUv, vec2 internalSize, ivec2 maxTexel, float expectedDepth) {
    vec2 position = previousUv * internalSize - 0.5;
    ivec2 baseTexel = ivec2(floor(position));
    vec2 f = position - vec2(baseTexel);
    DepthFootprint footprint;
    footprint.farthest = 0.0;
    footprint.nearest = 2.0 * BackgroundLinearDepth;
    footprint.nearestTexel = baseTexel;
    footprint.visibleShare = 0.0;
    for (int corner = 0; corner < 4; corner++) {
        ivec2 texel = clamp(baseTexel + ivec2(corner & 1, corner >> 1), ivec2(0), maxTexel);
        float stored = texelFetch(sampler2D(PrevDepth, LinearClamp), texel, 0).r;
        footprint.farthest = max(footprint.farthest, stored);
        if (stored < footprint.nearest) {
            footprint.nearest = stored;
            footprint.nearestTexel = texel;
        }
        float weight = mix(1.0 - f.x, f.x, float(corner & 1)) * mix(1.0 - f.y, f.y, float(corner >> 1));
        if (!(stored < expectedDepth * (1.0 - DisocclusionTolerance))) footprint.visibleShare += weight;
    }
    return footprint;
}

// A texel's view distance from its motion and its NDC depth. Background is read from the motion sentinel on the x
// channel alone, because the depth attachment is cleared to the background colour, not to the far plane.
float temporalViewDepth(vec2 motion, float ndcDepth) {
    return abs(motion.x) > MotionSentinel ? BackgroundLinearDepth : temporalLinearDepth(ndcDepth, CurrentDepth);
}

// Whether the surface at a texel is a narrow feature: along a row or a column, the run of texels at its depth through
// it, apart from the depths either side by the disocclusion tolerance, nearer or farther, is at most two texels long,
// as a blade narrower than a texel is, or two side by side, whether against the sky, over ground or beside a rock. It
// reads the depths last frame stored, or with current this frame's (temporalViewDepth).
bool temporalNarrowDepth(ivec2 texel, float depth, ivec2 maxTexel, bool current) {
    float limit = 1.0 - DisocclusionTolerance;
    bool run[8];   // left 1 and 2, right 1 and 2, up 1 and 2, down 1 and 2: the texel there is at this depth
    for (int i = 0; i < 8; i++) {
        int stride = (i & 1) + 1;
        ivec2 offset = i < 2 ? ivec2(-stride, 0) : i < 4 ? ivec2(stride, 0)
            : i < 6 ? ivec2(0, -stride) : ivec2(0, stride);
        ivec2 at = clamp(texel + offset, ivec2(0), maxTexel);
        float there = current ? temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), at, 0).rg,
                texelFetch(sampler2D(SceneDepth, LinearClamp), at, 0).r)
            : texelFetch(sampler2D(PrevDepth, LinearClamp), at, 0).r;
        run[i] = !(depth < there * limit || there < depth * limit);
    }
    bool narrowRow = !(run[0] && run[2]) && !(run[0] && run[1]) && !(run[2] && run[3]);
    bool narrowColumn = !(run[4] && run[6]) && !(run[4] && run[5]) && !(run[6] && run[7]);
    return narrowRow || narrowColumn;
}

// Step 6's stored lock carries three facts more. Where history was valid and the surface the pixel reprojected by
// moved, so that its depth test was skipped, the state holds minus one minus the lock. The surface is the dilated
// nearest, or the centre texel's own where the pixel reprojected by its own motion beside a fast edge. Where the pixel
// followed a nearer surface's edge (the band, step 1), the state holds minus three minus the lock, and on a pixel of
// the followed surface itself (the followed mark, step 1) minus five minus the lock. Both count as moved too. A frame
// with no valid history stores the lock plain. A lock lies in [0, 1], so the stored value lies in [-2, -1], [-4, -3]
// or [-6, -5], the lock reads back within the half float's rounding, and the next frame knows a moving surface showed
// there, whether the history there followed one, and whether that surface's own pixels stored it. Step 3 reads them.
// moved is TemporalPixel's: 1 moved, 2 the band, 3 the followed mark.
float temporalStoreLock(float lockValue, float moved) {
    return moved > 2.5 ? -5.0 - lockValue : moved > 1.5 ? -3.0 - lockValue : moved > 0.5 ? -1.0 - lockValue
        : lockValue;
}
bool temporalStoredMoved(float stored) { return stored < -0.5; }
bool temporalStoredBand(float stored) { return stored < -2.5; }
bool temporalStoredFollowed(float stored) { return stored < -4.5; }
// A mark's level is an odd whole number below zero, so minus one minus the stored value is an even number plus the
// lock, and its remainder over two is the lock, exactly for a half float.
float temporalStoredLock(float stored) {
    return temporalStoredMoved(stored) ? mod(-1.0 - stored, 2.0) : stored;
}

ivec2 temporalDisplaySize() { return ivec2(Sizes.zw); }

TemporalPixel temporalResolvePixel(ivec2 displayPixel) {
    vec2 internalSize = Sizes.xy;
    vec2 displaySize = Sizes.zw;
    vec2 jitter = Jitter.xy;
    float displayOverInternal = Jitter.z;
    bool historyValid = Jitter.w > 0.5;

    // The display pixel centre in upright UV (row 0 at the top) and in internal pixels, and the texel whose jittered
    // sample lands within half a texel of it.
    vec2 uv = (vec2(displayPixel) + 0.5) / displaySize;
    vec2 pixelCentre = uv * internalSize;
    ivec2 maxTexel = ivec2(internalSize) - ivec2(1);
    ivec2 centreTexel = clamp(ivec2(floor(pixelCentre + jitter)), ivec2(0), maxTexel);

    vec3 momentSum = vec3(0.0);
    vec3 momentSquares = vec3(0.0);
    vec3 neighbourMin = vec3(1.0e30);
    vec3 neighbourMax = vec3(-1.0e30);
    float alphaMin = 1.0e30;
    float alphaMax = -1.0e30;
    vec4 reconstruction = vec4(0.0);
    float reconstructionWeight = 0.0;
    float sampleWeight = 0.0;
    float closestDepth = 2.0 * BackgroundLinearDepth;
    ivec2 closestTexel = centreTexel;
    float reactiveDifference = 0.0;
    float lumas[9];

    // Step 4's kernel is separable, and each axis of a clamped 3x3 texel depends on that axis alone. So three weights
    // per axis at internal scale for the reconstruction, and three per axis at display scale for the sample weight,
    // form the nine products the loop takes: 12 kernel evaluations in place of 36.
    vec3 kernelX = vec3(0.0);
    vec3 kernelY = vec3(0.0);
    vec3 displayKernelX = vec3(0.0);
    vec3 displayKernelY = vec3(0.0);
    for (int i = 0; i < 3; i++) {
        ivec2 texel = clamp(centreTexel + ivec2(i - 1), ivec2(0), maxTexel);
        vec2 toSample = vec2(texel) + 0.5 - jitter - pixelCentre;
        kernelX[i] = lanczos2(toSample.x);
        kernelY[i] = lanczos2(toSample.y);
        displayKernelX[i] = lanczos2(toSample.x * displayOverInternal);
        displayKernelY[i] = lanczos2(toSample.y * displayOverInternal);
    }

    // The 3x3 reads each texel one step ahead. The compiler keeps the loop rolled, so a texel read at the top of its
    // own step stalls that step, while one read a step earlier arrives behind the arithmetic of the step before. The
    // texels, and the order of every sum over them, are unchanged. The last step reads its own texel again.
    ivec2 nextTexel = clamp(centreTexel + ivec2(-1), ivec2(0), maxTexel);
    vec4 nextScene = texelFetch(sampler2D(SceneColor, LinearClamp), nextTexel, 0);
    vec2 nextMotion = texelFetch(sampler2D(MotionTex, LinearClamp), nextTexel, 0).rg;
    float nextDepth = texelFetch(sampler2D(SceneDepth, LinearClamp), nextTexel, 0).r;
    vec3 nextOpaque = texelFetch(sampler2D(OpaqueColor, LinearClamp), nextTexel, 0).rgb;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 texel = nextTexel;
            vec4 sceneColor = nextScene;
            vec2 motion = nextMotion;
            float ndcDepth = nextDepth;
            vec3 opaqueColor = nextOpaque;
            nextTexel = clamp(centreTexel + (x == 1 ? ivec2(-1, min(y + 1, 1)) : ivec2(x + 1, y)), ivec2(0), maxTexel);
            nextScene = texelFetch(sampler2D(SceneColor, LinearClamp), nextTexel, 0);
            nextMotion = texelFetch(sampler2D(MotionTex, LinearClamp), nextTexel, 0).rg;
            nextDepth = texelFetch(sampler2D(SceneDepth, LinearClamp), nextTexel, 0).r;
            nextOpaque = texelFetch(sampler2D(OpaqueColor, LinearClamp), nextTexel, 0).rgb;
            vec3 weightedColor = toWeighted(min(max(sceneColor.rgb, vec3(0.0)), vec3(HalfMax)));
            vec3 ycc = rgbToYCoCg(weightedColor);
            lumas[(y + 1) * 3 + (x + 1)] = ycc.x;
            momentSum += ycc;
            momentSquares += ycc * ycc;
            neighbourMin = min(neighbourMin, ycc);
            neighbourMax = max(neighbourMax, ycc);
            alphaMin = min(alphaMin, sceneColor.a);
            alphaMax = max(alphaMax, sceneColor.a);

            // Step 4: Lanczos 2 on the distance from this texel's jittered sample to the pixel centre, in internal
            // pixels, as its column's weight times its row's. How close the nearest sample lands in display pixels is
            // what this frame is worth to the pixel.
            float lanczosWeight = kernelX[x + 1] * kernelY[y + 1];
            reconstruction += vec4(ycc, sceneColor.a) * lanczosWeight;
            reconstructionWeight += lanczosWeight;
            sampleWeight = max(sampleWeight, clamp(displayKernelX[x + 1] * displayKernelY[y + 1], 0.0, 1.0));

            // Step 1: the nearest surface in the neighbourhood carries the motion, and its unjittered sample position is
            // the surface point that motion was written for. Background is the motion sentinel on the x channel alone,
            // because the depth attachment is cleared to the background colour, not to the far plane. A clamped or
            // off-screen motion is finite and far below the sentinel, so it is never background. The loop carries the
            // nearest texel and its depth alone, and its motion and sample position are read back after it.
            float viewDepth = temporalViewDepth(motion, ndcDepth);
            if (viewDepth < closestDepth) {
                closestDepth = viewDepth;
                closestTexel = texel;
            }

            // Step 7: how much the transparent passes changed this texel.
            reactiveDifference = max(reactiveDifference,
                abs(temporalLuma(weightedColor) - temporalLuma(toWeighted(min(max(opaqueColor, vec3(0.0)), vec3(HalfMax))))));
        }
    }

    float reactive = clamp(reactiveDifference * ReactiveGain, 0.0, 1.0);
    vec3 mean = momentSum / 9.0;
    vec3 deviation = sqrt(max(momentSquares / 9.0 - mean * mean, vec3(0.0)));
    vec4 current = reconstruction / max(reconstructionWeight, 1.0e-4);
    current.xyz = clamp(current.xyz, neighbourMin, neighbourMax);   // the negative lobes cannot ring past the neighbourhood
    current.w = clamp(current.w, alphaMin, alphaMax);

    // The nearest texel's sample position and motion, and the centre texel's own, read back as the loop saw them. A
    // 3x3 with no depth below the starting bound took no nearest texel, and keeps the pixel centre and no motion.
    vec2 closestSample = pixelCentre;
    vec2 closestMotion = vec2(0.0);
    bool closestIsBackground = true;
    if (closestDepth < 2.0 * BackgroundLinearDepth) {
        closestSample = vec2(closestTexel) + 0.5 - jitter;
        closestMotion = texelFetch(sampler2D(MotionTex, LinearClamp), closestTexel, 0).rg;
        closestIsBackground = abs(closestMotion.x) > MotionSentinel;
    }
    vec2 centreSample = vec2(centreTexel) + 0.5 - jitter;
    vec2 centreMotion = texelFetch(sampler2D(MotionTex, LinearClamp), centreTexel, 0).rg;
    bool centreIsBackground = abs(centreMotion.x) > MotionSentinel;
    float centreDepth = temporalViewDepth(centreMotion,
        texelFetch(sampler2D(SceneDepth, LinearClamp), centreTexel, 0).r);

    // Steps 1 and 3: where this pixel was last frame, and the depth a static surface there had, from the dilated
    // texel's own surface point (temporalReproject).
    vec2 previousUv;
    float expectedDepth;
    bool depthTested;
    float travel;
    temporalReproject(uv, closestSample, closestMotion, closestDepth, closestIsBackground, internalSize, previousUv,
        expectedDepth, depthTested, travel);
    // Motion is clamped to two screens and a point behind last frame's camera is written two screens away, so any
    // such previous position falls outside [0, 1] here and is rejected like any other off-screen one.
    bool onScreen = all(greaterThanEqual(previousUv, vec2(0.0))) && all(lessThanEqual(previousUv, vec2(1.0)));
    float motionPixels = onScreen ? length((uv - previousUv) * displaySize) : 0.0;

    // Step 6's edge signal, which step 3 below also reads: where the centre texel moves otherwise than the dilated
    // nearest surface, a moving feature is passing over a background, and the pixel reads history along that feature's
    // motion rather than its own. A surface moving as a whole, a swaying blade or an avatar the camera follows, has no
    // such edge. A background centre moves by the camera's rotation alone. An edge counts once the two motions differ
    // by more than LockEdgeMotionFraction of the dilated motion, past the motion target's rounding, and by more than
    // LockEdgeFloorInternalPixels, past the float rounding of a background centre's round trip through NDC, which is
    // not exact below a quarter of the screen even when the camera is still. centreScreenMotion is how far the centre
    // texel's own surface moved on screen, in internal pixels.
    float edgeMotion = 0.0;
    float centreScreenMotion = 0.0;
    if (!closestIsBackground) {
        vec2 centreOwn = centreMotion;
        if (centreIsBackground) {
            vec2 centreUv = (vec2(centreTexel) + 0.5 - jitter) / internalSize;
            vec4 centrePrevious = BackgroundToPrevious * vec4(centreUv.x * 2.0 - 1.0, 1.0 - centreUv.y * 2.0, 1.0, 1.0);
            centreOwn = centrePrevious.w > 1.0e-6
                ? centreUv - vec2(centrePrevious.x / centrePrevious.w * 0.5 + 0.5, 0.5 - centrePrevious.y / centrePrevious.w * 0.5)
                : vec2(2.0);   // behind last frame's camera: as far apart as motion goes
        }
        edgeMotion = length((closestMotion - centreOwn) * internalSize);
        centreScreenMotion = length(centreOwn * internalSize);
    }
    bool movingEdge = edgeMotion > max(LockEdgeFloorInternalPixels, length(closestMotion * internalSize) * LockEdgeMotionFraction);

    // Steps 1 and 3 beside a fast edge: dilation carries the nearer surface's motion so its edge keeps its history, but
    // where that surface moved (its depth test is skipped), the centre texel lies on a farther surface, by more than
    // the disocclusion tolerance, and the two move more than DilationReachInternalPixels apart, that motion carries
    // this pixel beyond the edge's reach, onto another texel of the farther surface. The pixel then reprojects by its
    // centre texel's own motion and depth, so a pixel the nearer surface just uncovered finds it in the stored depths
    // and is disoccluded, and one it never covered keeps its own history. A still nearer surface keeps dilation
    // whatever the camera does: under a camera's translation, and against the sky, both surfaces are static and the
    // dilated history is valid. Where the nearer surface is also narrow in this frame's depth, step 5 below gives the
    // pixel's history that surface's current colour.
    //
    // Step 1's band, under the reach: a pixel beside or on a wide nearer surface that moved in the world. nearerMoved
    // is that test: the dilated texel's own sample lands more than WorldMotionMetres from where a static point would
    // have been, in internal pixels at its depth through last frame's projection (one internal pixel spans
    // 2 clipW / (P00 width) metres), plus the motion target's rounding. A static surface's travel is the motion
    // target's float error on positions relative to the render origin, one distance in the world that more pixels
    // show at a higher resolution or a nearer depth, so the test is in metres. Beside the surface's edge the centre
    // texel lies on a farther surface that moves more on screen, as under a camera following the nearer one. That
    // pixel's history followed the edge, which stays put on screen while the farther surface passes under it, so it
    // holds the edge's colour over a mix of the farther surface. On the surface itself, where the centre texel moves
    // with it, a pixel whose surface travelled in the world more than twice its motion on screen is followed, not
    // crossing a still view. Its history is its own colour, and a farther surface its lowest pixels touch, as the
    // ground under an avatar's feet lies within the disocclusion tolerance of them, takes that colour on where it is
    // uncovered. That pixel stores the followed mark rather than the band's, because once the surface stops its own
    // pixels read the same history and keep it (step 3). The band keeps the edge's anti-aliasing, and step 3 keeps a
    // pixel whose nearest surface did not move from taking either history on. A narrow nearer surface, a blade or a
    // line, is held by the thin-feature lock
    // instead. A surface crossing a still view leaves its history where it passed, as before: restarting the pixels
    // it uncovers there would show a sub-texel feature's raw sample, brighter than its converged value. Both read the
    // one narrow test of the nearer surface, and the band reads the dilated surface's travel before the pixel's own
    // motion replaces it.
    bool centreFarther = centreDepth > closestDepth * (1.0 + DisocclusionTolerance);
    bool ownReprojected = !depthTested && edgeMotion > DilationReachInternalPixels && centreFarther;
    float closestScreenMotion = length(closestMotion * internalSize);
    bool nearerMoved = !depthTested || travel > WorldMotionMetres * PreviousProjection[0][0] * internalSize.x * 0.5
        / (CurrentDepth.x > 0.5 ? closestDepth : 1.0) + closestScreenMotion * MovingSurfaceMotionFraction;
    bool band = historyValid && !ownReprojected && nearerMoved
        && (movingEdge ? centreScreenMotion > closestScreenMotion && centreFarther
            : travel > 2.0 * closestScreenMotion);
    bool nearerNarrow = (ownReprojected || band) && temporalNarrowDepth(closestTexel, closestDepth, maxTexel, true);
    bool narrowMoving = ownReprojected && nearerNarrow;
    band = band && !nearerNarrow;
    bool followed = band && !movingEdge;
    if (ownReprojected) {
        temporalReproject(uv, centreSample, centreMotion, centreDepth, centreIsBackground, internalSize, previousUv,
            expectedDepth, depthTested, travel);
        onScreen = all(greaterThanEqual(previousUv, vec2(0.0))) && all(lessThanEqual(previousUv, vec2(1.0)));
        motionPixels = onScreen ? length((uv - previousUv) * displaySize) : 0.0;
    }

    // Last frame's state around the previous position, read once for steps 3 and 2: the confidence blended by the
    // four texels' bilinear weights, and the lock as the largest of those that carry weight. A bilinear lock under
    // motion is blended with unlocked neighbours every frame and ran out about three times faster than LockDecay, so
    // a moving sub-texel feature lost its hold. At a texel centre only that texel carries weight, so a still lock
    // reads back unchanged and never spreads. carriedMoved is whether any texel that carries weight stored the lock
    // of a moving surface (temporalStoreLock), carriedBand whether any stored that of a band pixel, and
    // carriedFollowed whether that was the followed surface's own.
    vec2 fetchedState = vec2(0.0);
    bool carriedMoved = false;
    bool carriedBand = false;
    bool carriedFollowed = false;
    if (historyValid && onScreen) {
        vec2 statePosition = previousUv * displaySize - 0.5;
        ivec2 stateBase = ivec2(floor(statePosition));
        vec2 stateFraction = statePosition - vec2(stateBase);
        ivec2 lastState = ivec2(displaySize) - ivec2(1);
        vec2 s00 = texelFetch(sampler2D(HistoryConfidence, LinearClamp), clamp(stateBase, ivec2(0), lastState), 0).rg;
        vec2 s10 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(1, 0), ivec2(0), lastState), 0).rg;
        vec2 s01 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(0, 1), ivec2(0), lastState), 0).rg;
        vec2 s11 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(1, 1), ivec2(0), lastState), 0).rg;
        vec2 g = 1.0 - stateFraction;
        vec4 bilinear = vec4(g.x * g.y, stateFraction.x * g.y, g.x * stateFraction.y,
            stateFraction.x * stateFraction.y);
        vec4 carried = step(vec4(1.0e-3), bilinear);   // texels whose weight is more than rounding
        // The least state a carrying texel stored: the moved, band and followed marks lie below every plain lock,
        // each below the one before, so one least value answers all three.
        float carriedLeast = min(min(carried.x > 0.5 ? s00.y : 0.0, carried.y > 0.5 ? s10.y : 0.0),
            min(carried.z > 0.5 ? s01.y : 0.0, carried.w > 0.5 ? s11.y : 0.0));
        carriedMoved = temporalStoredMoved(carriedLeast);
        carriedBand = temporalStoredBand(carriedLeast);
        carriedFollowed = temporalStoredFollowed(carriedLeast);
        s00.y = temporalStoredLock(s00.y);
        s10.y = temporalStoredLock(s10.y);
        s01.y = temporalStoredLock(s01.y);
        s11.y = temporalStoredLock(s11.y);
        fetchedState = vec2(dot(bilinear, vec4(s00.x, s10.x, s01.x, s11.x)),
            max(max(carried.x * s00.y, carried.y * s10.y), max(carried.z * s01.y, carried.w * s11.y)));
    }

    // Step 3: disocclusion, one-sided. A pixel expects the depth of the surface it reprojected, and a stored depth
    // nearer than that means something covered it last frame. All four stored depths around the reprojected position
    // nearer drops the history. Where the expected surface shows under less than DisocclusionVisibleShare of their
    // bilinear weight, the pixel was mostly covered and drops it too, unless the nearest stored depth is narrow
    // (temporalNarrowDepth), a feature narrower than a texel, or two side by side, that the jitter missed this frame,
    // or the lock the pixel carries lies within half of LockDecay of whole. The narrow exception holds only where the
    // state the pixel carries says no moving surface showed there last frame (temporalStoreLock): a keyed line one or
    // two texels wide that moved on is as narrow as a missed still blade, and only that state tells them apart. A lock
    // within half a decay of whole was refreshed by a ridge on the last frame and has lost less than that since, as a
    // still sub-texel feature's has on the frame after the jitter showed it, and step 6 keeps its hold whole. Step 6
    // releases a lock by motion from LockMotionStartPixels on, so a feature moving more than a sixteenth of the way
    // from there to LockMotionEndPixels, about 1.2 display pixels a frame, leaves no such lock where it was. Where the
    // state says a moving surface showed there, neither exception holds, and any stored depth nearer than expected that
    // carries more than rounding of the weight drops the history: that surface's ridged pixels hold locks near whole,
    // as an avatar the camera follows keeps its own, and a history it covered in part holds its colour in that part,
    // which the clip leaves over a textured background. Where the state says a band pixel stored the history (step 1),
    // a pixel whose dilated nearest surface did not move in the world (nearerMoved) drops it, unless every stored
    // depth is farther than expected. The band followed a moving nearer surface, beside its edge or on it, and a pixel
    // of a still surface would carry that surface's colour away with it. A pixel beside or on the moving surface, and
    // a nearer surface reading the band where its edge was, keep it. A history the followed surface's own pixels
    // stored (the followed mark) drops only where one of the nine current texels around its position moves on screen
    // otherwise than the pixel reprojected, by more than FollowedHistoryMotionFraction of that motion: the followed
    // surface still shows there while the pixel's own surface passes, as the ground an avatar uncovers does under a
    // camera that follows it. A surface that stopped in the world, or turned back through zero travel, reads its own
    // pixels' history in place, or where the same surface shows moving with it under a camera that eases on after
    // it, and keeps it. Keyed on a moving edge instead, the drop never
    // fired on ground under a perspective camera, whose neighbouring texels move apart on screen by their depth step so
    // that every ground pixel reads as a moving edge. Sparing a pixel whose 3x3 holds a surface farther than its centre
    // spared every ground pixel at a grazing angle, where each ground texel lies farther than the one below it by more
    // than the tolerance.
    // Any farther stored depth kept a sub-pixel edge from reading as revealed, and on its own it kept the ring of
    // pixels around a moving object's old place, whose footprint reaches past its edge, from ever being disoccluded. A
    // background pixel expects BackgroundLinearDepth, so anything stored nearer there was covering it. A static surface
    // on or behind last frame's camera had no history there. The footprint sits at the display pixel's previous
    // position, where history is read, while the expected depth comes from the reprojected texel's own point, the
    // dilated nearest in the 3x3 unless the pixel reprojected by its own. The same four depths tell step 6 whether a
    // pixel at a moving edge reads history a farther surface left: every one of them farther than the moving surface's
    // expected depth. That runs for a moving surface too, whose depth test is skipped, and costs no fetch beyond the
    // four a depth-tested pixel already takes.
    bool followedElsewhere = true;
    if (carriedFollowed && !nearerMoved) {
        vec2 ownMotion = (uv - previousUv) * internalSize;
        ivec2 historyTexel = ivec2(floor(previousUv * internalSize + jitter));
        float apart = 0.0;
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                vec2 shown = texelFetch(sampler2D(MotionTex, LinearClamp),
                    clamp(historyTexel + ivec2(x, y), ivec2(0), maxTexel), 0).rg;
                apart = max(apart, length(shown * internalSize - ownMotion));
            }
        }
        followedElsewhere = apart > FollowedHistoryMotionFraction * length(ownMotion);
    }
    bool disoccluded = false;
    bool heldFromFarther = false;
    if (historyValid && onScreen && (depthTested || movingEdge)) {
        DepthFootprint footprint = temporalDepthFootprint(previousUv, internalSize, maxTexel, expectedDepth);
        disoccluded = depthTested && (!(expectedDepth > 1.0e-6)
            || footprint.farthest < expectedDepth * (1.0 - DisocclusionTolerance)
            || (carriedBand && !nearerMoved && !(expectedDepth < footprint.nearest * (1.0 - DisocclusionTolerance))
                && followedElsewhere)
            || (carriedMoved ? footprint.visibleShare < 1.0 - 1.0e-3
                : footprint.visibleShare < DisocclusionVisibleShare
                    && fetchedState.y <= 1.0 - 0.5 * LockDecay
                    && !temporalNarrowDepth(footprint.nearestTexel, footprint.nearest, maxTexel, false)));
        heldFromFarther = movingEdge && expectedDepth < footprint.nearest * (1.0 - DisocclusionTolerance);
    }

    // Step 2: Catmull-Rom history at the reprojected position. A NaN or an infinity never reaches the output, because
    // a comparison with either is false, which survives fast math where isnan may not.
    bool useHistory = historyValid && onScreen && !disoccluded;
    vec4 history = vec4(0.0);
    vec2 historyState = vec2(0.0);
    if (useHistory) {
        vec4 fetched = sampleHistoryCatmullRom(previousUv, displaySize);
        bool finite = all(greaterThan(fetched, vec4(-HalfMax))) && all(lessThan(fetched, vec4(HalfMax)))
            && all(greaterThan(fetchedState, vec2(-HalfMax))) && all(lessThan(fetchedState, vec2(HalfMax)));
        if (finite) {
            history = max(fetched, vec4(0.0));
            historyState = clamp(fetchedState, vec2(0.0), vec2(1.0));
        } else {
            useHistory = false;
        }
    }

    // Step 8, first half: the accumulated sample weight, capped lower as motion grows and cut by reactive content.
    float motionCap = mix(MaxAccumulation, MovingAccumulation, clamp(motionPixels / MotionAccumulationPixels, 0.0, 1.0));
    float accumulated = useHistory
        ? min(historyState.x * MaxAccumulation, motionCap) * (1.0 - reactive * ReactiveStrength)
        : 0.0;

    // Step 6: thin features. A ridge through the centre texel refreshes the lock, which decays by LockDecay a frame
    // whatever the preset. Large motion and reactive content release it, and so does motion at an edge, where the pixel
    // holds a moving feature's history, not its own. A pixel that reprojected by its own motion and kept its history
    // holds its own, because where a moving surface showed last frame step 3 keeps a history only where no stored depth
    // carrying weight lies nearer than the pixel's own surface. So a still sub-texel feature beside a fast surface
    // keeps its lock, while one that restarted lets go of a ridge the moving surface's colour may have given it. At a
    // moving edge whose history a farther surface left, the lock read with that history belongs to that surface, and a
    // nearer surface crossing a held thin feature would carry it onward, so it is dropped before a ridge can refresh
    // it. A thin feature taking a ridge keeps its own lock, and so does one beside a still nearer surface,
    // which is no moving edge. The hold on the clip stays whole while the lock is at least 1 / LockHoldGain, so a
    // sub-texel feature missed for a few frames keeps its luma, and it lets go over the rest of the lock.
    float centreLuma = lumas[4];
    float ridgeThreshold = max(LockRidgeAbsolute, LockRidgeRelative * centreLuma);
    bool ridge = isRidge(centreLuma, lumas[3], lumas[5], ridgeThreshold)
        || isRidge(centreLuma, lumas[1], lumas[7], ridgeThreshold)
        || isRidge(centreLuma, lumas[0], lumas[8], ridgeThreshold)
        || isRidge(centreLuma, lumas[2], lumas[6], ridgeThreshold);
    float lockValue = useHistory && !heldFromFarther ? max(historyState.y - LockDecay, 0.0) : 0.0;
    if (ridge) lockValue = 1.0;
    float motionRelease = clamp((motionPixels - LockMotionStartPixels) / (LockMotionEndPixels - LockMotionStartPixels), 0.0, 1.0);
    lockValue *= (1.0 - motionRelease) * (1.0 - clamp(reactive * LockReactiveRelease, 0.0, 1.0))
        * (1.0 - clamp((ownReprojected && useHistory ? 0.0 : edgeMotion) * LockEdgeRelease, 0.0, 1.0));
    float hold = clamp(lockValue * LockHoldGain, 0.0, 1.0);

    // Step 5: variance clipping in luma-weighted YCoCg, from the box centre towards the history, which keeps its hue.
    // Gamma is wide for a still pixel and narrows as it moves. A locked thin feature's luma neither drives the clip
    // nor loses its share of it, in proportion to its hold.
    float gamma = mix(GammaStill, GammaMoving, clamp(motionPixels / GammaMotionPixels, 0.0, 1.0));
    vec3 boxMin = mean - gamma * deviation;
    vec3 boxMax = mean + gamma * deviation;
    vec3 historyYcc = rgbToYCoCg(toWeighted(history.rgb));
    vec3 boxCentre = 0.5 * (boxMin + boxMax);
    vec3 boxExtent = 0.5 * (boxMax - boxMin) + vec3(1.0e-5);
    vec3 fromCentre = historyYcc - boxCentre;
    vec3 excess = abs(fromCentre) / boxExtent;
    excess.x *= 1.0 - hold;
    float clipScale = max(excess.x, max(excess.y, excess.z));
    vec3 clipped = clipScale > 1.0 ? boxCentre + fromCentre / clipScale : historyYcc;
    clipped.x = mix(clipped.x, historyYcc.x, hold);
    float historyAlpha = clamp(history.a, alphaMin, alphaMax);

    // Step 5 beside a fast narrow moving feature: where the pixel reprojected by its own motion, its history holds its
    // own surface alone, and the feature this frame's reconstruction also saw is missing from it. So the feature's
    // share of the reconstruction weight, on the texels nearer than the pixel's own surface, takes the feature's
    // current colour in the history, save the share the pixel's lock holds. The pixel then shows its current coverage
    // while its own surface stays accumulated, and it stores MovingShareConfidence, since that colour holds for this
    // frame alone. A pixel whose hold is whole keeps its own history and its confidence: it holds a still sub-texel
    // feature, which the next frame needs whole on the frames the jitter misses it. A surface wider than two texels
    // keeps its own history at its edge, where the same colour flickered with the jitter. The narrow test reads the run
    // through the nearest texel alone, so a wide object is narrow where it is one or two texels across in this frame's
    // depth, at a corner's tip or on a face seen at a grazing angle, and its colour is taken there too. The nearer
    // texels are summed here, where they are needed, rather than in the 3x3 every pixel runs: the sum is the same, and
    // five more accumulators carried through that loop made the whole program spill for every pixel.
    float movingShare = 0.0;
    if (narrowMoving) {
        float ownDepth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), centreTexel, 0).rg,
            texelFetch(sampler2D(SceneDepth, LinearClamp), centreTexel, 0).r);
        vec4 nearerSum = vec4(0.0);
        float nearerWeight = 0.0;
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                ivec2 texel = clamp(centreTexel + ivec2(x, y), ivec2(0), maxTexel);
                vec4 sceneColor = texelFetch(sampler2D(SceneColor, LinearClamp), texel, 0);
                vec3 ycc = rgbToYCoCg(toWeighted(min(max(sceneColor.rgb, vec3(0.0)), vec3(HalfMax))));
                float lanczosWeight = kernelX[x + 1] * kernelY[y + 1];
                float viewDepth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), texel, 0).rg,
                    texelFetch(sampler2D(SceneDepth, LinearClamp), texel, 0).r);
                if (ownDepth > viewDepth * (1.0 + DisocclusionTolerance)) {
                    nearerSum += vec4(ycc, sceneColor.a) * lanczosWeight;
                    nearerWeight += lanczosWeight;
                }
            }
        }
        if (nearerWeight > 1.0e-4) {
            movingShare = clamp(nearerWeight / max(reconstructionWeight, 1.0e-4), 0.0, 1.0) * (1.0 - hold);
            vec4 movingColor = nearerSum / nearerWeight;
            clipped = mix(clipped, clamp(movingColor.xyz, neighbourMin, neighbourMax), movingShare);
            historyAlpha = mix(historyAlpha, clamp(movingColor.w, alphaMin, alphaMax), movingShare);
        }
    }

    // Step 8, second half: blend in the weighted space. The current weight is 1 on a reset and falls to
    // sampleWeight / (MaxAccumulation + sampleWeight), about one in sixteen, raised wherever reactive content or a
    // disocclusion removed accumulated weight.
    float currentWeight = accumulated > 1.0e-3 ? sampleWeight / (accumulated + sampleWeight) : 1.0;
    vec3 resolvedYcc = mix(clipped, current.xyz, currentWeight);

    TemporalPixel result;
    result.color = fromWeighted(max(yCoCgToRgb(resolvedYcc), vec3(0.0)));
    result.alpha = mix(historyAlpha, current.w, currentWeight);
    result.confidence = movingShare > 0.0 ? MovingShareConfidence
        : min(accumulated + sampleWeight, motionCap) / MaxAccumulation;
    result.stability = lockValue;
    result.moved = followed ? 3.0 : band ? 2.0 : historyValid && !depthTested ? 1.0 : 0.0;
    result.disocclusion = historyValid && (!onScreen || disoccluded) ? 1.0 : 0.0;
    result.reactive = reactive;
    result.clip = useHistory && clipScale > 1.0 && length(clipped - historyYcc) > ClipFlagMinimumMove ? 1.0 : 0.0;
    return result;
}
";

        // ---- The resolve: the core plus the two history outputs ----
        public const string TemporalResolveFrag = "#version 450\n" + TemporalResolveCoreGlsl + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oState;
void main() {
    TemporalPixel p = temporalResolvePixel(ivec2(gl_FragCoord.xy));
    oColor = vec4(p.color, p.alpha + vUv.x * 1.0e-30);   // the vUv read changes no output, see the class summary
    oState = vec4(p.confidence, temporalStoreLock(p.stability, p.moved), 0.0, 1.0);
}";

        // ---- The depth store: this frame's linear view depth for next frame's disocclusion test ----
        public const string TemporalDepthStoreFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D SceneDepth;
layout(set=0, binding=1) uniform texture2D MotionTex;
layout(set=0, binding=2) uniform sampler Samp;
// The member is TemporalDepthStoreUniforms.GlslMembers, documented on the struct's field.
layout(set=0, binding=3) uniform DepthStore {" + TemporalDepthStoreUniforms.GlslMembers + @"};
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oDepth;
" + TemporalCommonGlsl + @"
void main() {
    ivec2 texel = ivec2(gl_FragCoord.xy);
    float ndcDepth = texelFetch(sampler2D(SceneDepth, Samp), texel, 0).r;
    vec2 motion = texelFetch(sampler2D(MotionTex, Samp), texel, 0).rg;
    float viewDepth = abs(motion.x) > MotionSentinel ? BackgroundLinearDepth : temporalLinearDepth(ndcDepth, CurrentDepth);
    oDepth = vec4(viewDepth + vUv.x * 1.0e-30, 0.0, 0.0, 1.0);   // the vUv read changes no output
}";
    }
}
