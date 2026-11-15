namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Shared verbatim by accumulation and validity. Surface, state and depth cannot take different rules.
    internal const string TemporalAccumulateReprojectionGlsl = @"    // Steps 1 and 3: where this pixel was last frame, by the surface its centre texel prepared, the dilated nearest or
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
    // carriedFollowed whether that was the followed surface's own. The shimmer record in blue and alpha is blended by
    // the same weights.
    vec2 fetchedState = vec2(0.0);
    vec2 fetchedRecord = vec2(0.0);
    bool carriedMoved = false;
    bool carriedBand = false;
    bool carriedFollowed = false;
    if (historyValid && onScreen) {
        vec2 statePosition = previousUv * displaySize - 0.5;
        ivec2 stateBase = ivec2(floor(statePosition));
        vec2 stateFraction = statePosition - vec2(stateBase);
        ivec2 lastState = ivec2(displaySize) - ivec2(1);
        vec4 s00 = texelFetch(sampler2D(HistoryConfidence, LinearClamp), clamp(stateBase, ivec2(0), lastState), 0);
        vec4 s10 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(1, 0), ivec2(0), lastState), 0);
        vec4 s01 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(0, 1), ivec2(0), lastState), 0);
        vec4 s11 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(1, 1), ivec2(0), lastState), 0);
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
        fetchedRecord = vec2(dot(bilinear, vec4(s00.z, s10.z, s01.z, s11.z)),
            dot(bilinear, vec4(s00.w, s10.w, s01.w, s11.w)));
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
    // At Quality, a slower-decaying lock counts as refreshed for one more frame at this half-LockDecay threshold.
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
";

    internal const string TemporalAccumulateFiniteGlsl = @"        bool finite = all(greaterThan(fetched, vec4(-HalfMax))) && all(lessThan(fetched, vec4(HalfMax)))
            && all(greaterThan(fetchedState, vec2(-HalfMax))) && all(lessThan(fetchedState, vec2(HalfMax)))
            && all(greaterThan(fetchedRecord, vec2(-HalfMax))) && all(lessThan(fetchedRecord, vec2(HalfMax)));
";
}
