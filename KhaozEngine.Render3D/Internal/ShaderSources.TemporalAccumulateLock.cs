namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // The same literal rules compose Display unchanged and the Fine lock evaluator.
    internal const string TemporalAccumulateHistoryGlsl = @"        if (finite) {
            history = max(fetched, vec4(0.0));
            historyState = clamp(fetchedState, vec2(0.0), vec2(MaxStillAccumulation / MaxAccumulation, 1.0));
            historyRecord = fetchedRecord;
        } else {
            useHistory = false;
        }
    }
    // The history in the clip's space, and this frame's luma less it, the change the shimmer record averages.
    vec3 historyYcc = rgbToYCoCg(toWeighted(history.rgb));
    float lumaChange = current.x - historyYcc.x;

";
    internal const string TemporalAccumulateMotionCapGlsl = @"    // Step 8, first half: the accumulated sample weight's cap, lower as motion grows. The weight itself waits for the
    // shimmer score, after the shading release.
    float motionCap = mix(MaxAccumulation, MovingAccumulation,
        clamp(motionPixels / MotionAccumulationPixels, 0.0, 1.0));

";
    internal const string TemporalAccumulateLockGlsl = @"    // Step 6: thin features. A ridge through the centre texel refreshes the lock. Still flicker loses the jitter-cycle
    // rate unless visibility changed, and other pixels lose LockDecay. Large motion and reactive content release it,
    // and so does motion at an edge, where the pixel
    // holds a moving feature's history, not its own. A pixel that reprojected by its own motion and kept its history
    // holds its own, because where a moving surface showed last frame step 3 keeps a history only where no stored depth
    // carrying weight lies nearer than the pixel's own surface. So a still sub-texel feature beside a fast surface
    // keeps its lock, while one that restarted lets go of a ridge the moving surface's colour may have given it. At a
    // moving edge whose history a farther surface left, the lock read with that history belongs to that surface, and a
    // nearer surface crossing a held thin feature would carry it onward, so it is dropped before a ridge can refresh
    // it. A thin feature taking a ridge keeps its own lock, and so does one beside a still nearer surface,
    // which is no moving edge. The hold on the clip stays whole while the lock is at least 1 / LockHoldGain, so a
    // sub-texel feature missed for a few frames keeps its luma, and it lets go over the rest of the lock.
    float lockLoss = temporalStillLockLoss(historyRecord, lumaChange, motionPixels, reactive);
#ifdef TemporalShimmerVisibility
    if (lockLoss != LockDecay && temporalShimmerVisibilityChanged(uv, centreTexel, previousUv)) lockLoss = LockDecay;
#endif
    float lockValue = useHistory && !heldFromFarther ? max(historyState.y - lockLoss, 0.0) : 0.0;
    if (ridge) lockValue = 1.0;
    float releaseEdgeMotion = ownReprojected && useHistory ? 0.0 : edgeMotion;
    // With positive width the ordered barriers preserve bits. Finite arguments cannot make a NaN factor:
    // scaling may overflow, but each clamp returns [0, 1], so every keep factor and product is finite and
    // nonnegative. The helper therefore maps +0 to +0 exactly. Test bits and the actual selected edge argument,
    // keeping -0, nonzero locks and every non-finite argument on the unchanged ordered path.
    // Keep this selection through SPIR-V and HLSL so FXC branches around the ordered release.
    [[dont_flatten]] if (floatBitsToUint(lockValue) != 0u
        || (floatBitsToUint(motionPixels) & 0x7f800000u) == 0x7f800000u
        || (floatBitsToUint(reactive) & 0x7f800000u) == 0x7f800000u
        || (floatBitsToUint(releaseEdgeMotion) & 0x7f800000u) == 0x7f800000u) {
        lockValue = temporalReleaseLock(lockValue, motionPixels, reactive, releaseEdgeMotion);
    }
    // Preserve the colour release's carried-lock classification before visibility clears the stored lock.
    bool carriedLock = !ridge && lockValue > 0.0;
    // A visibility release only clears this lock and the hold it gives, so a lock whose bits are +0.0 runs no check:
    // clearing it would store the same bits and its hold is +0.0 already. The bits are tested, not the value, so a
    // -0.0, a denormal or a NaN lock still runs the check.
    float signedVisibilityChange = 0.0;
    uint visibilityPath = 0u;
    bool visibilityReleased = floatBitsToUint(lockValue) != 0u && useHistory && !surface.background
        && PreviousJitter.z > 0.5 && temporalVisibilityReleases(uv, centreTexel, maxTexel, previousUv, expectedDepth,
            surface.visibilityMask, signedVisibilityChange, visibilityPath);
    if (visibilityReleased) lockValue = 0.0;
";
}
