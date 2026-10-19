using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

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
            || p.Outcome.Presentation != CoveragePresentation.Dispatched) return false;
        int slice = CoverageSlice(p, part.SkinnedMesh);
        var range = p.Owner.Topology.MaterialRanges[slice];
        var r = p.GpuReservation;
        var batch = _coverageGpu!.Batch!;
        var surface = new CoverageGpuSurfaceView(batch.Current, batch.Previous, batch.StaticData,
            r.CurrentBase, r.PreviousBase, r.StaticBase, r.VertexCount, r.TriangleCount);
        var draw = new CoveragePulledDraw(surface, range.FirstIndex, range.IndexCount, 0, p.Actor,
            CoverageSphere(p), false, ShadowCastKind.None);
        _targetOutlines.SetCoverageMaterial(drawIndex, mesh.OutlineMaterialSet);
        _targetOutlines.EnqueueCoverage(draw, drawIndex, ToRender(part.World), mesh.AlphaCutoff,
            part.Dissolve, part.DissolveComplement, _frameOrigin);
        return true;
    }
}
