using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

// Synchronous ground preparation and the accepted CPU source table share mesh admission with worker builds.
public sealed partial class TileWorldView
{
    // WaterDepthMode is init-only and captured at construction. Changing policy requires a new view.
    bool GroundSurfaceRequested => _waterDepthMode == TileWaterDepthMode.AuthoredBed;

    // Null is unavailable, including an unloaded or pending source. A retained empty snapshot is known empty.
    internal TileGroundSurfaceSnapshot? AcceptedGroundSurface(RegionCoord region, int plane) =>
        _loaded.TryGetValue(region, out RegionHandles? handles) && (uint)plane < (uint)handles.Surfaces.Length
            ? handles.Surfaces[plane] : null;

    void LoadGameplayRegion(RegionCoord region, TileRegionProps[]? props)
    {
        WaterAvailabilityChanged(region);
        var meshes = new MeshHandle?[_planes];
        var water = new TileWaterGeneration?[_planes];
        var surfaces = new TileGroundSurfaceSnapshot?[_planes];
        _loaded.EnsureCapacity(_loaded.Count + 1);
        _water.EnsureCapacity(_water.Count + _planes);
        TileRegionProps[] snapshots = props ?? new TileRegionProps[_planes];
        IReadOnlyList<GroundCoverInstance> cover;
        try
        {
            for (int plane = 0; plane < _planes; plane++)
            {
                TileWaterPreparation? preparation = CaptureWater(region, plane);
                if (preparation is not null)
                {
                    TileGroundCpuBuild ground = BuildGroundCpu(region, plane);
                    if (UploadGround(ground, preparation, out meshes[plane], out water[plane]))
                        surfaces[plane] = ground.Surface;
                    else Queue(region, plane);
                }
                if (props is null)
                    snapshots[plane] = _propClusters.Build(_doc, region, plane, OverrideLookup());
            }
            cover = BuildCover(region);
            SealGameplayWater(region, meshes, water, surfaces);
        }
        catch
        {
            FreeMeshes(meshes);
            foreach (TileWaterGeneration? generation in water) generation?.Dispose();
            for (int plane = 0; plane < _planes; plane++) ReleaseWater(region, plane);
            throw;
        }
        _loaded[region] = new RegionHandles(
            meshes, snapshots, cover, TileRegionResidencyState.Gameplay, profileManaged: false);
        for (int plane = 0; plane < _planes; plane++)
        {
            _loaded[region].Surfaces[plane] = surfaces[plane];
            if (water[plane] is { } generation) _water[(region, plane)] = generation;
        }
        RequestPropClusters(snapshots);
        GeneratedCoverCount += cover.Count;
    }

    // The synchronous factory used by both initial load and flush. Legacy views skip optical work entirely.
    internal TileGroundCpuBuild BuildGroundCpu(RegionCoord region, int plane)
    {
        _onCpuBuild?.Invoke(new TileWorldBuildKey(region, plane, TileWorldBuildKind.FullGround));
        return TileGroundCpuBuild.Build(_doc, _catalogs, region, plane, TileGroundLod.Full, _mesher, GroundSurfaceRequested);
    }

}
