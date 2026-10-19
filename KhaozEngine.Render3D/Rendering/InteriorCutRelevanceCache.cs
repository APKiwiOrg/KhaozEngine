using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Caches a rigid placement's bounds relevance and reuses its flag in stable submission slots.</summary>
internal sealed class InteriorCutRelevanceCache
{
    readonly Dictionary<(int Index, int Generation), Dictionary<(Matrix4x4 World, float Margin), bool>> _placements = new();
    readonly List<Placement> _submissions = new();
    int _submission;
    long _version = -1;
    InteriorCutFrame _frame;

    internal int Evaluations { get; private set; }
    internal int Lookups { get; private set; }

    readonly record struct Placement(MeshHandle Mesh, Matrix4x4 World, float Margin, bool Relevant);

    internal void BeginFrame() => _submission = 0;

    internal bool MayIntersect(InteriorHeightField field, InteriorCutFrame frame,
        MeshHandle mesh, in Matrix4x4 world, in MeshBounds bounds, float outwardMargin = 0f)
    {
        if (_version != field.Version || _frame != frame)
        {
            _placements.Clear();
            _submissions.Clear();
            _submission = 0;
            _version = field.Version;
            _frame = frame;
        }
        int submission = _submission++;
        if (submission < _submissions.Count)
        {
            Placement previous = _submissions[submission];
            if (previous.Mesh.Equals(mesh) && previous.World == world && previous.Margin == outwardMargin)
                return previous.Relevant;
        }
        // Static draws keep the previous publication's flag in their submission slot. Only a changed slot
        // hashes its transform. The placement map retains answers when culling or submission order changes.
        Lookups++;
        var key = (mesh.Index, mesh.Generation);
        if (!_placements.TryGetValue(key, out Dictionary<(Matrix4x4, float), bool>? placements))
            _placements.Add(key, placements = new Dictionary<(Matrix4x4, float), bool>());
        var placement = (world, outwardMargin);
        if (!placements.TryGetValue(placement, out bool relevant))
        {
            // Exactly the sphere used by the prop's main and shadow culls. Rigid meshes have no shader
            // sway displacement. Animated foliage has its own inflated bounds and never opts into this path.
            bounds.WorldSphere(world, out Vector3 centre, out float radius);
            var extent = new Vector3(radius + MathF.Abs(outwardMargin));
            relevant = field.MayIntersect(centre - extent, centre + extent, frame);
            Evaluations++;
            placements.Add(placement, relevant);
        }
        var stored = new Placement(mesh, world, outwardMargin, relevant);
        if (submission < _submissions.Count) _submissions[submission] = stored;
        else _submissions.Add(stored);
        return relevant;
    }

    // Opted-in layers are static. Keep absent placements until the field or frame changes, and drop all
    // placements of a mesh when its owner unloads it. A replaced slot never borrows its predecessor's answer.
    internal void Forget(MeshHandle mesh)
    {
        _placements.Remove((mesh.Index, mesh.Generation));
        _submissions.RemoveAll(placement => placement.Mesh.Equals(mesh));
    }
}
