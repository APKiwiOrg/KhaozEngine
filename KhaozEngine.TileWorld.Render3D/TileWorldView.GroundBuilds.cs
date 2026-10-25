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

    // Every plane stages its own publication group. After the last upload all groups validate again, then the
    // region and its groups publish together. A later plane can announce a source change while earlier planes
    // are staged. Source arrivals consumed by this capture must not cause a redundant upload on the first draw.
    void LoadGameplayRegion(RegionCoord region, TileRegionProps[]? props)
    {
        WaterAvailabilityChanged(region);
        var groups = new TileWaterPublication.Group?[_planes];
        _loaded.EnsureCapacity(_loaded.Count + 1);
        TileRegionProps[] snapshots = props ?? new TileRegionProps[_planes];
        IReadOnlyList<GroundCoverInstance> cover;
        try
        {
            for (int plane = 0; plane < _planes; plane++)
            {
                TileWaterPreparation preparation = CaptureWater(region, plane);
                TileGroundCpuBuild ground = BuildGroundCpu(region, plane);
                groups[plane] = StageGround(region, plane, ground, preparation);
                if (groups[plane] is null) Queue(region, plane);
                if (props is null)
                    snapshots[plane] = _propClusters.Build(_doc, region, plane, OverrideLookup());
            }
            cover = BuildCover(region);
            for (int plane = 0; plane < _planes; plane++)
            {
                if (groups[plane] is not { } group) continue;
                if (SealGround(group))
                {
                    if (_dirty.Remove((region, plane))) _dirtyOrder.Remove((region, plane));
                    continue;
                }
                DiscardGround(group);
                groups[plane] = null;
                Queue(region, plane);
            }
        }
        catch
        {
            foreach (TileWaterPublication.Group? group in groups)
                if (group is not null) DiscardGround(group);
            for (int plane = 0; plane < _planes; plane++) ReleaseWater(region, plane);
            throw;
        }
        var handles = new RegionHandles(new MeshHandle?[_planes], snapshots, cover, TileRegionResidencyState.Gameplay,
            profileManaged: false);
        _loaded[region] = handles;
        foreach (TileWaterPublication.Group? group in groups)
            if (group is not null) CommitGround(handles, group);
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
