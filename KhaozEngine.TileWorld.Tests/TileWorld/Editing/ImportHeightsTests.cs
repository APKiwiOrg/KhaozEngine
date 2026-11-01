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

    [Theory]
    [InlineData(0, 200, new[] { 40, 50, 60, 10, 20, 30 })]
    [InlineData(75, 75, new[] { 75, 75, 75, 75, 75, 75 })]
    public void Import_maps_image_rows_and_the_height_range(short min, short max, int[] expected)
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        ed.Execute(TileEditOps.ImportHeights(ThreeByTwo(), new TileRect(10, 10, 3, 2), 0, min, max));

        // Expected rows run south to north, opposite the image's top-to-bottom samples.
        for (int z = 0; z < 2; z++)
            for (int x = 0; x < 3; x++)
                Assert.Equal(expected[z * 3 + x], doc.CornerHeightCm(10 + x, 10 + z, 0));
    }

    [Theory]
    [InlineData(3, new[] { 40, 60, 80, 20, 55, 90, 0, 50, 100 })]
    [InlineData(1, new[] { 40, 20, 0 })]
    public void Upsampling_handles_full_and_single_column_rects(int width, int[] expected)
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        var image = new PgmImage(2, 2, 100, new ushort[] { 0, 100, 40, 80 });

        ed.Execute(TileEditOps.ImportHeights(image, new TileRect(10, 10, width, 3), 0, 0, 100));

        for (int z = 0; z < 3; z++)
            for (int x = 0; x < width; x++)
                Assert.Equal(expected[z * width + x], doc.CornerHeightCm(10 + x, 10 + z, 0));
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
