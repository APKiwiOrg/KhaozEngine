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
[Collection("AllocSensitive")]
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
    public void A_comment_may_open_immediately_after_the_magic()
    {
        // No whitespace between P5 and the hash, so the comment IS the separator that the header needs before
        // width.
        PgmImage image = PgmReader.Read(Pgm("P5# straight off the magic\n2 2\n255\n", 1, 2, 3, 4));

        Assert.Equal(2, image.Width);
        Assert.Equal(new ushort[] { 1, 2, 3, 4 }, image.Samples);
    }

    [Fact]
    public void A_crlf_header_spends_its_carriage_return_and_leaves_the_line_feed_as_sample_zero()
    {
        // Pinning the netpbm rule rather than endorsing this file: exactly one whitespace byte closes the
        // header, so a CRLF terminator shifts the whole raster by a byte. It cannot be fixed up, because a
        // raster whose first sample is genuinely 10 is byte for byte the same file. Write PGMs with LF.
        PgmImage image = PgmReader.Read(Pgm("P5\n2 2\n255\r\n", 11, 12, 13));

        Assert.Equal(10, image.Sample(0, 0));   // the orphaned line feed
        Assert.Equal(11, image.Sample(1, 0));
        Assert.Equal(12, image.Sample(0, 1));
        Assert.Equal(13, image.Sample(1, 1));
    }

    [Fact]
    public void A_sample_outside_the_image_is_refused_on_the_axis_that_is_out()
    {
        PgmImage image = PgmReader.Read(Pgm("P5\n2 1\n255\n", 1, 2));

        // Row 1 indexes a slot a 2 by 2 image would have, so an unchecked read would return a neighbour's
        // sample rather than throwing.
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Sample(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Sample(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Sample(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Sample(0, -1));
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

    [Theory]
    [InlineData("P2")]
    [InlineData("P6")]
    [InlineData("Q5")]
    public void A_non_P5_magic_is_refused(string magic)
    {
        var ex = Assert.Throws<TileWorldException>(
            () => PgmReader.Read(Pgm($"{magic}\n1 1\n255\n", 42)));

        Assert.Equal(TileWorldRefusal.PgmMagicUnsupported, ex.Reason);
        Assert.Equal("<bytes>", ex.SourceName);
        Assert.Equal("P5", ex.ExpectedMagic);
        Assert.Equal(Encoding.ASCII.GetBytes(magic), ex.ActualMagic);
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(Pgm("P5\n1 1\n255\n", 42)).Samples);
    }

    [Theory]
    [InlineData("")]
    [InlineData("P")]
    public void An_empty_or_truncated_magic_is_refused(string magic)
    {
        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(Encoding.ASCII.GetBytes(magic)));

        Assert.Equal(TileWorldRefusal.PgmMagicUnsupported, ex.Reason);
        Assert.Equal("<bytes>", ex.SourceName);
        Assert.Equal("P5", ex.ExpectedMagic);
        Assert.Equal(Encoding.ASCII.GetBytes(magic), ex.ActualMagic);
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(Pgm("P5\n1 1\n255\n", 42)).Samples);
    }

    [Fact]
    public void A_truncated_header_is_refused()
    {
        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(Pgm("P5\n1 1\n")));

        Assert.Equal(TileWorldRefusal.PgmHeaderIntegerMalformed, ex.Reason);
        Assert.Equal("<bytes>", ex.SourceName);
        Assert.Equal("maxval", ex.Field);
        Assert.Equal(7, ex.ByteOffset);
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(Pgm("P5\n1 1\n255\n", 42)).Samples);
    }

    [Theory]
    [InlineData("P5\nx 1\n255\n", "width", 3)]
    [InlineData("P5\n1 x\n255\n", "height", 5)]
    [InlineData("P5\n1 1\nx\n", "maxval", 7)]
    // A minus sign never reaches the number, so a negative dimension fails as a malformed header.
    [InlineData("P5\n-1 1\n255\n", "width", 3)]
    public void A_non_decimal_header_integer_is_refused(string header, string field, int offset)
    {
        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(Pgm(header, 42), "header.pgm"));

        Assert.Equal(TileWorldRefusal.PgmHeaderIntegerMalformed, ex.Reason);
        Assert.Equal("header.pgm", ex.SourceName);
        Assert.Equal(field, ex.Field);
        Assert.Equal(offset, ex.ByteOffset);
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(Pgm("P5\n1 1\n255\n", 42)).Samples);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(70000, 65535, 1)]
    public void A_maxval_outside_one_to_sixty_five_thousand_five_hundred_and_thirty_five_is_refused(
        int maxval, int validMaxval, ushort validSample)
    {
        var ex = Assert.Throws<TileWorldException>(
            () => PgmReader.Read(Pgm($"P5\n1 1\n{maxval}\n", 0, 1)));

        Assert.Equal(TileWorldRefusal.PgmMaxValueOutsideRange, ex.Reason);
        Assert.Equal("<bytes>", ex.SourceName);
        Assert.Equal("maxval", ex.Field);
        Assert.Equal(maxval, ex.MaxValue);
        Assert.Equal(1f, ex.Minimum);
        Assert.Equal(65535f, ex.Maximum);
        Assert.Equal(new[] { validSample }, PgmReader.Read(Pgm($"P5\n1 1\n{validMaxval}\n", 0, 1)).Samples);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void A_dimension_that_is_not_positive_is_refused(int width, int height)
    {
        var ex = Assert.Throws<TileWorldException>(
            () => PgmReader.Read(Pgm($"P5\n{width} {height}\n255\n", 42)));

        Assert.Equal(TileWorldRefusal.PgmDimensionsNonPositive, ex.Reason);
        Assert.Equal("<bytes>", ex.SourceName);
        Assert.Equal(width, ex.Width);
        Assert.Equal(height, ex.Height);
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(Pgm("P5\n1 1\n255\n", 42)).Samples);
    }

    [Theory]
    [InlineData(255, 8, 4L)]
    [InlineData(65535, 16, 8L)]
    public void A_raster_shorter_than_the_header_claims_is_refused(int maxval, int bits, long required)
    {
        string header = $"P5\n2 2\n{maxval}\n";
        var ex = Assert.Throws<TileWorldException>(
            () => PgmReader.Read(Pgm(header, new byte[(int)required - 1])));

        Assert.Equal(TileWorldRefusal.PgmRasterTruncated, ex.Reason);
        Assert.Equal("<bytes>", ex.SourceName);
        Assert.Equal(2, ex.Width);
        Assert.Equal(2, ex.Height);
        Assert.Equal(bits, ex.BitsPerSample);
        Assert.Equal(required, ex.RequiredByteCount);
        Assert.Equal(required - 1, ex.AvailableByteCount);
        Assert.Equal(new ushort[] { 0, 0, 0, 0 }, PgmReader.Read(Pgm(header, new byte[(int)required])).Samples);
    }

    [Fact]
    public void A_header_claiming_a_giant_raster_is_refused_without_allocating_it()
    {
        // 100000 by 100000 samples is 20 GB of ushorts. The reader compares the claim against the bytes it has
        // BEFORE it allocates, so this fails on the missing raster: without that order the test would die of an
        // out-of-memory allocation rather than a TileWorldException. The allocation bound below pins it.
        byte[] bytes = Pgm("P5\n100000 100000\n255\n", 1, 2, 3, 4);

        TileWorldException ex = null!;
        long allocated = AllocationMeasurement.Measure("A giant raster refusal",
            () => ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(bytes)));

        Assert.Equal(TileWorldRefusal.PgmRasterTruncated, ex.Reason);
        Assert.Equal("<bytes>", ex.SourceName);
        Assert.Equal(100000, ex.Width);
        Assert.Equal(100000, ex.Height);
        Assert.Equal(8, ex.BitsPerSample);
        Assert.Equal(10000000000L, ex.RequiredByteCount);
        Assert.Equal(4L, ex.AvailableByteCount);
        Assert.True(allocated < 1024 * 1024, $"the refusal allocated {allocated} bytes, it should allocate only its message");
        Assert.Equal(new ushort[] { 1, 2, 3, 4 }, PgmReader.Read(Pgm("P5\n2 2\n255\n", 1, 2, 3, 4)).Samples);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_missing_raster_delimiter_is_refused(bool atEnd)
    {
        byte[] bytes = Pgm("P5\n1 1\n255", atEnd ? Array.Empty<byte>() : new byte[] { 42 });
        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(bytes, "delimiter.pgm"));

        Assert.Equal(TileWorldRefusal.PgmRasterDelimiterMissing, ex.Reason);
        Assert.Equal("delimiter.pgm", ex.SourceName);
        Assert.Equal("maxval", ex.Field);
        Assert.Equal(10, ex.ByteOffset);
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(Pgm("P5\n1 1\n255\n", 42)).Samples);
    }

    [Theory]
    [InlineData("P51 1\n255\n", "width", 2)]
    [InlineData("P5\n1x1\n255\n", "height", 4)]
    [InlineData("P5\n1 1x255\n", "maxval", 6)]
    public void A_missing_header_separator_is_refused(string header, string field, int offset)
    {
        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(Pgm(header, 42), "separator.pgm"));

        Assert.Equal(TileWorldRefusal.PgmHeaderSeparatorMissing, ex.Reason);
        Assert.Equal("separator.pgm", ex.SourceName);
        Assert.Equal(field, ex.Field);
        Assert.Equal(offset, ex.ByteOffset);
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(Pgm("P5\n1 1\n255\n", 42)).Samples);
    }

    [Theory]
    [InlineData("P5\n2147483648 1\n255\n", "width", 12)]
    [InlineData("P5\n1 2147483648\n255\n", "height", 14)]
    [InlineData("P5\n1 1\n2147483648\n", "maxval", 16)]
    public void A_header_integer_outside_the_supported_range_is_refused(string header, string field, int offset)
    {
        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(Pgm(header, 42), "overflow.pgm"));

        Assert.Equal(TileWorldRefusal.PgmHeaderIntegerOverflow, ex.Reason);
        Assert.Equal("overflow.pgm", ex.SourceName);
        Assert.Equal(field, ex.Field);
        Assert.Equal(offset, ex.ByteOffset);
        Assert.Equal(2147483648L, ex.ParsedValue);
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(Pgm("P5\n1 1\n255\n", 42)).Samples);
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
    public void The_file_overload_carries_the_path_on_a_refusal()
    {
        using var tmp = new TempDir();
        string path = tmp.Sub("broken.pgm");
        File.WriteAllBytes(path, Pgm("P2\n1 1\n255\n", 42));

        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(path));

        Assert.Equal(TileWorldRefusal.PgmMagicUnsupported, ex.Reason);
        Assert.Equal(path, ex.SourceName);
        Assert.Equal("P5", ex.ExpectedMagic);
        Assert.Equal(new byte[] { (byte)'P', (byte)'2' }, ex.ActualMagic);
        File.WriteAllBytes(path, Pgm("P5\n1 1\n255\n", 42));
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(path).Samples);
    }

    [Fact]
    public void A_missing_file_is_reported_as_a_tile_world_error_naming_it()
    {
        using var tmp = new TempDir();
        string path = tmp.Sub("nothing-here.pgm");

        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(path));

        Assert.Equal(TileWorldRefusal.PgmReadFailed, ex.Reason);
        Assert.Equal(path, ex.SourceName);
        Assert.NotNull(ex.InnerException);
        File.WriteAllBytes(path, Pgm("P5\n1 1\n255\n", 42));
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(path).Samples);
    }

    [Fact]
    public void A_path_the_framework_itself_rejects_still_comes_back_as_a_tile_world_error()
    {
        // An empty path fails inside File.ReadAllBytes as an ArgumentException. A caller of this layer handles
        // one exception type from a bad file, so that must not escape raw.
        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(""));

        Assert.Equal(TileWorldRefusal.PgmReadFailed, ex.Reason);
        Assert.Equal("<empty path>", ex.SourceName);
        Assert.NotNull(ex.InnerException);
        Assert.Throws<ArgumentNullException>(() => PgmReader.Read(null!));
        using var tmp = new TempDir();
        string path = tmp.Sub("valid.pgm");
        File.WriteAllBytes(path, Pgm("P5\n1 1\n255\n", 42));
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(path).Samples);
    }

    [Theory]
    [InlineData("upload.pgm", "upload.pgm")]
    [InlineData(null, "<bytes>")]
    [InlineData("", "<bytes>")]
    public void A_byte_read_carries_its_source_name(string? name, string expectedName)
    {
        var ex = Assert.Throws<TileWorldException>(() => PgmReader.Read(Pgm("P2\n1 1\n255\n", 42), name));

        Assert.Equal(TileWorldRefusal.PgmMagicUnsupported, ex.Reason);
        Assert.Equal(expectedName, ex.SourceName);
        Assert.Equal("P5", ex.ExpectedMagic);
        Assert.Equal(new byte[] { (byte)'P', (byte)'2' }, ex.ActualMagic);
        Assert.Equal(new ushort[] { 42 }, PgmReader.Read(Pgm("P5\n1 1\n255\n", 42), name).Samples);
    }
}
