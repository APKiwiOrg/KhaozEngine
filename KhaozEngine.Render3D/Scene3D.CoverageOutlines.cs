using System;
using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>Queues an outline using the descriptor's frozen coverage surface, model and dissolve state.</summary>
    public void DrawSkinnedOutline(MeshOutlineGroup group, in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette)
    {
        MeshOutlineDrawGroup target = RequireMeshOutlineGroup(group);
        CoveragePreparation? p = ValidateCoverageDraw(draw, palette, true);
        int before = target.Parts.Count;
        AddSkinnedOutlinePart(group, draw.Mesh, palette, draw.Model, draw.Dissolve, draw.DissolveComplement > .5f);
        if (target.Parts.Count == before) return;
        target.Parts[^1] = target.Parts[^1] with { Coverage = draw.Coverage };
        RecordCoverageDraw(p, draw, true);
    }

    bool EnqueueCoverageOutline(in MeshOutlinePart part, SkinnedMeshEntry mesh, int drawIndex)
    {
        if (!TryFrozenCoverage(part.Coverage, out CoveragePreparation p)
            || p.Outcome.Presentation != CoveragePresentation.Covered) return false;
        int slice = CoverageSlice(p, part.SkinnedMesh);
        var range = p.Owner.Topology.MaterialRanges[slice];
        _targetOutlines.EnqueuePrepared(_model.CpuSkinnedVertexBuffer!, p.Owner.Indices[slice], range.IndexCount,
            GpuIndexFormat.UInt32, mesh.OutlineMaterialSet, drawIndex, ToRender(part.World), mesh.AlphaCutoff,
            part.Dissolve, part.DissolveComplement, _frameOrigin, p.BaseVertices[slice]);
        return true;
    }
}
