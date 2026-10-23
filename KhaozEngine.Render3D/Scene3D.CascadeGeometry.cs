using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>
    /// Render the key-light cascaded shadow depth pass for this frame using the already-fitted cascade DEPTH
    /// matrices in <see cref="_cascadeDepthVps"/> (from <see cref="SetShadowReceiverTail"/>): clear the atlas, then
    /// draw every skinned caster plus each cascade's REACHING rigid casters into that cascade's atlas column
    /// (reusing the already-uploaded instance/skinned buffers). The rigid casters come from
    /// <paramref name="spansPerCascade"/>, this frame's per-cascade caster draw lists (see
    /// <see cref="BuildCascadeCasterSpans"/>, which splits the one list <see cref="BuildShadowCasterSpans"/>
    /// built): terrain (splat meshes) casts only when the scene set <see cref="TerrainCastsShadows"/>, and
    /// nothing the consumer opted out of ever casts. Terrain always RECEIVES via the shared lighting block,
    /// whichever way that flag is set.
    /// Every draw takes the pipeline <see cref="ShadowDepthSelection"/> names for it, switched only when it differs
    /// from the one bound: a dissolving span takes the dissolve-aware pipeline (or its independently inverted
    /// sibling) so its shadow erodes with its mesh, a span of a MASK mesh takes a cutout
    /// pipeline so its shadow is its silhouette (issue #15), and a dissolving skinned caster takes a dissolve-aware
    /// pipeline on either skinning path (issue #387). NEVER camera-frustum-culled - every casting entry in the
    /// skinned draw lists is drawn (an entry only got there because it is visible to the main pass, the shadow
    /// pass, or both - see <see cref="ClassifySkinnedVisibility"/>), and an opted-out one is skipped. The
    /// receiver tail is set separately and always (even on a skipped frame), so this only records depth. Runs
    /// only when the tier is ShadowMap AND the dirty check requires a re-render (see
    /// <see cref="ShadowDepthPassDirty"/>). An unchanged static scene reuses the atlas.
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
        ResetShadowPassCounters();
        using (ProfileCpu("cpu.shadow.upload-packing"))
        using (ProfilePass(cl, "shadow.directional.setup"))
        {
            UploadFoliageShadowCascades(cl);
            _model.BeginShadowPass(cl, _cascadeDepthVps.AsSpan(0, count), count, _frameOrigin,
                _cascadeNoiseScales.AsSpan(0, count));
        }
        int rigidOccurrence = 0;

        // Rigid + CPU-skinned casters draw with the rigid depth pipeline + the per-cascade light matrix (bound by
        // the dynamic offset in BeginShadowCascadeRigid). CPU-skinned reuses the same rigid pipeline/light UBO.
        for (int c = 0; c < count; c++)
        {
            using var cascade = ProfilePass(cl, "shadow.cascade.rigid", c);
            using (ProfilePass(cl, "shadow.cascade.setup", c)) _model.BeginShadowCascadeRigid(cl, c);
            // The span list this cascade is about to walk, counted here rather than derived later, so the
            // instrument reports the list the loop actually iterated (issue #410).
            _shadowPassRigidSpans[c] = spansPerCascade[c].Count;
            // Which depth pipeline is bound right now. Switched only when a draw's variant differs from the last
            // one (the same pattern the main pass's dissolve draws use), so an all-opaque frame binds once per
            // cascade exactly as before, and a cutout set is rebound only when the mesh or the pipeline changes.
            ShadowDepthVariant bound = ShadowDepthVariant.Opaque;
            IGpuResourceSet? boundCutout = null;
            RenderProfilingScope rigidLeaf = default;
            string? rigidRoute = null;
            try
            {
                foreach (ShadowCasterSpan span in spansPerCascade[c])
                {
                    var m = _meshes[span.Index];
                    if (m is not { } mesh) continue;   // unloaded between the span build and here: skip its slice
                    ShadowDepthVariant variant = ShadowDepthSelection.ForRigidSpan(span.Kind, mesh.CutsOutShadow);
                    if (_profiling is not null)
                    {
                        string route = RigidShadowPass(variant);
                        if (route != rigidRoute)
                        {
                            rigidLeaf.Dispose();
                            rigidLeaf = ProfilePass(cl, route, rigidOccurrence++);
                            rigidRoute = route;
                        }
                    }
                    if (variant != bound)
                    {
                        BeginRigidDepthVariant(cl, c, variant);
                        bound = variant;
                        boundCutout = null;
                    }
                    if (mesh.CutsOutShadow && !ReferenceEquals(boundCutout, mesh.ShadowCutoutSet))
                    {
                        _model.BindShadowCutoutMaterial(cl, mesh.ShadowCutoutSet!);
                        boundCutout = mesh.ShadowCutoutSet;
                    }
                    _model.DrawShadowCasterRun(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, span.Start, span.Count);
                    _shadowPassRigidDraws++;
                    CountMeshDraw(mesh.IndexCount, span.Count);
                }
            }
            finally { rigidLeaf.Dispose(); }
            using (ProfilePass(cl, "shadow.cascade.cpu-skinned", c,
                executed: !UseGpuSkinning && (_profiling is null || CountSkinnedCasters() > 0)))
                if (!UseGpuSkinning && _cpuSkinnedDraws.Count > 0)
                {
                    // A CPU-skinned caster's deformed vertices and per-draw instance use the rigid layouts, so it
                    // takes the rigid pipeline for its kind: the plain one, or the dissolve one reading the
                    // threshold RenderInternal packed into its instance (issue #387). Opted out draws record nothing.
                    for (int d = 0; d < _cpuSkinnedDraws.Count; d++)
                    {
                        var dr = _cpuSkinnedDraws[d];
                        if (dr.ShadowKind == ShadowCastKind.None) continue;
                        ShadowDepthVariant variant = ShadowDepthSelection.ForCpuSkinned(dr.ShadowKind);
                        if (variant != bound) { BeginRigidDepthVariant(cl, c, variant); bound = variant; }
                        _model.DrawShadowSkinnedCaster(cl, dr.Ib, dr.IndexCount, dr.IndexFormat, dr.BaseVertex, (uint)d);
                        _shadowPassSkinnedDraws++;
                        CountSkinnedDraw(dr.IndexCount);
                    }
                }
        }

        for (int c = 0; c < count; c++) DrawFoliageShadowCascade(cl, c);

        // GPU-skinned casters (the default path): one LIGHT-MATRIX slot per (cascade, caster), folding that
        // cascade's column-transformed matrix. The caster's bones are NOT here: they were packed once per caster
        // into the shared palette in PrepareGpuSkinnedFrame and uploaded before this pass, so every cascade below
        // binds the SAME palette slot at the same offset (#407). That is what took the skinning half of a
        // shadowed frame's uniform upload from (1 + bones) * 64 per caster per cascade to 64. A DISSOLVING caster's
        // slot also carries its world matrix, the render origin and (noise scale, threshold) for the dissolve
        // depth pipeline (issue #387), 160 bytes. Pack every slot first, then bind + draw per cascade (the same
        // update-then-draw ordering the splat sync uses). A draw outside a cascade's ortho volume clips away.
        if (UseGpuSkinning && _gpuSkinnedDraws.Count > 0)
        {
            int gpuCount = _gpuSkinnedDraws.Count;
            bool packedShadowSlots = false;
            using (ProfileCpu("cpu.shadow.skinned-upload-packing"))
            using (ProfilePass(cl, "shadow.directional.skinned-uploads", executed: _profiling is null || CountSkinnedCasters() > 0))
            {
                for (int c = 0; c < count; c++)
                    for (int d = 0; d < gpuCount; d++)
                    {
                        var dr = _gpuSkinnedDraws[d];
                        if (dr.ShadowKind == ShadowCastKind.None) continue;   // opted out: no slot, no draw
                        uint slot = (uint)(c * gpuCount + d);
                        if (dr.ShadowKind is ShadowCastKind.Dissolving or ShadowCastKind.DissolvingInverted)
                        {
                            // The dedicated dissolve vector carries the threshold, independently of alpha cutoff.
                            _model.PackSkinnedShadowSlot(slot, dr.World, _cascadeDepthVps[c], _frameOrigin,
                                _cascadeNoiseScales[c], dr.DissolveParams.X, dr.DissolveComplement);
                            _frameStats.AddSkinnedUniformUpload(ShadowMapRenderer.SkinnedDissolvePayloadBytes);
                        }
                        else
                        {
                            _model.PackSkinnedShadowSlot(slot, dr.World, _cascadeDepthVps[c]);
                            _frameStats.AddSkinnedUniformUpload(64);
                        }
                        packedShadowSlots = true;
                    }
                if (packedShadowSlots) _model.UploadSkinnedShadowSlots(cl);
            }
            for (int c = 0; c < count; c++)
            {
                using var cascade = ProfilePass(cl, "shadow.cascade.gpu-skinned", c, executed: _profiling is null || CountSkinnedCasters() > 0);
                // Bound on the cascade's first casting draw, so the all-opaque frame binds exactly as before.
                ShadowDepthVariant? boundSkinned = null;
                for (int d = 0; d < gpuCount; d++)
                {
                    var dr = _gpuSkinnedDraws[d];
                    if (dr.ShadowKind == ShadowCastKind.None) continue;
                    ShadowDepthVariant variant = ShadowDepthSelection.ForGpuSkinned(dr.ShadowKind);
                    if (variant != boundSkinned)
                    {
                        _model.BindShadowCascadeSkinned(cl, c,
                            dissolve: variant != ShadowDepthVariant.Skinned,
                            invertedDissolve: variant == ShadowDepthVariant.SkinnedDissolveInverted);
                        boundSkinned = variant;
                    }
                    _model.DrawGpuSkinnedShadowCaster(cl, dr.RestVb, dr.Ib, dr.IndexCount, dr.IndexFormat,
                        (uint)(c * gpuCount + d), dr.Slot);
                    _shadowPassSkinnedDraws++;
                    CountSkinnedDraw(dr.IndexCount);
                }
            }
        }
        else
        {
            SkipProfilePass(cl, "shadow.directional.skinned-uploads");
            for (int c = 0; c < count; c++) SkipProfilePass(cl, "shadow.cascade.gpu-skinned", c);
        }
        using (ProfilePass(cl, "shadow.directional.finish")) _model.EndShadowPass(cl);
    }

}
