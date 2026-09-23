using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The skinned half of the shadow-caster policy (issue #387): a skinned draw can opt out of the key light's
    /// depth pass with <c>castsShadows: false</c>, exactly as a rigid instance can, and a dissolving skinned draw
    /// sheds its shadow with its body instead of keeping a solid shadow under an almost invisible character.
    /// <para>
    /// Each queued draw is classified once (<see cref="ShadowDepthSelection.ClassifySkinnedCaster"/>) where the
    /// frame records it, and the depth pass picks the pipeline per draw from that classification. An opted-out draw
    /// is also no shadow-visibility reason to keep an off-camera draw alive, so it is culled with nothing uploaded,
    /// and it does not count as a skinned caster for the atlas dirty check.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>As <see cref="DrawSkinned(SkinnedMeshHandle, ReadOnlySpan{Matrix4x4}, Matrix4x4, Color, Material)"/>,
        /// with the shadow-caster opt-out (issue #387): <paramref name="castsShadows"/> false keeps this draw out of
        /// the key light's depth pass, so it casts nothing while it still draws and still RECEIVES shadows. The knob
        /// is CPU-side: no pipeline switch in the colour pass and no change to what the draw uploads. <c>true</c> is
        /// the material overload.</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint,
            Material material, bool castsShadows)
            => DrawSkinned(h, boneMatrices, model, tint, material, 0f, 0f, default, castsShadows);

        /// <summary>The CharDissolve overload plus the shadow-caster opt-out (issue #387): as
        /// <see cref="DrawSkinned(SkinnedMeshHandle, ReadOnlySpan{Matrix4x4}, Matrix4x4, Color, Material, float, float, Color)"/>,
        /// but <paramref name="castsShadows"/> false keeps this draw out of the depth pass. With it left true a
        /// positive <paramref name="dissolve"/> also thins the draw's SHADOW by the same world-space noise mask that
        /// thins the body, on the GPU-skinned and the CPU-skinned path alike.</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint,
            Material material, float dissolve, float edgeWidth, Color edgeColor, bool castsShadows)
        {
            if (!_skinnedSlots.IsValid(h.Index, h.Generation)) return;
            var entry = _skinnedMeshes[h.Index];
            if (entry is null) return;
            // This draw's bones go into slot N (N = its submission index), padded to the per-draw window so the
            // dynamic-offset bind selects exactly this draw's palette. Slot N maps to bone byte offset
            // N * SlotBytes and to instance buffer element N in the render loop.
            int slot = _skinnedInstances.Items.Count;
            ComposeBonesIntoSlot(_boneMatrices, slot, boneMatrices, entry.InverseBind);
            _skinnedInstances.Add(h, model, tint, material, dissolve, edgeWidth, edgeColor, castsShadows);
        }

        /// <summary>The skinned draws recorded this frame that write into the depth pass: the active path's draw
        /// list minus the opted-out entries. Feeds the dirty check's <c>anySkinnedCaster</c> and the diagnostics'
        /// skinned-caster count, so a scene whose only skinned draws opted out can reuse its atlas.</summary>
        int CountSkinnedCasters()
        {
            int n = 0;
            if (UseGpuSkinning)
            {
                foreach (GpuSkinnedDraw dr in _gpuSkinnedDraws)
                    if (dr.ShadowKind != ShadowCastKind.None) n++;
            }
            else
            {
                foreach (CpuSkinnedDraw dr in _cpuSkinnedDraws)
                    if (dr.ShadowKind != ShadowCastKind.None) n++;
            }
            return n;
        }
    }
}
