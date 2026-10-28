using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>Classifies queued skinned draws and records the frame streams shared by colour and shadow passes.</summary>
public sealed partial class Scene3D
{
    void PrepareSkinnedDraws(IGpuCommandList cl, FrustumPlanes camFrustum, bool shadowMapActive,
        int shadowCascadeCount, bool hasPointShadowRequests)
    {
        var skinnedItems = _skinnedInstances.Items;
        _cpuSkinnedVerts.Clear();
        _cpuSkinnedInstances.Clear();
        _cpuSkinnedDraws.Clear();
        _gpuSkinnedDraws.Clear();
        _drawnSkinnedInstances = 0;
        _culledSkinnedInstances = 0;
        if (skinnedItems.Count > 0)
        {
            var boneSpan = CollectionsMarshal.AsSpan(_boneMatrices);
            const int cap = SkinningMath.MaxBonesPerDraw;
            for (int i = 0; i < skinnedItems.Count; i++)
            {
                var it = skinnedItems[i];
                if (!_skinnedSlots.IsValid(it.Mesh.Index, it.Mesh.Generation)) continue;
                var entry = _skinnedMeshes[it.Mesh.Index];
                if (entry is null) continue;
                var src = _skinnedCpuVerts[it.Mesh.Index];
                if (src is null) continue;

                var (visibleMain, visibleShadow) = ClassifySkinnedVisibility(entry.Bounds, it.World, FrustumCulling,
                    camFrustum, shadowMapActive && it.CastsShadows, _shadowFrustums.AsSpan(0, shadowCascadeCount), _frameOrigin);
                visibleMain &= !it.ShadowOnly;
                PointShadowCasterSphere pointSphere = PointShadowCasterSphere.FromRestBounds(
                    entry.Bounds, it.World, SkinnedCullSafetyFactor);
                bool visiblePointShadow = hasPointShadowRequests && it.CastsShadows
                    && PointRequestCanRetainSkinned(pointSphere, Post.Quality.Shadows.PointShadows);
                if (!visibleMain && !visibleShadow && !visiblePointShadow)
                {
                    if (!it.ShadowOnly) _culledSkinnedInstances++;
                    continue;
                }
                if (visibleMain) _drawnSkinnedInstances++;
                else if (!it.ShadowOnly) _culledSkinnedInstances++;

                bool dissolving = it.Dissolving;
                ShadowCastKind shadowKind = ShadowDepthSelection.ClassifySkinnedCaster(it);   // issue #387
                // During a dissolve the emissive channel carries the edge colour. The cutoff remains in
                // SpecParams.z while the threshold and edge width travel in a dedicated dissolve vector.
                Vector4 emissive = dissolving ? it.DissolveEdge : it.Material.Emissive;
                Vector4 specParams = new(it.Material.Specular, it.Material.Shininess, entry.AlphaCutoff, 0f);
                Vector2 dissolveParams = dissolving
                    ? new Vector2(it.DissolveThreshold, it.DissolveEdgeWidth)
                    : Vector2.Zero;

                if (UseGpuSkinning)
                {
                    // Record the draw. The GPU deforms the rest-pose buffer. The palette lives at slot i of
                    // _boneMatrices (submission index), packed into the shared palette at the compacted slot.
                    _gpuSkinnedDraws.Add(new GpuSkinnedDraw(entry.Vb, entry.Ib, entry.IndexCount, entry.IndexFormat,
                        entry.SkinnedMaterialSet, i * cap, entry.InverseBind.Length, (uint)_gpuSkinnedDraws.Count,
                        ToRender(it.World), it.Tint, emissive, specParams, dissolveParams, it.DissolveComplement,
                        visibleMain, dissolving, shadowKind, pointSphere, it.Motion));   // reduced after visibility classification
                }
                else
                {
                    int baseVertex = _cpuSkinnedVerts.Count;
                    var palette = boneSpan.Slice(i * cap, cap);   // this draw's composed bone window (slot i)
                    for (int v = 0; v < src.Length; v++)
                        _cpuSkinnedVerts.Add(SkinningMath.SkinVertex(src[v], palette));
                    _cpuSkinnedInstances.Add(new ModelRenderer.InstanceData
                    {
                        Model = ToRender(it.World),   // reduced AFTER visibility classification above
                        Tint = it.Tint,
                        Emissive = emissive,
                        SpecParams = specParams,
                        IsDynamic = 1f,   // skinned character: tag it so the main ground-decal pass rejects it (issue #235)
                        // The colour and rigid dissolve depth pipelines read this dedicated vector.
                        Dissolve = dissolveParams,
                        DissolveComplement = it.DissolveComplement,
                    });
                    _cpuSkinnedDraws.Add(new CpuSkinnedDraw(entry.Ib, entry.IndexCount, entry.IndexFormat, baseVertex,
                        entry.MaterialSet, dissolving, visibleMain, shadowKind, pointSphere, it.Motion, it.Mesh.Index, src.Length));
                }
            }
            // Sizes the frame's three skinned destinations and uploads the ONE shared bone palette both passes
            // read. See Scene3D.GpuSkinning.cs.
            using (ProfilePass(cl, "geometry.skinned-uploads",
                executed: UseGpuSkinning ? _gpuSkinnedDraws.Count > 0 : _cpuSkinnedDraws.Count > 0))
            {
                if (UseGpuSkinning) PrepareGpuSkinnedFrame(cl, shadowMapActive);
                else if (_cpuSkinnedDraws.Count > 0) UploadCpuSkinnedFrame(cl);
            }
        }
        else SkipProfilePass(cl, "geometry.skinned-uploads");
    }
}
