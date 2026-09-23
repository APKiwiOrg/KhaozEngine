using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>Compacted per-frame CPU and GPU skinned draw records shared by colour and shadow passes.</summary>
public sealed partial class Scene3D
{
    /// <summary>One GPU-skinned draw (built per frame in RenderInternal when <see cref="UseGpuSkinning"/> is on).
    /// Carries the mesh's rest-pose vertex + index buffers (uploaded once at load - the GPU deforms them), the
    /// set-1 material set, the composed bone-palette slice (offset into <c>_boneMatrices</c> + bone count), the
    /// compacted per-caster slot, and the per-draw matrices/material the vertex shader folds. The shadow depth
    /// pass packs + draws each casting entry reached by a key or point-light shadow volume. The
    /// main pass skips a <see cref="VisibleMain"/>-false entry (camera-culled, kept only as a shadow caster).</summary>
    readonly struct GpuSkinnedDraw
    {
        public readonly IGpuBuffer RestVb, Ib;
        public readonly int IndexCount;
        public readonly GpuIndexFormat IndexFormat;
        public readonly IGpuResourceSet? SkinnedMaterialSet;
        public readonly int BoneSpanStart;   // into _boneMatrices (submission index * MaxBonesPerDraw)
        public readonly int BoneCount;
        public readonly uint Slot;            // compacted per-caster slot: the main header window AND the shared palette
        public readonly Matrix4x4 World;
        public readonly PointShadowCasterSphere PointSphere;
        public readonly Vector4 Tint, Emissive, SpecParams;
        public readonly Vector2 DissolveParams;
        public readonly bool VisibleMain;
        public readonly bool Dissolve;
        public readonly ShadowCastKind ShadowKind;   // how it takes part in the depth pass (issue #387)
        public GpuSkinnedDraw(IGpuBuffer restVb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat,
            IGpuResourceSet? skinnedMaterialSet, int boneSpanStart, int boneCount, uint slot,
            in Matrix4x4 world, Vector4 tint, Vector4 emissive, Vector4 specParams, Vector2 dissolveParams,
            bool visibleMain, bool dissolve,
            ShadowCastKind shadowKind, PointShadowCasterSphere pointSphere)
        {
            RestVb = restVb; Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat;
            SkinnedMaterialSet = skinnedMaterialSet; BoneSpanStart = boneSpanStart; BoneCount = boneCount; Slot = slot;
            World = world; Tint = tint; Emissive = emissive; SpecParams = specParams;
            DissolveParams = dissolveParams; VisibleMain = visibleMain; Dissolve = dissolve;
            ShadowKind = shadowKind;
            PointSphere = pointSphere;
        }
    }

    /// <summary>One CPU-skinned draw: the mesh's index buffer + count, the base vertex of its deformed verts in
    /// the shared skinned vertex stream, and its optional material set. Built per frame in RenderInternal.
    /// Every entry here was CPU-skinned and uploaded (needed by at least one of the main or shadow pass). The
    /// key and point shadow passes select entries whose <see cref="ShadowKind"/> casts,
    /// while the main pass draw loop skips an entry whose <see cref="VisibleMain"/> is false (camera-culled,
    /// kept only because it is still a shadow caster).</summary>
    readonly struct CpuSkinnedDraw
    {
        public readonly IGpuBuffer Ib;
        public readonly int IndexCount;
        public readonly GpuIndexFormat IndexFormat;
        public readonly int BaseVertex;
        public readonly IGpuResourceSet? MaterialSet;
        public readonly bool Dissolve;   // route through the CharDissolve pipeline variant
        public readonly bool VisibleMain;   // draw in the main visible pass, always true when culling is off
        public readonly ShadowCastKind ShadowKind;   // how it takes part in the depth pass (issue #387)
        public readonly PointShadowCasterSphere PointSphere;
        public CpuSkinnedDraw(IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, int baseVertex,
            IGpuResourceSet? materialSet, bool dissolve, bool visibleMain, ShadowCastKind shadowKind,
            PointShadowCasterSphere pointSphere)
        {
            Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat; BaseVertex = baseVertex; MaterialSet = materialSet; Dissolve = dissolve; VisibleMain = visibleMain;
            ShadowKind = shadowKind;
            PointSphere = pointSphere;
        }
    }

}
