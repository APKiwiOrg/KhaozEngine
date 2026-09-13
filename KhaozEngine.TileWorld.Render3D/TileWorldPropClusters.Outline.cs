using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldPropClusters
{
    readonly Dictionary<PropClusterKey, SortedDictionary<long, TilePropLayerSnapshot>> _outlineSnapshots = new();
    readonly SinglePlacementList _outlinePlacement = new();
    MeshHandle? _outlineHlodMesh;
    PropClusterKey _outlineHlodKey;
    long _outlineHlodGeneration = -1;
    long _outlineHlodObject;
    bool _outlineHlodCached;

    internal bool DrawOutline(TileRegionProps regionProps, long objectId, Vector3 focus, Color color,
        float widthPixels)
    {
        foreach (KeyValuePair<string, TilePropLayerSnapshot> item in regionProps.Layers)
        {
            int placementIndex = IndexOf(item.Value.ObjectIds, objectId);
            if (placementIndex < 0) continue;
            PropClusterKey key = Key(item.Key, regionProps.Region, regionProps.Plane);
            if (_clusterOwner is null || !_clusterOwner.TryGetDrawState(key, focus, out PropClusterDrawState state))
                return true;

            MeshOutlineGroup group = _scene.BeginMeshOutline(color, widthPixels);
            if (state.DrawsIndividuals)
                DrawIndividualOutline(group, item.Value, placementIndex, focus, state.IndividualDissolveFloor);
            if (state.DrawsMerged)
                DrawMergedOutline(group, key, objectId, state);
            return true;
        }
        return false;
    }

    void DrawIndividualOutline(MeshOutlineGroup group, TilePropLayerSnapshot snapshot, int placementIndex,
        Vector3 focus, float dissolveFloor)
    {
        PropLayer layer = snapshot.Layer;
        if (layer.PartMeshes is null) return;
        _outlinePlacement.Value = snapshot.Placements[placementIndex];
        PropRenderer.EmitParts(_outlinePlacement, layer.PartMeshes, layer.LodPartMeshes, layer.LodDistance,
            focus, layer.DrawRadius, layer.FadeBandWidth, dissolveFloor,
            new OutlineSink(_scene, group), SubmitOutlinePart, lodCrossfadeWidth: layer.LodCrossfadeWidth);
    }

    static void SubmitOutlinePart(OutlineSink sink, MeshHandle mesh, Matrix4x4 world, float dissolve,
        float complement) => sink.Scene.DrawMeshOutlineDissolved(sink.Group, mesh, world, dissolve,
            complement > 0.5f);

    void DrawMergedOutline(MeshOutlineGroup group, PropClusterKey key, long objectId,
        in PropClusterDrawState state)
    {
        if (!_outlineSnapshots.TryGetValue(key, out SortedDictionary<long, TilePropLayerSnapshot>? generations)
            || !generations.TryGetValue(state.MergedSourceGeneration, out TilePropLayerSnapshot? accepted)) return;
        int placementIndex = IndexOf(accepted.ObjectIds, objectId);
        if (placementIndex < 0 || accepted.Layer.HlodSourceMeshes is null) return;

        if (!_outlineHlodCached || _outlineHlodKey != key
            || _outlineHlodGeneration != state.MergedSourceGeneration || _outlineHlodObject != objectId)
        {
            ClearOutlineHlodMesh();
            GltfMesh mesh = PropHlod.BuildPlacementMesh(accepted.Placements, placementIndex,
                accepted.Layer.HlodSourceMeshes, accepted.Layer.HlodWeldCell);
            _outlineHlodMesh = mesh.TriangleCount > 0 ? _scene.LoadMesh(mesh) : null;
            _outlineHlodKey = key;
            _outlineHlodGeneration = state.MergedSourceGeneration;
            _outlineHlodObject = objectId;
            _outlineHlodCached = true;
        }

        if (_outlineHlodMesh is { } handle)
            _scene.DrawMeshOutlineDissolved(group, handle, Matrix4x4.Identity, state.MergedDissolve,
                state.MergedComplement);
    }

    void RememberOutlineSnapshot(PropClusterKey key, long generation, TilePropLayerSnapshot snapshot)
    {
        if (!_outlineSnapshots.TryGetValue(key, out SortedDictionary<long, TilePropLayerSnapshot>? generations))
        {
            generations = new SortedDictionary<long, TilePropLayerSnapshot>();
            _outlineSnapshots.Add(key, generations);
        }
        generations[generation] = snapshot;
    }

    void PruneOutlineSnapshots(Vector3 focus)
    {
        foreach (KeyValuePair<PropClusterKey, SortedDictionary<long, TilePropLayerSnapshot>> item in _outlineSnapshots)
        {
            if (item.Value.Count <= 2) continue;
            long latest = LastKey(item.Value);
            long accepted = _clusterOwner is not null
                && _clusterOwner.TryGetDrawState(item.Key, focus, out PropClusterDrawState state)
                ? state.MergedSourceGeneration : -1;
            var remove = new List<long>();
            foreach (long generation in item.Value.Keys)
                if (generation != latest && generation != accepted) remove.Add(generation);
            foreach (long generation in remove) item.Value.Remove(generation);
        }
    }

    void ForgetOutlineSnapshots(RegionCoord region, int plane)
    {
        var remove = new List<PropClusterKey>();
        foreach (PropClusterKey key in _outlineSnapshots.Keys)
            if (key.X == region.Rx && key.Z == region.Rz && key.Plane == plane) remove.Add(key);
        foreach (PropClusterKey key in remove) _outlineSnapshots.Remove(key);
        if (_outlineHlodCached && _outlineHlodKey.X == region.Rx
            && _outlineHlodKey.Z == region.Rz && _outlineHlodKey.Plane == plane)
            ClearOutlineHlodMesh();
    }

    void ClearOutlineState()
    {
        ClearOutlineHlodMesh();
        _outlineSnapshots.Clear();
    }

    internal void ClearOutlineSelection() => ClearOutlineHlodMesh();

    void ClearOutlineHlodMesh()
    {
        if (_outlineHlodMesh is { } handle) _scene.UnloadMesh(handle);
        _outlineHlodMesh = null;
        _outlineHlodCached = false;
        _outlineHlodGeneration = -1;
        _outlineHlodObject = 0;
    }

    static int IndexOf(IReadOnlyList<long> ids, long objectId)
    {
        int lo = 0;
        int hi = ids.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            long value = ids[mid];
            if (value == objectId) return mid;
            if (value < objectId) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    static long LastKey(SortedDictionary<long, TilePropLayerSnapshot> values)
    {
        long last = -1;
        foreach (long key in values.Keys) last = key;
        return last;
    }

    readonly record struct OutlineSink(ITileWorldScene Scene, MeshOutlineGroup Group);

    sealed class SinglePlacementList : IReadOnlyList<PropPlacement>
    {
        public PropPlacement Value;
        public int Count => 1;
        public PropPlacement this[int index] => index == 0 ? Value : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<PropPlacement> GetEnumerator()
        {
            yield return Value;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
