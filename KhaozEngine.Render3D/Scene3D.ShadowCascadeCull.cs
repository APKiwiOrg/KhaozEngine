using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The per-cascade half of the shadow depth pass: which of this frame's casters each cascade actually draws.
    /// <para>
    /// Before this, <c>RenderShadowDepthPass</c> drew EVERY caster span into EVERY cascade, so a cascade 0 fitted to
    /// a ~19 m slice sphere still rasterized the whole world's caster set. On an MMO-shaped frame (Ruinborne: 13.6k
    /// instanced foliage casters plus a few thousand props over a 500 m island, four cascades) that is four full
    /// re-rasterizations of everything, every frame the pass is dirty, for a shadow that can only land inside four
    /// small light-space rects.
    /// </para>
    /// <para>
    /// Each cascade now splits the shared caster list into the sub-spans that actually reach it, tested with
    /// <see cref="ShadowCascadeCull"/> (light-space XY plus the far plane, never the near plane - see that type for
    /// why XY culling is exact for a directional light and why the near plane must stay out of it). The FULL span
    /// list is still what the dirty check hashes, so the atlas-reuse contract is untouched: culling is a pure
    /// function of the caster transforms (in the signature) and the cascade matrices (in the light-matrix compare),
    /// so nothing can change the drawn set without one of those already dirtying the pass.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>
        /// How many consecutive REJECTED instances a kept sub-span will draw through rather than split at.
        /// <para>
        /// Casters arrive in submission order, which for a streamed world is chunk-coherent but not sorted by
        /// cascade, so an exact split (gap 0) can shatter one 13.6k-instance draw into hundreds of 1-2 instance
        /// draws. Past a point the per-draw encode cost outweighs the instances saved: rasterizing a handful of
        /// extra casters that clip away is far cheaper than another draw call. This is the trade, and it is a
        /// PERFORMANCE knob only - drawing an instance the cull rejected is always correct (it is exactly what the
        /// pass did before this file existed), so a wrong value here can never change a pixel.
        /// </para>
        /// <para>
        /// The value is measured, not guessed: <c>ShadowCascadeCullPerfGpuTests</c> sweeps it over an MMO-shaped
        /// caster load (16.5k casters, four cascades at 2048, Metal on Apple silicon). Two results out of it are
        /// solid across repeated runs. An EXACT split (gap 0) shatters 5 instanced draws into 2026 and costs about
        /// 1.7 ms of shadow pass against the 1.4 ms of not culling at all, so it is worse than doing nothing. And
        /// everything from 8 to 64 is a shallow bowl at roughly 1.1 to 1.3 ms whose spread is inside the harness's
        /// own run-to-run noise, so there is no sharp optimum to find. 8 is the pick from that flat region: at or
        /// near the best in every run, and the fewest rasterized instances of the viable gaps, which is the right
        /// side to err on because heavier caster geometry than the harness's shifts the balance further toward
        /// instances and away from draw calls.
        /// </para>
        /// </summary>
        internal const int DefaultShadowCullMergeGap = 8;

        /// <summary>The live <see cref="DefaultShadowCullMergeGap"/>. Settable so the perf harness can sweep it in
        /// one process. Not public: it changes no output, so it is not a consumer-facing knob.</summary>
        internal int ShadowCullMergeGap { get; set; } = DefaultShadowCullMergeGap;

        // This frame's per-caster world bounding spheres (xyz = centre, w = radius), index-aligned to the flat
        // caster instance list BuildShadowCasterSpans produces, so a cascade's split can walk spans and spheres
        // together. Rebuilt every shadow frame beside that list, reused (Cleared, never per-frame allocated).
        readonly List<Vector4> _shadowCasterSpheres = new();

        // One per-instance cascade bitmask, index-aligned to the same list. Grown, never per-frame allocated.
        byte[] _shadowCasterMasks = Array.Empty<byte>();

        // Per-cascade drawn span lists, rebuilt on every dirty pass. Reused, never per-frame allocated.
        readonly List<ShadowCasterSpan>[] _cascadeCasterSpans = CreateCascadeSpanLists();
        readonly int[] _cascadeCasterCounts = new int[ShadowSettings.MaxCascades];
        int _shadowCasterCandidateCount;

        static List<ShadowCasterSpan>[] CreateCascadeSpanLists()
        {
            var lists = new List<ShadowCasterSpan>[ShadowSettings.MaxCascades];
            for (int i = 0; i < lists.Length; i++) lists[i] = new List<ShadowCasterSpan>();
            return lists;
        }

        /// <summary>
        /// Whether each cascade draws only the casters that reach it (default, and the whole point of this file) or
        /// every caster unconditionally (the pre-cull behaviour). Output is identical either way - the cull only
        /// drops geometry the rasterizer would have clipped - so this exists as a kill switch and as the A/B lever
        /// the pixel-identity tests compare against, NOT as a quality setting.
        /// </summary>
        public bool ShadowCascadeCulling { get; set; } = true;

        /// <summary>How many rigid caster instances the last rendered depth pass had to consider, before any
        /// per-cascade culling. Zero until a depth pass has rendered. Diagnostics.</summary>
        public int ShadowCasterCandidateCount => _shadowCasterCandidateCount;

        /// <summary>How many rigid caster instances the last rendered depth pass actually drew into
        /// <paramref name="cascade"/>. With culling off this equals <see cref="ShadowCasterCandidateCount"/> for
        /// every cascade. Out-of-range returns 0. Diagnostics.</summary>
        public int ShadowCascadeCasterCount(int cascade)
            => (uint)cascade < (uint)_cascadeCasterCounts.Length ? _cascadeCasterCounts[cascade] : 0;

        /// <summary>How many rigid caster SPANS the last rendered depth pass walked for <paramref name="cascade"/>.
        /// The counterpart of <see cref="ShadowCascadeCasterCount"/>: culling trades this up (a split run becomes
        /// several spans) to trade that down, which is the whole reason the merge gap exists. A span whose mesh was
        /// unloaded issues no draw, so draws can sit below this count: see
        /// <see cref="ShadowPassDiagnostics.RigidDrawCalls"/> for the issued-draw number. Diagnostics.</summary>
        public int ShadowCascadeSpanCount(int cascade)
            => (uint)cascade < (uint)_cascadeCasterSpans.Length ? _cascadeCasterSpans[cascade].Count : 0;

        /// <summary>
        /// Test every caster sphere against every cascade in ONE pass, writing a bitmask per instance (bit
        /// <c>c</c> set when the caster reaches cascade <c>c</c>). <paramref name="masks"/> must be at least as long
        /// as <paramref name="spheres"/>.
        /// <para>
        /// One pass rather than one per cascade, for two reasons. The sphere array is the big buffer here (16 bytes
        /// times the caster count, hundreds of KB on an MMO frame) and walking it once instead of four times keeps
        /// it in cache. And each cascade's test structure stays in registers across all four tests of an instance,
        /// so the per-instance work is four cheap dot-product triples rather than four separate strided walks.
        /// Pure + headless-testable.
        /// </para>
        /// </summary>
        internal static void ComputeCascadeMasks(ReadOnlySpan<Vector4> spheres, ReadOnlySpan<ShadowCascadeCull> culls,
            int count, Span<byte> masks)
        {
            int n = Math.Min(spheres.Length, masks.Length);
            int c = Math.Min(count, culls.Length);
            for (int i = 0; i < n; i++)
            {
                Vector4 b = spheres[i];
                var centre = new Vector3(b.X, b.Y, b.Z);
                int m = 0;
                for (int k = 0; k < c; k++)
                    if (culls[k].Intersects(centre, b.W)) m |= 1 << k;
                masks[i] = (byte)m;
            }
        }

        /// <summary>
        /// Split <paramref name="source"/> (this frame's full caster draw list, in draw order) into the sub-spans
        /// that reach cascade <paramref name="cascade"/>, appending them to <paramref name="dst"/> in the same
        /// order. <paramref name="masks"/> is <see cref="ComputeCascadeMasks"/>'s output, one byte per caster
        /// INSTANCE in the same draw order, so the walk consumes it linearly alongside the spans.
        /// <para>
        /// A rejected instance ends the kept sub-span only once <paramref name="mergeGap"/> consecutive rejections
        /// have accumulated (see <see cref="ShadowCullMergeGap"/>): below that the span draws straight through them.
        /// Keeping a rejected instance is always safe, so <paramref name="mergeGap"/> trades draw calls for
        /// rasterized instances and never fidelity. Pure + headless-testable, and deliberately the ONE definition of
        /// what a cascade draws.
        /// </para>
        /// </summary>
        internal static void BuildCascadeSpans(ReadOnlySpan<ShadowCasterSpan> source, ReadOnlySpan<byte> masks,
            int cascade, int mergeGap, List<ShadowCasterSpan> dst)
        {
            dst.Clear();
            uint gapLimit = (uint)Math.Max(0, mergeGap);
            int bit = 1 << cascade;
            int slot = 0;
            foreach (ShadowCasterSpan span in source)
            {
                uint keepStart = 0, keepLen = 0, gap = 0;
                for (uint s = 0; s < span.Count; s++)
                {
                    // A mask array shorter than the span list means the two were built from different frames, which
                    // is a bug, not a runtime condition. Keep the instance rather than silently dropping a caster.
                    bool keep = slot >= masks.Length || (masks[slot] & bit) != 0;
                    slot++;
                    if (keep)
                    {
                        if (keepLen == 0) { keepStart = span.Start + s; keepLen = 1; }
                        else keepLen += gap + 1;   // absorb the small gap we drew through
                        gap = 0;
                        continue;
                    }
                    if (keepLen == 0) continue;    // nothing open yet: rejected instances before the first keep are free
                    if (++gap > gapLimit)
                    {
                        dst.Add(span with { Start = keepStart, Count = keepLen });
                        keepLen = 0;
                        gap = 0;
                    }
                }
                if (keepLen > 0) dst.Add(span with { Start = keepStart, Count = keepLen });
            }
        }

        /// <summary>
        /// Fill <see cref="_cascadeCasterSpans"/> for this frame's <paramref name="count"/> cascades from the full
        /// caster list in <c>_shadowCasterRunsScratch</c>. Called only on a dirty pass (a skipped frame reuses the
        /// atlas and draws nothing, so it must not pay for this either). The cull runs against the ABSOLUTE cascade
        /// fits, matching the absolute instance transforms the spheres were built from.
        /// </summary>
        void BuildCascadeCasterSpans(int count)
        {
            int instances = _shadowCasterSpheres.Count;
            _shadowCasterCandidateCount = instances;
            ReadOnlySpan<ShadowCasterSpan> source = CollectionsMarshal.AsSpan(_shadowCasterRunsScratch);

            if (ShadowCascadeCulling)
            {
                if (_shadowCasterMasks.Length < instances)
                    _shadowCasterMasks = new byte[Math.Max(instances, _shadowCasterMasks.Length * 2)];
                Span<ShadowCascadeCull> culls = stackalloc ShadowCascadeCull[ShadowSettings.MaxCascades];
                int resolution = _model.ShadowMap.Resolution;
                for (int c = 0; c < count; c++)
                    culls[c] = ShadowCascadeCull.FromLightViewProj(_cascadeCpuVpsAbsolute[c], resolution);
                ComputeCascadeMasks(CollectionsMarshal.AsSpan(_shadowCasterSpheres), culls, count,
                    _shadowCasterMasks.AsSpan(0, instances));
            }

            for (int c = 0; c < count; c++)
            {
                List<ShadowCasterSpan> dst = _cascadeCasterSpans[c];
                if (ShadowCascadeCulling)
                    BuildCascadeSpans(source, _shadowCasterMasks.AsSpan(0, instances), c, ShadowCullMergeGap, dst);
                else
                {
                    dst.Clear();
                    dst.AddRange(_shadowCasterRunsScratch);
                }
                int drawn = 0;
                foreach (ShadowCasterSpan span in CollectionsMarshal.AsSpan(dst)) drawn += (int)span.Count;
                _cascadeCasterCounts[c] = drawn;
            }
            for (int c = count; c < _cascadeCasterCounts.Length; c++) _cascadeCasterCounts[c] = 0;
        }
    }
}
