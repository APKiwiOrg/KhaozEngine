using System;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

// Background ground ownership for the opt-in three-state residency path. The legacy public LoadRegion path
// remains synchronous in TileWorldView.cs.
public sealed partial class TileWorldView
{
    void LoadProfileRegion(RegionCoord region, TileRegionResidencyState residency)
    {
        WaterAvailabilityChanged(region);
        var props = new TileRegionProps[_planes];
        for (int plane = 0; plane < _planes; plane++)
            props[plane] = _propClusters.Build(_doc, region, plane, OverrideLookup());

        var handles = new RegionHandles(new MeshHandle?[_planes], props, Array.Empty<GroundCoverInstance>(),
            residency, profileManaged: true);
        _loaded[region] = handles;
        MarkCoverObjectsStale(region);
        RequestPropClusters(props);
        if (residency == TileRegionResidencyState.Gameplay) RequestGameplay(region, handles);
        else RequestGround(region, handles, TileGroundLod.Coarse4);
    }

    // Retains the accepted meshes, props and clusters, and any coarse build still pending, until full ground
    // and cover replace them. Nothing here builds, and the region's objects are not scanned again.
    void PromoteToGameplay(RegionCoord region, RegionHandles handles)
    {
        handles.ProfileManaged = true;
        handles.Residency = TileRegionResidencyState.Gameplay;
        RequestGameplay(region, handles);
    }

    // Full ground and cover from one capture of the region's ground ring and materials.
    void RequestGameplay(RegionCoord region, RegionHandles handles)
    {
        TileWorldDocument snapshot = CaptureGround(region);
        TileWorldCatalogs materials = CaptureMaterials();
        RequestGround(region, handles, TileGroundLod.Full, snapshot, materials);
        RequestCover(region, handles, snapshot, materials);
    }

    void DemoteToDecor(RegionCoord region, RegionHandles handles)
    {
        handles.ProfileManaged = true;
        CancelCover(region);
        ReplaceCover(handles, Array.Empty<GroundCoverInstance>());
        ReleaseAnimatedFoliage(handles);
        for (int plane = 0; plane < _planes; plane++) ReleaseWater(region, plane);
        handles.Residency = TileRegionResidencyState.Decor;
        RequestGround(region, handles, TileGroundLod.Coarse4);
    }

    void RequestGround(RegionCoord region, RegionHandles handles, TileGroundLod lod) =>
        RequestGround(region, handles, lod, CaptureGround(region), CaptureMaterials());

    void RequestGround(RegionCoord region, RegionHandles handles, TileGroundLod lod, TileWorldDocument snapshot,
                       TileWorldCatalogs materials)
    {
        for (int plane = 0; plane < _planes; plane++)
            RequestGroundPlane(region, handles, plane, lod, snapshot, materials);
    }

    // A full request leaves a pending coarse build alone, so a promoted region keeps a fallback until full
    // ground lands. A coarse request replaces the full representation outright.
    void RequestGroundPlane(RegionCoord region, RegionHandles handles, int plane, TileGroundLod lod,
                            TileWorldDocument snapshot, TileWorldCatalogs materials)
    {
        TileWaterPreparation water = CaptureWater(region, plane);
        long generation = NextProfileGeneration();
        if (lod == TileGroundLod.Full)
        {
            handles.FullGroundGenerations[plane] = generation;
        }
        else
        {
            _profileBuilds.Cancel(GroundKey(region, plane, TileWorldBuildKind.FullGround));
            handles.FullGroundGenerations[plane] = 0;
            handles.CoarseGroundGenerations[plane] = generation;
        }
        TileWorldBuildKey key = GroundKey(region, plane, KindOf(lod));
        _profileBuilds.Request(new TileWorldBuildRequest<TileProfileBuildInput>(key, generation,
            new TileGroundBuildInput(snapshot, materials, region, plane, lod, _mesher, water, GroundSurfaceRequested)));
        TrackPendingWater(key, generation, water);
    }

    static TileWorldBuildKey GroundKey(RegionCoord region, int plane, TileWorldBuildKind kind) =>
        new(region, plane, kind);

    static TileWorldBuildKind KindOf(TileGroundLod lod) => lod == TileGroundLod.Full
        ? TileWorldBuildKind.FullGround
        : TileWorldBuildKind.CoarseGround;

    // Full ground lands only on the gameplay generation that asked for it, and retires the coarse fallback.
    // Coarse ground lands only while no full replacement has been accepted since it was requested.
    void ApplyGround(TileWorldBuildKey key, long generation, TileGroundCpuBuild ground, TileWaterPreparation water)
    {
        if (!_loaded.TryGetValue(key.Region, out RegionHandles? handles)) return;
        int plane = key.Plane;
        if ((uint)plane >= (uint)_planes) return;
        if (key.Kind == TileWorldBuildKind.FullGround)
        {
            if (handles.Residency != TileRegionResidencyState.Gameplay ||
                handles.FullGroundGenerations[plane] != generation)
                return;
            _profileBuilds.Cancel(GroundKey(key.Region, plane, TileWorldBuildKind.CoarseGround));
            handles.CoarseGroundGenerations[plane] = 0;
        }
        else if (handles.CoarseGroundGenerations[plane] != generation)
        {
            return;
        }

        PublishGround(key.Region, plane, handles, ground, water);
    }

    void CancelGround(RegionCoord region, int plane)
    {
        _profileBuilds.Cancel(GroundKey(region, plane, TileWorldBuildKind.FullGround));
        _profileBuilds.Cancel(GroundKey(region, plane, TileWorldBuildKind.CoarseGround));
    }

    void InvalidateGround(RegionCoord region, RegionHandles handles, int plane)
    {
        CancelGround(region, plane);
        handles.FullGroundGenerations[plane] = 0;
        handles.CoarseGroundGenerations[plane] = 0;
    }
}
