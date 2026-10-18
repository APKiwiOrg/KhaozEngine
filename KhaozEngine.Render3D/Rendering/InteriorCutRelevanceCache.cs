using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Shares one bounds query across a rigid placement's colour, shadow and highlight submissions.</summary>
internal sealed class InteriorCutRelevanceCache
{
    readonly Dictionary<(int Index, int Generation), Dictionary<Matrix4x4, bool>> _placements = new();
    long _version = -1;
    InteriorCutFrame _frame;

    internal int Evaluations { get; private set; }

    internal bool MayIntersect(InteriorHeightField field, InteriorCutFrame frame,
        MeshHandle mesh, in Matrix4x4 world, in MeshBounds bounds)
    {
        if (_version != field.Version || _frame != frame)
        {
            _placements.Clear();
            _version = field.Version;
            _frame = frame;
        }
        var key = (mesh.Index, mesh.Generation);
        if (!_placements.TryGetValue(key, out Dictionary<Matrix4x4, bool>? placements))
            _placements.Add(key, placements = new Dictionary<Matrix4x4, bool>());
        if (placements.TryGetValue(world, out bool relevant)) return relevant;
        // Exactly the sphere used by the prop's main and shadow culls. Rigid meshes have no shader
        // sway displacement. Animated foliage has its own inflated bounds and never opts into this path.
        bounds.WorldSphere(world, out Vector3 centre, out float radius);
        var extent = new Vector3(radius);
        relevant = field.MayIntersect(centre - extent, centre + extent, frame);
        Evaluations++;
        placements.Add(world, relevant);
        return relevant;
    }

    // Opted-in layers are static. Keep absent placements until the field or frame changes, and drop all
    // placements of a mesh when its owner unloads it. A replaced slot never borrows its predecessor's answer.
    internal void Forget(MeshHandle mesh) => _placements.Remove((mesh.Index, mesh.Generation));
}
