using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

// The dirty-queue drain. A legacy region rebuilds here on the calling thread. A profile-managed region submits
// detached ground and cover requests to the shared queue instead, and only an explicit prime or capture waits
// for any of them.
public sealed partial class TileWorldView
{
    // Regions whose cover this drain already requested, so one region's planes share one cover build.
    readonly HashSet<RegionCoord> _flushCover = new();
    // This drain's ground snapshots by region, so a region's planes and its cover share one capture.
    readonly Dictionary<RegionCoord, TileWorldDocument> _flushSnapshots = new();
    TileWorldCatalogs? _flushMaterials;
    // The regions a profile prime settles on the calling thread. Reused across primes.
    readonly HashSet<RegionCoord> _primeSettle = new();

    /// <summary>Rebuilds queued region-planes oldest first, up to
    /// <see cref="TileWorldViewOptions.MaxRebuildsPerFlush"/> of them, and refreshes the roof rule's indoor flag.
    /// Called at the start of <see cref="Draw"/>, so an explicit call is only needed to pay the cost off the draw
    /// path. Whatever the budget did not reach stays queued and is counted by
    /// <see cref="PendingRebuilds"/>.</summary>
    public void Flush() => Flush(_options.MaxRebuildsPerFlush);

    /// <summary>The same drain against an explicit budget, which overrides
    /// <see cref="TileWorldViewOptions.MaxRebuildsPerFlush"/> for this one call. <see cref="int.MaxValue"/> is the
    /// settle-now form a loading moment wants, and is what a legacy residency prime finishes with, so a teleport
    /// does not spend frames drawing borders meshed against a neighbour that had not arrived yet.
    /// <para>A rebuild that produces NO mesh (the region-plane has no drawable tile) does not spend budget. It
    /// uploads nothing and frees nothing, and a four-plane document where one plane is authored would otherwise
    /// burn three quarters of every flush on region-planes that mesh to null.</para>
    /// <para>A rebuild that THROWS is dropped along with everything already completed ahead of it: the exception
    /// propagates, the queue keeps only what this call had not reached, and that region-plane keeps its previous
    /// mesh rather than being retried on every frame from here on.</para>
    /// <para>A region kept by a residency profile is never rebuilt here. Each of its marks submits detached ground
    /// to the background queue, spending one unit of budget. A gameplay region also requests its cover once per
    /// region per drain, while a decor region submits coarse ground only. Its previous mesh and cover keep drawing
    /// until the replacements are applied.</para></summary>
    /// <param name="maxRebuilds">Mesh-producing rebuilds this call may perform, treated as 1 when below 1.</param>
    public void Flush(int maxRebuilds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // One settings lookup a frame, which is what it costs to stay right when the tile UNDER a stationary
        // observer is edited: the setter alone only sees the observer moving. A flip refills the interior, and
        // so does any MarkDirty, which is the only edit channel there is (the document raises no events).
        bool indoors = IndoorsAt(_observer);
        if (indoors != ObserverIndoors) _interiorStale = true;
        ObserverIndoors = indoors;
        EnsureInterior();
        _interiorInputsDirty |= _dirtyOrder.Count > 0;
        RefreshWaterPreparation();
        if (_dirtyOrder.Count == 0)
        {
            SweepWaterMetadata();
            return;
        }

        int budget = Math.Max(1, maxRebuilds);
        int taken = 0;
        int scanned = 0;
        try
        {
            while (scanned < _dirtyOrder.Count && taken < budget)
            {
                (RegionCoord region, int plane) = _dirtyOrder[scanned];
                scanned++;
                _dirty.Remove((region, plane));
                // A mark on a region that is not loaded costs nothing to drop, so it must not spend the budget:
                // the residency marks eight neighbours per streamed region and most of them are never loaded.
                if (!_loaded.TryGetValue(region, out RegionHandles? handles)) continue;
                if (handles.ProfileManaged)
                {
                    SubmitProfileRebuild(region, handles, plane);
                    taken++;
                    continue;
                }
                // Build BEFORE freeing. A mesher that throws must leave the old handle live and drawable rather
                // than a freed one in the slot, which would be a use-after-free on the next frame.
                InvalidateGround(region, handles, plane);
                TileWaterPreparation water = CaptureWater(region, plane);
                TileGroundCpuBuild rebuilt = BuildGroundCpu(region, plane);
                if (!PublishGround(region, plane, handles, rebuilt, water)) continue;
                handles.Props[plane] = _propClusters.Build(_doc, region, plane, OverrideLookup());
                _propClusters.Request(handles.Props[plane]);
                ReleaseAnimatedFoliage(handles, plane);
                RebuildCover(region, handles);
                // Only a mesh that was actually built counts. The budget exists to bound uploads and handle
                // churn, and an empty region-plane produces neither.
                if (rebuilt.Mesh is not null) taken++;
            }
        }
        finally
        {
            // In a finally so a throwing mesher cannot leave the set and the list disagreeing, which would let a
            // later MarkDirty push a duplicate of an entry the list still holds.
            _dirtyOrder.RemoveRange(0, scanned);
            _flushCover.Clear();
            _flushSnapshots.Clear();
            _flushMaterials = null;
            SweepWaterMetadata();
        }
    }

    // Stream dependency marks rebuild ground against arriving or departing neighbours. A decor region only
    // refreshes its coarse ground. A gameplay region also refreshes the props snapshot it draws and requests
    // its cover once for this drain.
    void SubmitProfileRebuild(RegionCoord region, RegionHandles handles, int plane)
    {
        if (!_flushSnapshots.TryGetValue(region, out TileWorldDocument? snapshot))
        {
            snapshot = CaptureGround(region);
            _flushSnapshots.Add(region, snapshot);
        }
        _flushMaterials ??= CaptureMaterials();
        if (handles.Residency == TileRegionResidencyState.Decor)
        {
            RequestGroundPlane(region, handles, plane, TileGroundLod.Coarse4, snapshot, _flushMaterials);
            return;
        }
        RequestGroundPlane(region, handles, plane, TileGroundLod.Full, snapshot, _flushMaterials);
        handles.Props[plane] = _propClusters.Build(_doc, region, plane, OverrideLookup());
        _propClusters.Request(handles.Props[plane]);
        ReleaseAnimatedFoliage(handles, plane);
        if (_flushCover.Add(region)) RequestCover(region, handles, snapshot, _flushMaterials);
    }

    /// <summary>Finishes a profile prime once every source load is complete. Every dirty mark is submitted
    /// against its final snapshot, then full ground is built and applied on this thread for the gameplay
    /// arrivals and for the gameplay neighbours whose borders they change, the ground reach of
    /// <see cref="DirtyRegionMargin"/> tiles. A neighbour promoted by the same prime is excluded, as is every
    /// promotion, every cover build and every other dependency: those stream through the queue, and nothing
    /// here waits for them. A prime with no arrivals builds nothing on this thread.</summary>
    /// <param name="focus">The region the queue settles nearest first.</param>
    /// <param name="arrivals">Regions newly loaded by this prime.</param>
    /// <param name="promotions">Regions this prime moved from decor to gameplay.</param>
    internal void FlushProfilePrime(RegionCoord focus, IReadOnlySet<RegionCoord> arrivals,
                                    IReadOnlySet<RegionCoord> promotions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentNullException.ThrowIfNull(promotions);
        _primeSettle.Clear();
        foreach (RegionCoord arrival in arrivals)
        {
            if (!_loaded.TryGetValue(arrival, out RegionHandles? handles)) continue;
            if (handles.ProfileManaged && handles.Residency == TileRegionResidencyState.Gameplay)
                _primeSettle.Add(arrival);
            AddSettledBorders(arrival, arrivals, promotions);
        }
        Flush(int.MaxValue);
        _profileBuilds.PrimeGameplay(focus, _primeSettle);
    }

    void AddSettledBorders(RegionCoord arrival, IReadOnlySet<RegionCoord> arrivals,
                           IReadOnlySet<RegionCoord> promotions)
    {
        TileRect reach = arrival.Rect.Expand(DirtyRegionMargin);
        RegionCoord min = RegionCoord.Of(reach.X, reach.Z);
        RegionCoord max = RegionCoord.Of(reach.X1 - 1, reach.Z1 - 1);
        for (int rz = min.Rz; rz <= max.Rz; rz++)
            for (int rx = min.Rx; rx <= max.Rx; rx++)
            {
                var neighbour = new RegionCoord(rx, rz);
                if (arrivals.Contains(neighbour) || promotions.Contains(neighbour)) continue;
                if (_loaded.TryGetValue(neighbour, out RegionHandles? handles) && handles.ProfileManaged &&
                    handles.Residency == TileRegionResidencyState.Gameplay)
                    _primeSettle.Add(neighbour);
            }
    }
}
