namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The temporal resolve's PER-TEXEL PREPARATION (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3): every rule the
    /// resolve derives from one internal texel alone, as GLSL functions with no stage inputs or outputs. Both entry
    /// points call them. The fused resolve (<see cref="TemporalResolveFrag"/>) prepares its 3x3 and its centre texel
    /// inline, and the split's first pass (<see cref="TemporalPrepareFrag"/>) prepares each internal texel once into
    /// the targets its second pass reads (<see cref="TemporalSplitFormats"/>).
    /// <para>A program that includes it declares <c>SceneDepth</c>, <c>MotionTex</c>, <c>LinearClamp</c> and the
    /// <c>Resolve</c> block, and defines <c>temporalNarrowThere</c>, the depth the narrow test reads at a texel, before
    /// it (<see cref="TemporalNarrowBothGlsl"/> or <see cref="TemporalNarrowCurrentGlsl"/>).</para>
    /// </summary>
    internal static partial class ShaderSources
    {
        // ---- The narrow test's depth at a texel, for a program that binds this frame's inputs and last frame's
        //      stored depths: this frame's (temporalViewDepth) or the one last frame stored ----
        internal const string TemporalNarrowBothGlsl = @"
float temporalNarrowThere(ivec2 at, bool current) {
    return current ? temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), at, 0).rg,
            texelFetch(sampler2D(SceneDepth, LinearClamp), at, 0).r)
        : texelFetch(sampler2D(PrevDepth, LinearClamp), at, 0).r;
}
";

        // ---- The same for the split's first pass, which binds no stored depth and runs this frame's test alone ----
        internal const string TemporalNarrowCurrentGlsl = @"
float temporalNarrowThere(ivec2 at, bool current) {
    return temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), at, 0).rg,
        texelFetch(sampler2D(SceneDepth, LinearClamp), at, 0).r);
}
";

        // ---- The per-texel rules ----
        internal const string TemporalPrepareGlsl = @"
float temporalLuma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }
vec3 toWeighted(vec3 c) { return c / (1.0 + temporalLuma(c)); }
vec3 fromWeighted(vec3 w) { return w / max(1.0 - temporalLuma(w), 1.0e-4); }
vec3 rgbToYCoCg(vec3 c) {
    return vec3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b, 0.5 * c.r - 0.5 * c.b, -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}
vec3 yCoCgToRgb(vec3 c) { return vec3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z); }

// A texel's colour in the luma-weighted space the resolve blends in, held inside [0, HalfMax] first, since an infinity
// would become NaN in the weighting.
vec3 temporalWeighted(vec3 c) { return toWeighted(min(max(c, vec3(0.0)), vec3(HalfMax))); }

// Step 7: how much the transparent passes changed a texel, its weighted luma against the opaque copy's.
float temporalReactiveDifference(vec3 weightedColor, vec3 opaqueColor) {
    return abs(temporalLuma(weightedColor) - temporalLuma(temporalWeighted(opaqueColor)));
}

// A value rounded to the nearest half float, ties to even, and below the half's least normal value, 2^-14, to zero of
// its sign. It is written in integer steps because packHalf2x16 and unpackHalf2x16 are not one rounding on every
// backend: on NVIDIA Vulkan the fused pass's pair inline, the split's first pass's and a round to nearest even gave
// three different values (TemporalEntryIdentityGpuTests). Its result is a normal half or a zero, which a half-float
// target stores unchanged. Masks rather than a comparison flush the small values, which measured cheaper in the fused
// pass. Every value it rounds lies inside the half's range.
float temporalHalf(float v) {
    uint bits = floatBitsToUint(v);
    // The 13 low mantissa bits dropped, ties to even. A carry into the exponent is the next power of two.
    uint rounded = (bits + 0xfffu + ((bits >> 13) & 1u)) & 0xffffe000u;
    // All ones below 2^-14, where only the sign is kept.
    uint small = uint(int((bits & 0x7fffffffu) - 0x38800000u) >> 31);
    return uintBitsToFloat(rounded & (~small | 0x80000000u));
}

// A texel's weighted YCoCg and reactive difference as both entry points use them, rounded to half float, the precision
// the split stores them at, so the fused pass gathers what the split stores.
vec4 temporalPrepared(vec3 ycc, float reactiveDifference) {
    return vec4(temporalHalf(ycc.x), temporalHalf(ycc.y), temporalHalf(ycc.z), temporalHalf(reactiveDifference));
}

// THE 3x3's VISIT ORDER, for every loop over it in both entry points: a row at a time from the top, each row from
// the left, y outer and x inner from -1 to 1. temporalNeighbourTexel is the texel of step (x, y), clamped to the image.
// The fused pass and the split's second pass read the 3x3 one texel ahead: temporalFirstTexel is the texel the first
// step reads, and temporalNextTexel the one after step (x, y). The last step reads the last row's first texel again,
// inside the 3x3, so no read leaves it. The compiler keeps the loop rolled, so a texel read at the top of its own step
// stalls that step, while one read a step earlier arrives behind the arithmetic of the step before. The texels, and
// the order of every sum over them, are unchanged. The split's first pass reads each texel in its own step, which
// measured faster there.
ivec2 temporalNeighbourTexel(ivec2 centreTexel, int x, int y, ivec2 maxTexel) {
    return clamp(centreTexel + ivec2(x, y), ivec2(0), maxTexel);
}
ivec2 temporalFirstTexel(ivec2 centreTexel, ivec2 maxTexel) {
    return temporalNeighbourTexel(centreTexel, -1, -1, maxTexel);
}
ivec2 temporalNextTexel(ivec2 centreTexel, int x, int y, ivec2 maxTexel) {
    ivec2 next = x == 1 ? ivec2(-1, min(y + 1, 1)) : ivec2(x + 1, y);
    return temporalNeighbourTexel(centreTexel, next.x, next.y, maxTexel);
}

// Step 1: the nearest surface in the 3x3 carries the motion. Its texels are visited in the order above, and a tie keeps
// the first.
void temporalDilate(ivec2 texel, float viewDepth, inout float closestDepth, inout ivec2 closestTexel) {
    if (viewDepth < closestDepth) {
        closestDepth = viewDepth;
        closestTexel = texel;
    }
}

// Steps 1 and 3 for one texel's surface point: the depth the point had last frame if static, and whether it is. The
// expected depth and the moving-surface test use the texel's own point, its unjittered sample at its own depth, which
// is the point the motion target wrote that motion for. CurrentToPrevious takes it to last frame's view space as if
// static, and PreviousProjection gives the UV it had there. On a static surface that UV and the texel's own previous
// position agree up to float precision and the motion target's half-float rounding. More than
// MovingSurfaceInternalPixels apart, plus MovingSurfaceMotionFraction of the motion for that rounding, the surface
// moved, and skips the depth test. Under perspective so does one whose static point sat on or behind last frame's
// camera plane. Under orthographic that point keeps the test, which rejects it as not in front. travel is how far
// apart the two lie, in internal pixels, how far the surface moved against a static point where it stood, and it
// counts as beyond every bound for a point on or behind last frame's camera plane. Background reprojects from camera
// rotation alone (temporalPreviousUv) and keeps the test.
void temporalReprojectSurface(vec2 sampleInternal, vec2 motion, float depth, bool isBackground, vec2 internalSize,
    out float expectedDepth, out bool depthTested, out float travel) {
    expectedDepth = BackgroundLinearDepth;
    depthTested = true;
    travel = 0.0;
    if (!isBackground) {
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

// Whether the surface at a texel is a narrow feature: along a row or a column, the run of texels at its depth through
// it, apart from the depths either side by the disocclusion tolerance, nearer or farther, is at most two texels long,
// as a blade narrower than a texel is, or two side by side, whether against the sky, over ground or beside a rock. It
// reads the depths last frame stored, or with current this frame's (temporalNarrowThere).
bool temporalNarrowDepth(ivec2 texel, float depth, ivec2 maxTexel, bool current) {
    float limit = 1.0 - DisocclusionTolerance;
    bool run[8];   // left 1 and 2, right 1 and 2, up 1 and 2, down 1 and 2: the texel there is at this depth
    for (int i = 0; i < 8; i++) {
        int stride = (i & 1) + 1;
        ivec2 offset = i < 2 ? ivec2(-stride, 0) : i < 4 ? ivec2(stride, 0)
            : i < 6 ? ivec2(0, -stride) : ivec2(0, stride);
        float there = temporalNarrowThere(clamp(texel + offset, ivec2(0), maxTexel), current);
        run[i] = !(depth < there * limit || there < depth * limit);
    }
    bool narrowRow = !(run[0] && run[2]) && !(run[0] && run[1]) && !(run[2] && run[3]);
    bool narrowColumn = !(run[4] && run[6]) && !(run[4] && run[5]) && !(run[6] && run[7]);
    return narrowRow || narrowColumn;
}

// What every display pixel whose 3x3 is centred on a texel reprojects by, from that texel's 3x3 alone: the motion and
// whether it reprojects as background, by the camera's rotation (temporalPreviousUv), the expected depth and whether
// the depth test runs (step 3), steps 1 and 3's marks (nearerMoved, band, followed, ownReprojected), whether the fast
// feature is narrow (narrowMoving, step 5), and step 6's edge signal (movingEdge and edgeMotion).
struct TemporalSurface {
    vec2 motion; bool background; float expectedDepth; bool depthTested; bool nearerMoved; bool band; bool followed;
    bool ownReprojected; bool narrowMoving; bool movingEdge; float edgeMotion;
};

TemporalSurface temporalPrepareSurface(ivec2 centreTexel, ivec2 closestTexel, float closestDepth, ivec2 maxTexel) {
    vec2 internalSize = Sizes.xy;
    vec2 jitter = Jitter.xy;
    bool historyValid = Jitter.w > 0.5;

    // The nearest texel's sample position and motion, and the centre texel's own, read back as the 3x3 saw them. A
    // 3x3 with no depth below the starting bound took no nearest texel, and reprojects as background, with no motion.
    vec2 closestSample = vec2(centreTexel) + 0.5 - jitter;
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

    // Steps 1 and 3: the depth a static surface had last frame, from the dilated texel's own surface point
    // (temporalReprojectSurface).
    float expectedDepth;
    bool depthTested;
    float travel;
    temporalReprojectSurface(closestSample, closestMotion, closestDepth, closestIsBackground, internalSize,
        expectedDepth, depthTested, travel);

    // Step 6's edge signal, which step 3 also reads: where the centre texel moves otherwise than the dilated nearest
    // surface, a moving feature is passing over a background, and the pixel reads history along that feature's motion
    // rather than its own. A surface moving as a whole, a swaying blade or an avatar the camera follows, has no such
    // edge. A background centre moves by the camera's rotation alone. An edge counts once the two motions differ by
    // more than LockEdgeMotionFraction of the dilated motion, past the motion target's rounding, and by more than
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
                ? centreUv - vec2(centrePrevious.x / centrePrevious.w * 0.5 + 0.5,
                    0.5 - centrePrevious.y / centrePrevious.w * 0.5)
                : vec2(2.0);   // behind last frame's camera: as far apart as motion goes
        }
        edgeMotion = length((closestMotion - centreOwn) * internalSize);
        centreScreenMotion = length(centreOwn * internalSize);
    }
    bool movingEdge = edgeMotion > max(LockEdgeFloorInternalPixels,
        length(closestMotion * internalSize) * LockEdgeMotionFraction);

    // Steps 1 and 3 beside a fast edge: dilation carries the nearer surface's motion so its edge keeps its history, but
    // where that surface moved (its depth test is skipped), the centre texel lies on a farther surface, by more than
    // the disocclusion tolerance, and the two move more than DilationReachInternalPixels apart, that motion carries
    // this pixel beyond the edge's reach, onto another texel of the farther surface. The pixel then reprojects by its
    // centre texel's own motion and depth, so a pixel the nearer surface just uncovered finds it in the stored depths
    // and is disoccluded, and one it never covered keeps its own history. A still nearer surface keeps dilation
    // whatever the camera does: under a camera's translation, and against the sky, both surfaces are static and the
    // dilated history is valid. Where the nearer surface is also narrow in this frame's depth, step 5 gives the
    // pixel's history that surface's current colour.
    //
    // Step 1's band, under the reach: a pixel beside or on a wide nearer surface that moved in the world. nearerMoved
    // is that test: the dilated texel's own sample lands more than WorldMotionMetres from where a static point would
    // have been, in internal pixels at its depth through last frame's projection (one internal pixel spans 2 clipW /
    // (P00 width) metres), plus the motion target's rounding. A static surface's travel is the motion target's float
    // error on positions relative to the render origin, one distance in the world that more pixels show at a higher
    // resolution or a nearer depth, so the test is in metres. Beside the surface's edge the centre texel lies on a
    // farther surface that moves more on screen, as under a camera following the nearer one. That pixel's history
    // followed the edge, which stays put on screen while the farther surface passes under it, so it holds the edge's
    // colour over a mix of the farther surface. On the surface itself, where the centre texel moves with it, a pixel
    // whose surface travelled in the world more than FollowedTravelRatio times its motion on screen is followed, not
    // crossing a still view. Its history is its own colour, and a farther surface its lowest pixels touch, as the
    // ground under an avatar's feet lies within the disocclusion tolerance of them, takes that colour on where it is
    // uncovered. That pixel stores the followed mark rather than the band's, because once the surface stops its own
    // pixels read the same history and keep it (step 3). The band keeps the edge's anti-aliasing, and step 3 keeps a
    // pixel whose nearest surface did not move from taking either history on. A narrow nearer surface, a blade or a
    // line, is held by the thin-feature lock instead. A surface crossing a still view leaves its history where it
    // passed, as before: restarting the pixels it uncovers there would show a sub-texel feature's raw sample, brighter
    // than its converged value. Both read the one narrow test of the nearer surface, and the band reads the dilated
    // surface's travel before the pixel's own motion replaces it.
    bool centreFarther = centreDepth > closestDepth * (1.0 + DisocclusionTolerance);
    bool ownReprojected = !depthTested && edgeMotion > DilationReachInternalPixels && centreFarther;
    float closestScreenMotion = length(closestMotion * internalSize);
    bool nearerMoved = !depthTested || travel > WorldMotionMetres * PreviousProjection[0][0] * internalSize.x * 0.5
        / (CurrentDepth.x > 0.5 ? closestDepth : 1.0) + closestScreenMotion * MovingSurfaceMotionFraction;
    bool band = historyValid && !ownReprojected && nearerMoved
        && (movingEdge ? centreScreenMotion > closestScreenMotion && centreFarther
            : travel > FollowedTravelRatio * closestScreenMotion);
    bool nearerNarrow = (ownReprojected || band) && temporalNarrowDepth(closestTexel, closestDepth, maxTexel, true);
    bool narrowMoving = ownReprojected && nearerNarrow;
    band = band && !nearerNarrow;
    bool followed = band && !movingEdge;
    TemporalSurface surface;
    surface.motion = closestMotion;
    surface.background = closestIsBackground;
    if (ownReprojected) {
        temporalReprojectSurface(centreSample, centreMotion, centreDepth, centreIsBackground, internalSize,
            expectedDepth, depthTested, travel);
        surface.motion = centreMotion;
        surface.background = centreIsBackground;
    }
    // A background fixed to the screen (the starfield, Params.y) places its stars by pixel whatever the camera does,
    // so it reprojects in place, with zero motion, not by the camera's rotation. Its expected depth stays
    // BackgroundLinearDepth, so a star a mover uncovered still finds a nearer stored depth and drops its history.
    if (surface.background && Params.y > 0.5) {
        surface.motion = vec2(0.0);
        surface.background = false;
    }
    surface.expectedDepth = expectedDepth;
    surface.depthTested = depthTested;
    surface.nearerMoved = nearerMoved;
    surface.band = band;
    surface.followed = followed;
    surface.ownReprojected = ownReprojected;
    surface.narrowMoving = narrowMoving;
    surface.movingEdge = movingEdge;
    surface.edgeMotion = edgeMotion;
    return surface;
}

// The split stores a texel's surface for its second pass in three targets: the motion and the flags in a half-float
// target, which holds the motion target's own half floats and a whole number below 256 exactly, and the expected depth
// and the edge motion in single-float targets.
const int SurfaceBackground = 1;
const int SurfaceDepthTested = 2;
const int SurfaceNearerMoved = 4;
const int SurfaceBand = 8;
const int SurfaceFollowed = 16;
const int SurfaceOwnReprojected = 32;
const int SurfaceNarrowMoving = 64;
const int SurfaceMovingEdge = 128;

vec4 temporalStoreSurface(TemporalSurface s) {
    int flags = (s.background ? SurfaceBackground : 0) | (s.depthTested ? SurfaceDepthTested : 0)
        | (s.nearerMoved ? SurfaceNearerMoved : 0) | (s.band ? SurfaceBand : 0) | (s.followed ? SurfaceFollowed : 0)
        | (s.ownReprojected ? SurfaceOwnReprojected : 0) | (s.narrowMoving ? SurfaceNarrowMoving : 0)
        | (s.movingEdge ? SurfaceMovingEdge : 0);
    return vec4(s.motion, float(flags), 0.0);
}

TemporalSurface temporalStoredSurface(vec4 motionAndFlags, float expectedDepth, float edgeMotion) {
    int flags = int(motionAndFlags.z + 0.5);
    TemporalSurface s;
    s.motion = motionAndFlags.xy;
    s.background = (flags & SurfaceBackground) != 0;
    s.expectedDepth = expectedDepth;
    s.depthTested = (flags & SurfaceDepthTested) != 0;
    s.nearerMoved = (flags & SurfaceNearerMoved) != 0;
    s.band = (flags & SurfaceBand) != 0;
    s.followed = (flags & SurfaceFollowed) != 0;
    s.ownReprojected = (flags & SurfaceOwnReprojected) != 0;
    s.narrowMoving = (flags & SurfaceNarrowMoving) != 0;
    s.movingEdge = (flags & SurfaceMovingEdge) != 0;
    s.edgeMotion = edgeMotion;
    return s;
}
";
    }
}
