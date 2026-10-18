using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly InteriorCutRelevanceCache _interiorRelevance = new();
    bool _interiorRelevanceDirty;
    InteriorCutFrame _interiorRelevanceFrame;

    bool RelevantInteriorCut(MeshHandle handle, in Matrix4x4 world)
    {
        if (InteriorCut is not { } field || !_slots.IsValid(handle.Index, handle.Generation)
            || _meshes[handle.Index] is not { } mesh) return false;
        return _interiorRelevance.MayIntersect(field, new InteriorCutFrame(_frameOrigin),
            handle, world, mesh.Bounds);
    }

    void RefreshInteriorRelevance()
    {
        var frame = new InteriorCutFrame(_frameOrigin);
        if (!_interiorRelevanceDirty && _interiorRelevanceFrame == frame) return;
        // InteriorCut promises the next render, including replacements after submission. Retain the request
        // separately from the effective flag so a previously missed draw can become relevant before packing.
        for (int i = 0; i < _instances.Items.Count; i++)
        {
            SceneInstances.Instance draw = _instances.Items[i];
            if (draw.InteriorCutRequested) _instances.SetInteriorCut(i, RelevantInteriorCut(draw.Mesh, draw.World));
        }
        foreach (MeshOutlineDrawGroup group in _meshOutlineGroups)
            for (int i = 0; i < group.Parts.Count; i++)
            {
                MeshOutlinePart part = group.Parts[i];
                if (part.InteriorCutRequested)
                    group.Parts[i] = part with { CutInsideInteriors = RelevantInteriorCut(part.Mesh, part.World) };
            }
        for (int i = 0; i < _silhouetteDraws.Count; i++)
        {
            SilhouetteDraw draw = _silhouetteDraws[i];
            if (draw.InteriorCutRequested)
                _silhouetteDraws[i] = draw with { CutInsideInteriors = RelevantInteriorCut(draw.Mesh, draw.World) };
        }
        _interiorRelevanceDirty = false;
        _interiorRelevanceFrame = frame;
    }
}
