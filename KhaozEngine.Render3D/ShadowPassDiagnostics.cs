using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Fixed per-cascade <see cref="int"/> storage for <see cref="ShadowPassDiagnostics"/>, one slot per
    /// <see cref="ShadowSettings.MaxCascades"/>. An inline array rather than an <c>int[]</c> so the snapshot stays a
    /// pure value: copying the struct copies the counts, and nothing the scene overwrites next frame is aliased into
    /// a diagnostics reading a consumer may hold for a frame or two.
    /// </summary>
    [InlineArray(ShadowSettings.MaxCascades)]
    internal struct ShadowCascadeCounts
    {
        int _element0;
    }

    /// <summary>
    /// Why the key-light shadow depth pass did or did not render on the last frame, and what it recorded when it
    /// did. A last-frame snapshot in the same shape as <see cref="Scene3DPassTimingsMs"/>, read via
    /// <see cref="Scene3D.LastShadowPassDiagnostics"/>.
    /// <para>
    /// Every field is captured from the decision the pass ACTED on, not recomputed afterwards. The reason bits are
    /// the exact five inputs handed to <see cref="Scene3D.ShadowDepthPassDirty"/>, and the counts are read inside
    /// <c>RenderShadowDepthPass</c> as it walks each cascade, so a span the pass skipped (its mesh was unloaded
    /// between the span build and the draw) is visible as a span count above the raw draw count rather than being
    /// silently folded away.
    /// </para>
    /// <para>
    /// A struct, always on, and allocation-free: reading the property copies value fields only, which is what lets a
    /// consumer sample it every frame into an F3-style telemetry line. It is default-valued (every field false or 0,
    /// including <see cref="Active"/>) whenever the resolved shadow tier is not <see cref="ShadowMode.ShadowMap"/>.
    /// </para>
    /// <para>
    /// <b>The counts are what the LAST FRAME recorded.</b> A skipped frame reports 0 spans and 0 draw calls, because
    /// a skipped frame records none: it reuses the persistent atlas. That is deliberately different from
    /// <see cref="Scene3D.ShadowCascadeSpanCount"/> and <see cref="Scene3D.ShadowCascadeCasterCount"/>, which keep
    /// reporting the last RENDERED pass's numbers across skipped frames. Sum the counts over frames to compare a
    /// scene's shadow load against its frame time, and read the reason bits beside them to see what kept the pass
    /// dirty.
    /// </para>
    /// </summary>
    public readonly struct ShadowPassDiagnostics
    {
        readonly ShadowCascadeCounts _rigidSpanCounts;

        /// <summary>Whether the resolved shadow tier was <see cref="ShadowMode.ShadowMap"/> this frame.</summary>
        public bool Active { get; }

        /// <summary>Whether the depth pass recorded caster draws this frame.</summary>
        public bool Rendered { get; }

        /// <summary>Whether the previous depth atlas was reused this frame.</summary>
        public bool Skipped { get; }

        /// <summary>Whether a prior depth atlas existed before this frame's decision. <c>false</c> on the first
        /// shadow frame, which is the one reason that forces a render on its own with every other bit clear.</summary>
        public bool HadPrevious { get; }

        /// <summary>Whether at least one animated skinned caster forced the pass to render. This bit is set from the
        /// caster count alone, with no pose compare: bone palettes are not hashed, so ANY skinned caster present
        /// dirties the pass on every frame, including a wholly stationary scene.</summary>
        public bool AnySkinnedCaster { get; }

        /// <summary>Whether the shadow atlas resolution changed since its last rendered pass.</summary>
        public bool ResolutionChanged { get; }

        /// <summary>Whether a fitted cascade matrix changed since the last rendered pass.</summary>
        public bool LightMatrixChanged { get; }

        /// <summary>Whether the rigid caster signature changed since the last rendered pass.</summary>
        public bool CasterDataChanged { get; }

        /// <summary>How many skinned casters were queued for the shadow pass.</summary>
        public int SkinnedCasterCount { get; }

        /// <summary>How many cascades were active this frame.</summary>
        public int CascadeCount { get; }

        /// <summary>How many rigid caster DRAW CALLS the pass actually issued this frame, across every cascade. This
        /// counts <c>DrawShadowCasterRun</c> invocations, so it is at or below <see cref="TotalRigidSpanCount"/>: a
        /// span whose mesh was unloaded between the span build and the draw is walked and then skipped. 0 on a
        /// skipped frame.</summary>
        public int RigidDrawCalls { get; }

        /// <summary>How many skinned caster DRAW CALLS the pass actually issued this frame, across every cascade
        /// (the CPU-skinned and GPU-skinned paths both count here). Roughly
        /// <see cref="SkinnedCasterCount"/> times <see cref="CascadeCount"/>, since a skinned caster is drawn into
        /// every cascade unconditionally. 0 on a skipped frame.</summary>
        public int SkinnedDrawCalls { get; }

        /// <summary>Every draw call the depth pass issued this frame: <see cref="RigidDrawCalls"/> plus
        /// <see cref="SkinnedDrawCalls"/>. 0 on a skipped frame.</summary>
        public int TotalDrawCalls => RigidDrawCalls + SkinnedDrawCalls;

        /// <summary>How many rigid caster spans the pass walked for <paramref name="cascade"/> this frame, after the
        /// per-cascade cull split the shared caster list. The unit the pass iterates, which is why it is reported
        /// per cascade: an even split across cascades and a lopsided one cost very differently for the same total.
        /// Out of range returns 0, and so does every cascade on a skipped frame.</summary>
        public int RigidSpanCount(int cascade)
            => (uint)cascade < (uint)ShadowSettings.MaxCascades ? _rigidSpanCounts[cascade] : 0;

        /// <summary>The sum of <see cref="RigidSpanCount"/> over this frame's active cascades. 0 on a skipped
        /// frame.</summary>
        public int TotalRigidSpanCount
        {
            get
            {
                int total = 0;
                for (int c = 0; c < ShadowSettings.MaxCascades; c++) total += _rigidSpanCounts[c];
                return total;
            }
        }

        internal ShadowPassDiagnostics(bool active, bool rendered, bool skipped, bool hadPrevious,
            bool anySkinnedCaster, bool resolutionChanged, bool lightMatrixChanged, bool casterDataChanged,
            int skinnedCasterCount, int cascadeCount, ReadOnlySpan<int> rigidSpanCounts,
            int rigidDrawCalls, int skinnedDrawCalls)
        {
            Active = active;
            Rendered = rendered;
            Skipped = skipped;
            HadPrevious = hadPrevious;
            AnySkinnedCaster = anySkinnedCaster;
            ResolutionChanged = resolutionChanged;
            LightMatrixChanged = lightMatrixChanged;
            CasterDataChanged = casterDataChanged;
            SkinnedCasterCount = skinnedCasterCount;
            CascadeCount = cascadeCount;
            RigidDrawCalls = rigidDrawCalls;
            SkinnedDrawCalls = skinnedDrawCalls;
            ShadowCascadeCounts counts = default;
            int n = Math.Min(rigidSpanCounts.Length, ShadowSettings.MaxCascades);
            for (int c = 0; c < n; c++) counts[c] = rigidSpanCounts[c];
            _rigidSpanCounts = counts;
        }
    }
}
