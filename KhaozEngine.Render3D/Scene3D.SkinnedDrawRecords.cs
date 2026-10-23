using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>Compacted per-frame CPU and GPU skinned draw records shared by colour and shadow passes.</summary>
public sealed partial class Scene3D
{
    readonly List<CoveragePulledDraw> _coverageDraws = new();
    readonly List<ModelRenderer.InstanceData> _coverageInstances = new();
    readonly List<IGpuResourceSet?> _coverageMaterials = new();

    System.Action<string, CoveragePulledDraw>? _coveragePullObserverForTests;
    internal System.Action<string, CoveragePulledDraw>? CoveragePullObserverForTests
    {
        set { _model.CoveragePullObserverForTests = value; _targetOutlines.CoveragePullObserverForTests = value; _coveragePullObserverForTests = value; }
    }

    void DrawCoverageMain(IGpuCommandList commands)
    {
        if (_coverageDraws.Count == 0) return;
        for (int i = 0; i < _coverageDraws.Count; i++) _model.PackCoverage(_coverageDraws[i], _coverageInstances[i]);
        _model.UploadCoverage(commands);
        for (int i = 0; i < _coverageDraws.Count; i++)
        {
            var draw = _coverageDraws[i];
            if (!draw.VisibleMain) continue;
            if (_res.MotionAllocated)
            {
                _model.DrawCoverageMotion(commands, draw, CoveragePreviousModel(draw.Actor, _coverageInstances[i].Model), _coverageMaterials[i]);
            }
            else _model.DrawCoverage(commands, draw, _coverageMaterials[i]);
            CountSkinnedDraw(draw.CornerCount);
        }
    }
    // Indexed by this frame's submission index. Begin resets the queue, and each submitted index is overwritten.
    PreparedSkinningPose[] _submittedSkinningPoses = System.Array.Empty<PreparedSkinningPose>();

    /// <summary>One GPU-skinned draw (built per frame in RenderInternal when <see cref="UseGpuSkinning"/> is on).
    /// Carries the mesh's rest-pose vertex + index buffers (uploaded once at load - the GPU deforms them), the
    /// set-1 material set, the prepared pose, the legacy LBS motion slice, the method-local palette slot, the
    /// compacted per-caster slot, and the per-draw matrices/material the vertex shader folds. The shadow depth
    /// pass packs + draws each casting entry reached by a key or point-light shadow volume. The
    /// main pass skips a <see cref="VisibleMain"/>-false entry (camera-culled or shadow-only).</summary>
    readonly struct GpuSkinnedDraw
    {
        public readonly IGpuBuffer RestVb, Ib;
        public readonly int IndexCount;
        public readonly GpuIndexFormat IndexFormat;
        public readonly IGpuResourceSet? SkinnedMaterialSet;
        public readonly int BoneSpanStart;   // into _boneMatrices (submission index * MaxBonesPerDraw)
        public readonly int BoneCount;
        public readonly PreparedSkinningPose PreparedPose;
        public readonly uint PaletteSlot;
        public readonly uint Slot;            // compacted per-caster header slot, independent of method-local palettes
        public readonly Matrix4x4 World;
        public readonly PointShadowCasterSphere PointSphere;
        public readonly Vector4 Tint, Emissive, SpecParams;
        public readonly Vector2 DissolveParams;
        public readonly float DissolveComplement;
        public readonly bool VisibleMain;
        public readonly bool Dissolve;
        public readonly ShadowCastKind ShadowKind;   // how it takes part in the depth pass (issue #387)
        public readonly MotionKey Motion;       // the submission's key, None when unkeyed (the skinned motion pass reads it)
        public GpuSkinnedDraw(IGpuBuffer restVb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat,
            IGpuResourceSet? skinnedMaterialSet, int boneSpanStart, int boneCount, uint slot,
            in PreparedSkinningSlot palette,
            in Matrix4x4 world, Vector4 tint, Vector4 emissive, Vector4 specParams, Vector2 dissolveParams,
            float dissolveComplement, bool visibleMain, bool dissolve,
            ShadowCastKind shadowKind, PointShadowCasterSphere pointSphere, MotionKey motion)
        {
            RestVb = restVb; Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat;
            SkinnedMaterialSet = skinnedMaterialSet; BoneSpanStart = boneSpanStart; BoneCount = boneCount; Slot = slot;
            PreparedPose = palette.Pose; PaletteSlot = palette.Slot;
            World = world; Tint = tint; Emissive = emissive; SpecParams = specParams;
            DissolveParams = dissolveParams; VisibleMain = visibleMain; Dissolve = dissolve;
            DissolveComplement = dissolveComplement;
            ShadowKind = shadowKind;
            PointSphere = pointSphere;
            Motion = motion;
        }
    }

    /// <summary>One CPU-skinned draw: the mesh's index buffer + count, the base vertex of its deformed verts in
    /// the shared skinned vertex stream, and its optional material set. Built per frame in RenderInternal.
    /// Every entry here was CPU-skinned and uploaded (needed by at least one of the main or shadow pass). The
    /// key and point shadow passes select entries whose <see cref="ShadowKind"/> casts,
    /// while the main pass draw loop skips an entry whose <see cref="VisibleMain"/> is false (camera-culled,
    /// kept only because it is still a shadow caster), including a shadow-only draw.</summary>
    readonly struct CpuSkinnedDraw
    {
        public readonly IGpuBuffer Ib;
        public readonly int IndexCount;
        public readonly GpuIndexFormat IndexFormat;
        public readonly int BaseVertex;
        public readonly IGpuResourceSet? MaterialSet;
        public readonly bool Dissolve;   // route through the CharDissolve pipeline variant
        public readonly bool VisibleMain;   // draw in the main visible pass, false for shadow-only draws
        public readonly ShadowCastKind ShadowKind;   // how it takes part in the depth pass (issue #387)
        public readonly PointShadowCasterSphere PointSphere;
        public readonly MotionKey Motion;      // the submission's key, None when unkeyed
        public readonly int MeshIndex;         // the skinned mesh slot, for its rest vertices (CPU-skinned motion)
        public readonly int VertexCount;       // how many deformed vertices start at BaseVertex
        public CpuSkinnedDraw(IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, int baseVertex,
            IGpuResourceSet? materialSet, bool dissolve, bool visibleMain, ShadowCastKind shadowKind,
            PointShadowCasterSphere pointSphere, MotionKey motion, int meshIndex, int vertexCount)
        {
            Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat; BaseVertex = baseVertex; MaterialSet = materialSet; Dissolve = dissolve; VisibleMain = visibleMain;
            ShadowKind = shadowKind;
            PointSphere = pointSphere;
            Motion = motion; MeshIndex = meshIndex; VertexCount = vertexCount;
        }
    }

}

/// <summary>Assigned only after visibility admission. Palette slots compact independently per method while
/// draw headers keep their original compacted order.</summary>
internal readonly struct PreparedSkinningSlot
{
    internal PreparedSkinningPose Pose { get; }
    internal uint Slot { get; }

    internal PreparedSkinningSlot(in PreparedSkinningPose pose, ref uint lbsSlots, ref uint dqSlots)
    {
        Pose = pose;
        Slot = pose.Profile.Method == SkinningMethod.DualQuaternionRigid ? dqSlots++ : lbsSlots++;
    }
}
