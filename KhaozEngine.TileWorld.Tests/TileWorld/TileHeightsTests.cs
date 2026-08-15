using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileHeightsTests
{
    [Fact]
    public void Corner_heights_default_to_zero_and_higher_planes_derive_from_plane_height()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Assert.Equal(0, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(300, doc.CornerHeightCm(10, 10, 1));
        Assert.Equal(900, doc.CornerHeightCm(10, 10, 3));
        doc.SetCornerHeightCm(10, 10, 0, 150);
        Assert.Equal(150, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(450, doc.CornerHeightCm(10, 10, 1));
        Assert.Equal(1.5f, doc.CornerHeight(10, 10, 0));
    }

    [Fact]
    public void First_write_on_a_higher_plane_fills_the_derived_lattice_first()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetCornerHeightCm(5, 5, 0, 100);
        doc.SetCornerHeightCm(20, 20, 2, 999);
        Assert.Equal(999, doc.CornerHeightCm(20, 20, 2));
        Assert.Equal(700, doc.CornerHeightCm(5, 5, 2));
        Assert.Equal(600, doc.CornerHeightCm(6, 6, 2));
    }

    [Fact]
    public void Far_edge_corner_reads_the_neighbour_region_or_edge_extends()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        doc.SetCornerHeightCm(64, 10, 0, 250);
        Assert.Equal(250, doc.CornerHeightCm(64, 10, 0));
        doc.SetCornerHeightCm(63, 63, 0, 80);
        // (63, 64) is region (0, 0)'s north far edge: edge-extended from its row 63.
        Assert.Equal(80, doc.CornerHeightCm(63, 64, 0));
        // (64, 64) is region (1, 0)'s north far edge: extended from ITS row 63 (resolution order prefers the
        // region to the south over the one to the south-west), so it reads (64, 63), not the diagonal (63, 63).
        doc.SetCornerHeightCm(64, 63, 0, 90);
        Assert.Equal(90, doc.CornerHeightCm(64, 64, 0));
        Assert.Equal(0, doc.CornerHeightCm(-1, -1, 0));
        Assert.False(doc.TrySetCornerHeightCm(-1, -1, 0, 5));
        Assert.Throws<TileWorldException>(() => doc.SetCornerHeightCm(-1, -1, 0, 5));
    }

    [Fact]
    public void HeightAt_is_bilinear_in_metres()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetCornerHeightCm(10, 10, 0, 0);
        doc.SetCornerHeightCm(11, 10, 0, 100);
        doc.SetCornerHeightCm(10, 11, 0, 100);
        doc.SetCornerHeightCm(11, 11, 0, 200);
        Assert.Equal(1.0f, doc.HeightAt(10.5f, 10.5f, 0), 4);
        Assert.Equal(0.5f, doc.HeightAt(10.5f, 10.0f, 0), 4);
        Assert.Equal(2.0f, doc.HeightAt(11.0f, 11.0f, 0), 4);
        doc.TileSize = 2f;
        Assert.Equal(1.0f, doc.HeightAt(21f, 21f, 0), 4);
    }
}
