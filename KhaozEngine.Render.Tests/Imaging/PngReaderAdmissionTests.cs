using System;
using System.IO;
using KhaozEngine.Imaging;
using Xunit;
using static KhaozEngine.Tests.Imaging.PngReaderTests;

namespace KhaozEngine.Tests.Imaging;

public class PngReaderAdmissionTests
{
    [Fact]
    public void Input_without_the_png_signature_is_refused()
    {
        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() => PngReader.Decode(new byte[] { 0 }));

        Assert.IsAssignableFrom<MalformedDataException>(error);
        Assert.Equal(PngDecodeRefusal.SignatureInvalid, error.Reason);
        Assert.Equal(1, error.AvailableBytes);
    }

    [Theory]
    [InlineData(false, PngDecodeRefusal.ChunkLengthUnsupported)]
    [InlineData(true, PngDecodeRefusal.ChunkDataTruncated)]
    public void Chunk_lengths_are_validated_before_slicing(bool truncate, PngDecodeRefusal reason)
    {
        byte[] chunk = Chunk("tEXt", new byte[] { 0, 0 });
        if (truncate) Array.Resize(ref chunk, chunk.Length - 1);
        else chunk[0] = 0x80;
        byte[] png = truncate
            ? Join(Signature, Chunk("IHDR", Header(1, 1, 8, 0, 0)), chunk)
            : Join(Signature, Chunk("IHDR", Header(1, 1, 8, 0, 0)), chunk,
                Chunk("IDAT", Compress(new byte[] { 0, 42 })), Chunk("IEND", Array.Empty<byte>()));

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() => PngReader.Decode(png));

        Assert.Equal(reason, error.Reason);
        Assert.Equal(33, error.Offset);
        Assert.Equal(truncate ? 2u : 0x80000002u, error.DeclaredLength);
        if (truncate) Assert.Equal(13, error.AvailableBytes);
    }

    [Theory]
    [InlineData(false, 33)]
    [InlineData(true, 20)]
    public void Header_must_be_unique_and_first(bool precededByAncillary, int offset)
    {
        byte[] header = Chunk("IHDR", Header(1, 1, 8, 0, 0));
        byte[] first = precededByAncillary ? Chunk("tEXt", Array.Empty<byte>()) : header;
        byte[] png = Join(Signature, first, header,
            Chunk("IDAT", Compress(new byte[] { 0, 42 })), Chunk("IEND", Array.Empty<byte>()));

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() => PngReader.Decode(png));

        Assert.Equal(PngDecodeRefusal.HeaderOutOfOrder, error.Reason);
        Assert.Equal(offset, error.Offset);
    }

    [Fact]
    public void Data_chunks_must_be_contiguous()
    {
        byte[] header = Chunk("IHDR", Header(1, 1, 8, 0, 0));
        byte[] data = Chunk("IDAT", Compress(new byte[] { 0, 42 }));
        byte[] gap = Chunk("tEXt", Array.Empty<byte>());
        byte[] end = Chunk("IEND", Array.Empty<byte>());
        Assert.Equal(new byte[] { 42 }, PngReader.Decode(Join(Signature, header, data,
            Chunk("IDAT", Array.Empty<byte>()), gap, end)).Bytes);

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() =>
            PngReader.Decode(Join(Signature, header, data, gap, Chunk("IDAT", Array.Empty<byte>()), end)));

        Assert.Equal(PngDecodeRefusal.DataOutOfOrder, error.Reason);
        Assert.Equal("IDAT", error.ChunkType);
        Assert.Equal(45 + data.Length, error.Offset);
    }

    [Fact]
    public void Compressed_allocation_cap_checks_the_accumulated_payload_before_writing()
    {
        PngReader.RejectCompressedAllocationForTests(PngReader.MaxDecodedBytes - 1L, 1);

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() =>
            PngReader.RejectCompressedAllocationForTests(PngReader.MaxDecodedBytes - 1L, 2));

        Assert.Equal(PngDecodeRefusal.CompressedAllocationCapExceeded, error.Reason);
        Assert.Equal(PngReader.MaxDecodedBytes + 1L, error.RequiredBytes);
        Assert.Equal(PngReader.MaxDecodedBytes, error.MaximumBytes);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    public void End_chunk_requires_header_data_and_an_empty_payload(bool headerSeen, bool dataSeen,
        int payloadLength)
    {
        byte[] header = headerSeen ? Chunk("IHDR", Header(1, 1, 8, 0, 0)) : Array.Empty<byte>();
        byte[] data = dataSeen ? Chunk("IDAT", Compress(new byte[] { 0, 42 })) : Array.Empty<byte>();
        byte[] png = Join(Signature, header, data,
            Chunk("IEND", new byte[payloadLength]));

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() => PngReader.Decode(png));

        Assert.Equal(PngDecodeRefusal.EndChunkInvalid, error.Reason);
        Assert.Equal(8 + header.Length + data.Length, error.Offset);
        Assert.Equal(payloadLength, error.PayloadLength);
        Assert.Equal(headerSeen, error.HeaderSeen);
        Assert.Equal(dataSeen, error.DataSeen);
    }

    [Fact]
    public void Bytes_after_the_end_chunk_are_refused()
    {
        byte[] control = BuildPng(1, 1, 8, 0, 0, new byte[] { 0, 42 });
        Assert.Equal(new byte[] { 42 }, PngReader.Decode(control).Bytes);

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() =>
            PngReader.Decode(Join(control, new byte[] { 1 })));

        Assert.Equal(PngDecodeRefusal.DataAfterEnd, error.Reason);
        Assert.Equal(control.Length, error.Offset);
        Assert.Equal(1, error.TrailingBytes);
    }

    [Fact]
    public void A_complete_end_chunk_is_required()
    {
        byte[] control = BuildPng(1, 1, 8, 0, 0, new byte[] { 0, 42 });
        Assert.Equal(new byte[] { 42 }, PngReader.Decode(control).Bytes);

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() =>
            PngReader.Decode(control.AsSpan(0, control.Length - 12)));

        Assert.Equal(PngDecodeRefusal.EndChunkMissing, error.Reason);
        Assert.True(error.HeaderSeen);
        Assert.True(error.DataSeen);
    }

    [Fact]
    public void Header_has_the_wire_format_length()
    {
        byte[] header = Header(1, 1, 8, 0, 0);
        byte[] png = Join(Signature, Chunk("IHDR", header.AsSpan(0, 12).ToArray()),
            Chunk("IDAT", Compress(new byte[] { 0, 42 })), Chunk("IEND", Array.Empty<byte>()));

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() => PngReader.Decode(png));

        Assert.Equal(PngDecodeRefusal.HeaderLengthMismatch, error.Reason);
        Assert.Equal(12, error.ActualLength);
        Assert.Equal(13, error.ExpectedLength);
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 0u)]
    [InlineData(0x80000000u, 1u)]
    [InlineData(1u, 0x80000000u)]
    public void Dimensions_must_fit_positive_supported_integers(uint width, uint height)
    {
        byte[] png = Join(Signature, Chunk("IHDR", Header(unchecked((int)width), unchecked((int)height), 8, 0, 0)),
            Chunk("IDAT", Compress(new byte[] { 0, 42 })), Chunk("IEND", Array.Empty<byte>()));

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() => PngReader.Decode(png));

        Assert.Equal(PngDecodeRefusal.DimensionsInvalid, error.Reason);
        Assert.Equal(width, error.Width);
        Assert.Equal(height, error.Height);
    }

    [Fact]
    public void Unsupported_critical_chunks_are_distinguished_from_ancillary_chunks()
    {
        byte[] header = Chunk("IHDR", Header(1, 1, 8, 0, 0));
        byte[] data = Chunk("IDAT", Compress(new byte[] { 0, 42 }));
        byte[] end = Chunk("IEND", Array.Empty<byte>());
        Assert.Equal(new byte[] { 42 }, PngReader.Decode(Join(Signature, header,
            Chunk("aBCD", Array.Empty<byte>()), data, end)).Bytes);

        PngDecodeNotSupportedException error = Assert.Throws<PngDecodeNotSupportedException>(() =>
            PngReader.Decode(Join(Signature, header, Chunk("ABCD", Array.Empty<byte>()), data, end)));

        Assert.Equal(PngDecodeRefusal.CriticalChunkUnsupported, error.Reason);
        Assert.Equal("ABCD", error.ChunkType);
    }

    [Fact]
    public void Unsupported_color_type_is_reported()
    {
        byte[] png = BuildPng(1, 1, 8, 1, 0, new byte[] { 0, 42 });

        PngDecodeNotSupportedException error = Assert.Throws<PngDecodeNotSupportedException>(() => PngReader.Decode(png));

        Assert.Equal(PngDecodeRefusal.ColorTypeUnsupported, error.Reason);
        Assert.Equal(1, error.ColorType);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Compression_and_filter_methods_must_be_supported(byte compression, byte filter)
    {
        byte[] header = Header(1, 1, 8, 0, 0);
        header[10] = compression;
        header[11] = filter;
        byte[] png = Join(Signature, Chunk("IHDR", header),
            Chunk("IDAT", Compress(new byte[] { 0, 42 })), Chunk("IEND", Array.Empty<byte>()));

        PngDecodeNotSupportedException error = Assert.Throws<PngDecodeNotSupportedException>(() => PngReader.Decode(png));

        Assert.Equal(PngDecodeRefusal.CompressionOrFilterMethodUnsupported, error.Reason);
        Assert.Equal((int)compression, error.CompressionMethod);
        Assert.Equal((int)filter, error.FilterMethod);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Unsupported_row_filters_name_the_row(int row)
    {
        byte[] filtered = { 0, 42, 0, 43 };
        Assert.Equal(new byte[] { 42, 43 }, PngReader.Decode(BuildPng(1, 2, 8, 0, 0, filtered)).Bytes);
        filtered[row * 2] = 5;

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() =>
            PngReader.Decode(BuildPng(1, 2, 8, 0, 0, filtered)));

        Assert.Equal(PngDecodeRefusal.RowFilterUnsupported, error.Reason);
        Assert.Equal(5, error.FilterType);
        Assert.Equal(row, error.RowIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Corrupt_compressed_streams_keep_the_native_error_as_a_png_refusal(bool corruptChecksum)
    {
        byte[] header = Chunk("IHDR", Header(1, 1, 8, 0, 0));
        byte[] compressed = Compress(new byte[] { 0, 42 });
        byte[] end = Chunk("IEND", Array.Empty<byte>());
        Assert.Equal(new byte[] { 42 }, PngReader.Decode(Join(Signature, header, Chunk("IDAT", compressed), end)).Bytes);
        if (corruptChecksum) compressed[^1] ^= 1;
        else compressed[2] = 7; // Reserved DEFLATE block type, after the valid two-byte zlib header.

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() =>
            PngReader.Decode(Join(Signature, header, Chunk("IDAT", compressed), end)));

        Assert.Equal(PngDecodeRefusal.CompressedStreamCorrupt, error.Reason);
        Assert.IsType<InvalidDataException>(error.InnerException);
    }
}
