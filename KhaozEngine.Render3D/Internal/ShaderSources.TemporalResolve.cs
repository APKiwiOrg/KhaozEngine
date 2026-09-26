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
    /// the per-pixel resolve as a function with no stage inputs or outputs, so the planned temporal debug views and
    /// sampled temporal counts can re-evaluate exactly what the resolve decided, through the resource set it bound.</para>
    /// <para><b>THE UNIFORM BLOCKS ARE THE C# STRUCTS.</b> Each block's members are spliced from
    /// <see cref="TemporalResolveUniforms.GlslMembers"/> and <see cref="TemporalDepthStoreUniforms.GlslMembers"/>, so the
    /// text cannot drift from the fields, and the fields carry the meaning.</para>
    /// <para><b>A MOVING SURFACE SKIPS THE DEPTH TEST.</b> <see cref="TemporalResolveUniforms.CurrentToPrevious"/> maps a
    /// static point, so the expected depth is only meaningful where the dilated texel's motion carries its own sample
    /// within <see cref="TemporalResolveTuning.MovingSurfaceInternalPixels"/>, plus
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
struct TemporalPixel { vec3 color; float confidence; float stability; float disocclusion; float reactive; float clip; float alpha; };

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
    vec2 closestSample = pixelCentre;
    vec2 closestMotion = vec2(0.0);
    bool closestIsBackground = true;
    float reactiveDifference = 0.0;
    vec2 centreMotion = vec2(0.0);
    bool centreIsBackground = true;
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

    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 texel = clamp(centreTexel + ivec2(x, y), ivec2(0), maxTexel);
            vec4 sceneColor = texelFetch(sampler2D(SceneColor, LinearClamp), texel, 0);
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
            vec2 samplePosition = vec2(texel) + 0.5 - jitter;
            float lanczosWeight = kernelX[x + 1] * kernelY[y + 1];
            reconstruction += vec4(ycc, sceneColor.a) * lanczosWeight;
            reconstructionWeight += lanczosWeight;
            sampleWeight = max(sampleWeight, clamp(displayKernelX[x + 1] * displayKernelY[y + 1], 0.0, 1.0));

            // Step 1: the nearest surface in the neighbourhood carries the motion, and its unjittered sample position is
            // the surface point that motion was written for. Background is the motion sentinel on the x channel alone,
            // because the depth attachment is cleared to the background colour, not to the far plane. A clamped or
            // off-screen motion is finite and far below the sentinel, so it is never background.
            vec2 motion = texelFetch(sampler2D(MotionTex, LinearClamp), texel, 0).rg;
            float ndcDepth = texelFetch(sampler2D(SceneDepth, LinearClamp), texel, 0).r;
            bool isBackground = abs(motion.x) > MotionSentinel;
            if (x == 0 && y == 0) {
                centreMotion = motion;
                centreIsBackground = isBackground;
            }
            float viewDepth = isBackground ? BackgroundLinearDepth : temporalLinearDepth(ndcDepth, CurrentDepth);
            if (viewDepth < closestDepth) {
                closestDepth = viewDepth;
                closestSample = samplePosition;
                closestMotion = motion;
                closestIsBackground = isBackground;
            }

            // Step 7: how much the transparent passes changed this texel.
            vec3 opaqueColor = texelFetch(sampler2D(OpaqueColor, LinearClamp), texel, 0).rgb;
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

    // Steps 1 and 3: where this pixel was last frame, and the depth a static surface there had. Background reprojects
    // from camera rotation alone. A surface reads history where the dilated motion carries this display pixel. The
    // expected depth and the moving-surface test use the dilated texel's own surface point, its unjittered sample at its
    // own depth, which is the point the motion target wrote that motion for. CurrentToPrevious takes it to last
    // frame's view space as if static, and PreviousProjection gives the UV it had there. On a static surface that UV
    // and the texel's own previous position agree up to float precision and the motion target's half-float rounding.
    // More than MovingSurfaceInternalPixels apart, plus MovingSurfaceMotionFraction of the motion for that rounding, the
    // surface moved, and skips the depth test. So does one whose static point sat on or behind last frame's camera
    // plane.
    vec2 ndcXY = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    vec2 previousUv = vec2(-1.0);
    float expectedDepth = BackgroundLinearDepth;
    bool depthTested = true;
    if (closestIsBackground) {
        vec4 previousClip = BackgroundToPrevious * vec4(ndcXY, 1.0, 1.0);
        if (previousClip.w > 1.0e-6)
            previousUv = vec2(previousClip.x / previousClip.w * 0.5 + 0.5, 0.5 - previousClip.y / previousClip.w * 0.5);
    } else {
        previousUv = uv - closestMotion;
        vec2 closestUv = closestSample / internalSize;
        vec2 closestNdc = vec2(closestUv.x * 2.0 - 1.0, 1.0 - closestUv.y * 2.0);
        float clipW = CurrentDepth.x > 0.5 ? closestDepth : 1.0;
        vec4 previousView = CurrentToPrevious * vec4(closestNdc * clipW, closestDepth, 1.0);
        expectedDepth = -previousView.z;
        vec4 staticClip = PreviousProjection * previousView;
        depthTested = false;
        if (staticClip.w > 1.0e-6) {
            vec2 staticUv = vec2(staticClip.x / staticClip.w * 0.5 + 0.5, 0.5 - staticClip.y / staticClip.w * 0.5);
            vec2 surfaceMotion = (staticUv - (closestUv - closestMotion)) * internalSize;
            float movingThreshold = MovingSurfaceInternalPixels + length(closestMotion * internalSize) * MovingSurfaceMotionFraction;
            depthTested = length(surfaceMotion) <= movingThreshold;
        }
    }
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
    // not exact below a quarter of the screen even when the camera is still.
    float edgeMotion = 0.0;
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
    }
    bool movingEdge = edgeMotion > max(LockEdgeFloorInternalPixels, length(closestMotion * internalSize) * LockEdgeMotionFraction);

    // Step 3: disocclusion, one-sided. The farthest of the four stored depths around the reprojected position keeps a
    // sub-pixel edge from reading as revealed. A background pixel expects BackgroundLinearDepth, so anything stored
    // nearer there was covering it. A static surface on or behind last frame's camera had no history there. The
    // footprint sits at the display pixel's previous position, where history is read, while the expected depth comes
    // from the dilated texel's own point. That is safe: the dilated texel carries the nearest depth in the 3x3, and the
    // test fires only on a stored depth nearer than expected, so the nearest expectation can only make it fire less.
    // The same four depths tell step 6 whether a pixel at a moving edge reads history a farther surface left: every
    // one of them farther than the moving surface's expected depth. That runs for a moving surface too, whose depth
    // test is skipped, and costs no fetch beyond the four a depth-tested pixel already takes.
    bool disoccluded = false;
    bool heldFromFarther = false;
    if (historyValid && onScreen && (depthTested || movingEdge)) {
        ivec2 baseTexel = ivec2(floor(previousUv * internalSize - 0.5));
        float farthest = 0.0;
        float nearest = 2.0 * BackgroundLinearDepth;
        for (int corner = 0; corner < 4; corner++) {
            ivec2 texel = clamp(baseTexel + ivec2(corner & 1, corner >> 1), ivec2(0), maxTexel);
            float stored = texelFetch(sampler2D(PrevDepth, LinearClamp), texel, 0).r;
            farthest = max(farthest, stored);
            nearest = min(nearest, stored);
        }
        disoccluded = depthTested
            && (!(expectedDepth > 1.0e-6) || farthest < expectedDepth * (1.0 - DisocclusionTolerance));
        heldFromFarther = movingEdge && expectedDepth < nearest * (1.0 - DisocclusionTolerance);
    }

    // Step 2: Catmull-Rom history at the reprojected position. A NaN or an infinity never reaches the output, because
    // a comparison with either is false, which survives fast math where isnan may not.
    bool useHistory = historyValid && onScreen && !disoccluded;
    vec4 history = vec4(0.0);
    vec2 historyState = vec2(0.0);
    if (useHistory) {
        vec4 fetched = sampleHistoryCatmullRom(previousUv, displaySize);
        // The state's four texels around the position: confidence blended by their bilinear weights, and the lock as
        // the largest of those that carry weight. A bilinear lock under motion is blended with unlocked neighbours every
        // frame and ran out about three times faster than LockDecay, so a moving sub-texel feature lost its hold. At a
        // texel centre only that texel carries weight, so a still lock reads back unchanged and never spreads.
        vec2 statePosition = previousUv * displaySize - 0.5;
        ivec2 stateBase = ivec2(floor(statePosition));
        vec2 stateFraction = statePosition - vec2(stateBase);
        ivec2 lastState = ivec2(displaySize) - ivec2(1);
        vec2 s00 = texelFetch(sampler2D(HistoryConfidence, LinearClamp), clamp(stateBase, ivec2(0), lastState), 0).rg;
        vec2 s10 = texelFetch(sampler2D(HistoryConfidence, LinearClamp), clamp(stateBase + ivec2(1, 0), ivec2(0), lastState), 0).rg;
        vec2 s01 = texelFetch(sampler2D(HistoryConfidence, LinearClamp), clamp(stateBase + ivec2(0, 1), ivec2(0), lastState), 0).rg;
        vec2 s11 = texelFetch(sampler2D(HistoryConfidence, LinearClamp), clamp(stateBase + ivec2(1, 1), ivec2(0), lastState), 0).rg;
        vec2 g = 1.0 - stateFraction;
        vec4 bilinear = vec4(g.x * g.y, stateFraction.x * g.y, g.x * stateFraction.y, stateFraction.x * stateFraction.y);
        vec4 carried = step(vec4(1.0e-3), bilinear);   // texels whose weight is more than rounding
        vec2 fetchedState = vec2(dot(bilinear, vec4(s00.x, s10.x, s01.x, s11.x)),
            max(max(carried.x * s00.y, carried.y * s10.y), max(carried.z * s01.y, carried.w * s11.y)));
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
    // holds a moving feature's history, not its own. At a moving edge whose history a farther surface left, the lock
    // read with that history belongs to that surface, and a nearer surface crossing a held thin feature would carry it
    // onward, so it is dropped before a ridge can refresh it. A thin feature taking a ridge keeps its own lock, and so
    // does one beside a still nearer surface, which is no moving edge. The hold on the clip stays whole while the lock
    // is at least 1 / LockHoldGain, so a sub-texel feature missed for a few frames keeps its luma, and it lets go over
    // the rest of the lock.
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
        * (1.0 - clamp(edgeMotion * LockEdgeRelease, 0.0, 1.0));
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

    // Step 8, second half: blend in the weighted space. The current weight is 1 on a reset and falls to
    // sampleWeight / (MaxAccumulation + sampleWeight), about one in sixteen, raised wherever reactive content or a
    // disocclusion removed accumulated weight.
    float currentWeight = accumulated > 1.0e-3 ? sampleWeight / (accumulated + sampleWeight) : 1.0;
    vec3 resolvedYcc = mix(clipped, current.xyz, currentWeight);

    TemporalPixel result;
    result.color = fromWeighted(max(yCoCgToRgb(resolvedYcc), vec3(0.0)));
    result.alpha = mix(historyAlpha, current.w, currentWeight);
    result.confidence = min(accumulated + sampleWeight, motionCap) / MaxAccumulation;
    result.stability = lockValue;
    result.disocclusion = historyValid && (!onScreen || disoccluded) ? 1.0 : 0.0;
    result.reactive = reactive;
    result.clip = useHistory && clipScale > 1.0 ? 1.0 : 0.0;
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
    oState = vec4(p.confidence, p.stability, 0.0, 1.0);
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
