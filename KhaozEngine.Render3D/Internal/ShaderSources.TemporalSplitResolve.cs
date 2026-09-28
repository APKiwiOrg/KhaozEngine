namespace KhaozEngine.Render3D.Internal
{
    /// <summary>The two-pass resolve's pass two (ShaderSources.TemporalSplit.cs has the design): once per display
    /// pixel, over pass one's targets, the history pair and the previous depth.</summary>
    internal static partial class ShaderSources
    {
        // ---- Pass two's bindings and the history helpers, copies of TemporalResolveCoreGlsl's ----
        const string TemporalSplitBindingsGlsl = @"
layout(set=0, binding=0) uniform texture2D Prepared;
layout(set=0, binding=1) uniform texture2D PreparedReproject;
layout(set=0, binding=2) uniform texture2D PreparedExpected;
layout(set=0, binding=3) uniform texture2D PreparedEdge;
layout(set=0, binding=4) uniform texture2D CurrentViewDepth;
layout(set=0, binding=5) uniform texture2D PrevDepth;
layout(set=0, binding=6) uniform texture2D HistoryColor;
layout(set=0, binding=7) uniform texture2D HistoryConfidence;
layout(set=0, binding=8) uniform sampler LinearClamp;
// The members are TemporalResolveUniforms.GlslMembers, documented on the struct's fields.
layout(set=0, binding=9) uniform Resolve {" + TemporalResolveUniforms.GlslMembers + @"};
";

        const string TemporalSplitHistoryGlsl = @"
struct TemporalPixel {
    vec3 color; float confidence; float stability; float moved; float disocclusion; float reactive; float clip;
    float alpha;
};

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

// temporalNarrowDepth over the depths last frame stored.
bool splitNarrowPrevious(ivec2 texel, float depth, ivec2 maxTexel) {
    float limit = 1.0 - DisocclusionTolerance;
    bool run[8];   // left 1 and 2, right 1 and 2, up 1 and 2, down 1 and 2: the texel there is at this depth
    for (int i = 0; i < 8; i++) {
        int stride = (i & 1) + 1;
        ivec2 offset = i < 2 ? ivec2(-stride, 0) : i < 4 ? ivec2(stride, 0)
            : i < 6 ? ivec2(0, -stride) : ivec2(0, stride);
        float there = texelFetch(sampler2D(PrevDepth, LinearClamp), clamp(texel + offset, ivec2(0), maxTexel), 0).r;
        run[i] = !(depth < there * limit || there < depth * limit);
    }
    bool narrowRow = !(run[0] && run[2]) && !(run[0] && run[1]) && !(run[2] && run[3]);
    bool narrowColumn = !(run[4] && run[6]) && !(run[4] && run[5]) && !(run[6] && run[7]);
    return narrowRow || narrowColumn;
}

float temporalStoreLock(float lockValue, bool moved, bool band) {
    return band ? -3.0 - lockValue : moved ? -1.0 - lockValue : lockValue;
}
bool temporalStoredMoved(float stored) { return stored < -0.5; }
bool temporalStoredBand(float stored) { return stored < -2.5; }
float temporalStoredLock(float stored) {
    return temporalStoredBand(stored) ? -3.0 - stored : temporalStoredMoved(stored) ? -1.0 - stored : stored;
}
";

        // ---- Pass two's per-pixel resolve: temporalResolvePixel over pass one's targets ----
        const string TemporalSplitPixelGlsl = @"
TemporalPixel temporalSplitPixel(ivec2 displayPixel) {
    vec2 internalSize = Sizes.xy;
    vec2 displaySize = Sizes.zw;
    vec2 jitter = Jitter.xy;
    float displayOverInternal = Jitter.z;
    bool historyValid = Jitter.w > 0.5;

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
    float reactiveDifference = 0.0;
    float lumas[9];

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

    // The 3x3 over pass one's colours, each texel read one step ahead as the single pass reads its inputs.
    ivec2 nextTexel = clamp(centreTexel + ivec2(-1), ivec2(0), maxTexel);
    vec4 nextPrepared = texelFetch(sampler2D(Prepared, LinearClamp), nextTexel, 0);
    float nextReactive = texelFetch(sampler2D(PreparedReproject, LinearClamp), nextTexel, 0).x;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec4 prepared = nextPrepared;
            float texelReactive = nextReactive;
            nextTexel = clamp(centreTexel + (x == 1 ? ivec2(-1, min(y + 1, 1)) : ivec2(x + 1, y)), ivec2(0), maxTexel);
            nextPrepared = texelFetch(sampler2D(Prepared, LinearClamp), nextTexel, 0);
            nextReactive = texelFetch(sampler2D(PreparedReproject, LinearClamp), nextTexel, 0).x;
            vec3 ycc = prepared.xyz;
            lumas[(y + 1) * 3 + (x + 1)] = ycc.x;
            momentSum += ycc;
            momentSquares += ycc * ycc;
            neighbourMin = min(neighbourMin, ycc);
            neighbourMax = max(neighbourMax, ycc);
            alphaMin = min(alphaMin, prepared.w);
            alphaMax = max(alphaMax, prepared.w);
            float lanczosWeight = kernelX[x + 1] * kernelY[y + 1];
            reconstruction += vec4(ycc, prepared.w) * lanczosWeight;
            reconstructionWeight += lanczosWeight;
            sampleWeight = max(sampleWeight, clamp(displayKernelX[x + 1] * displayKernelY[y + 1], 0.0, 1.0));
            reactiveDifference = max(reactiveDifference, texelReactive);
        }
    }

    float reactive = clamp(reactiveDifference * ReactiveGain, 0.0, 1.0);
    vec3 mean = momentSum / 9.0;
    vec3 deviation = sqrt(max(momentSquares / 9.0 - mean * mean, vec3(0.0)));
    vec4 current = reconstruction / max(reconstructionWeight, 1.0e-4);
    current.xyz = clamp(current.xyz, neighbourMin, neighbourMax);
    current.w = clamp(current.w, alphaMin, alphaMax);

    // Pass one's decisions for the centre texel: the motion and surface the pixel reprojects by and steps 1, 3 and 6's
    // flags for it.
    vec4 reproject = texelFetch(sampler2D(PreparedReproject, LinearClamp), centreTexel, 0);
    int flags = int(reproject.w + 0.5);
    bool chosenBackground = (flags & SplitBackground) != 0;
    bool depthTested = (flags & SplitDepthTested) != 0;
    bool nearerMoved = (flags & SplitNearerMoved) != 0;
    bool band = (flags & SplitBand) != 0;
    bool ownReprojected = (flags & SplitOwnReprojected) != 0;
    bool narrowMoving = (flags & SplitNarrowMoving) != 0;
    bool movingEdge = (flags & SplitMovingEdge) != 0;
    float expectedDepth = texelFetch(sampler2D(PreparedExpected, LinearClamp), centreTexel, 0).r;
    float edgeRelease = texelFetch(sampler2D(PreparedEdge, LinearClamp), centreTexel, 0).r;

    // temporalReproject's previous position for this display pixel.
    vec2 previousUv = vec2(-1.0);
    if (chosenBackground) {
        vec2 ndcXY = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
        vec4 previousClip = BackgroundToPrevious * vec4(ndcXY, 1.0, 1.0);
        if (previousClip.w > 1.0e-6)
            previousUv = vec2(previousClip.x / previousClip.w * 0.5 + 0.5, 0.5 - previousClip.y / previousClip.w * 0.5);
    } else {
        previousUv = uv - reproject.yz;
    }
    bool onScreen = all(greaterThanEqual(previousUv, vec2(0.0))) && all(lessThanEqual(previousUv, vec2(1.0)));
    float motionPixels = onScreen ? length((uv - previousUv) * displaySize) : 0.0;

    vec2 fetchedState = vec2(0.0);
    bool carriedMoved = false;
    bool carriedBand = false;
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
        vec4 carried = step(vec4(1.0e-3), bilinear);
        float carriedLeast = min(min(carried.x > 0.5 ? s00.y : 0.0, carried.y > 0.5 ? s10.y : 0.0),
            min(carried.z > 0.5 ? s01.y : 0.0, carried.w > 0.5 ? s11.y : 0.0));
        carriedMoved = temporalStoredMoved(carriedLeast);
        carriedBand = temporalStoredBand(carriedLeast);
        s00.y = temporalStoredLock(s00.y);
        s10.y = temporalStoredLock(s10.y);
        s01.y = temporalStoredLock(s01.y);
        s11.y = temporalStoredLock(s11.y);
        fetchedState = vec2(dot(bilinear, vec4(s00.x, s10.x, s01.x, s11.x)),
            max(max(carried.x * s00.y, carried.y * s10.y), max(carried.z * s01.y, carried.w * s11.y)));
    }

    bool disoccluded = false;
    bool heldFromFarther = false;
    if (historyValid && onScreen && (depthTested || movingEdge)) {
        DepthFootprint footprint = temporalDepthFootprint(previousUv, internalSize, maxTexel, expectedDepth);
        disoccluded = depthTested && (!(expectedDepth > 1.0e-6)
            || footprint.farthest < expectedDepth * (1.0 - DisocclusionTolerance)
            || (carriedBand && !nearerMoved && !(expectedDepth < footprint.nearest * (1.0 - DisocclusionTolerance)))
            || (carriedMoved ? footprint.visibleShare < 1.0 - 1.0e-3
                : footprint.visibleShare < DisocclusionVisibleShare
                    && fetchedState.y <= 1.0 - 0.5 * LockDecay
                    && !splitNarrowPrevious(footprint.nearestTexel, footprint.nearest, maxTexel)));
        heldFromFarther = movingEdge && expectedDepth < footprint.nearest * (1.0 - DisocclusionTolerance);
    }

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

    float motionCap = mix(MaxAccumulation, MovingAccumulation,
        clamp(motionPixels / MotionAccumulationPixels, 0.0, 1.0));
    float accumulated = useHistory
        ? min(historyState.x * MaxAccumulation, motionCap) * (1.0 - reactive * ReactiveStrength)
        : 0.0;

    float centreLuma = lumas[4];
    float ridgeThreshold = max(LockRidgeAbsolute, LockRidgeRelative * centreLuma);
    bool ridge = isRidge(centreLuma, lumas[3], lumas[5], ridgeThreshold)
        || isRidge(centreLuma, lumas[1], lumas[7], ridgeThreshold)
        || isRidge(centreLuma, lumas[0], lumas[8], ridgeThreshold)
        || isRidge(centreLuma, lumas[2], lumas[6], ridgeThreshold);
    float lockValue = useHistory && !heldFromFarther ? max(historyState.y - LockDecay, 0.0) : 0.0;
    if (ridge) lockValue = 1.0;
    float motionRelease = clamp((motionPixels - LockMotionStartPixels) / (LockMotionEndPixels - LockMotionStartPixels),
        0.0, 1.0);
    lockValue *= (1.0 - motionRelease) * (1.0 - clamp(reactive * LockReactiveRelease, 0.0, 1.0))
        * (1.0 - (ownReprojected && useHistory ? 0.0 : edgeRelease));
    float hold = clamp(lockValue * LockHoldGain, 0.0, 1.0);

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

    // Step 5's moving share, over pass one's colours and this frame's view depths.
    float movingShare = 0.0;
    if (narrowMoving) {
        float ownDepth = texelFetch(sampler2D(CurrentViewDepth, LinearClamp), centreTexel, 0).r;
        vec4 nearerSum = vec4(0.0);
        float nearerWeight = 0.0;
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                ivec2 texel = clamp(centreTexel + ivec2(x, y), ivec2(0), maxTexel);
                vec4 prepared = texelFetch(sampler2D(Prepared, LinearClamp), texel, 0);
                float lanczosWeight = kernelX[x + 1] * kernelY[y + 1];
                float viewDepth = texelFetch(sampler2D(CurrentViewDepth, LinearClamp), texel, 0).r;
                if (ownDepth > viewDepth * (1.0 + DisocclusionTolerance)) {
                    nearerSum += prepared * lanczosWeight;
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

    float currentWeight = accumulated > 1.0e-3 ? sampleWeight / (accumulated + sampleWeight) : 1.0;
    vec3 resolvedYcc = mix(clipped, current.xyz, currentWeight);

    TemporalPixel result;
    result.color = fromWeighted(max(yCoCgToRgb(resolvedYcc), vec3(0.0)));
    result.alpha = mix(historyAlpha, current.w, currentWeight);
    result.confidence = movingShare > 0.0 ? MovingShareConfidence
        : min(accumulated + sampleWeight, motionCap) / MaxAccumulation;
    result.stability = lockValue;
    result.moved = band ? 2.0 : historyValid && !depthTested ? 1.0 : 0.0;
    result.disocclusion = historyValid && (!onScreen || disoccluded) ? 1.0 : 0.0;
    result.reactive = reactive;
    result.clip = useHistory && clipScale > 1.0 && length(clipped - historyYcc) > ClipFlagMinimumMove ? 1.0 : 0.0;
    return result;
}
";

        // ---- Pass two: once per display pixel, into the history pair's write targets ----
        public const string TemporalSplitFrag = "#version 450\n" + TemporalSplitBindingsGlsl + TemporalCommonGlsl
            + TemporalResolveTuningGlsl + TemporalSplitPureGlsl + TemporalSplitHistoryGlsl + TemporalSplitPixelGlsl + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oState;
void main() {
    TemporalPixel p = temporalSplitPixel(ivec2(gl_FragCoord.xy));
    oColor = vec4(p.color, p.alpha + vUv.x * 1.0e-30);   // the vUv read changes no output
    oState = vec4(p.confidence, temporalStoreLock(p.stability, p.moved > 0.5, p.moved > 1.5), 0.0, 1.0);
}";
    }
}
