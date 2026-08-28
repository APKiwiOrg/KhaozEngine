using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// THE GPU-SKINNING PATH'S PER-FRAME WORK (<see cref="Scene3D.UseGpuSkinning"/>, opt-in, default off): sizing
    /// this frame's three skinned uniform destinations, packing the ONE shared bone palette, and the main pass's
    /// own draw loop. The shadow half lives in <c>Scene3D.ShadowCasters.cs</c>, which reads the same palette.
    /// <para>
    /// WHAT A FRAME UPLOADS, AND WHY IT IS SHAPED THIS WAY (issue #407). A caster's palette is the same bytes in
    /// the main pass and in every shadow cascade, so it goes up ONCE, here, before either pass draws. Only the
    /// matrices in front of it are per draw: <c>{ Model; P }</c> per caster in the main pass, <c>{ LightMvp }</c>
    /// per caster-cascade in the depth pass. They cannot share one set with the palette because a resource-set bind
    /// carries exactly one dynamic offset, which is why the palette is a set of its own in both pipelines.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>Size this frame's skinned destinations and upload the shared bone palette. Called from the
        /// skinned-visibility pass once <c>_gpuSkinnedDraws</c> is built, BEFORE the shadow depth pass and the main
        /// pass, both of which read the palette this records.</summary>
        void PrepareGpuSkinnedFrame(IGpuCommandList cl, bool shadowMapActive)
        {
            if (_gpuSkinnedDraws.Count == 0) return;

            _model.EnsureSkinnedMainCapacity((uint)_gpuSkinnedDraws.Count);
            // ONE palette slot per CASTER, whatever the cascade count. The depth slots stay one per (cascade,
            // caster) because each folds that cascade's own light matrix, and that is all they hold now.
            _model.EnsureSkinnedBonePaletteCapacity((uint)_gpuSkinnedDraws.Count);
            if (shadowMapActive)
                _model.EnsureSkinnedShadowCapacity((uint)(_gpuSkinnedDraws.Count * Math.Max(1, _cascadeCount)));

            Span<Matrix4x4> boneSpan = CollectionsMarshal.AsSpan(_boneMatrices);
            for (int d = 0; d < _gpuSkinnedDraws.Count; d++)
            {
                GpuSkinnedDraw dr = _gpuSkinnedDraws[d];
                _model.PackSkinnedBonePalette(dr.Slot, boneSpan.Slice(dr.BoneSpanStart, dr.BoneCount));
                _frameStats.AddSkinnedUniformUpload((long)dr.BoneCount * 64);
            }
            _model.UploadSkinnedBonePalette(cl);
        }

        /// <summary>The main pass's GPU-skinned draws. An entry with <c>VisibleMain</c> false is camera-culled and
        /// drawn only into the shadow map (see ClassifySkinnedVisibility), so it must NOT also draw here. Packs
        /// every visible draw's 128-byte header slot, uploads them in one write, then draws through the skinned
        /// pipeline: rest-pose buffer at vertex slot 0, set 0 = shared frame block + this draw's header window,
        /// set 1 = material, set 2 = this caster's palette (already uploaded by
        /// <see cref="PrepareGpuSkinnedFrame"/>).</summary>
        void DrawGpuSkinnedMain(IGpuCommandList cl)
        {
            if (_gpuSkinnedDraws.Count == 0) return;

            bool packedMainSlots = false;
            for (int d = 0; d < _gpuSkinnedDraws.Count; d++)
            {
                GpuSkinnedDraw dr = _gpuSkinnedDraws[d];
                if (!dr.VisibleMain) continue;
                _model.PackSkinnedMainSlot(dr.Slot, dr.World, dr.Tint, dr.Emissive, dr.SpecParams);
                // Model + P alone: 1072 bytes left with #604's frame block and 3072 more with #407's palette.
                _frameStats.AddSkinnedUniformUpload(ModelRenderer.SkinnedHeaderBytes);
                packedMainSlots = true;
            }
            if (packedMainSlots) _model.UploadSkinnedMainSlots(cl);

            bool dissolveBound = false;
            _model.BindSkinnedPass(cl);
            for (int d = 0; d < _gpuSkinnedDraws.Count; d++)
            {
                GpuSkinnedDraw dr = _gpuSkinnedDraws[d];
                if (!dr.VisibleMain) continue;
                if (dr.Dissolve != dissolveBound)   // switch pipelines only when the dissolve state changes
                {
                    if (dr.Dissolve) _model.BindSkinnedDissolvePass(cl); else _model.BindSkinnedPass(cl);
                    dissolveBound = dr.Dissolve;
                }
                // Slot indexes the header window and paletteSlot the shared palette. They are the same number here
                // (both are the caster's compacted index), which is exactly what the depth pass cannot say.
                _model.DrawGpuSkinned(cl, dr.RestVb, dr.Ib, dr.IndexCount, dr.IndexFormat, dr.Slot, dr.Slot,
                    dr.SkinnedMaterialSet);
                CountSkinnedDraw(dr.IndexCount);
            }
        }
    }
}
