using System;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Format;

/// <summary>
/// The one varint definition in the tree, contracts 15. Unsigned LEB128, seven value bits per byte, low
/// group first, the high bit set on every byte but the last, at most five bytes for a 32 bit value and ten
/// for a 64 bit one, minimal encodings only. Every reader is total: a malformed varint returns false with a
/// stable reason token, never an exception, because the bytes come from a remote peer.
/// </summary>
public class ContentVarintTests
{
    [Theory]
    [InlineData(0u, 1)]
    [InlineData(1u, 1)]
    [InlineData(127u, 1)]
    [InlineData(128u, 2)]
    [InlineData(16383u, 2)]
    [InlineData(16384u, 3)]
    [InlineData(uint.MaxValue, 5)]
    public void RoundTripsEveryBoundary(uint value, int expectedSize)
    {
        Span<byte> buffer = stackalloc byte[5];
        int written = ContentVarint.Write(buffer, value);
        Assert.Equal(expectedSize, written);
        Assert.Equal(expectedSize, ContentVarint.Size(value));

        int offset = 0;
        Assert.True(ContentVarint.TryRead(buffer[..written], ref offset, out uint read, out string? reason));
        Assert.Null(reason);
        Assert.Equal(value, read);
        Assert.Equal(written, offset);
    }

    [Theory]
    [InlineData(0ul, 1)]
    [InlineData(127ul, 1)]
    [InlineData(128ul, 2)]
    [InlineData(uint.MaxValue, 5)]
    [InlineData(1ul << 63, 10)]
    [InlineData(ulong.MaxValue, 10)]
    public void RoundTripsEveryBoundaryAt64Bits(ulong value, int expectedSize)
    {
        Span<byte> buffer = stackalloc byte[10];
        int written = ContentVarint.WriteUInt64(buffer, value);
        Assert.Equal(expectedSize, written);
        Assert.Equal(expectedSize, ContentVarint.SizeUInt64(value));

        int offset = 0;
        Assert.True(ContentVarint.TryReadUInt64(buffer[..written], ref offset, out ulong read, out string? reason));
        Assert.Null(reason);
        Assert.Equal(value, read);
        Assert.Equal(written, offset);
    }

    [Fact]
    public void ANonMinimalEncodingIsRejected()
    {
        // 0x81 0x00 decodes to 1 under a permissive reader, and 1 already has a one-byte encoding. Accepting
        // both spellings would give one value two byte forms, which is how byte equality stops being value
        // equality and how a canonical format stops being canonical.
        byte[] overlong = [0x81, 0x00];
        int offset = 0;
        Assert.False(ContentVarint.TryRead(overlong, ref offset, out uint value, out string? reason));
        Assert.Equal("varint-not-minimal", reason);
        Assert.Equal(0, offset);
        Assert.Equal(0u, value);

        byte[] overlongZero = [0x80, 0x00];
        offset = 0;
        Assert.False(ContentVarint.TryRead(overlongZero, ref offset, out _, out reason));
        Assert.Equal("varint-not-minimal", reason);

        byte[] overlong64 = [0x81, 0x80, 0x00];
        offset = 0;
        Assert.False(ContentVarint.TryReadUInt64(overlong64, ref offset, out _, out reason));
        Assert.Equal("varint-not-minimal", reason);
    }

    [Fact]
    public void ANonTerminatingVarintIsRejectedAfterFiveBytes()
    {
        byte[] runaway = [0x80, 0x80, 0x80, 0x80, 0x80, 0x01];
        int offset = 0;
        Assert.False(ContentVarint.TryRead(runaway, ref offset, out uint value, out string? reason));
        Assert.Equal("varint-overflow", reason);
        Assert.Equal(0, offset);
        Assert.Equal(0u, value);
    }

    [Fact]
    public void AValueTooWideForThirtyTwoBitsIsRejected()
    {
        // Five bytes, but the fifth carries value bits above bit 31.
        byte[] tooWide = [0xFF, 0xFF, 0xFF, 0xFF, 0x1F];
        int offset = 0;
        Assert.False(ContentVarint.TryRead(tooWide, ref offset, out _, out string? reason));
        Assert.Equal("varint-overflow", reason);
    }

    [Fact]
    public void ANonTerminatingVarintIsRejectedAfterTenBytesAt64Bits()
    {
        byte[] runaway = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01];
        int offset = 0;
        Assert.False(ContentVarint.TryReadUInt64(runaway, ref offset, out ulong value, out string? reason));
        Assert.Equal("varint-overflow", reason);
        Assert.Equal(0, offset);
        Assert.Equal(0ul, value);

        // Ten bytes, but the tenth carries value bits above bit 63.
        byte[] tooWide = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02];
        offset = 0;
        Assert.False(ContentVarint.TryReadUInt64(tooWide, ref offset, out _, out reason));
        Assert.Equal("varint-overflow", reason);
    }

    [Fact]
    public void ZigZagMapsTheSmallSignedValues()
    {
        Assert.Equal(0u, ContentVarint.ZigZag(0));
        Assert.Equal(1u, ContentVarint.ZigZag(-1));
        Assert.Equal(2u, ContentVarint.ZigZag(1));
        Assert.Equal(3u, ContentVarint.ZigZag(-2));

        Span<byte> buffer = stackalloc byte[5];
        foreach (int value in new[] { 0, -1, 1, -2, 2, 63, -64, int.MinValue, int.MaxValue })
        {
            Assert.Equal(value, ContentVarint.UnZigZag(ContentVarint.ZigZag(value)));

            int written = ContentVarint.WriteSigned(buffer, value);
            Assert.Equal(written, ContentVarint.SizeSigned(value));
            int offset = 0;
            Assert.True(ContentVarint.TryReadSigned(buffer[..written], ref offset, out int read, out string? reason));
            Assert.Null(reason);
            Assert.Equal(value, read);
        }
    }

    [Fact]
    public void ZigZagAtSixtyFourBitsRoundTrips()
    {
        Assert.Equal(0ul, ContentVarint.ZigZag64(0));
        Assert.Equal(1ul, ContentVarint.ZigZag64(-1));
        Assert.Equal(2ul, ContentVarint.ZigZag64(1));

        Span<byte> buffer = stackalloc byte[10];
        foreach (long value in new[] { 0L, -1L, 1L, long.MinValue, long.MaxValue })
        {
            Assert.Equal(value, ContentVarint.UnZigZag64(ContentVarint.ZigZag64(value)));

            int written = ContentVarint.WriteSigned64(buffer, value);
            Assert.Equal(written, ContentVarint.SizeSigned64(value));
            int offset = 0;
            Assert.True(ContentVarint.TryReadSigned64(buffer[..written], ref offset, out long read, out string? reason));
            Assert.Null(reason);
            Assert.Equal(value, read);
        }
    }

    [Fact]
    public void AReadPastTheEndOfTheSpanReturnsFalseRatherThanThrowing()
    {
        byte[] truncated = [0x80];
        int offset = 0;
        Assert.False(ContentVarint.TryRead(truncated, ref offset, out uint value, out string? reason));
        Assert.Equal("field-truncated", reason);
        Assert.Equal(0, offset);
        Assert.Equal(0u, value);

        offset = 0;
        Assert.False(ContentVarint.TryRead(ReadOnlySpan<byte>.Empty, ref offset, out _, out reason));
        Assert.Equal("field-truncated", reason);

        // An offset already at the end, and one part way through a longer buffer, are the same case.
        byte[] buffer = [0x01, 0x80];
        offset = 1;
        Assert.False(ContentVarint.TryRead(buffer, ref offset, out _, out reason));
        Assert.Equal("field-truncated", reason);
        Assert.Equal(1, offset);

        offset = 0;
        Assert.False(ContentVarint.TryReadUInt64([0x80, 0x80], ref offset, out _, out reason));
        Assert.Equal("field-truncated", reason);
    }

    [Fact]
    public void ASequenceOfVarintsReadsBackInOrder()
    {
        Span<byte> buffer = stackalloc byte[32];
        int written = ContentVarint.Write(buffer, 300);
        written += ContentVarint.Write(buffer[written..], 0);
        written += ContentVarint.WriteSigned(buffer[written..], -7);

        int offset = 0;
        Assert.True(ContentVarint.TryRead(buffer[..written], ref offset, out uint first, out _));
        Assert.True(ContentVarint.TryRead(buffer[..written], ref offset, out uint second, out _));
        Assert.True(ContentVarint.TryReadSigned(buffer[..written], ref offset, out int third, out _));
        Assert.Equal(300u, first);
        Assert.Equal(0u, second);
        Assert.Equal(-7, third);
        Assert.Equal(written, offset);
    }
}
