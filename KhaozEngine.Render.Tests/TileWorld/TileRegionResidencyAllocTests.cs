using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The resident set is read every update, so the non-allocating read of it has to stay non-allocating:
/// the view's and the ring's collect doors, and the settled update that walks the set once a frame.</summary>
[Collection("AllocSensitive")]  // its zero-alloc assertions must not run alongside the GC-churning parallel tests
public sealed class TileRegionResidencyAllocTests
{
    static TileWorldView View(RecordingTileWorldScene scene, TileWorldSource source) =>
        new(scene, source.Document, TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(source.Document.TileSize, source.Document.PlaneHeight));

    static IReadOnlyList<RegionCoord> Sorted(IEnumerable<RegionCoord> regions) =>
        regions.OrderBy(c => c.Rz).ThenBy(c => c.Rx).ToList();

    [Fact]
    public void Collecting_the_resident_set_matches_the_snapshot_and_allocates_nothing_once_warm()
    {
        using var tmp = new TempDir();
        TileWorldSource source = TileWorldSource.Open(TileRenderTestData.SaveGrid(tmp, 3, 3));
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, source);
        var residency = new TileRegionResidency(source, view, TileResidencyConfig.Default);
        residency.PrimeAround(TileRenderTestData.CentreOf(new RegionCoord(1, 1)));

        var list = new List<RegionCoord> { new(99, 99) };
        var set = new HashSet<RegionCoord>();
        view.CollectLoadedRegions(list);
        residency.CollectResident(set);

        // Cleared first, then the same nine the allocating snapshot reports.
        Assert.Equal(Sorted(view.LoadedRegions), Sorted(list));
        Assert.Equal(Sorted(residency.Resident), Sorted(set));
        Assert.Equal(9, list.Count);

        AllocAssert.NoPerCallAllocation("TileWorldView.CollectLoadedRegions and TileRegionResidency.CollectResident", () =>
        {
            for (int i = 0; i < 64; i++)
            {
                view.CollectLoadedRegions(list);
                residency.CollectResident(set);
            }
        });
    }

    [Fact]
    public void A_settled_update_allocates_nothing()
    {
        using var tmp = new TempDir();
        TileWorldSource source = TileWorldSource.Open(TileRenderTestData.SaveGrid(tmp, 3, 3));
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, source);
        var residency = new TileRegionResidency(source, view, TileResidencyConfig.Default);
        TileCoord observer = TileRenderTestData.CentreOf(new RegionCoord(1, 1));
        residency.PrimeAround(observer);
        // Warm the ring's reused sets and lists at the size a settled ring holds.
        residency.Update(observer);

        // The ring walks the resident set once an update. Before the collect door it did so through the allocating
        // snapshot, one fresh array per frame.
        AllocAssert.NoPerCallAllocation("TileRegionResidency.Update on a settled ring", () =>
        {
            for (int i = 0; i < 64; i++) residency.Update(observer);
        });
        Assert.Equal(9, view.LoadedRegionCount);
    }
}
