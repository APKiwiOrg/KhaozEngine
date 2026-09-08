using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldPropClusters
{
    ITileWorldPropClusterOwner? _clusterOwner;
    TileWorldBuildQueue<PropClusterBuildRequest, PropClusterCpuBuild>? _clusterBuilds;
    readonly HashSet<PropClusterKey> _requestedClusters = new();
    float _tileSize;

    void InitializeRendering(float tileSize, TileWorldBuildQueueOptions? options, IChunkBuildDispatcher? dispatcher)
    {
        _tileSize = tileSize;
        if (_layers.Length == 0) return;

        _clusterOwner = _scene.CreatePropClusterOwner();
        _clusterBuilds = new TileWorldBuildQueue<PropClusterBuildRequest, PropClusterCpuBuild>(
            request => _clusterOwner.BuildCpu(request.Input),
            result => _clusterOwner.Apply(result.Payload.Key, result.Payload),
            options,
            dispatcher);
    }

    internal void Request(TileRegionProps snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_clusterBuilds is null) return;

        foreach (KeyValuePair<string, TilePropLayerSnapshot> item in snapshot.Layers)
        {
            PropClusterKey key = Key(item.Key, snapshot.Region, snapshot.Plane);
            if (!_requestedClusters.Add(key)) _clusterOwner!.Invalidate(key);
            _clusterBuilds.Request(new TileWorldBuildRequest<PropClusterBuildRequest>(
                new TileWorldBuildKey(snapshot.Region, snapshot.Plane, TileWorldBuildKind.Hlod, item.Key),
                snapshot.Generation,
                new PropClusterBuildRequest(
                    key,
                    snapshot.Generation,
                    Area(snapshot.Region),
                    item.Value.Layer,
                    item.Value.Placements)));
        }
    }

    internal void Pump(Vector3 focus)
    {
        if (_clusterBuilds is null) return;
        int x = (int)MathF.Floor(TileWorldSpace.TileX(focus.X, _tileSize));
        int z = (int)MathF.Floor(TileWorldSpace.TileZ(focus.Z, _tileSize));
        _clusterBuilds.Pump(RegionCoord.Of(x, z));
    }

    internal void Draw(Vector3 focus) => _clusterOwner?.Draw(focus);

    internal void Unload(RegionCoord region, int plane)
    {
        if (_clusterBuilds is null) return;
        for (int i = 0; i < _layers.Length; i++)
        {
            string layerId = _layers[i].Definition.Id;
            PropClusterKey key = Key(layerId, region, plane);
            _clusterBuilds.Cancel(new TileWorldBuildKey(region, plane, TileWorldBuildKind.Hlod, layerId));
            _clusterOwner!.Unload(key);
            _requestedClusters.Remove(key);
        }
    }

    void DisposeRendering()
    {
        _clusterBuilds?.Dispose();
        _clusterBuilds = null;
        _clusterOwner?.Dispose();
        _clusterOwner = null;
        _requestedClusters.Clear();
    }

    PropClusterKey Key(string layerId, RegionCoord region, int plane) =>
        new(layerId, region.Rx, region.Rz, plane);

    RectArea Area(RegionCoord region)
    {
        float minX = region.OriginX * _tileSize;
        float maxX = (region.OriginX + TileRegion.Size) * _tileSize;
        float minZ = -(region.OriginZ + TileRegion.Size) * _tileSize;
        float maxZ = -region.OriginZ * _tileSize;
        return new RectArea(minX, minZ, maxX, maxZ);
    }
}
