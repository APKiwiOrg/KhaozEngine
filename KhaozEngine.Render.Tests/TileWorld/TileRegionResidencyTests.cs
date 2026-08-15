using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The region ring over a world on disk: what a prime loads, how the ring follows the observer with a
/// hysteresis band, the per-update load budget, regions the manifest does not list, the refusal to unload a
/// region with unsaved edits, and the border remesh a streamed neighbour forces. Every test drives the recording
/// scene fake, so none of this needs a device.</summary>
public class TileRegionResidencyTests
{
    // Far enough from every ridge assertion that nothing here depends on the prop draw radius.
    static readonly Vector3 Focus = new(64f, 0f, 32f);

    static TileWorldView View(RecordingTileWorldScene scene, TileWorldSource source,
                              TileWorldViewOptions? options = null) =>
        new(scene, source.Document, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), options);

    static TileCoord At(int rx, int rz) => TileRenderTestData.CentreOf(new RegionCoord(rx, rz));

    // The plane-0 ground handle a region drew this frame, found by its world transform rather than by draw
    // order, because the view iterates a dictionary and promises no order.
    static int GroundHandle(RecordingTileWorldScene scene, TileWorldDocument doc, RegionCoord region)
    {
        Matrix4x4 world = TileGroundMesher.WorldMatrix(doc, region);
        return scene.Drawn.Single(d => d.World == world).Handle.Index;
    }

    // Draws one frame and returns the plane-0 ground handle of the region under test.
    static int DrawAndRead(RecordingTileWorldScene scene, TileWorldView view, TileWorldDocument doc, RegionCoord region)
    {
        scene.ClearFrame();
        view.Draw(Focus);
        return GroundHandle(scene, doc, region);
    }

    // Every Y the mesh carries on the region's east border column, which is the lattice column the neighbour
    // owns. Region-local x, because a region mesh is region-local and placed by its world matrix.
    static IReadOnlyList<float> BorderHeights(RecordingTileWorldScene scene, int handle)
    {
        GltfMesh mesh = scene.GroundMeshes[handle];
        return mesh.Vertices
            .Where(v => Math.Abs(v.Position.X - TileRegion.Size) < 0.001f)
            .Select(v => v.Position.Y)
            .ToList();
    }

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
    public void Dirty_regions_log_once_each()
    {
        using var tmp = new TempDir();
        TileWorldSource source = TileWorldSource.Open(TileRenderTestData.SaveGrid(tmp, 5, 1));
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, source);
        var log = new List<string>();
        var residency = new TileRegionResidency(source, view, TileResidencyConfig.Default) { Log = log.Add };

        residency.PrimeAround(At(1, 0));
        source.Document.GetRegion(new RegionCoord(0, 0))!.Dirty = true;
        source.Document.GetRegion(new RegionCoord(1, 0))!.Dirty = true;

        // Both are past the band now, and each says so in its own line.
        residency.Update(At(4, 0));
        Assert.Equal(2, log.Count);
        Assert.Contains(log, line => line.Contains("(0, 0)", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("(1, 0)", StringComparison.Ordinal));

        // A second departure of the same two regions is not news.
        residency.Update(At(4, 0));
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void A_streamed_in_neighbour_rebuilds_the_border()
    {
        using var tmp = new TempDir();
        TileWorldSource source = TileWorldSource.Open(TileRenderTestData.SaveRidgeGrid(tmp, 2, 1));
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, source);
        var residency = new TileRegionResidency(source, view,
                                                new TileResidencyConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerUpdate: 1));
        var west = new RegionCoord(0, 0);

        // One region a call, so the west region is meshed while its neighbour is still absent. Its east border
        // reads the neighbour's corner column, which edge-extends to 0 while that region is not there.
        residency.Update(At(0, 0));
        Assert.Equal(new[] { west }, residency.Resident.ToArray());
        int first = DrawAndRead(scene, view, source.Document, west);
        IReadOnlyList<float> flat = BorderHeights(scene, first);
        Assert.NotEmpty(flat);
        Assert.All(flat, y => Assert.Equal(0d, y, 3));

        // The neighbour arrives with a 3 m ridge standing on that shared column. Without the neighbour mark the
        // west mesh would keep its flat border forever, which is a full-height crack down the region seam.
        residency.Update(At(0, 0));
        Assert.Equal(2, residency.Resident.Count);
        int rebuilt = DrawAndRead(scene, view, source.Document, west);
        Assert.NotEqual(first, rebuilt);
        IReadOnlyList<float> ridge = BorderHeights(scene, rebuilt);
        Assert.Equal(flat.Count, ridge.Count);
        Assert.All(ridge, y => Assert.Equal(TileRenderTestData.RidgeHeightCm / 100d, y, 3));
    }

    [Fact]
    public void A_streamed_out_neighbour_rebuilds_the_border()
    {
        using var tmp = new TempDir();
        TileWorldSource source = TileWorldSource.Open(TileRenderTestData.SaveRidgeGrid(tmp, 2, 1));
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, source);
        var residency = new TileRegionResidency(source, view, TileResidencyConfig.Default);
        var west = new RegionCoord(0, 0);

        residency.PrimeAround(At(0, 0));
        Assert.Equal(2, residency.Resident.Count);
        int withNeighbour = DrawAndRead(scene, view, source.Document, west);
        Assert.All(BorderHeights(scene, withNeighbour), y => Assert.Equal(TileRenderTestData.RidgeHeightCm / 100d, y, 3));

        // Region (-2, 0) is 2 regions from the west region and 3 from the east one, so exactly one of them
        // leaves. The survivor was blending against data that is now gone and has to be remeshed.
        residency.Update(At(-2, 0));
        Assert.Equal(new[] { west }, residency.Resident.ToArray());
        int alone = DrawAndRead(scene, view, source.Document, west);
        Assert.NotEqual(withNeighbour, alone);
        IReadOnlyList<float> flat = BorderHeights(scene, alone);
        Assert.NotEmpty(flat);
        Assert.All(flat, y => Assert.Equal(0d, y, 3));
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
