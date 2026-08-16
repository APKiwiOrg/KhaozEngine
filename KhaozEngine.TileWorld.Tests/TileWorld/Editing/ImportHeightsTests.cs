using System;
using System.IO;
using System.Text;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Editing;

/// <summary>Headless tests for <see cref="TileEditOps.ImportHeights(PgmImage, TileRect, int, short, short)"/>:
/// the one-to-one case that pins the north-south orientation, the bilinear midpoints of an upsample, the
/// linear map from sample to centimetres, and the round trip through the editing document.</summary>
public class ImportHeightsTests
{
    static readonly TileWorldCatalogs Cat = TileWorldCatalogs.Greybox();

    static TileEditingDocument Editing(out TileWorldDocument doc)
    {
        doc = TileWorldTestData.FlatWorld();
        return new TileEditingDocument(doc, Cat);
    }

    // A 3 by 2 image whose maxval is 200 and which is imported over 0..200 cm, so every sample IS its height in
    // centimetres and the test reads as the orientation check it is meant to be.
    static PgmImage ThreeByTwo() => new(3, 2, 200, new ushort[] { 10, 20, 30, 40, 50, 60 });

    [Fact]
    public void An_image_the_size_of_the_rect_maps_one_to_one_with_row_zero_on_the_north_corners()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        ed.Execute(TileEditOps.ImportHeights(ThreeByTwo(), new TileRect(10, 10, 3, 2), 0, 0, 200));

        // Image row 0 is the NORTH edge, and tile z grows northward, so it lands on z 11, the rect's far row.
        Assert.Equal(10, doc.CornerHeightCm(10, 11, 0));
        Assert.Equal(20, doc.CornerHeightCm(11, 11, 0));
        Assert.Equal(30, doc.CornerHeightCm(12, 11, 0));
        Assert.Equal(40, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(50, doc.CornerHeightCm(11, 10, 0));
        Assert.Equal(60, doc.CornerHeightCm(12, 10, 0));
    }

    [Fact]
    public void Upsampling_a_two_by_two_image_lands_the_bilinear_midpoints_between_its_samples()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        // Top row 0 and 100, bottom row 40 and 80, over a maxval of 100 imported to 0..100 cm, so again sample
        // equals centimetre. Onto a 3 by 3 corner rect the four samples land on the corners of the rect and
        // every other corner is the average of the two or four samples around it.
        var image = new PgmImage(2, 2, 100, new ushort[] { 0, 100, 40, 80 });

        ed.Execute(TileEditOps.ImportHeights(image, new TileRect(10, 10, 3, 3), 0, 0, 100));

        // North row (z 12): the image's top row, its midpoint in the middle.
        Assert.Equal(0, doc.CornerHeightCm(10, 12, 0));
        Assert.Equal(50, doc.CornerHeightCm(11, 12, 0));
        Assert.Equal(100, doc.CornerHeightCm(12, 12, 0));
        // Middle row (z 11): halfway down, so each value is the mean of the column above and below it, and the
        // centre corner is the mean of all four samples.
        Assert.Equal(20, doc.CornerHeightCm(10, 11, 0));
        Assert.Equal(55, doc.CornerHeightCm(11, 11, 0));
        Assert.Equal(90, doc.CornerHeightCm(12, 11, 0));
        // South row (z 10): the image's bottom row.
        Assert.Equal(40, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(60, doc.CornerHeightCm(11, 10, 0));
        Assert.Equal(80, doc.CornerHeightCm(12, 10, 0));
    }

    [Fact]
    public void A_rect_one_corner_wide_takes_the_images_first_column_down_its_whole_height()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        // Top row 0 and 100, bottom row 40 and 80, maxval 100 imported to 0..100 cm, so sample equals
        // centimetre again.
        var image = new PgmImage(2, 2, 100, new ushort[] { 0, 100, 40, 80 });

        // One corner wide, so there is no span to stretch the image's x across and the east column (100 and 80)
        // is never sampled. Down the three corners the west column stretches as before: the north corner takes
        // the top sample, the south corner the bottom one, and the middle corner their mean.
        ed.Execute(TileEditOps.ImportHeights(image, new TileRect(10, 10, 1, 3), 0, 0, 100));

        Assert.Equal(0, doc.CornerHeightCm(10, 12, 0));
        Assert.Equal(20, doc.CornerHeightCm(10, 11, 0));
        Assert.Equal(40, doc.CornerHeightCm(10, 10, 0));
    }

    [Fact]
    public void A_sample_maps_linearly_from_zero_and_maxval_onto_the_height_range()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        var image = new PgmImage(3, 1, 10, new ushort[] { 0, 5, 10 });

        ed.Execute(TileEditOps.ImportHeights(image, new TileRect(10, 10, 3, 1), 0, -1000, 1000));

        Assert.Equal(-1000, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(0, doc.CornerHeightCm(11, 10, 0));
        Assert.Equal(1000, doc.CornerHeightCm(12, 10, 0));
    }

    [Fact]
    public void A_half_centimetre_rounds_away_from_zero_on_both_sides()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        // One sample halfway up a maxval of 2, so the mapped height sits exactly on the half centimetre.
        var image = new PgmImage(1, 1, 2, new ushort[] { 1 });

        ed.Execute(TileEditOps.ImportHeights(image, new TileRect(10, 10, 1, 1), 0, 0, 5));
        Assert.Equal(3, doc.CornerHeightCm(10, 10, 0));

        ed.Execute(TileEditOps.ImportHeights(image, new TileRect(11, 10, 1, 1), 0, -5, 0));
        Assert.Equal(-3, doc.CornerHeightCm(11, 10, 0));
    }

    [Fact]
    public void A_backwards_height_range_or_an_empty_image_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => TileEditOps.ImportHeights(ThreeByTwo(), new TileRect(10, 10, 3, 2), 0, 100, 99));
        Assert.Throws<ArgumentException>(
            () => TileEditOps.ImportHeights(default(PgmImage), new TileRect(10, 10, 3, 2), 0, 0, 100));
    }

    [Fact]
    public void An_equal_min_and_max_flattens_the_rect_to_that_height()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        ed.Execute(TileEditOps.ImportHeights(ThreeByTwo(), new TileRect(10, 10, 3, 2), 0, 75, 75));

        Assert.Equal(75, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(75, doc.CornerHeightCm(12, 11, 0));
    }

    [Fact]
    public void The_import_executes_and_undoes_through_the_editing_document()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetCornerHeightCm(10, 10, 0, 250);

        SetCornerHeightsCommand cmd = TileEditOps.ImportHeights(ThreeByTwo(), new TileRect(10, 10, 3, 2), 0, 0, 200);
        ed.Execute(cmd);

        Assert.Equal(6, cmd.CornerCount);
        Assert.Equal(6, cmd.WrittenCount);
        Assert.Equal(new TileDirtyRect(new TileRect(9, 9, 4, 3), 0), Assert.Single(cmd.DirtyRects));
        Assert.Equal(40, doc.CornerHeightCm(10, 10, 0));

        Assert.True(ed.Undo());
        Assert.Equal(250, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(0, doc.CornerHeightCm(12, 11, 0));
    }

    [Fact]
    public void The_path_overload_reads_the_file_and_builds_the_same_import()
    {
        using var tmp = new TempDir();
        string path = tmp.Sub("terrain.pgm");
        byte[] head = Encoding.ASCII.GetBytes("P5\n3 2\n200\n");
        var bytes = new byte[head.Length + 6];
        head.CopyTo(bytes, 0);
        new byte[] { 10, 20, 30, 40, 50, 60 }.CopyTo(bytes, head.Length);
        File.WriteAllBytes(path, bytes);

        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        ed.Execute(TileEditOps.ImportHeights(path, new TileRect(10, 10, 3, 2), 0, 0, 200));

        Assert.Equal(10, doc.CornerHeightCm(10, 11, 0));
        Assert.Equal(60, doc.CornerHeightCm(12, 10, 0));
    }
}
