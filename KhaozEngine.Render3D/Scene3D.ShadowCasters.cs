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
        /// <summary>A caster with rigid dissolve or an interior cut, drawn through the dissolve-aware depth pipeline.
        /// The fragment removes the dissolve noise mask and any opted-in interior span.</summary>
        Dissolving = 2,
        /// <summary>A dissolving caster whose SHADOW dither is inverted (issue #391): drawn through the inverted
        /// dissolve depth pipeline at the mirrored threshold. Retained for independent shadow-only control.
        /// LOD and HLOD handoffs use the shared complement phase through <see cref="Dissolving"/>.</summary>
        DissolvingInverted = 3,
    }

    /// <summary>
    /// The shadow-caster half of <see cref="Scene3D"/>: which queued instances write into the key light's cascade
    /// atlas, and the depth pass that draws them.
    /// <para>
    /// Three policies live here, all opt-in and all inert by default. A per-instance <c>castsShadows</c> flag
    /// (issue #287) keeps chosen geometry out of the depth pass entirely - what a consumer sets on a dense
    /// decorative layer (ground cover, understory) whose hundreds of small shadows cost more than they read. A
    /// caster carrying the 14.5.0 rigid dissolve is drawn through the dissolve-aware depth pipeline, so its shadow
    /// thins as it fades: before this, a prop at 85 percent dissolve still cast a fully solid shadow, which then
    /// popped out at the hard cull radius, and across an HLOD crossfade band the individual props AND the merged
    /// mesh both cast at full strength, roughly doubling shadow density. And the scene-wide
    /// <see cref="TerrainCastsShadows"/> (issue #280) lets splat terrain cast, which a sculpted world with real
    /// hills needs and the flat ground the receive-only rule was written for did not.
    /// </para>
    /// <para>
    /// Across an HLOD crossfade both halves use one threshold and opposite DissolveComplement phases in the
    /// ordinary dissolve-aware depth pipeline. The shadow-only inversion introduced for #391 remains available
    /// independently, but the shared phase now keeps colour and shadow ownership identical.
    /// </para>
    /// <para>
    /// A MASK caster (a leaf card, issue #15) is not a fourth kind: whether a mesh alpha-tests is fixed at load, so
    /// the pass reads it off the mesh and <see cref="ShadowDepthSelection"/> routes that mesh's spans through a
    /// cutout pipeline, whatever their kind. Opaque meshes keep the depth-only pipeline.
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

        /// <summary>One captured caster instance: its uploaded world matrix, dissolve threshold and interior opt-in.
        /// These all change the recorded depth, so a dissolve or cut bit that changes while the light matrices
        /// and transforms hold still must still re-render the atlas.</summary>
        internal readonly record struct ShadowCasterInstance(Matrix4x4 Model, float Dissolve, bool CutInsideInteriors = false);

        // Per-instance caster classification, index-aligned to _instanceData (filled by GroupInstances, which is the
        // one place that knows each uploaded slot's source instance). Reused, never per-frame allocated.
        readonly List<ShadowCastKind> _instanceCastKinds = new();

        /// <summary>Queue one instance with the shadow-caster opt-out (issue #287): <paramref name="castsShadows"/>
        /// false draws it exactly like the <see cref="Draw(MeshHandle, Matrix4x4, Color, Material)"/> overload but
        /// keeps it out of the shadow depth pass, so it casts nothing while still RECEIVING shadows. The knob is
        /// per-instance and CPU-side: no pipeline switch, no change to the uploaded instance bytes, and a
        /// <c>true</c> value is byte-identical to the material overload.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material, bool castsShadows)
            => Draw(new RigidInstanceDraw(mesh, world) { Tint = tint, Material = material, CastsShadows = castsShadows });

        /// <summary>The dissolve overload plus the shadow-caster opt-out: as
        /// <see cref="Draw(MeshHandle, Matrix4x4, Color, Material, float, float, Color)"/>, but
        /// <paramref name="castsShadows"/> false keeps this instance out of the depth pass. With it left true a
        /// positive <paramref name="dissolve"/> now also thins the instance's SHADOW by the same noise mask that
        /// thins the mesh (issue #287), so a fading prop stops casting a solid shadow under an almost-invisible
        /// caster.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolve, float edgeWidth, Color edgeColor, bool castsShadows)
            => Draw(new RigidInstanceDraw(mesh, world)
            {
                Tint = tint,
                Material = material,
                Dissolve = dissolve,
                DissolveEdgeWidth = edgeWidth,
                DissolveEdgeColor = edgeColor,
                CastsShadows = castsShadows,
            });

        /// <summary>Independent inverted shadow dither at the mirrored threshold (#391). Colour is unchanged.
        /// LOD and HLOD handoffs use <see cref="RigidInstanceDraw.DissolveComplement"/> instead.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolve, float edgeWidth, Color edgeColor, bool castsShadows, bool invertShadowDissolve)
            => Draw(new RigidInstanceDraw(mesh, world)
            {
                Tint = tint,
                Material = material,
                Dissolve = dissolve,
                DissolveEdgeWidth = edgeWidth,
                DissolveEdgeColor = edgeColor,
                CastsShadows = castsShadows,
                InvertShadowDissolve = invertShadowDissolve,
            });

        /// <summary>
        /// Whether splat-terrain chunks write into the key light's cascade atlas (issue #280). Default <c>false</c>,
        /// which is the receive-only rule the pass shipped with: terrain always RECEIVES, and only models, tile
        /// ground and characters cast. At that default not a single pixel moves, so every existing scene and every
        /// baked golden is unaffected.
        /// <para>
        /// Set it <c>true</c> for a SCULPTED world, which is the case the receive-only premise did not anticipate:
        /// its reason was that terrain self-shadowing is negligible on flat MMO ground with no overhangs, and a
        /// 49 m mountain is neither. With it off, a mountain throws no shadow at all while the trees standing on it
        /// do, so the ground the mountain should shade reads as fully lit and carries individual prop shadows.
        /// Turning it on subsumes those prop shadows naturally, with no special-casing: a tree inside the
        /// mountain's shadow just stands on already-shadowed ground.
        /// </para>
        /// <para>
        /// Terrain chunks go through the SAME caster path as everything else, so they are per-cascade culled by
        /// <see cref="ShadowCascadeCulling"/> (a far cascade draws the chunks that reach it, not the whole world)
        /// and they can be opted out per instance like any other caster. The cost is the chunk geometry rasterized
        /// once per cascade it reaches, so it scales with resident chunk count rather than with prop count. Watch
        /// for terrain self-shadow acne when turning it on: <c>ShadowSettings.ShadowNormalOffset</c> is the knob,
        /// raised before either depth bias (see the bias-tuning note in docs/USING-KHAOZENGINE.md).
        /// </para>
        /// </summary>
        public bool TerrainCastsShadows { get; set; }

        /// <summary>Whether a mesh takes part in the depth pass AT ALL, from its splat-material slot and the
        /// scene's <see cref="TerrainCastsShadows"/> flag. A splat mesh is terrain, so it casts only with the flag
        /// on. Everything else always casts, tile ground included (a tile world's ground carries authored height,
        /// so it casts like a model mesh, tile-world design section 7.5, and the terrain rule is deliberately keyed
        /// on the splat material rather than on "draws through a ground pipeline"). Pure, so the caster walk and
        /// the tests read one definition of the rule.</summary>
        internal static bool MeshCastsShadows(int splatMaterial, bool terrainCastsShadows)
            => splatMaterial < 0 || terrainCastsShadows;

        /// <summary>How one queued instance participates in the depth pass: opted out, plainly, or dissolving. Pure
        /// (no scene state), so the classification is unit-testable and stays the single definition both
        /// <see cref="GroupInstances"/> and the tests read.
        /// <para>
        /// A SHADOW-ONLY instance (issue #974) is classified here exactly as the visible draw it would otherwise
        /// have been, and can never come out <see cref="ShadowCastKind.None"/>: the whole point of it is to cast,
        /// and <see cref="SceneInstances.Instance"/>'s constructor refuses the shadow-only plus opt-out pair, so
        /// <see cref="SceneInstances.Instance.CastsShadows"/> is true on every one of them. Shadow-only is a
        /// COLOUR-pass question and is answered by the main-pass mask instead (see Scene3D.ShadowOnly.cs).
        /// </para></summary>
        internal static ShadowCastKind ClassifyCaster(in SceneInstances.Instance instance)
            => !instance.CastsShadows ? ShadowCastKind.None
             : !instance.Dissolving && !instance.CutInsideInteriors ? ShadowCastKind.Opaque
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
        /// EXACT order <see cref="RenderShadowDepthPass"/> draws it. Stale-handle runs are excluded, matching what
        /// the pass draws, and so is anything the consumer opted out of casting and (unless
        /// <see cref="TerrainCastsShadows"/> is set) every terrain splat mesh. The same two
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
                // Splat terrain is receive-only unless the scene opted in (TerrainCastsShadows, issue #280), which
                // is the one rule this walk and the depth pass share. TILE GROUND IS NOT EXCLUDED EITHER WAY: see
                // MeshCastsShadows for why the rule is keyed on the splat material rather than on "draws through a
                // ground pipeline".
                if (!MeshCastsShadows(mesh.SplatMaterial, TerrainCastsShadows)) continue;
                int first = spans.Count;
                AppendCasterSpans(run.Mesh.Index, run.Mesh.Generation, run.Start, run.Count, _instanceCastKinds, spans);
                for (int i = first; i < spans.Count; i++)
                {
                    ShadowCasterSpan span = spans[i];
                    for (uint s = 0; s < span.Count; s++)
                    {
                        ModelRenderer.InstanceData d = _instanceData[(int)(span.Start + s)];
                        instances.Add(new ShadowCasterInstance(d.Model, d.Dissolve.X, d.SpecParams.W > 0.5f));
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
        /// it - any skinned caster forces a re-render). The skinned casters the last rendered pass DREW have all
        /// gone away (<paramref name="skinnedCastersCleared"/>, issue #23: presence alone cannot see that, so the
        /// frame a character despawns would otherwise reuse an atlas with its shadow still baked into it). The map
        /// resolution changed (<paramref name="resolutionChanged"/>, which also reallocates the target). The fitted
        /// light matrix changed (<paramref name="lightMatrixChanged"/>). The rigid caster set / world transforms /
        /// dissolves changed (<paramref name="casterDataChanged"/>). Otherwise the previous depth map is still
        /// correct and the pass is skipped (the map is reused, NOT cleared).
        /// </summary>
        internal static bool ShadowDepthPassDirty(bool hadPrevious, bool anySkinnedCaster, bool skinnedCastersCleared,
            bool resolutionChanged, bool lightMatrixChanged, bool casterDataChanged)
            => !hadPrevious || anySkinnedCaster || skinnedCastersCleared
             || resolutionChanged || lightMatrixChanged || casterDataChanged;

        /// <summary>
        /// Pure sequence compare of two captured shadow-caster signatures (each a per-span mesh handle + instance
        /// count + cast kind list, and the flat per-instance world-matrix + dissolve list, in draw order). Uploaded
        /// offsets and non-caster gaps are ignored: adjacent spans of the same mesh, generation and cast kind are
        /// compared as one group, so compacting receive-only instances never dirties an unchanged atlas. Returns
        /// <c>true</c> when they DIFFER, so the depth map must re-render. Both signatures are captured by
        /// <see cref="BuildShadowCasterSpans"/> in the exact draw order of <see cref="RenderShadowDepthPass"/>, so an
        /// equal signature proves the same casters, at the same transforms, with the same dissolves and the same
        /// opt-outs, as the last rendered pass. No GPU, headless-testable.
        /// </summary>
        internal static bool ShadowCastersChanged(
            List<ShadowCasterSpan> spansA, List<ShadowCasterInstance> instancesA,
            List<ShadowCasterSpan> spansB, List<ShadowCasterInstance> instancesB)
        {
            if (instancesA.Count != instancesB.Count) return true;
            int a = 0, b = 0;
            while (a < spansA.Count && b < spansB.Count)
                if (NextCanonicalCasterSpan(spansA, ref a) != NextCanonicalCasterSpan(spansB, ref b)) return true;
            if (a != spansA.Count || b != spansB.Count) return true;
            for (int i = 0; i < instancesA.Count; i++) if (instancesA[i] != instancesB[i]) return true;
            return false;
        }

        // Normalize only the signature's view. The actual draw spans keep their upload offsets and gaps.
        // BuildShadowCasterSpans has already excluded None spans, so equal neighbours differ only in packing.
        static ShadowCasterSpan NextCanonicalCasterSpan(List<ShadowCasterSpan> spans, ref int cursor)
        {
            ShadowCasterSpan first = spans[cursor++];
            uint count = first.Count;
            while (cursor < spans.Count)
            {
                ShadowCasterSpan next = spans[cursor];
                if (next.Index != first.Index || next.Generation != first.Generation || next.Kind != first.Kind) break;
                count += next.Count;
                cursor++;
            }
            return first with { Start = 0, Count = count };
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

        /// <summary>Bind cascade <paramref name="cascade"/> on the rigid depth pipeline <paramref name="variant"/>
        /// names (every rigid and CPU-skinned variant). The GPU-skinned variants bind through
        /// <c>BindShadowCascadeSkinned</c> and are refused here, as <see cref="ShadowDepthSelection"/> refuses a
        /// kind it has no pipeline for. Internal so the headless tests can pin that refusal.</summary>
        internal void BeginRigidDepthVariant(IGpuCommandList cl, int cascade, ShadowDepthVariant variant)
        {
            switch (variant)
            {
                case ShadowDepthVariant.Opaque: _model.BeginShadowCascadeRigid(cl, cascade); break;
                case ShadowDepthVariant.Dissolve: _model.BeginShadowCascadeRigidDissolve(cl, cascade); break;
                case ShadowDepthVariant.DissolveInverted: _model.BeginShadowCascadeRigidDissolveInverted(cl, cascade); break;
                case ShadowDepthVariant.Cutout: _model.BeginShadowCascadeRigidCutout(cl, cascade, inverted: false); break;
                case ShadowDepthVariant.CutoutInverted: _model.BeginShadowCascadeRigidCutout(cl, cascade, inverted: true); break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant), variant,
                        "a GPU-skinned depth variant binds through BindShadowCascadeSkinned, not a rigid pipeline");
            }
        }
    }
}
