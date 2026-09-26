using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The draw entry points of <see cref="Scene3D"/> (TEMPORAL-FOUNDATIONS-DESIGN section 3). A draw descriptor carries
    /// every knob of one draw, and every plain overload builds one with no motion key and forwards here, so there is
    /// exactly one path into each draw queue. A new draw knob becomes a descriptor property rather than another
    /// overload.
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>
        /// Queue one rigid instance with every knob in <paramref name="draw"/>: mesh, absolute world transform, tint,
        /// material, dissolve and edge, shadow casting, the shadow-only and complement phases, and the motion key.
        /// Every <c>Draw(MeshHandle, ...)</c> overload, both <c>Draw(PropHandle, ...)</c> overloads and
        /// <see cref="DrawShadowOnly"/> forward here with <see cref="MotionKey.None"/>, so what they queue is
        /// unchanged. A shadow-only draw that does not cast draws nowhere and throws <see cref="ArgumentException"/>,
        /// exactly as the instance queue always has.
        /// </summary>
        public void Draw(in RigidInstanceDraw draw) => _instances.Add(in draw);

        /// <summary>Queue one instance: draw <paramref name="mesh"/> at world transform <paramref name="world"/> (no tint).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world) => Draw(new RigidInstanceDraw(mesh, world));

        /// <summary>Queue one instance with a per-instance RGBA <paramref name="tint"/> that multiplies the lit color.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint) => Draw(new RigidInstanceDraw(mesh, world) { Tint = tint });

        /// <summary>Queue one instance with a per-instance <paramref name="tint"/> and <paramref name="material"/>
        /// (emissive glow + specular).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material)
            => Draw(new RigidInstanceDraw(mesh, world) { Tint = tint, Material = material });

        /// <summary>As the material overload, but dissolves this rigid instance (issue #253): <paramref name="dissolve"/>
        /// is the 0..1 threshold (0 = solid, 1 = fully gone), with a glowing emissive edge of <paramref name="edgeColor"/>
        /// and width <paramref name="edgeWidth"/> (a fraction of the noise range). Mirrors the <see cref="DrawSkinned(SkinnedMeshHandle,ReadOnlySpan{Matrix4x4},Matrix4x4,Color,Material,float,float,Color)"/>
        /// dissolve overload but on the instanced path: no pipeline switch and no batching change (the discard folds
        /// into the shared ModelFrag), so it stays one instanced draw per mesh. A <paramref name="dissolve"/> of 0
        /// draws exactly like the material overload, so it is safe to call unconditionally while gating the value on a
        /// fade. Presentation only - never feed sim/RNG/netcode from the dissolve value.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolve, float edgeWidth, Color edgeColor)
            => Draw(new RigidInstanceDraw(mesh, world)
            {
                Tint = tint, Material = material, Dissolve = dissolve, DissolveEdgeWidth = edgeWidth,
                DissolveEdgeColor = edgeColor,
            });

        /// <summary>The queued rigid instances in submission order, live until the next <see cref="Begin"/>. For
        /// tests.</summary>
        internal IReadOnlyList<SceneInstances.Instance> QueuedInstancesForTests => _instances.Items;

        /// <summary>
        /// Queue one skinned draw with every knob in <paramref name="draw"/>. <paramref name="boneMatrices"/> are this
        /// frame's joint world transforms (model space), one per bone in the mesh's skin, and the engine composes them
        /// with the mesh's inverse-bind. Every <c>DrawSkinned</c> overload forwards here with
        /// <see cref="MotionKey.None"/>. A stale or default handle queues nothing, and a bone count that differs from
        /// the mesh's throws <see cref="ArgumentException"/>. Presentation only - never feed sim/RNG/netcode from bone
        /// state.
        /// </summary>
        public void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> boneMatrices)
        {
            SkinnedMeshHandle h = draw.Mesh;
            if (!_skinnedSlots.IsValid(h.Index, h.Generation)) return;
            var entry = _skinnedMeshes[h.Index];
            if (entry is null) return;
            // This draw's bones go into slot N (N = its submission index), padded to the per-draw window so the
            // dynamic-offset bind selects exactly this draw's palette. Slot N maps to bone byte offset
            // N * SlotBytes and to instance buffer element N in the render loop.
            int slot = _skinnedInstances.Items.Count;
            ComposeBonesIntoSlot(_boneMatrices, slot, boneMatrices, entry.InverseBind);
            _skinnedInstances.Add(in draw);
        }

        /// <summary>Queue one skinned draw. <paramref name="boneMatrices"/> are this frame's joint world
        /// transforms (model space), one per bone in the mesh's skin, and the engine composes them with the mesh's
        /// inverse-bind. Passing the mesh's <see cref="SkinnedGltfMesh.RestPose"/> yields no deformation.
        /// Presentation only - never feed sim/RNG/netcode from bone state.</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint)
            => DrawSkinned(new SkinnedInstanceDraw(h, model) { Tint = tint }, boneMatrices);

        /// <summary>As <see cref="DrawSkinned(SkinnedMeshHandle,ReadOnlySpan{Matrix4x4},Matrix4x4,Color)"/> with an
        /// explicit <paramref name="material"/> (emissive + specular).</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint, Material material)
            => DrawSkinned(new SkinnedInstanceDraw(h, model) { Tint = tint, Material = material }, boneMatrices);

        /// <summary>As the material overload, but dissolves the mesh for a <see cref="CharDissolve"/> teleport:
        /// <paramref name="dissolve"/> is the 0..1 threshold (0 = solid, 1 = fully gone, fed from
        /// <see cref="ITransition.Cover"/>), with a glowing emissive edge of <paramref name="edgeColor"/> and width
        /// <paramref name="edgeWidth"/> (a fraction of the noise range). A <paramref name="dissolve"/> of 0 draws
        /// exactly like the material overload (the normal pipeline), so it is safe to call unconditionally while
        /// gating the value on the transition. The draw's SHADOW erodes with the same mask (issue #387).</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint,
            Material material, float dissolve, float edgeWidth, Color edgeColor)
            => DrawSkinned(new SkinnedInstanceDraw(h, model)
            {
                Tint = tint, Material = material, Dissolve = dissolve, DissolveEdgeWidth = edgeWidth,
                DissolveEdgeColor = edgeColor,
            }, boneMatrices);

        /// <summary>The queued skinned draws in submission order, live until the next <see cref="Begin"/>. For
        /// tests.</summary>
        internal IReadOnlyList<SkinnedSceneInstances.Instance> QueuedSkinnedInstancesForTests => _skinnedInstances.Items;

        /// <summary>A copy of skinned slot <paramref name="slot"/>'s composed palette window, all
        /// <see cref="SkinningMath.MaxBonesPerDraw"/> matrices. For tests.</summary>
        internal Matrix4x4[] BonePaletteSlotForTests(int slot)
            => _boneMatrices.GetRange(slot * SkinningMath.MaxBonesPerDraw, SkinningMath.MaxBonesPerDraw).ToArray();
    }
}
