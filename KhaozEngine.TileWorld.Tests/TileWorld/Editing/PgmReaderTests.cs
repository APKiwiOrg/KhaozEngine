using System;
using System.IO;
using System.Text;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Editing;

/// <summary>Headless tests for <see cref="PgmReader"/> against hand-built byte arrays: the 8 and 16 bit
/// rasters, the header rules (comments, the single whitespace byte before the raster), and every malformed
/// file it has to refuse by name rather than decode into a wrong heightmap.</summary>
public class PgmReaderTests
{
    // A PGM built from an ASCII header and the exact raster bytes that follow it, so each test states the file
    // it is feeding the reader byte for byte instead of going through an encoder.
    static byte[] Pgm(string header, params byte[] raster)
    {
        byte[] head = Encoding.ASCII.GetBytes(header);
        var bytes = new byte[head.Length + raster.Length];
        head.CopyTo(bytes, 0);
        raster.CopyTo(bytes, head.Length);
        return bytes;
    }

    [Fact]
    public void An_eight_bit_raster_reads_row_major_with_the_top_row_first()
    {
        PgmImage image = PgmReader.Read(Pgm("P5\n2 2\n255\n", 10, 20, 30, 40));

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(255, image.MaxValue);
        Assert.Equal(new ushort[] { 10, 20, 30, 40 }, image.Samples);
        Assert.Equal(10, image.Sample(0, 0));
        Assert.Equal(20, image.Sample(1, 0));
        Assert.Equal(30, image.Sample(0, 1));
        Assert.Equal(40, image.Sample(1, 1));
    }

    [Fact]
    public void A_sixteen_bit_raster_reads_two_bytes_per_sample_big_endian()
    {
        // 0x0001, 0x0100, 0xFFFF, 0x1234: the second and third are past what one byte could carry, so a
        // little-endian read or an 8 bit read would land on visibly different numbers.
        PgmImage image = PgmReader.Read(
            Pgm("P5\n2 2\n65535\n", 0x00, 0x01, 0x01, 0x00, 0xFF, 0xFF, 0x12, 0x34));

        Assert.Equal(65535, image.MaxValue);
        Assert.Equal(new ushort[] { 1, 256, 65535, 4660 }, image.Samples);
    }

    [Fact]
    public void A_maxval_of_exactly_two_hundred_and_fifty_six_reads_as_sixteen_bit()
    {
        // The 8 versus 16 bit switch is maxval < 256, so 256 itself is the first two-byte file.
        PgmImage image = PgmReader.Read(Pgm("P5\n2 1\n256\n", 0x01, 0x00, 0x00, 0x05));

        Assert.Equal(new ushort[] { 256, 5 }, image.Samples);
    }

    [Fact]
    public void Comments_are_skipped_wherever_they_appear_in_the_header()
    {
        PgmImage image = PgmReader.Read(
            Pgm("P5\n# written by some painting tool\n2 2\n# and one between height and maxval\n255\n",
                1, 2, 3, 4));

        Assert.Equal(2, image.Width);
        Assert.Equal(new ushort[] { 1, 2, 3, 4 }, image.Samples);
    }

    [Fact]
    public void Exactly_one_whitespace_byte_closes_the_header()
    {
        // The second newline is NOT a separator, it is sample 0 of the raster, worth 10. A reader that skipped
        // every whitespace byte after maxval would shift the whole image by one sample.
        PgmImage image = PgmReader.Read(Pgm("P5\n2 2\n255\n", 0x0A, 20, 30, 40));

        Assert.Equal(10, image.Sample(0, 0));
        Assert.Equal(20, image.Sample(1, 0));
    }

    [Fact]
    public void Bytes_after_the_raster_are_ignored()
    {
        PgmImage image = PgmReader.Read(Pgm("P5\n2 2\n255\n", 1, 2, 3, 4, 0x0A, 0x0A, 99));

        Assert.Equal(new ushort[] { 1, 2, 3, 4 }, image.Samples);
    }

    [Fact]
    public void An_ascii_greymap_is_refused()
    {
        var ex = Assert.Throws<TileWorldException>(
            () => PgmReader.Read(Encoding.ASCII.GetBytes("P2\n2 2\n255\n1 2 3 4\n")));

        Assert.Contains("<bytes>", ex.Message, StringComparison.Ordinal);
        Assert.Contains("P5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_colour_pixmap_is_refused()
    {
        var ex = Assert.Throws<TileWorldException>(
            () => PgmReader.Read(Pgm("P6\n2 2\n255\n", new byte[12])));

        Assert.Contains("P5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_or_truncated_header_is_refused()
    {
        Assert.Throws<TileWorldException>(() => PgmReader.Read(Array.Empty<byte>()));
        Assert.Throws<TileWorldException>(() => PgmReader.Read(Encoding.ASCII.GetBytes("P5\n2 2\n")));
        Assert.Throws<TileWorldException>(() => PgmReader.Read(Encoding.ASCII.GetBytes("P5\n2 x\n255\n")));
    }

    [Fact]
    public void A_maxval_outside_one_to_sixty_five_thousand_five_hundred_and_thirty_five_is_refused()
    {
        var zero = Assert.Throws<TileWorldException>(
            () => PgmReader.Read(Pgm("P5\n2 2\n0\n", 1, 2, 3, 4)));
        var huge = Assert.Throws<TileWorldException>(
            () => PgmReader.Read(Pgm("P5\n2 2\n70000\n", new byte[8])));

        Assert.Contains("maxval", zero.Message, StringComparison.Ordinal);
        Assert.Contains("maxval", huge.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dimension_that_is_not_positive_is_refused()
    {
        Assert.Throws<TileWorldException>(() => PgmReader.Read(Pgm("P5\n0 2\n255\n")));
        Assert.Throws<TileWorldException>(() => PgmReader.Read(Pgm("P5\n2 0\n255\n")));
        // A minus sign never reaches the number, so a negative dimension fails as a malformed header.
        Assert.Throws<TileWorldException>(() => PgmReader.Read(Pgm("P5\n-2 2\n255\n", new byte[8])));
    }

    [Fact]
    public void A_raster_shorter_than_the_header_claims_is_refused()
    {
        var eight = Assert.Throws<TileWorldException>(
            () => PgmReader.Read(Pgm("P5\n2 2\n255\n", 1, 2, 3)));
        var sixteen = Assert.Throws<TileWorldException>(
            () => PgmReader.Read(Pgm("P5\n2 2\n65535\n", 1, 2, 3, 4, 5, 6, 7)));

        Assert.Contains("4 bytes", eight.Message, StringComparison.Ordinal);
        Assert.Contains("8 bytes", sixteen.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_claiming_a_giant_raster_is_refused_without_allocating_it()
    {
        // 100000 by 100000 samples is 20 GB of ushorts. The reader compares the claim against the bytes it has
        // BEFORE it allocates, so this fails on the missing raster: without that order the test would die of an
        // out-of-memory allocation rather than a TileWorldException. The allocation bound below pins it.
        byte[] bytes = Pgm("P5\n100000 100000\n255\n", 1, 2, 3, 4);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(bytes));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("10000000000 bytes", ex.Message, StringComparison.Ordinal);
        Assert.True(allocated < 1024 * 1024, $"the refusal allocated {allocated} bytes, it should allocate only its message");
    }

    [Fact]
    public void The_file_overload_reads_the_bytes_on_disk()
    {
        using var tmp = new TempDir();
        string path = tmp.Sub("heights.pgm");
        File.WriteAllBytes(path, Pgm("P5\n2 2\n255\n", 5, 6, 7, 8));

        PgmImage image = PgmReader.Read(path);

        Assert.Equal(new ushort[] { 5, 6, 7, 8 }, image.Samples);
    }

    [Fact]
    public void The_file_overload_names_the_path_in_its_message()
    {
        using var tmp = new TempDir();
        string path = tmp.Sub("broken.pgm");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("P2\n2 2\n255\n"));

        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(path));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_is_reported_as_a_tile_world_error_naming_it()
    {
        using var tmp = new TempDir();
        string path = tmp.Sub("nothing-here.pgm");

        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(path));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_given_name_rides_the_message_of_a_byte_read()
    {
        var ex = Assert.Throws<TileWorldException>(
            () => PgmReader.Read(Encoding.ASCII.GetBytes("P2\n"), "upload.pgm"));

        Assert.Contains("upload.pgm", ex.Message, StringComparison.Ordinal);
    }
}
