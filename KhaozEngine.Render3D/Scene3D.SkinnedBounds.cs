using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    // Borrowed only while measuring the reach of a cached attachment. Consumers cannot mutate the upload.
    internal ReadOnlySpan<SkinnedVertex> SkinnedSourceVertices(SkinnedMeshHandle source) =>
        SkinnedBoundsSource(source).Source.Vertices;

    /// <summary>Expand a source's culling envelope and carry it to every view that can still draw this frame.</summary>
    internal void GrowSkinnedBounds(SkinnedMeshHandle source, in MeshBounds extra)
    {
        SkinnedMeshEntry entry = SkinnedBoundsSource(source);
        Vector3 min = Vector3.Min(entry.Bounds.Min, extra.Min);
        Vector3 max = Vector3.Max(entry.Bounds.Max, extra.Max);
        if (min == entry.Bounds.Min && max == entry.Bounds.Max) return;

        entry.Bounds = new MeshBounds(min, max);
        if (!_skinnedLiveViews.ContainsKey(source.Index) && _pendingSkinnedViewReleases.Count == 0) return;
        foreach (SkinnedMeshEntry? view in _skinnedMeshes)
            if (view is { IsView: true } && view.ViewOf == source.Index)
                view.Bounds = BoundsWithSkinnedViewDisplacement(in entry.Bounds, view.ViewDisplacement);
    }

    SkinnedMeshEntry SkinnedBoundsSource(SkinnedMeshHandle source) =>
        _skinnedSlots.IsValid(source.Index, source.Generation) && _skinnedMeshes[source.Index] is { IsView: false } entry
            ? entry
            : throw new ArgumentException("Skinned bounds growth needs a live source mesh.", nameof(source));

    internal MeshBounds SkinnedBoundsAt(SkinnedMeshHandle handle) => _skinnedMeshes[handle.Index]!.Bounds;
}
