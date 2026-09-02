using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Pins <see cref="TileWorldDocument.FindObject"/>: what it answers, what it misses, and that the
/// lookup itself allocates nothing, since the tile netcode resolves an interaction target through it on every
/// command and on every reconcile replay of an unacked one.</summary>
[Collection("AllocSensitive")]  // its zero-alloc assertion must not run alongside the GC-churning parallel tests
public class TileWorldDocumentFindObjectTests
{
    static TileWorldDocument TwoRegionsWithObjects()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        doc.AddObject("tree", 5, 5, 0, 0);      // id 1, region (0, 0)
        doc.AddObject("wall", 70, 5, 0, 2);     // id 2, region (1, 0)
        doc.AddObject("tree", 6, 6, 0, 0);      // id 3, region (0, 0)
        return doc;
    }

    [Fact]
    public void Finds_across_regions_and_misses_what_is_not_there()
    {
        TileWorldDocument doc = TwoRegionsWithObjects();
        Assert.Equal("tree", doc.FindObject(1)!.ArchetypeId);
        Assert.Equal("wall", doc.FindObject(2)!.ArchetypeId);
        Assert.Equal(6, doc.FindObject(3)!.X);
        Assert.Null(doc.FindObject(0));
        Assert.Null(doc.FindObject(4));
        Assert.Null(doc.FindObject(long.MaxValue));

        Assert.True(doc.RemoveObject(2));
        Assert.Null(doc.FindObject(2));
        Assert.NotNull(doc.FindObject(1));

        // Indexed to a region it is not in (the stale-index case RebuildObjectIndex exists for): a miss, not a
        // wrong answer and not a throw.
        doc.GetRegion(new RegionCoord(0, 0))!.Objects.RemoveAll(o => o.Id == 3);
        Assert.Null(doc.FindObject(3));
    }

    [Fact]
    public void The_lookup_allocates_nothing()
    {
        TileWorldDocument doc = TwoRegionsWithObjects();
        doc.FindObject(1);
        doc.FindObject(99);
        AllocAssert.NoPerCallAllocation("TileWorldDocument.FindObject", () =>
        {
            for (int i = 0; i < 64; i++)
            {
                Assert.NotNull(doc.FindObject(3));
                Assert.Null(doc.FindObject(99));
            }
        });
    }
}
