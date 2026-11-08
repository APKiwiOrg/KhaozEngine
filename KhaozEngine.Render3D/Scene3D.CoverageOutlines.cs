using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    void ReserveCoverageOutlineSurfaces()
    {
        int index = 0;
        foreach (var group in _meshOutlineGroups)
            for (int partIndex = 0; partIndex < group.Parts.Count; partIndex++)
            {
                var part = group.Parts[partIndex];
                if ((!part.DissolveComplement && part.Dissolve >= 1f) || (part.DissolveComplement && part.Dissolve <= 0f)) continue;
                if (part.Kind == MeshOutlinePartKind.Rigid)
                {
                    if (_slots.IsValid(part.Mesh.Index, part.Mesh.Generation) && _meshes[part.Mesh.Index] is not null) index++;
                    continue;
                }
                if (!_skinnedSlots.IsValid(part.SkinnedMesh.Index, part.SkinnedMesh.Generation)
                    || _skinnedMeshes[part.SkinnedMesh.Index] is null) continue;
                var p = PublishedCoverage(part.Motion, part.SkinnedMesh);
                if (p is not null)
                {
                    _targetOutlines.ReserveCoverageSurface(index, p.Surface);
                    group.Parts[partIndex] = part with { CoverageBindingSlot = index };
                }
                index++;
            }
    }

    bool EnqueueCoverageOutline(in MeshOutlinePart part, SkinnedMeshEntry mesh, int index)
    {
        var p = PublishedCoverage(part.Motion, part.SkinnedMesh);
        if (p is null) return false;
        var range = p.Owner.Topology.MaterialRanges[CoverageSlice(p, part.SkinnedMesh)];
        var draw = new CoveragePulledDraw(p.Surface, range.FirstIndex, range.IndexCount, (uint)index, checked((uint)part.CoverageBindingSlot),
            p.Owner.Actor, p.Owner.Bounds.Sphere(p), false, ShadowCastKind.None);
        _targetOutlines.SetCoverageMaterial(index, mesh.OutlineMaterialSet);
        _targetOutlines.EnqueueCoverage(draw, index, ToRender(part.World), mesh.AlphaCutoff,
            part.Dissolve, part.DissolveComplement, _frameOrigin);
        return true;
    }
}
