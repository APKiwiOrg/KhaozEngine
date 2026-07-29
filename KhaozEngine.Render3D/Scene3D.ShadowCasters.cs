using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>How one queued rigid instance takes part in the key light's shadow depth pass (issue #287).
    /// Classified once per frame, right where the instances are grouped, and consumed by the depth pass.</summary>
    enum ShadowCastKind : byte
    {
        /// <summary>Opted out: the instance draws and receives shadows, but writes no depth, so it casts nothing.</summary>
        None = 0,
        /// <summary>The unchanged default: a solid caster drawn through the plain depth-only pipeline.</summary>
        Opaque = 1,
        /// <summary>A caster carrying a rigid dissolve: drawn through the dissolve-aware depth pipeline, so its
        /// shadow erodes with the same noise mask that erodes the mesh instead of staying solid to the cull edge.</summary>
        Dissolving = 2,
        /// <summary>A dissolving caster whose SHADOW dither is inverted (issue #391): drawn through the inverted
        /// dissolve depth pipeline, which keeps exactly what <see cref="Dissolving"/> discards. The merged half of an
        /// HLOD crossfade, so the two halves cover the mask between them instead of nesting.</summary>
        DissolvingInverted = 3,
    }

    /// <summary>
    /// The shadow-caster half of <see cref="Scene3D"/>: which queued instances write into the key light's cascade
    /// atlas, and the depth pass that draws them.
    /// <para>
    /// Two policies live here, both opt-in and both inert by default. A per-instance <c>castsShadows</c> flag
    /// (issue #287) keeps chosen geometry out of the depth pass entirely - what a consumer sets on a dense
    /// decorative layer (ground cover, understory) whose hundreds of small shadows cost more than they read. And a
    /// caster carrying the 14.5.0 rigid dissolve is drawn through the dissolve-aware depth pipeline, so its shadow
    /// thins as it fades: before this, a prop at 85 percent dissolve still cast a fully solid shadow, which then
    /// popped out at the hard cull radius, and across an HLOD crossfade band the individual props AND the merged
    /// mesh both cast at full strength, roughly doubling shadow density.
    /// </para>
    /// <para>
    /// Across an HLOD crossfade the two halves' shadow dithers are COMPLEMENTARY, which is what
    /// <see cref="ShadowCastKind.DissolvingInverted"/> exists for (issue #391). It was not always: both halves ran
    /// the same "discard where mask &lt; threshold" test, at thresholds t and 1 - t, and those keep-sets NEST rather
    /// than complement (for t &lt; 0.5 one contains the other), so the union bottomed out at half the mask at band
    /// centre and the canopy shadow visibly thinned mid-band. The merged half now inverts its test, so the union is
    /// the whole mask at every t while each end stays continuous with the single-half draws that bracket the band.
    /// </para>
    /// <para>
    /// Everything here classifies in ABSOLUTE space and reads the already-uploaded instance buffer, so the depth
    /// pass still costs no second upload. A frame that queues no dissolve and opts nothing out classifies every
    /// instance <see cref="ShadowCastKind.Opaque"/>, produces exactly one span per mesh run, and issues the same
    /// draws in the same order as the pre-policy pass.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>One contiguous span of same-kind casters inside a mesh run: the draw unit of the depth pass, and
        /// (minus <see cref="Start"/>, which is implied by draw order) the per-frame caster signature the dirty check
        /// compares. An all-opaque frame yields one span per run, so this is the pre-policy run list exactly.</summary>
        internal readonly record struct ShadowCasterSpan(int Index, int Generation, uint Start, uint Count, ShadowCastKind Kind);

        /// <summary>One captured caster instance: its uploaded world matrix plus the dissolve threshold driving the
        /// depth-pass discard. The dissolve rides along because it CHANGES the recorded depth (a fading caster
        /// records fewer fragments), so a dissolve that moves while the light matrices and transforms hold still
        /// must still re-render the atlas.</summary>
        internal readonly record struct ShadowCasterInstance(Matrix4x4 Model, float Dissolve);

        // Per-instance caster classification, index-aligned to _instanceData (filled by GroupInstances, which is the
        // one place that knows each uploaded slot's source instance). Reused, never per-frame allocated.
        readonly List<ShadowCastKind> _instanceCastKinds = new();

        /// <summary>Queue one instance with the shadow-caster opt-out (issue #287): <paramref name="castsShadows"/>
        /// false draws it exactly like the <see cref="Draw(MeshHandle, Matrix4x4, Color, Material)"/> overload but
        /// keeps it out of the shadow depth pass, so it casts nothing while still RECEIVING shadows. The knob is
        /// per-instance and CPU-side: no pipeline switch, no change to the uploaded instance bytes, and a
        /// <c>true</c> value is byte-identical to the material overload.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material, bool castsShadows)
            => _instances.Add(mesh, world, tint, material, 0f, 0f, default, castsShadows);

        /// <summary>The dissolve overload plus the shadow-caster opt-out: as
        /// <see cref="Draw(MeshHandle, Matrix4x4, Color, Material, float, float, Color)"/>, but
        /// <paramref name="castsShadows"/> false keeps this instance out of the depth pass. With it left true a
        /// positive <paramref name="dissolve"/> now also thins the instance's SHADOW by the same noise mask that
        /// thins the mesh (issue #287), so a fading prop stops casting a solid shadow under an almost-invisible
        /// caster.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolve, float edgeWidth, Color edgeColor, bool castsShadows)
            => _instances.Add(mesh, world, tint, material, dissolve, edgeWidth, edgeColor, castsShadows);

        /// <summary>The dissolve + opt-out overload plus the inverted SHADOW dither (issue #391):
        /// <paramref name="invertShadowDissolve"/> true records this instance's depth through the inverted dissolve
        /// pipeline, which keeps exactly what the plain one discards. Pass it on ONE of two instances dithering at
        /// mirrored thresholds (an HLOD crossfade's merged half against its fading props) so their shadows cover the
        /// noise mask between them instead of nesting, which is what left the shadow at half density mid-band. It
        /// changes the SHADOW only: same uploaded instance bytes, same colour pass, and <c>false</c> is
        /// byte-identical to the overload above.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolve, float edgeWidth, Color edgeColor, bool castsShadows, bool invertShadowDissolve)
            => _instances.Add(mesh, world, tint, material, dissolve, edgeWidth, edgeColor, castsShadows, invertShadowDissolve);

        /// <summary>How one queued instance participates in the depth pass: opted out, plainly, or dissolving. Pure
        /// (no scene state), so the classification is unit-testable and stays the single definition both
        /// <see cref="GroupInstances"/> and the tests read.</summary>
        internal static ShadowCastKind ClassifyCaster(in SceneInstances.Instance instance)
            => !instance.CastsShadows ? ShadowCastKind.None
             : !instance.Dissolving ? ShadowCastKind.Opaque
             : instance.InvertShadowDissolve ? ShadowCastKind.DissolvingInverted
             : ShadowCastKind.Dissolving;

        /// <summary>
        /// Append the maximal contiguous same-kind caster spans of one mesh run to <paramref name="spans"/>, skipping
        /// <see cref="ShadowCastKind.None"/> stretches entirely (that geometry is not drawn into the atlas at all).
        /// <paramref name="kinds"/> is the per-instance classification, index-aligned to the uploaded instance array;
        /// an EMPTY list means "unclassified", which yields the whole run as one opaque span (the pre-policy shape).
        /// Pure + headless-testable: this is the one definition of what the depth pass draws, so the pass and the
        /// caster signature it is compared against can never disagree about it.
        /// </summary>
        internal static void AppendCasterSpans(int meshIndex, int generation, uint start, uint count,
            IReadOnlyList<ShadowCastKind> kinds, List<ShadowCasterSpan> spans)
        {
            if (count == 0) return;
            if (kinds.Count == 0)
            {
                spans.Add(new ShadowCasterSpan(meshIndex, generation, start, count, ShadowCastKind.Opaque));
                return;
            }
            uint spanStart = start;
            uint spanLen = 0;
            ShadowCastKind spanKind = ShadowCastKind.None;
            for (uint s = 0; s < count; s++)
            {
                uint slot = start + s;
                ShadowCastKind kind = slot < (uint)kinds.Count ? kinds[(int)slot] : ShadowCastKind.Opaque;
                if (spanLen > 0 && kind == spanKind) { spanLen++; continue; }
                if (spanLen > 0) spans.Add(new ShadowCasterSpan(meshIndex, generation, spanStart, spanLen, spanKind));
                if (kind == ShadowCastKind.None) { spanLen = 0; continue; }   // opted out: no span at all
                spanStart = slot; spanLen = 1; spanKind = kind;
            }
            if (spanLen > 0) spans.Add(new ShadowCasterSpan(meshIndex, generation, spanStart, spanLen, spanKind));
        }

        /// <summary>
        /// Build this frame's caster draw list into <paramref name="spans"/> + <paramref name="instances"/>, in the
        /// EXACT order <see cref="RenderShadowDepthPass"/> draws it. Terrain (splat) meshes and stale-handle runs are
        /// excluded, matching what the pass draws, and so is anything the consumer opted out of casting. The same two
        /// lists are the per-frame caster SIGNATURE the dirty check compares against the last rendered pass (see
        /// <see cref="ShadowCastersChanged"/>), so a caster set, transform, opt-out or dissolve that moved
        /// re-renders the atlas. Reused buffers (Cleared, not reallocated), so the per-frame check stays
        /// allocation-free.
        /// <para>
        /// The same walk also captures each caster's WORLD bounding sphere into <c>_shadowCasterSpheres</c>,
        /// index-aligned to <paramref name="instances"/>, which is what the per-cascade cull then tests (see
        /// Scene3D.ShadowCascadeCull.cs). Doing it here means one sphere transform per caster per frame rather than
        /// one per caster PER CASCADE, and it is the only place a slot's mesh bounds and its world matrix are both
        /// already in hand.
        /// </para>
        /// </summary>
        void BuildShadowCasterSpans(List<ShadowCasterSpan> spans, List<ShadowCasterInstance> instances)
        {
            spans.Clear();
            instances.Clear();
            _shadowCasterSpheres.Clear();
            foreach (var run in _runs)
            {
                if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                var m = _meshes[run.Mesh.Index];
                if (m is not { } mesh) continue;
                if (mesh.SplatMaterial >= 0) continue;   // terrain does not cast (receive-only) - matches the depth pass
                int first = spans.Count;
                AppendCasterSpans(run.Mesh.Index, run.Mesh.Generation, run.Start, run.Count, _instanceCastKinds, spans);
                for (int i = first; i < spans.Count; i++)
                {
                    ShadowCasterSpan span = spans[i];
                    for (uint s = 0; s < span.Count; s++)
                    {
                        ModelRenderer.InstanceData d = _instanceData[(int)(span.Start + s)];
                        instances.Add(new ShadowCasterInstance(d.Model, d.Dissolve.X));
                        mesh.Bounds.WorldSphere(d.Model, out Vector3 bc, out float br);
                        _shadowCasterSpheres.Add(new Vector4(bc, br));
                    }
                }
            }
        }

        /// <summary>
        /// Decide whether the shadow depth pass must re-render this frame, or whether the persistent depth map from
        /// the last rendered pass is still correct and can be reused. Dirty when ANY shadow-relevant input changed
        /// since that pass (the depth map is NOT re-rendered otherwise, which is the whole point: a static scene
        /// under a static sun pays the depth pass once). There is no valid previous map
        /// (<paramref name="hadPrevious"/> false, e.g. the first shadow frame). <paramref name="anySkinnedCaster"/>
        /// is present (a skinned caster's bone pose can animate every frame, and hashing bone palettes is not worth
        /// it - any skinned caster forces a re-render). The map resolution changed
        /// (<paramref name="resolutionChanged"/>, which also reallocates the target). The fitted light matrix changed
        /// (<paramref name="lightMatrixChanged"/>). The rigid caster set / world transforms / dissolves changed
        /// (<paramref name="casterDataChanged"/>). Otherwise the previous depth map is still correct and the pass is
        /// skipped (the map is reused, NOT cleared).
        /// </summary>
        internal static bool ShadowDepthPassDirty(bool hadPrevious, bool anySkinnedCaster,
            bool resolutionChanged, bool lightMatrixChanged, bool casterDataChanged)
            => !hadPrevious || anySkinnedCaster || resolutionChanged || lightMatrixChanged || casterDataChanged;

        /// <summary>
        /// Pure sequence compare of two captured shadow-caster signatures (each a per-span mesh handle + instance
        /// count + cast kind list, and the flat per-instance world-matrix + dissolve list, in draw order). Returns
        /// <c>true</c> when they DIFFER, so the depth map must re-render. Both signatures are captured by
        /// <see cref="BuildShadowCasterSpans"/> in the exact draw order of <see cref="RenderShadowDepthPass"/>, so an
        /// equal signature proves the same casters, at the same transforms, with the same dissolves and the same
        /// opt-outs, as the last rendered pass. No GPU, headless-testable.
        /// </summary>
        internal static bool ShadowCastersChanged(
            List<ShadowCasterSpan> spansA, List<ShadowCasterInstance> instancesA,
            List<ShadowCasterSpan> spansB, List<ShadowCasterInstance> instancesB)
        {
            if (spansA.Count != spansB.Count || instancesA.Count != instancesB.Count) return true;
            for (int i = 0; i < spansA.Count; i++) if (spansA[i] != spansB[i]) return true;
            for (int i = 0; i < instancesA.Count; i++) if (instancesA[i] != instancesB[i]) return true;
            return false;
        }

        /// <summary>
        /// Pure compare of two per-cascade fitted-matrix sets (the CPU pre-clip-correct <see cref="Internal.ShadowMapMath.BuildLightViewProj"/>
        /// outputs, cascade 0..count-1 in order). Returns <c>true</c> when they DIFFER (a different count, or ANY
        /// cascade's matrix moved), so the depth pass must re-render - a camera pan past a texel or a moving sun (which
        /// re-fits every cascade) both trip it, while a fully static scene compares equal and reuses the persistent
        /// atlas. No GPU, headless-testable (the day/night dirty-tracking guarantee, now across all cascades).
        /// </summary>
        internal static bool ShadowCascadeVpsChanged(ReadOnlySpan<Matrix4x4> a, int aCount, ReadOnlySpan<Matrix4x4> b, int bCount)
        {
            if (aCount != bCount) return true;
            for (int i = 0; i < aCount; i++) if (a[i] != b[i]) return true;
            return false;
        }

        /// <summary>
        /// Render the key-light cascaded shadow depth pass for this frame using the already-fitted cascade DEPTH
        /// matrices in <see cref="_cascadeDepthVps"/> (from <see cref="SetShadowReceiverTail"/>): clear the atlas, then
        /// draw every skinned caster plus each cascade's REACHING rigid casters into that cascade's atlas column
        /// (reusing the already-uploaded instance/skinned buffers). The rigid casters come from
        /// <paramref name="spansPerCascade"/>, this frame's per-cascade caster draw lists (see
        /// <see cref="BuildCascadeCasterSpans"/>, which splits the one list <see cref="BuildShadowCasterSpans"/>
        /// built): terrain (splat meshes) do NOT cast (model-only casting -
        /// terrain self-shadowing is visually negligible in the test scenes and the flat MMO ground has no overhangs),
        /// and neither does anything the consumer opted out of. Terrain always RECEIVES via the shared lighting block.
        /// A dissolving span switches to the dissolve-aware depth pipeline (or its inverted sibling, for the merged
        /// half of an HLOD crossfade) so its shadow erodes with its mesh, and the plain pipeline is re-bound before
        /// the skinned casters, which never dissolve in the depth pass. NEVER
        /// camera-frustum-culled - every entry in <c>_cpuSkinnedDraws</c> is drawn unconditionally (an entry only got
        /// there because it is visible to the main pass, the shadow pass, or both - see
        /// <see cref="ClassifySkinnedVisibility"/>). The receiver tail is set separately and always (even on a
        /// skipped frame), so this only records depth. Runs only when the tier is ShadowMap AND the dirty check
        /// requires a re-render (see <see cref="ShadowDepthPassDirty"/>). An unchanged static scene reuses the atlas.
        /// </summary>
        void RenderShadowDepthPass(IGpuCommandList cl, List<ShadowCasterSpan>[] spansPerCascade)
        {
            // Cascaded depth pass: clear the whole atlas + upload every cascade's column-transformed matrix (plus the
            // render origin the dissolve variant's world-anchored noise needs), then draw each cascade's own caster
            // spans into its atlas column (scissor-clipped). Reuses the instance buffer the model pass uploaded (no
            // second upload). Skinned casters are still drawn into every cascade (there are a handful of them, and
            // their bounds move with the pose). The rigid spans are per-cascade, so a near cascade no longer
            // rasterizes the whole world to fill a 19 m footprint.
            int count = _cascadeCount;
            _model.BeginShadowPass(cl, _cascadeDepthVps.AsSpan(0, count), count, _frameOrigin,
                _cascadeNoiseScales.AsSpan(0, count));

            // Rigid + CPU-skinned casters draw with the rigid depth pipeline + the per-cascade light matrix (bound by
            // the dynamic offset in BeginShadowCascadeRigid). CPU-skinned reuses the same rigid pipeline/light UBO.
            for (int c = 0; c < count; c++)
            {
                _model.BeginShadowCascadeRigid(cl, c);
                // Which of the two depth pipelines is bound right now. Switched only when a span's kind differs from
                // the last one (the same pattern the main pass's dissolve draws use), so an all-opaque frame binds
                // once per cascade exactly as before.
                ShadowCastKind bound = ShadowCastKind.Opaque;
                foreach (ShadowCasterSpan span in spansPerCascade[c])
                {
                    var m = _meshes[span.Index];
                    if (m is not { } mesh) continue;   // unloaded between the span build and here: skip its slice
                    if (span.Kind != bound)
                    {
                        if (span.Kind == ShadowCastKind.Dissolving) _model.BeginShadowCascadeRigidDissolve(cl, c);
                        else if (span.Kind == ShadowCastKind.DissolvingInverted) _model.BeginShadowCascadeRigidDissolveInverted(cl, c);
                        else _model.BeginShadowCascadeRigid(cl, c);
                        bound = span.Kind;
                    }
                    _model.DrawShadowCasterRun(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, span.Start, span.Count);
                    CountMeshDraw(mesh.IndexCount, span.Count);
                }
                if (!UseGpuSkinning && _cpuSkinnedDraws.Count > 0)
                {
                    // The skinned instance stream carries no dissolve, but bind the plain pipeline back anyway so the
                    // skinned casters never depend on which kind the last rigid span happened to leave bound.
                    if (bound != ShadowCastKind.Opaque) _model.BeginShadowCascadeRigid(cl, c);
                    for (int d = 0; d < _cpuSkinnedDraws.Count; d++)
                    {
                        var dr = _cpuSkinnedDraws[d];
                        _model.DrawShadowSkinnedCaster(cl, dr.Ib, dr.IndexCount, dr.IndexFormat, dr.BaseVertex, (uint)d);
                        CountSkinnedDraw(dr.IndexCount);
                    }
                }
            }

            // GPU-skinned casters (opt-in): one combined-UBO slot per (cascade, caster), folding that cascade's
            // column-transformed matrix. Pack every slot first, then bind + draw per cascade (the same update-then-draw
            // ordering the splat sync uses). A draw outside a cascade's ortho volume clips away.
            if (UseGpuSkinning && _gpuSkinnedDraws.Count > 0)
            {
                var boneSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_boneMatrices);
                int gpuCount = _gpuSkinnedDraws.Count;
                for (int c = 0; c < count; c++)
                    for (int d = 0; d < gpuCount; d++)
                    {
                        var dr = _gpuSkinnedDraws[d];
                        _model.PackSkinnedShadowSlot(cl, (uint)(c * gpuCount + d), dr.World, _cascadeDepthVps[c],
                            boneSpan.Slice(dr.BoneSpanStart, dr.BoneCount));
                        _frameStats.BufferUpdateBytes += (long)(1 + dr.BoneCount) * 64;
                    }
                for (int c = 0; c < count; c++)
                {
                    _model.BindShadowCascadeSkinned(cl, c);
                    for (int d = 0; d < gpuCount; d++)
                    {
                        var dr = _gpuSkinnedDraws[d];
                        _model.DrawGpuSkinnedShadowCaster(cl, dr.RestVb, dr.Ib, dr.IndexCount, dr.IndexFormat, (uint)(c * gpuCount + d));
                        CountSkinnedDraw(dr.IndexCount);
                    }
                }
            }
            _model.EndShadowPass(cl);
        }
    }
}
