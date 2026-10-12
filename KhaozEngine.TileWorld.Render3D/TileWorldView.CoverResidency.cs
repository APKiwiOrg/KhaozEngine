using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

// Background cover ownership for profile-managed regions. Capture reads the live document on the frame thread
// and the build runs on a worker against the detached input. The legacy synchronous path stays in
// TileWorldView.Foliage.cs.
public sealed partial class TileWorldView
{
    // Solid, roof and door footprints by region, so a capture queries its neighbourhood without walking the world.
    readonly TileCoverObjectIndex _coverObjects;
    // Anchor regions whose objects changed or streamed since the last capture. Refreshed at capture rather than
    // at the notification, because a region that streams out is unloaded from the source after it is announced.
    readonly HashSet<RegionCoord> _coverObjectsStale = new();

    /// <summary>Queues one gameplay region's cover against its current terrain, materials and exclusions. A
    /// world with no foliage layer has no cover to build, so the region's cover is simply cleared.</summary>
    void RequestCover(RegionCoord region, RegionHandles handles)
    {
        if (_doc.FoliageLayers.Count == 0)
        {
            CancelCover(region);
            ReplaceCover(handles, Array.Empty<GroundCoverInstance>());
            return;
        }
        RefreshCoverObjects();
        long generation = NextProfileGeneration();
        handles.CoverGeneration = generation;
        TileCoverBuildInput input = TileCoverBuildInput.Capture(_doc, _catalogs, region, _mesher.SmoothNormals,
            _coverObjects);
        _profileBuilds.Request(new TileWorldBuildRequest<TileProfileBuildInput>(CoverKey(region), generation, input));
    }

    /// <summary>Drops any queued or running cover build of one region, so its result can never land.</summary>
    void CancelCover(RegionCoord region)
    {
        _profileBuilds.Cancel(CoverKey(region));
        if (_loaded.TryGetValue(region, out RegionHandles? handles)) handles.CoverGeneration = 0;
    }

    // Cover lands only on the gameplay generation that asked for it. The batch it replaces is released here,
    // on the scene thread, exactly once.
    void ApplyCover(RegionCoord region, long generation, IReadOnlyList<GroundCoverInstance> cover)
    {
        if (!_loaded.TryGetValue(region, out RegionHandles? handles) ||
            handles.Residency != TileRegionResidencyState.Gameplay || handles.CoverGeneration != generation)
            return;
        handles.CoverGeneration = 0;
        ReplaceCover(handles, cover);
    }

    void ReplaceCover(RegionHandles handles, IReadOnlyList<GroundCoverInstance> cover)
    {
        IReadOnlyList<GroundCoverInstance> old = handles.Cover;
        if (ReferenceEquals(old, cover)) return;
        if (old.Count > 0) _scene.ReleaseGroundCover(old);
        GeneratedCoverCount += cover.Count - old.Count;
        handles.Cover = cover;
    }

    void MarkCoverObjectsStale(TileRect worldRect)
    {
        RegionCoord min = RegionCoord.Of(worldRect.X, worldRect.Z);
        RegionCoord max = RegionCoord.Of(worldRect.X1 - 1, worldRect.Z1 - 1);
        for (int rz = min.Rz; rz <= max.Rz; rz++)
            for (int rx = min.Rx; rx <= max.Rx; rx++)
                _coverObjectsStale.Add(new RegionCoord(rx, rz));
    }

    void RefreshCoverObjects()
    {
        if (_coverObjectsStale.Count == 0) return;
        foreach (RegionCoord region in _coverObjectsStale) _coverObjects.Refresh(region);
        _coverObjectsStale.Clear();
    }

    static TileWorldBuildKey CoverKey(RegionCoord region) => new(region, 0, TileWorldBuildKind.Cover);
}
