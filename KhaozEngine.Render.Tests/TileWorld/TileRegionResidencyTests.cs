using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The region ring over a world on disk: what a prime loads, how the ring follows the observer with a
/// hysteresis band, the per-update load budget, regions the manifest does not list, and the refusal to unload a
/// region with unsaved edits. Every test drives the recording scene fake, so none of this needs a device.</summary>
public class TileRegionResidencyTests
{
    static TileWorldView View(RecordingTileWorldScene scene, TileWorldSource source) =>
        new(scene, source.Document, TileRenderTestData.Catalogs, new GreyboxMeshResolver());

    static TileCoord At(int rx, int rz) => TileRenderTestData.CentreOf(new RegionCoord(rx, rz));

    static IReadOnlyList<RegionCoord> Sorted(IReadOnlyCollection<RegionCoord> regions) =>
        regions.OrderBy(c => c.Rz).ThenBy(c => c.Rx).ToList();

    static RegionCoord[] Row(params int[] rx) => rx.Select(x => new RegionCoord(x, 0)).ToArray();

    [Fact]
    public void PrimeAround_loads_the_ring()
    {
        using var tmp = new TempDir();
        TileWorldSource source = TileWorldSource.Open(TileRenderTestData.SaveGrid(tmp, 3, 3));
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, source);
        var residency = new TileRegionResidency(source, view, TileResidencyConfig.Default);

        residency.PrimeAround(At(1, 1));

        // All nine, in one call: the per-update budget of 2 is an Update knob and a prime ignores it.
        Assert.Equal(9, residency.Resident.Count);
        Assert.Equal(9, view.LoadedRegionCount);
        Assert.Equal(9, source.Document.Regions.Count);
        for (int rz = 0; rz < 3; rz++)
            for (int rx = 0; rx < 3; rx++)
                Assert.Contains(new RegionCoord(rx, rz), residency.Resident);
    }

    [Fact]
    public void Update_moves_the_ring_with_hysteresis()
    {
        using var tmp = new TempDir();
        TileWorldSource source = TileWorldSource.Open(TileRenderTestData.SaveGrid(tmp, 5, 1));
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, source);
        var residency = new TileRegionResidency(source, view, TileResidencyConfig.Default);

        residency.PrimeAround(At(1, 0));
        Assert.Equal(Row(0, 1, 2), Sorted(residency.Resident));

        // One region east. (3, 0) arrives, and (0, 0) is now 2 regions away, which is inside the unload radius:
        // the hysteresis band is the whole point, so it stays.
        residency.Update(At(2, 0));
        Assert.Equal(Row(0, 1, 2, 3), Sorted(residency.Resident));

        // One more. (0, 0) is 3 away now, past the band, and leaves the view AND the source.
        residency.Update(At(3, 0));
        Assert.Equal(Row(1, 2, 3, 4), Sorted(residency.Resident));
        Assert.False(source.IsLoaded(new RegionCoord(0, 0)));
        Assert.True(source.IsKnown(new RegionCoord(0, 0)));
    }

    [Fact]
    public void Update_caps_loads_per_call()
    {
        using var tmp = new TempDir();
        TileWorldSource source = TileWorldSource.Open(TileRenderTestData.SaveGrid(tmp, 3, 3));
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, source);
        var residency = new TileRegionResidency(source, view, TileResidencyConfig.Default);

        // Nearest first: the observer's own region, then the ring at Chebyshev 1 in (z, then x) scan order.
        residency.Update(At(1, 1));
        Assert.Equal(new[] { new RegionCoord(0, 0), new RegionCoord(1, 1) }, Sorted(residency.Resident));

        residency.Update(At(1, 1));
        Assert.Equal(4, residency.Resident.Count);
        Assert.Contains(new RegionCoord(1, 0), residency.Resident);
        Assert.Contains(new RegionCoord(2, 0), residency.Resident);

        // Nine regions at two a call, and the ring is complete after the fifth.
        residency.Update(At(1, 1));
        residency.Update(At(1, 1));
        residency.Update(At(1, 1));
        Assert.Equal(9, residency.Resident.Count);

        residency.Update(At(1, 1));
        Assert.Equal(9, residency.Resident.Count);
    }

    [Fact]
    public void Unknown_regions_are_skipped()
    {
        using var tmp = new TempDir();
        TileWorldSource source = TileWorldSource.Open(TileRenderTestData.SaveGrid(tmp, 2, 2));
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, source);
        var residency = new TileRegionResidency(source, view, TileResidencyConfig.Default);

        // The ring around (0, 0) covers nine regions and the manifest lists four of them. The other five are
        // simply not there, and an edge of the world is not an error.
        residency.PrimeAround(At(0, 0));
        Assert.Equal(4, residency.Resident.Count);
        Assert.DoesNotContain(new RegionCoord(-1, 0), residency.Resident);
        Assert.DoesNotContain(new RegionCoord(0, -1), residency.Resident);

        // An unknown region must not eat the load budget either, or a world edge would starve the ring: a budget
        // of 4 over a nine-region ring holding four known regions fills it in one call.
        using TileWorldView second = View(new RecordingTileWorldScene(), source);
        var capped = new TileRegionResidency(source, second,
                                             new TileResidencyConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerUpdate: 4));
        capped.Update(At(0, 0));
        Assert.Equal(4, capped.Resident.Count);
    }

    [Fact]
    public void Dirty_regions_are_not_unloaded()
    {
        using var tmp = new TempDir();
        TileWorldSource source = TileWorldSource.Open(TileRenderTestData.SaveGrid(tmp, 5, 1));
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, source);
        var log = new List<string>();
        var residency = new TileRegionResidency(source, view, TileResidencyConfig.Default) { Log = log.Add };

        residency.PrimeAround(At(1, 0));
        var kept = new RegionCoord(0, 0);
        source.Document.GetRegion(kept)!.Dirty = true;

        // Far enough out that a clean region would go. Unloading this one would throw in the source and lose the
        // edit, so it stays resident and says so exactly once.
        residency.Update(At(3, 0));
        Assert.Contains(kept, residency.Resident);
        Assert.True(source.IsLoaded(kept));
        Assert.Single(log);
        Assert.Contains("(0, 0)", log[0], StringComparison.Ordinal);

        residency.Update(At(3, 0));
        Assert.Contains(kept, residency.Resident);
        Assert.Single(log);

        // Saved, so the next update is free to drop it.
        source.Document.GetRegion(kept)!.Dirty = false;
        residency.Update(At(3, 0));
        Assert.DoesNotContain(kept, residency.Resident);
        Assert.False(source.IsLoaded(kept));
    }

    [Fact]
    public void A_degenerate_config_is_refused()
    {
        using var tmp = new TempDir();
        TileWorldSource source = TileWorldSource.Open(TileRenderTestData.SaveGrid(tmp, 1, 1));
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, source);

        // No hysteresis band at all: the ring would churn on every step across a region border.
        Assert.Throws<ArgumentException>(() =>
            new TileRegionResidency(source, view, new TileResidencyConfig(LoadRadius: 2, UnloadRadius: 2, MaxLoadsPerUpdate: 1)));
        Assert.Throws<ArgumentException>(() =>
            new TileRegionResidency(source, view, new TileResidencyConfig(LoadRadius: -1, UnloadRadius: 4, MaxLoadsPerUpdate: 1)));
        Assert.Throws<ArgumentException>(() =>
            new TileRegionResidency(source, view, new TileResidencyConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerUpdate: 0)));
        // A defaulted struct is all zeroes, which is the degenerate band above rather than the defaults.
        Assert.Throws<ArgumentException>(() => new TileRegionResidency(source, view, default));
    }
}
