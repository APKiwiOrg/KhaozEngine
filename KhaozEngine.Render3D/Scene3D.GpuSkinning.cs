using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// THE GPU-SKINNING PATH'S PER-FRAME WORK (<see cref="Scene3D.UseGpuSkinning"/>, the default): sizing
    /// this frame's three skinned uniform destinations, packing the ONE shared bone palette, and the main pass's
    /// own draw loop. The shadow half lives in <c>Scene3D.ShadowCasters.cs</c>, which reads the same palette.
    /// <para>
    /// WHAT A FRAME UPLOADS, AND WHY IT IS SHAPED THIS WAY (issue #407). A caster's palette is the same bytes in
    /// the main pass, every shadow cascade and point faces, so it goes up ONCE before any pass draws. Only the
    /// matrices in front of it are per draw: <c>{ Model; P }</c> per caster in the main pass, <c>{ LightMvp }</c>
    /// per caster-cascade in the depth pass. They cannot share one set with the palette because a resource-set bind
    /// carries exactly one dynamic offset, which is why the palette is a set of its own in both pipelines.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>Size this frame's skinned destinations and upload the shared bone palette. Called from the
        /// skinned-visibility pass once <c>_gpuSkinnedDraws</c> is built, before key, point and main draws that
        /// share the palette this records.</summary>
        void PrepareGpuSkinnedFrame(IGpuCommandList cl, bool shadowMapActive)
        {
            if (_gpuSkinnedDraws.Count == 0) return;

            _model.EnsureSkinnedMainCapacity((uint)_gpuSkinnedDraws.Count);
            // ONE palette slot per CASTER, whatever the cascade count. The depth slots stay one per (cascade,
            // caster) because each folds that cascade's own light matrix, and that is all they hold now.
            uint lbsSlots = 0, dqSlots = 0;
            foreach (GpuSkinnedDraw draw in _gpuSkinnedDraws)
            {
                if (draw.PreparedPose.Profile.Method == SkinningMethod.DualQuaternionRigid) dqSlots++;
                else lbsSlots++;
            }
            if (lbsSlots > 0) _model.EnsureSkinnedBonePaletteCapacity(lbsSlots, SkinningMethod.LinearBlend);
            if (dqSlots > 0) _model.EnsureSkinnedBonePaletteCapacity(dqSlots, SkinningMethod.DualQuaternionRigid);
            if (shadowMapActive)
                _model.EnsureSkinnedShadowCapacity((uint)(_gpuSkinnedDraws.Count * Math.Max(1, _cascadeCount)));

            Span<Matrix4x4> boneSpan = CollectionsMarshal.AsSpan(_boneMatrices);
            for (int d = 0; d < _gpuSkinnedDraws.Count; d++)
            {
                GpuSkinnedDraw dr = _gpuSkinnedDraws[d];
                _model.PackPreparedSkinningPalette(dr.PaletteSlot, dr.PreparedPose);
            }
            if (lbsSlots > 0) _frameStats.AddSkinnedUniformUpload(_model.UploadSkinningPalette(cl, SkinningMethod.LinearBlend));
            if (dqSlots > 0) _frameStats.AddSkinnedUniformUpload(_model.UploadSkinningPalette(cl, SkinningMethod.DualQuaternionRigid));
            if (_res.MotionAllocated) PrepareGpuSkinnedMotion(cl, boneSpan);   // Scene3D.MotionTarget.cs
        }

        /// <summary>The main pass's GPU-skinned draws. An entry with <c>VisibleMain</c> false is camera-culled and
        /// retained for a key or point shadow, or shadow-only, so it must NOT also draw here. Packs
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
                _model.PackSkinnedMainSlot(dr.Slot, dr.World, dr.Tint, dr.Emissive, dr.SpecParams,
                    dr.DissolveParams, dissolveComplement: dr.DissolveComplement);
                packedMainSlots = true;
            }
            if (packedMainSlots) _frameStats.AddSkinnedUniformUpload(_model.UploadSkinnedMainSlots(cl));

            bool dissolveBound = false;
            SkinningProfile? profileBound = null;
            for (int d = 0; d < _gpuSkinnedDraws.Count; d++)
            {
                GpuSkinnedDraw dr = _gpuSkinnedDraws[d];
                if (!dr.VisibleMain) continue;
                if (dr.Dissolve != dissolveBound || dr.PreparedPose.Profile != profileBound)
                {
                    _model.BindSkinnedMaterialPass(cl, dr.PreparedPose.Profile, dr.Dissolve);
                    dissolveBound = dr.Dissolve;
                    profileBound = dr.PreparedPose.Profile;
                }
                // Headers compact all methods together. Each palette compacts only its own method.
                _model.DrawGpuSkinned(cl, dr.RestVb, dr.Ib, dr.IndexCount, dr.IndexFormat, dr.Slot, dr.PaletteSlot,
                    dr.SkinnedMaterialSet, dr.PreparedPose.Profile);
                CountSkinnedDraw(dr.IndexCount);
            }
        }
    }
}
