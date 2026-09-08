using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

// Background ground ownership for the opt-in three-state residency path. The legacy public LoadRegion path
// remains synchronous in TileWorldView.cs.
public sealed partial class TileWorldView
{
    readonly TileWorldBuildQueue<TileGroundBuildInput, GltfMesh?> _groundBuilds;

    readonly record struct TileGroundBuildInput(
        TileWorldDocument Document,
        TileWorldCatalogs Catalogs,
        RegionCoord Region,
        int Plane,
        TileGroundLod Lod,
        TileGroundMesherOptions Options);

    void LoadProfileRegion(RegionCoord region, TileRegionResidencyState residency)
    {
        var props = new TileRegionProps[_planes];
        for (int plane = 0; plane < _planes; plane++)
            props[plane] = _propClusters.Build(_doc, region, plane, OverrideLookup());

        IReadOnlyList<GroundCoverInstance> cover = residency == TileRegionResidencyState.Gameplay
            ? BuildCover(region)
            : Array.Empty<GroundCoverInstance>();
        var handles = new RegionHandles(
            new MeshHandle?[_planes], props, cover, residency,
            new long[_planes]);
        _loaded[region] = handles;
        RequestPropClusters(props);
        GeneratedCoverCount += cover.Count;
        RequestGround(region, handles,
            residency == TileRegionResidencyState.Gameplay ? TileGroundLod.Full : TileGroundLod.Coarse4);
    }

    void PromoteToGameplay(RegionCoord region, RegionHandles handles)
    {
        IReadOnlyList<GroundCoverInstance> cover = BuildCover(region);
        handles.Cover = cover;
        handles.Residency = TileRegionResidencyState.Gameplay;
        GeneratedCoverCount += cover.Count;
        RequestGround(region, handles, TileGroundLod.Full);
    }

    void RequestGround(RegionCoord region, RegionHandles handles, TileGroundLod lod)
    {
        TileWorldDocument snapshot = SnapshotGroundDocument(region);
        TileWorldCatalogs catalogs = TileWorldCatalogs.Merge(_catalogs);
        var options = new TileGroundMesherOptions
        {
            JitterAmplitude = _options.Mesher.JitterAmplitude,
            SmoothNormals = _options.Mesher.SmoothNormals,
            Slots = _options.Mesher.Slots,
        };
        for (int plane = 0; plane < _planes; plane++)
            RequestGroundPlane(region, handles, plane, lod, snapshot, catalogs, options);
    }

    void RequestGroundPlane(RegionCoord region, RegionHandles handles, int plane, TileGroundLod lod)
    {
        TileWorldDocument snapshot = SnapshotGroundDocument(region);
        TileWorldCatalogs catalogs = TileWorldCatalogs.Merge(_catalogs);
        var options = new TileGroundMesherOptions
        {
            JitterAmplitude = _options.Mesher.JitterAmplitude,
            SmoothNormals = _options.Mesher.SmoothNormals,
            Slots = _options.Mesher.Slots,
        };
        RequestGroundPlane(region, handles, plane, lod, snapshot, catalogs, options);
    }

    void RequestGroundPlane(
        RegionCoord region, RegionHandles handles, int plane, TileGroundLod lod,
        TileWorldDocument snapshot, TileWorldCatalogs catalogs, TileGroundMesherOptions options)
    {
        TileWorldBuildKind kind = KindOf(lod);
        TileWorldBuildKind other = lod == TileGroundLod.Full
            ? TileWorldBuildKind.CoarseGround
            : TileWorldBuildKind.FullGround;
        _groundBuilds.Cancel(new TileWorldBuildKey(region, plane, other));
        long generation = ++handles.GroundGenerations[plane];
        _groundBuilds.Request(new TileWorldBuildRequest<TileGroundBuildInput>(
            new TileWorldBuildKey(region, plane, kind), generation,
            new TileGroundBuildInput(snapshot, catalogs, region, plane, lod, options)));
    }

    static TileWorldBuildKind KindOf(TileGroundLod lod) => lod == TileGroundLod.Full
        ? TileWorldBuildKind.FullGround
        : TileWorldBuildKind.CoarseGround;

    static GltfMesh? BuildGroundCpu(TileWorldBuildRequest<TileGroundBuildInput> request)
    {
        TileGroundBuildInput input = request.Input;
        return TileGroundMesher.Build(
            input.Document, input.Catalogs, input.Region, input.Plane, input.Lod, input.Options);
    }

    void ApplyGroundBuild(TileWorldBuildResult<GltfMesh?> result)
    {
        if (!_loaded.TryGetValue(result.Key.Region, out RegionHandles? handles)) return;
        int plane = result.Key.Plane;
        if ((uint)plane >= (uint)_planes || handles.GroundGenerations[plane] != result.Generation) return;

        TileGroundLod lod = result.Key.Kind == TileWorldBuildKind.FullGround
            ? TileGroundLod.Full
            : TileGroundLod.Coarse4;
        if ((lod == TileGroundLod.Full) != (handles.Residency == TileRegionResidencyState.Gameplay)) return;

        MeshHandle? replacement = result.Payload is null ? null : _scene.LoadMesh(result.Payload, _material);
        if (handles.Meshes[plane] is { } old) _scene.UnloadMesh(old);
        handles.Meshes[plane] = replacement;
        _water.Remove((result.Key.Region, plane));
    }

    void PumpGround(Vector3 focus)
    {
        int x = (int)MathF.Floor(TileWorldSpace.TileX(focus.X, _doc.TileSize));
        int z = (int)MathF.Floor(TileWorldSpace.TileZ(focus.Z, _doc.TileSize));
        _groundBuilds.Pump(RegionCoord.Of(x, z));
    }

    void PrimeGroundIfRequested(int maxRebuilds)
    {
        if (maxRebuilds == int.MaxValue) _groundBuilds.PrimeGameplay(default);
    }

    void CancelGround(RegionCoord region, int plane)
    {
        _groundBuilds.Cancel(new TileWorldBuildKey(region, plane, TileWorldBuildKind.FullGround));
        _groundBuilds.Cancel(new TileWorldBuildKey(region, plane, TileWorldBuildKind.CoarseGround));
    }

    void InvalidateGround(RegionCoord region, RegionHandles handles, int plane)
    {
        CancelGround(region, plane);
        handles.GroundGenerations[plane]++;
    }

    TileWorldDocument SnapshotGroundDocument(RegionCoord centre)
    {
        var snapshot = new TileWorldDocument
        {
            Id = _doc.Id,
            DisplayName = _doc.DisplayName,
            TileSize = _doc.TileSize,
            PlaneCount = _doc.PlaneCount,
            PlaneHeight = _doc.PlaneHeight,
        };
        for (int rz = centre.Rz - 1; rz <= centre.Rz + 1; rz++)
            for (int rx = centre.Rx - 1; rx <= centre.Rx + 1; rx++)
            {
                var coord = new RegionCoord(rx, rz);
                if (_doc.GetRegion(coord) is not { } source) continue;
                TileRegion copy = snapshot.GetOrCreateRegion(coord);
                for (int plane = 0; plane < _planes; plane++) CopyPlane(source.Planes[plane], copy.Planes[plane]);
                copy.Dirty = false;
            }
        return snapshot;
    }

    static void CopyPlane(TilePlaneData source, TilePlaneData destination)
    {
        destination.Heights = (short[]?)source.Heights?.Clone();
        destination.Underlay = (ushort[]?)source.Underlay?.Clone();
        destination.Overlay = (ushort[]?)source.Overlay?.Clone();
        destination.OverlayShape = (byte[]?)source.OverlayShape?.Clone();
        destination.OverlayRotation = (byte[]?)source.OverlayRotation?.Clone();
        destination.Settings = (byte[]?)source.Settings?.Clone();
    }
}
