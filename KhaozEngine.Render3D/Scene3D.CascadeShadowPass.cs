using System.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    public sealed partial class Scene3D
    {
        /// <summary>Coordinate the persistent cascade atlas and the per-frame animated caster passes.</summary>
        float RenderCascadeShadowPass(IGpuCommandList cl, bool shadowMapActive, int skinnedCasterCount)
        {
            float shadowDepthMs = 0f;
            _shadowPassSkippedLastFrame = false;
            _lastShadowPassDiagnostics = default;
            FoliageShadowDrawCalls = 0;
            if (_shadowProbeActive)
            {
                _shadowProbeCounts.DirectionalActive = shadowMapActive;
                _shadowProbeCounts.DirectionalCascadeCount = shadowMapActive ? _cascadeCount : 0;
            }
            if (shadowMapActive)
            {
                long preparationStart = ShadowProbeTimestamp();
                long timingStart = EnableTiming ? Stopwatch.GetTimestamp() : 0;
                // Always set the receiver tail so the model + splat fragments sample the atlas (whether or not its depth
                // is re-rendered this frame). Then decide whether the depth pass must actually re-run: the atlas
                // persists across frames, so an unchanged static scene reuses it and skips every caster draw.
                SetShadowReceiverTail();
                BuildShadowCasterSpans(_shadowCasterRunsScratch, _shadowCasterModelsScratch);
                if (_shadowProbeActive) _shadowProbeCounts.DirectionalInvalidationCasters = _shadowCasterSpheres.Count;
                bool hadPrevious = _shadowPassRendered;
                bool anySkinnedCaster = skinnedCasterCount > 0;
                // A skinned caster dirties the pass while it EXISTS, so the frame the last one vanishes on is the
                // frame that has to lift its shadow off the atlas (issue #23). Nothing else here can see that: the
                // skinned casters are not in the rigid signature, and a frozen camera under a held sun leaves every
                // other input clean, so without this the atlas would be reused with the character still on it.
                bool skinnedCastersCleared = SkinnedCastersCleared(anySkinnedCaster);
                bool resolutionChanged = hadPrevious && _model.ShadowMap.Resolution != _lastShadowResolution;
                bool lightMatrixChanged = hadPrevious && ShadowCascadeVpsChanged(
                    _cascadeCpuVps, _cascadeCount, _lastCascadeCpuVps, _lastShadowCascadeCount);
                bool casterDataChanged = hadPrevious && (InteriorShadowChanged || ShadowCastersChanged(
                    _shadowCasterRunsScratch, _shadowCasterModelsScratch, _lastShadowCasterRuns, _lastShadowCasterModels));
                bool anyFoliageCaster = _foliageShadowDraws.Count > 0;
                bool dirty = FoliageShadowSelection.NeedsDepthPass(anyFoliageCaster, _lastAnyFoliageCaster)
                    || ShadowDepthPassDirty(
                    hadPrevious: hadPrevious,
                    anySkinnedCaster: anySkinnedCaster,
                    skinnedCastersCleared: skinnedCastersCleared,
                    resolutionChanged: resolutionChanged,
                    lightMatrixChanged: lightMatrixChanged,
                    casterDataChanged: casterDataChanged);
                _shadowProbeTicks.DirectionalPreparation = ShadowProbeElapsed(preparationStart, _shadowProbeTicks.DirectionalPreparation);
                long commitStart;
                if (dirty)
                {
                    // Split the one caster list into the sub-spans each cascade actually reaches, then draw. Only on
                    // a dirty pass: a skipped frame draws nothing, so it must not pay for the split either. The
                    // SIGNATURE above stays the FULL list on purpose - the cull is a pure function of the caster
                    // transforms (already in that signature) and the cascade matrices (already in the light-matrix
                    // compare), so it can never change the drawn set without one of those dirtying the pass first.
                    long selectionStart = ShadowProbeTimestamp();
                    BuildCascadeCasterSpans(_cascadeCount);
                    _shadowProbeTicks.DirectionalSelection = ShadowProbeElapsed(selectionStart, _shadowProbeTicks.DirectionalSelection);
                    long recordingStart = ShadowProbeTimestamp();
                    RenderShadowDepthPass(cl, _cascadeCasterSpans);
                    _shadowProbeTicks.DirectionalRecording = ShadowProbeElapsed(recordingStart, _shadowProbeTicks.DirectionalRecording);
                    commitStart = ShadowProbeTimestamp();
                    // What this pass just drew becomes next frame's reference (see Scene3D.ShadowDirtyState.cs).
                    CommitShadowDirtyState(anySkinnedCaster, anyFoliageCaster);
                }
                else
                {
                    _shadowPassSkippedLastFrame = true;
                    commitStart = ShadowProbeTimestamp();
                }
                // Recorded AFTER the decision so one snapshot describes one frame: the per-cascade rigid span counts
                // and the raw draw calls only exist once the pass has walked them, and a skipped frame must report
                // zero of both rather than the last rendered pass's numbers (issue #410).
                RecordShadowPassDiagnostics(dirty, hadPrevious, anySkinnedCaster, skinnedCastersCleared,
                    resolutionChanged, lightMatrixChanged, casterDataChanged, skinnedCasterCount, _cascadeCount);
                _shadowProbeTicks.DirectionalCommit = ShadowProbeElapsed(commitStart, _shadowProbeTicks.DirectionalCommit);
                if (_shadowProbeActive)
                {
                    _shadowProbeCounts.DirectionalRendered = dirty;
                    _shadowProbeCounts.DirectionalSkipped = !dirty;
                    _shadowProbeCounts.DirectionalRigidDrawCalls = _lastShadowPassDiagnostics.RigidDrawCalls;
                    _shadowProbeCounts.DirectionalSkinnedDrawCalls = _lastShadowPassDiagnostics.SkinnedDrawCalls;
                    _shadowProbeCounts.DirectionalFoliageDrawCalls = FoliageShadowDrawCalls;
                }
                if (EnableTiming) shadowDepthMs = ElapsedMs(timingStart);
            }
            else
                _model.ClearShadowUniforms();

            return shadowDepthMs;
        }
    }
}
