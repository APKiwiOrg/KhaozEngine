using System;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The instrument half of the shadow depth pass: the raw per-frame counters the pass writes as it records, and
    /// the one place <see cref="ShadowPassDiagnostics"/> is built from them.
    /// <para>
    /// It exists because the pass's cost and the pass's REASON are recorded in different places. The six dirty
    /// inputs are known before <c>RenderShadowDepthPass</c> runs, the per-cascade span counts and the raw draw calls
    /// only exist after it has walked them, and a frame that skipped recorded neither. Building the snapshot in one
    /// step at the end of the shadow decision is what keeps those halves consistent: every field in a given snapshot
    /// describes the same frame.
    /// </para>
    /// <para>
    /// The counters are plain fields plus one fixed-size array, all written only from inside the pass, so the
    /// instrument adds no allocation to a frame and cannot change which frames the pass renders. Issue #410 is the
    /// consumer of this: a field trace can tell a stationary scene that re-records because a skinned caster is
    /// present apart from one that re-records because the sun moved, without a debugger.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        // Written by RenderShadowDepthPass as it records, read once by RecordShadowPassDiagnostics at the end of the
        // frame's shadow decision. Fixed size and never reallocated, so the instrument allocates nothing per frame.
        readonly int[] _shadowPassRigidSpans = new int[ShadowSettings.MaxCascades];
        int _shadowPassRigidDraws;
        int _shadowPassSkinnedDraws;

        /// <summary>Zero this frame's recorded counters. Called at the top of <c>RenderShadowDepthPass</c>, so the
        /// numbers a snapshot carries are always the ones THIS pass recorded rather than an older pass's.</summary>
        void ResetShadowPassCounters()
        {
            Array.Clear(_shadowPassRigidSpans);
            _shadowPassRigidDraws = 0;
            _shadowPassSkinnedDraws = 0;
        }

        /// <summary>
        /// Publish this frame's <see cref="LastShadowPassDiagnostics"/> from the dirty inputs the decision was taken
        /// on plus what the pass recorded. <paramref name="rendered"/> is the decision itself, so
        /// <see cref="ShadowPassDiagnostics.Rendered"/> can never disagree with whether the pass ran.
        /// <para>
        /// A skipped frame reports zero counts rather than the last rendered pass's, because it recorded nothing.
        /// Reading the live counters would make a skipped frame look like it repainted the atlas, which is the exact
        /// question the instrument exists to answer.
        /// </para>
        /// </summary>
        void RecordShadowPassDiagnostics(bool rendered, bool hadPrevious, bool anySkinnedCaster,
            bool skinnedCastersCleared, bool resolutionChanged, bool lightMatrixChanged, bool casterDataChanged,
            int skinnedCasterCount, int cascadeCount)
        {
            ReadOnlySpan<int> spans = rendered ? _shadowPassRigidSpans : ReadOnlySpan<int>.Empty;
            _lastShadowPassDiagnostics = new ShadowPassDiagnostics(
                active: true, rendered: rendered, skipped: !rendered, hadPrevious: hadPrevious,
                anySkinnedCaster: anySkinnedCaster, skinnedCastersCleared: skinnedCastersCleared,
                resolutionChanged: resolutionChanged,
                lightMatrixChanged: lightMatrixChanged, casterDataChanged: casterDataChanged,
                skinnedCasterCount: skinnedCasterCount, cascadeCount: cascadeCount,
                rigidSpanCounts: spans,
                rigidDrawCalls: rendered ? _shadowPassRigidDraws : 0,
                skinnedDrawCalls: rendered ? _shadowPassSkinnedDraws : 0);
        }
    }
}
