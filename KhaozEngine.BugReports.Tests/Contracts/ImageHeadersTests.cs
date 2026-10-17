using System;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Contracts;

public sealed class ImageHeadersTests
{
    [Fact]
    public void Png_and_jpeg_sizes_read_by_media_type()
    {
        Assert.True(ImageHeaders.TryReadSize(ImageHeaders.PngMediaType, TestBundles.Png(2561, 1440), out int width, out int height));
        Assert.Equal((2561, 1440), (width, height));
        Assert.True(ImageHeaders.TryReadSize(ImageHeaders.JpegMediaType, TestBundles.Jpeg(6, 3), out width, out height));
        Assert.Equal((6, 3), (width, height));
    }

    [Fact]
    public void Empty_bytes_unknown_types_and_mismatched_formats_fail()
    {
        Assert.False(ImageHeaders.TryReadSize(ImageHeaders.PngMediaType, ReadOnlySpan<byte>.Empty, out _, out _));
        Assert.False(ImageHeaders.TryReadSize("image/gif", TestBundles.Png(4, 4), out _, out _));
        Assert.False(ImageHeaders.TryReadSize(ImageHeaders.PngMediaType, TestBundles.Jpeg(4, 4), out _, out _));
        Assert.False(ImageHeaders.TryReadSize(ImageHeaders.JpegMediaType, TestBundles.JpegWithoutFrame(), out int width, out int height));
        Assert.Equal((0, 0), (width, height));
    }
}
