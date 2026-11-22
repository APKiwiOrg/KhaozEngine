using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>
/// Skinned mesh views: a second index buffer over a loaded skinned mesh's vertices and materials.
/// <para>
/// Every skinned pass (colour, motion, cascade and point shadows, target outlines, dissolve) draws an entry's whole
/// index buffer from the draw record it copies at preparation. A view is a second entry that shares the source's
/// material sets, cutoff and inverse binds and owns a filtered index buffer. A plain view shares the source's
/// vertices and bounds. A displaced view owns its vertices and grows the source bounds by its displacement.
/// Each pass draws the view's geometry with the existing shader, pipeline and draw call.
/// </para>
/// <para>
/// A view is released one frame late. Queued draws name a handle and resolve it only when the frame is prepared, so
/// an outfit change in the middle of a frame must not drop the draws already queued through the old view. The view's
/// slot therefore stays live until the next frame boundary, and its owned buffers then ride the retire queue like
/// every other unload. Design: docs/design/ATTACHMENT-REGIONS-AND-LAYERS-DESIGN.md.
/// </para>
/// </summary>
public sealed partial class Scene3D
{
    // Live views per source slot. A view unloaded this frame has already left its count.
    readonly Dictionary<int, int> _skinnedLiveViews = new();
    // Views unloaded this frame. Released at the next frame boundary, or at once when their source unloads.
    readonly List<SkinnedMeshHandle> _pendingSkinnedViewReleases = new();

    /// <summary>Create a view that draws <paramref name="indices"/> over <paramref name="source"/>'s vertices with its
    /// materials, alpha cutoff, bounds and inverse binds. The view draws, outlines and casts shadows through every
    /// skinned API exactly like a loaded mesh. The index buffer takes the narrowest format that fits.
    /// <para>Refuses with <see cref="SkinnedMeshViewArgumentException"/>: a default or stale source, a source that is
    /// itself a view, a source carrying host coverage evidence, no indices, a count that is not whole triangles, or
    /// an index past the source's vertices. A refused call creates nothing. A fully hidden mesh is the caller's to
    /// skip, never a view of nothing.</para>
    /// <para>The source cannot unload while the view lives. <see cref="UnloadSkinnedMesh"/> on the view keeps it
    /// drawable until the frame ends, then frees its index buffer.</para></summary>
    public SkinnedMeshHandle CreateSkinnedMeshView(SkinnedMeshHandle source, ReadOnlySpan<uint> indices)
    {
        SkinnedMeshEntry entry = ValidateSkinnedViewSource(source, indices);
        SkinnedGltfMesh mesh = entry.Source;
        // The view's own mesh shares the source's vertex, bind and pose arrays and carries no coverage, so the
        // coverage paths treat a view as an unannotated mesh and never as a host.
        var viewMesh = new SkinnedGltfMesh(mesh.Vertices, indices.ToArray(), mesh.InverseBind, mesh.RestPose, mesh.Skeleton);
        IGpuBuffer indexBuffer = CreateIndexBuffer(viewMesh.Indices32, viewMesh.IndexFormat);
        return StoreSkinnedView(source, entry, viewMesh, entry.Vb, indexBuffer, in entry.Bounds);
    }

    SkinnedMeshEntry ValidateSkinnedViewSource(SkinnedMeshHandle source, ReadOnlySpan<uint> indices)
    {
        ObjectDisposedException.ThrowIf(_disposalComplete, this);
        if (!_skinnedSlots.IsValid(source.Index, source.Generation) || _skinnedMeshes[source.Index] is not { } entry)
            throw ViewRefusal(SkinnedMeshViewRefusal.SourceNotLive, source,
                "A skinned mesh view needs a live source handle.", nameof(source));
        if (entry.IsView)
            throw ViewRefusal(SkinnedMeshViewRefusal.SourceIsView, source,
                "A skinned mesh view cannot be made over another view.", nameof(source));
        if (entry.Source.Coverage is not null)
            throw ViewRefusal(SkinnedMeshViewRefusal.SourceHasCoverage, source,
                "A coverage host cannot be viewed: coverage tables index the host's own triangles.", nameof(source));
        if (indices.IsEmpty)
            throw ViewRefusal(SkinnedMeshViewRefusal.IndicesEmpty, source,
                "A skinned mesh view needs at least one triangle. Skip a fully hidden mesh instead.", nameof(indices));
        if (indices.Length % 3 != 0)
            throw new SkinnedMeshViewArgumentException(SkinnedMeshViewRefusal.IndexCountNotTriangles,
                "A skinned mesh view's index count must be whole triangles.", nameof(indices))
            { SourceIndex = source.Index, SourceGeneration = source.Generation, IndexCount = indices.Length };
        int vertexCount = entry.Source.Vertices.Length;
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] < (uint)vertexCount) continue;
            throw new SkinnedMeshViewArgumentException(SkinnedMeshViewRefusal.IndexOutOfRange,
                "A skinned mesh view's index reaches past the source's vertices.", nameof(indices))
            { SourceIndex = source.Index, SourceGeneration = source.Generation, IndexPosition = i, VertexCount = vertexCount };
        }

        return entry;
    }

    SkinnedMeshHandle StoreSkinnedView(SkinnedMeshHandle source, SkinnedMeshEntry entry, SkinnedGltfMesh viewMesh,
        IGpuBuffer vertexBuffer, IGpuBuffer indexBuffer, in MeshBounds bounds, double? viewDisplacement = null)
    {
        var view = new SkinnedMeshEntry(vertexBuffer, indexBuffer, viewMesh.Indices32.Length, viewMesh.IndexFormat,
            entry.MaterialSet, entry.SkinnedMaterialSet, entry.OutlineMaterialSet, entry.AlphaCutoff, viewMesh,
            in bounds, viewOf: source.Index, viewDisplacement: viewDisplacement);
        SkinnedMeshHandle handle = StoreSkinnedEntry(view, viewMesh.Vertices);
        _skinnedLiveViews[source.Index] = _skinnedLiveViews.GetValueOrDefault(source.Index) + 1;
        return handle;
    }

    /// <summary>Whether <paramref name="handle"/> names a live skinned mesh view, including one unloaded during the
    /// current frame, which keeps drawing until the frame ends. False for a loaded mesh and for a stale handle.</summary>
    public bool IsSkinnedMeshView(SkinnedMeshHandle handle) =>
        _skinnedSlots.IsValid(handle.Index, handle.Generation) && _skinnedMeshes[handle.Index] is { IsView: true };

    // UnloadSkinnedMesh's view branch. True when the handle names a view: it leaves its source's live count now and
    // keeps its slot until the frame boundary. A second unload of the same view is a double free.
    bool DeferSkinnedViewRelease(SkinnedMeshHandle h)
    {
        if (!_skinnedSlots.IsValid(h.Index, h.Generation) || _skinnedMeshes[h.Index] is not { IsView: true } view)
            return false;
        if (IsPendingSkinnedViewRelease(h))
            throw new ArgumentException(
                $"UnloadSkinnedMesh: view (index {h.Index}, generation {h.Generation}) was already unloaded this frame.",
                nameof(h));
        int live = _skinnedLiveViews[view.ViewOf] - 1;
        if (live == 0) _skinnedLiveViews.Remove(view.ViewOf);
        else _skinnedLiveViews[view.ViewOf] = live;
        _pendingSkinnedViewReleases.Add(h);
        return true;
    }

    // UnloadSkinnedMesh's source branch, run before the slot is freed. Refuses while a live view draws from the
    // source, then releases the views unloaded this frame at once. Their draws this frame are dropped with the
    // source's own, as a source unloaded mid frame always drops its queued draws. A stale handle falls through to
    // the slot map, which refuses it.
    void ReleaseViewsOfSkinnedSource(SkinnedMeshHandle h)
    {
        if (!_skinnedSlots.IsValid(h.Index, h.Generation)) return;
        RefuseSourcesWithLiveViews(new ReadOnlySpan<SkinnedMeshHandle>(in h), nameof(h));
        for (int i = _pendingSkinnedViewReleases.Count - 1; i >= 0; i--)
        {
            SkinnedMeshHandle view = _pendingSkinnedViewReleases[i];
            if (_skinnedMeshes[view.Index]!.ViewOf != h.Index) continue;
            ReleaseSkinnedView(view);
            _pendingSkinnedViewReleases.RemoveAt(i);
        }
    }

    /// <summary>Refuse, before anything changes, when any live handle in <paramref name="sources"/> has a live view.
    /// Throws <see cref="SkinnedMeshViewArgumentException"/> with <see cref="SkinnedMeshViewRefusal.SourceHasLiveViews"/>
    /// for the first such handle. Default, stale and view handles pass, and so does every handle once the scene is
    /// disposed, because those unloads refuse nothing. A view unloaded this frame no longer counts. An owner of several
    /// handles calls this first so a refusal leaves every handle it owns live.</summary>
    internal void RefuseSourcesWithLiveViews(ReadOnlySpan<SkinnedMeshHandle> sources, string? paramName)
    {
        if (_disposalComplete) return;
        foreach (SkinnedMeshHandle h in sources)
        {
            if (!_skinnedSlots.IsValid(h.Index, h.Generation) || !_skinnedLiveViews.TryGetValue(h.Index, out int live))
                continue;
            throw new SkinnedMeshViewArgumentException(SkinnedMeshViewRefusal.SourceHasLiveViews,
                "A skinned mesh cannot unload while a view of it lives. Unload its views first.", paramName)
            { SourceIndex = h.Index, SourceGeneration = h.Generation, LiveViews = live };
        }
    }

    // Frame boundary: every view unloaded during the frame that just ended has had its draws recorded.
    void ReleasePendingSkinnedViews()
    {
        foreach (SkinnedMeshHandle view in _pendingSkinnedViewReleases) ReleaseSkinnedView(view);
        _pendingSkinnedViewReleases.Clear();
    }

    // Material sets stay with the source. Only a displaced view owns its vertex buffer.
    void ReleaseSkinnedView(SkinnedMeshHandle view)
    {
        SkinnedMeshEntry entry = _skinnedMeshes[view.Index]!;
        _skinnedSlots.Free(view.Index, view.Generation);
        _retired.Retire(entry.OwnedViewVertexBuffer, entry.Ib, null);
        _skinnedMeshes[view.Index] = null;
        _skinnedCpuVerts[view.Index] = null;
    }

    bool IsPendingSkinnedViewRelease(SkinnedMeshHandle h)
    {
        foreach (SkinnedMeshHandle pending in _pendingSkinnedViewReleases)
            if (pending.Index == h.Index && pending.Generation == h.Generation) return true;
        return false;
    }

    // After a material set replacement rebuilt every source, each view takes its source's new sets. A view's source
    // is always live, because a source cannot unload while a view of it lives and releases its unloaded views first.
    void RebindSkinnedViewSets()
    {
        for (int i = 0; i < _skinnedMeshes.Count; i++)
            if (_skinnedMeshes[i] is { IsView: true } view)
                _skinnedMeshes[i] = view.WithSourceSets(_skinnedMeshes[view.ViewOf]!);
    }

    static SkinnedMeshViewArgumentException ViewRefusal(SkinnedMeshViewRefusal reason, SkinnedMeshHandle source,
        string message, string paramName) =>
        new(reason, message, paramName) { SourceIndex = source.Index, SourceGeneration = source.Generation };
}
