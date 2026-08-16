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
    public void Far_east_edge_reads_the_western_region_when_the_owner_is_missing()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        doc.SetCornerHeightCm(127, 10, 0, 70);
        // (128, 10) belongs to region (2, 0), which does not exist, so the read edge-extends west from region
        // (1, 0)'s column 63, row 10.
        Assert.Equal(70, doc.CornerHeightCm(128, 10, 0));
    }

    [Fact]
    public void Far_north_east_corner_reads_the_diagonal_region_when_owner_west_and_south_are_missing()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(1, 0));
        doc.SetCornerHeightCm(127, 63, 0, 55);
        // (128, 64) has no owner (2, 1), no western region (1, 1) and no southern region (2, 0), so only the
        // south-west diagonal (1, 0) is left, read at its (63, 63).
        Assert.Equal(55, doc.CornerHeightCm(128, 64, 0));
    }

    [Fact]
    public void Negative_coordinates_index_the_owning_region_correctly()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(-1, -1));
        doc.SetCornerHeightCm(-1, -1, 0, 33);
        doc.SetCornerHeightCm(-64, -64, 0, 44);
        Assert.Equal(33, doc.CornerHeightCm(-1, -1, 0));
        Assert.Equal(44, doc.CornerHeightCm(-64, -64, 0));
        Assert.Equal(0, doc.CornerHeightCm(-2, -1, 0));
    }

    [Fact]
    public void Writing_into_an_unloaded_region_says_to_load_it_rather_than_create_it()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.UnloadedRegionHashes[new RegionCoord(5, 5)] = "abc123";
        var ex = Assert.Throws<TileWorldException>(() => doc.SetCornerHeightCm(320, 320, 0, 1));
        Assert.Contains("(5, 5)", ex.Message);
        Assert.Contains("not loaded", ex.Message);
        Assert.False(doc.TrySetCornerHeightCm(320, 320, 0, 1));
    }

    [Fact]
    public void HeightAt_is_bilinear_in_metres()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetCornerHeightCm(10, 10, 0, 0);
        doc.SetCornerHeightCm(11, 10, 0, 100);
        doc.SetCornerHeightCm(10, 11, 0, 100);
        doc.SetCornerHeightCm(11, 11, 0, 200);
        // World z is minus tile z, so tile z 10.5 is sampled at world z -10.5.
        Assert.Equal(1.0f, doc.HeightAt(10.5f, -10.5f, 0), 4);
        Assert.Equal(0.5f, doc.HeightAt(10.5f, -10.0f, 0), 4);
        Assert.Equal(2.0f, doc.HeightAt(11.0f, -11.0f, 0), 4);
        // A POSITIVE world z is south of the authored square, on flat ground.
        Assert.Equal(0f, doc.HeightAt(10.5f, 10.5f, 0), 4);
        doc.TileSize = 2f;
        Assert.Equal(1.0f, doc.HeightAt(21f, -21f, 0), 4);
    }
}
