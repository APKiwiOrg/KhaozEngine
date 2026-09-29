namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The temporal resolve's PER-PIXEL ACCUMULATION (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3): every rule the
    /// resolve applies per display pixel, as GLSL functions with no stage inputs or outputs. Both entry points call
    /// them over the same 3x3 statistics (<c>temporalGather</c>) and the same prepared surface
    /// (<c>TemporalSurface</c>, <see cref="TemporalPrepareGlsl"/>): the fused resolve after preparing its 3x3 inline,
    /// the split's second pass (<see cref="TemporalAccumulateFrag"/>) after reading its first pass's targets.
    /// <para>A program that includes it declares <c>SceneColor</c>, <c>SceneDepth</c>, <c>MotionTex</c>,
    /// <c>PrevDepth</c>, <c>HistoryColor</c>, <c>HistoryConfidence</c>, <c>LinearClamp</c> and the <c>Resolve</c>
    /// block, and includes <see cref="TemporalPrepareGlsl"/> before it.</para>
    /// </summary>
    internal static partial class ShaderSources
    {
        internal const string TemporalAccumulateGlsl = @"
struct TemporalPixel {
    vec3 color; float confidence; float stability; float moved; float disocclusion; float reactive; float clip;
    float alpha;
};

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

// Step 6's ridge through the centre texel of the 3x3, over its lumas in reading order, l4 the centre: across the row,
// the column or either diagonal, the centre stands out from both sides above or below.
bool temporalRidge(float l0, float l1, float l2, float l3, float l4, float l5, float l6, float l7, float l8) {
    float centreLuma = l4;
    float ridgeThreshold = max(LockRidgeAbsolute, LockRidgeRelative * centreLuma);
    return isRidge(centreLuma, l3, l5, ridgeThreshold)
        || isRidge(centreLuma, l1, l7, ridgeThreshold)
        || isRidge(centreLuma, l0, l8, ridgeThreshold)
        || isRidge(centreLuma, l2, l6, ridgeThreshold);
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

// The display pixel centre in upright UV (row 0 at the top) and in internal pixels, and the texel whose jittered
// sample lands within half a texel of it, the centre of the pixel's 3x3.
void temporalPixelSite(ivec2 displayPixel, out vec2 uv, out vec2 pixelCentre, out ivec2 maxTexel,
    out ivec2 centreTexel) {
    vec2 internalSize = Sizes.xy;
    uv = (vec2(displayPixel) + 0.5) / Sizes.zw;
    pixelCentre = uv * internalSize;
    maxTexel = ivec2(internalSize) - ivec2(1);
    centreTexel = clamp(ivec2(floor(pixelCentre + Jitter.xy)), ivec2(0), maxTexel);
}

// Step 4's kernel is separable, and each axis of a clamped 3x3 texel depends on that axis alone. So three weights per
// axis at internal scale for the reconstruction, and three per axis at display scale for the sample weight, form the
// nine products the 3x3 takes: 12 kernel evaluations in place of 36.
struct TemporalKernels { avec3 x; avec3 y; avec3 displayX; avec3 displayY; };

TemporalKernels temporalKernels(vec2 pixelCentre, ivec2 centreTexel, ivec2 maxTexel) {
    vec2 jitter = Jitter.xy;
    float displayOverInternal = Jitter.z;
    TemporalKernels kernels;
    kernels.x = avec3(0.0);
    kernels.y = avec3(0.0);
    kernels.displayX = avec3(0.0);
    kernels.displayY = avec3(0.0);
    for (int i = 0; i < 3; i++) {
        ivec2 texel = clamp(centreTexel + ivec2(i - 1), ivec2(0), maxTexel);
        vec2 toSample = vec2(texel) + 0.5 - jitter - pixelCentre;
        kernels.x[i] = toAfloat(lanczos2(toSample.x));
        kernels.y[i] = toAfloat(lanczos2(toSample.y));
        kernels.displayX[i] = toAfloat(lanczos2(toSample.x * displayOverInternal));
        kernels.displayY[i] = toAfloat(lanczos2(toSample.y * displayOverInternal));
    }
    return kernels;
}

// Step 4: Lanczos 2 on the distance from the jittered sample of the 3x3's texel in column x and row y (0 to 2) to the
// pixel centre, in internal pixels, as its column's weight times its row's.
afloat temporalLanczosWeight(TemporalKernels kernels, int x, int y) { return kernels.x[x] * kernels.y[y]; }

// The 3x3's statistics, gathered a texel at a time in reading order: the moments and range of its weighted YCoCg and
// alpha, its Lanczos reconstruction, what this frame is worth to the pixel, and its largest reactive difference.
struct TemporalNeighbourhood {
    vec3 momentSum; vec3 momentSquares; vec3 neighbourMin; vec3 neighbourMax; afloat alphaMin; afloat alphaMax;
    vec4 reconstruction; float reconstructionWeight; afloat sampleWeight; afloat reactiveDifference;
};

TemporalNeighbourhood temporalNeighbourhood() {
    TemporalNeighbourhood n;
    n.momentSum = vec3(0.0);
    n.momentSquares = vec3(0.0);
    n.neighbourMin = vec3(TemporalRangeLimit);
    n.neighbourMax = vec3(-TemporalRangeLimit);
    n.alphaMin = afloat(TemporalRangeLimit);
    n.alphaMax = afloat(-TemporalRangeLimit);
    n.reconstruction = vec4(0.0);
    n.reconstructionWeight = 0.0;
    n.sampleWeight = afloat(0.0);
    n.reactiveDifference = afloat(0.0);
    return n;
}

// One texel of the 3x3, in column x and row y (0 to 2): its weighted YCoCg (temporalWeighted), its alpha and its
// reactive difference (temporalReactiveDifference). How close the nearest sample lands in display pixels is what this
// frame is worth to the pixel.
void temporalGather(inout TemporalNeighbourhood n, TemporalKernels kernels, int x, int y, vec3 ycc, afloat alpha,
    afloat reactiveDifference) {
    n.momentSum += ycc;
    n.momentSquares += ycc * ycc;
    n.neighbourMin = min(n.neighbourMin, ycc);
    n.neighbourMax = max(n.neighbourMax, ycc);
    n.alphaMin = min(n.alphaMin, alpha);
    n.alphaMax = max(n.alphaMax, alpha);
    float lanczosWeight = toFloat(temporalLanczosWeight(kernels, x, y));
    n.reconstruction += vec4(ycc, toFloat(alpha)) * lanczosWeight;
    n.reconstructionWeight += lanczosWeight;
    n.sampleWeight = max(n.sampleWeight, clamp(kernels.displayX[x] * kernels.displayY[y], afloat(0.0), afloat(1.0)));
    n.reactiveDifference = max(n.reactiveDifference, reactiveDifference);
}
" + TemporalDisplayKernelGlsl + @"
// Step 1: where this display pixel was last frame if it moved as the surface it reprojects by did. A surface reads
// history where its motion carries the pixel. Background reprojects from camera rotation alone, and a point on or
// behind last frame's camera plane has no previous position.
vec2 temporalPreviousUv(vec2 uv, vec2 motion, bool isBackground) {
    vec2 previousUv = vec2(-1.0);
    if (isBackground) {
        vec2 ndcXY = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
        vec4 previousClip = BackgroundToPrevious * vec4(ndcXY, 1.0, 1.0);
        if (previousClip.w > 1.0e-6)
            previousUv = vec2(previousClip.x / previousClip.w * 0.5 + 0.5, 0.5 - previousClip.y / previousClip.w * 0.5);
    } else {
        previousUv = uv - motion;
    }
    return previousUv;
}

// The per-pixel resolve over a gathered 3x3 (temporalGather), its ridge (temporalRidge) and the surface its centre
// texel prepared (temporalPrepareSurface), in the display pixel at uv whose 3x3 is centred on centreTexel.
TemporalPixel temporalAccumulatePixel(vec2 uv, ivec2 centreTexel, ivec2 maxTexel, TemporalNeighbourhood n,
    TemporalKernels kernels, bool ridge, TemporalSurface surface) {
    vec2 internalSize = Sizes.xy;
    vec2 displaySize = Sizes.zw;
    vec2 jitter = Jitter.xy;
    bool historyValid = Jitter.w > 0.5;
    vec3 neighbourMin = toVec3(n.neighbourMin);
    vec3 neighbourMax = toVec3(n.neighbourMax);
    float alphaMin = toFloat(n.alphaMin);
    float alphaMax = toFloat(n.alphaMax);
    float reconstructionWeight = n.reconstructionWeight;
    float sampleWeight = toFloat(n.sampleWeight);

    float reactive = clamp(toFloat(n.reactiveDifference) * ReactiveGain, 0.0, 1.0);
    vec3 mean = n.momentSum / 9.0;
    vec3 deviation = sqrt(max(n.momentSquares / 9.0 - mean * mean, vec3(0.0)));
    vec4 current = n.reconstruction / max(reconstructionWeight, 1.0e-4);
    // The negative lobes cannot ring past the neighbourhood.
    current.xyz = clamp(current.xyz, neighbourMin, neighbourMax);
    current.w = clamp(current.w, alphaMin, alphaMax);

    // Steps 1 and 3: where this pixel was last frame, by the surface its centre texel prepared, the dilated nearest or
    // beside a fast edge the centre texel's own, and the depth a static surface there had. Motion is clamped to two
    // screens and a point behind last frame's camera is written two screens away, so any such previous position falls
    // outside [0, 1] here and is rejected like any other off-screen one.
    float expectedDepth = surface.expectedDepth;
    bool depthTested = surface.depthTested;
    bool nearerMoved = surface.nearerMoved;
    bool band = surface.band;
    bool followed = surface.followed;
    bool ownReprojected = surface.ownReprojected;
    bool narrowMoving = surface.narrowMoving;
    bool movingEdge = surface.movingEdge;
    float edgeMotion = surface.edgeMotion;
    vec2 previousUv = temporalPreviousUv(uv, surface.motion, surface.background);
    bool onScreen = all(greaterThanEqual(previousUv, vec2(0.0))) && all(lessThanEqual(previousUv, vec2(1.0)));
    float motionPixels = onScreen ? length((uv - previousUv) * displaySize) : 0.0;

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
        // each below the one before, so one least value answers all three, and a footprint carrying two marks
        // answers as the lower (step 3).
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
    // stored (the followed mark) drops only where the pixel moved on screen more than FollowedStillDisplayPixels and
    // one of the nine current texels around its position moves on screen otherwise than the pixel reprojected, by
    // more than FollowedHistoryMotionFraction of that motion: the followed surface still shows there while the
    // pixel's own surface passes, as the ground an avatar uncovers does under a camera that follows it. A background
    // texel, whose motion is the sentinel, is never the followed surface and is left out. A surface that stopped in
    // the world, or turned back through zero travel, reads its own pixels' history in place, whatever passes beside
    // it, or where the same surface shows moving with it under a camera that eases on after it, and keeps it. A
    // footprint whose carrying texels stored both marks, one the followed surface's and one the band's, reads as the
    // followed mark, the least of them, so its band history drops only as the followed mark's does. That takes a
    // fractional position across the outline, as under a camera that eases on. Keyed on a moving edge instead, the
    // drop never fired on ground under a perspective camera, whose neighbouring texels move apart on screen by their
    // depth step so that every ground pixel reads as a moving edge. Sparing a pixel whose 3x3 holds a surface farther
    // than its centre spared every ground pixel at a grazing angle, where each ground texel lies farther than the one
    // below it by more than the tolerance.
    // Any farther stored depth kept a sub-pixel edge from reading as revealed, and on its own it kept the ring of
    // pixels around a moving object's old place, whose footprint reaches past its edge, from ever being disoccluded. A
    // background pixel expects BackgroundLinearDepth, so anything stored nearer there was covering it. A static surface
    // on or behind last frame's camera had no history there. The footprint sits at the display pixel's previous
    // position, where history is read, while the expected depth comes from the reprojected texel's own point, the
    // dilated nearest in the 3x3 unless the pixel reprojected by its own. The same four depths tell step 6 whether a
    // pixel at a moving edge reads history a farther surface left: every one of them farther than the moving surface's
    // expected depth. That runs for a moving surface too, whose depth test is skipped, and costs no fetch beyond the
    // four a depth-tested pixel already takes.
    bool followedElsewhere = !carriedFollowed;
    if (carriedFollowed && !nearerMoved && motionPixels > FollowedStillDisplayPixels) {
        vec2 ownMotion = (uv - previousUv) * internalSize;
        ivec2 historyTexel = ivec2(floor(previousUv * internalSize + jitter));
        float apart = 0.0;
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                vec2 shown = texelFetch(sampler2D(MotionTex, LinearClamp),
                    temporalNeighbourTexel(historyTexel, x, y, maxTexel), 0).rg;
                if (!(abs(shown.x) > MotionSentinel)) apart = max(apart, length(shown * internalSize - ownMotion));
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
    float motionCap = mix(MaxAccumulation, MovingAccumulation,
        clamp(motionPixels / MotionAccumulationPixels, 0.0, 1.0));
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
    float lockValue = useHistory && !heldFromFarther ? max(historyState.y - LockDecay, 0.0) : 0.0;
    if (ridge) lockValue = 1.0;
    float motionRelease = clamp((motionPixels - LockMotionStartPixels)
        / (LockMotionEndPixels - LockMotionStartPixels), 0.0, 1.0);
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
                ivec2 texel = temporalNeighbourTexel(centreTexel, x, y, maxTexel);
                vec4 sceneColor = texelFetch(sampler2D(SceneColor, LinearClamp), texel, 0);
                vec3 ycc = rgbToYCoCg(temporalWeighted(sceneColor.rgb));
                float lanczosWeight = toFloat(temporalLanczosWeight(kernels, x + 1, y + 1));
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

    // Step 4 for a converged pixel: the display-sized reconstruction in proportion to its share and its weight, held
    // to the neighbourhood's range as the internal one is.
    float displayShare = temporalDisplayShare(useHistory, historyState.x, motionPixels, reactive);
    if (displayShare > 0.0) {
        vec4 display = temporalDisplayReconstruction(kernels, centreTexel, maxTexel);
        displayShare *= clamp(display.w / DisplayKernelFullWeight, 0.0, 1.0);
        current.xyz = mix(current.xyz, clamp(display.xyz / max(display.w, 1.0e-4), neighbourMin, neighbourMax),
            displayShare);
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
    }
}
