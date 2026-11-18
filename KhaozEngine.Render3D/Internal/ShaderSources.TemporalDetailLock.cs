namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // One emitted visibility decision keeps specialization and diagnostic rewrites unambiguous.
    internal const string TemporalDetailLockGlsl = @"
struct TemporalDetailLockState { float value; bool carried; bool visibility; float signedChange; uint path; };
TemporalDetailLockState detailEvaluateLock(vec2 historyState, vec2 historyRecord, float lumaChange,
    float motionPixels, float reactive, bool useHistory, bool heldFromFarther, bool ridge, bool ownReprojected,
    float edgeMotion, vec2 uv, ivec2 centreTexel, ivec2 maxTexel, vec2 previousUv, float expectedDepth,
    TemporalSurface surface) {
" + TemporalAccumulateLockGlsl + @"
    return TemporalDetailLockState(lockValue, carriedLock, visibilityReleased, signedVisibilityChange, visibilityPath);
}
bool detailCandidateLocked(vec4 fetched, vec2 fetchedState, vec2 fetchedRecord, vec4 current,
    float motionPixels, float reactive, bool useHistory, bool heldFromFarther, bool ridge, bool ownReprojected,
    float edgeMotion, vec2 uv, ivec2 centreTexel, ivec2 maxTexel, vec2 previousUv, float expectedDepth,
    TemporalSurface surface) {
    vec4 history = vec4(0.0);
    vec2 historyState = vec2(0.0);
    vec2 historyRecord = vec2(0.0);
    if (useHistory) {
" + TemporalAccumulateFiniteGlsl + TemporalAccumulateHistoryGlsl + @"
    return detailEvaluateLock(historyState, historyRecord, lumaChange, motionPixels, reactive, useHistory,
        heldFromFarther, ridge, ownReprojected, edgeMotion, uv, centreTexel, maxTexel, previousUv,
        expectedDepth, surface).value > 0.0;
}
// Preserve the diagnostic name in the accumulation without emitting a second visibility decision.
#define visibilityReleased detailChosenLock.visibility
";

    internal const string TemporalDetailAfterFetchGlsl = "\n" + TemporalAccumulateFiniteGlsl
        + TemporalAccumulateHistoryGlsl + TemporalAccumulateMotionCapGlsl + @"
    TemporalDetailLockState detailChosenLock = detailEvaluateLock(historyState, historyRecord, lumaChange,
        motionPixels, reactive, useHistory, heldFromFarther, ridge, ownReprojected, edgeMotion,
        uv, centreTexel, maxTexel, previousUv, expectedDepth, surface);
    float lockValue = detailChosenLock.value;
    bool carriedLock = detailChosenLock.carried;
    float signedVisibilityChange = detailChosenLock.signedChange;
    uint visibilityPath = detailChosenLock.path;
" + TemporalAccumulateAfterLockGlsl;
}
