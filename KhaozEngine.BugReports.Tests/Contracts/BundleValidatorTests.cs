using System;
using System.IO;
using System.Linq;
using System.Text;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Contracts;

public sealed class BundleValidatorTests
{
    const string Build = "sections/build.json";

    static BundleValidationResult Validate(TestBundles bundle) => BundleValidator.Validate(bundle.Build());

    static string? ErrorOf(TestBundles bundle) => Validate(bundle).Error;

    [Fact]
    public void Valid_bundle_passes_and_returns_the_manifest()
    {
        BundleValidationResult result = BundleValidator.Validate(TestBundles.Valid());
        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Equal(TestBundles.ReportId, result.Manifest!.ReportId);
    }

    [Fact]
    public void Over_max_bytes_is_too_large()
        => Assert.Equal("too_large", BundleValidator.Validate(TestBundles.Valid(), maxBytes: 100).Error);

    [Fact]
    public void Non_seekable_stream_over_the_cap_is_too_large()
    {
        var stream = new ZeroStream(1001);
        BundleValidationResult result = BundleValidator.Validate(stream, maxBytes: 1000);
        Assert.Equal("too_large", result.Error);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Garbage_is_not_a_zip()
        => Assert.Equal("not_a_zip", BundleValidator.Validate(new MemoryStream(Encoding.UTF8.GetBytes("this is not a zip archive"))).Error);

    [Fact]
    public void More_than_128_entries_is_rejected()
    {
        TestBundles bundle = TestBundles.Default();
        for (int i = 0; i < 128; i++)
            bundle.WithEntry("sections/s" + i + ".json", Encoding.UTF8.GetBytes("{}"));
        Assert.Equal("too_many_entries", ErrorOf(bundle));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("../manifest.json")]
    [InlineData("sections/../x.json")]
    [InlineData("clip/120.jpg")]
    public void Unlisted_names_are_rejected(string name)
        => Assert.Equal("entry_not_allowed: " + name, ErrorOf(TestBundles.Default().WithEntry(name, [1, 2, 3])));

    [Fact]
    public void Duplicate_entry_is_rejected()
        => Assert.Equal("duplicate_entry: " + Build, ErrorOf(TestBundles.Default().WithDuplicate(Build)));

    [Fact]
    public void Deflated_entry_is_rejected_without_inflating()
    {
        TestBundles bundle = TestBundles.Default().WithEntry(Build, new byte[64 * 1024 * 1024]).Deflated(Build);
        Assert.Equal("compressed_entry: " + Build, ErrorOf(bundle));
    }

    [Fact]
    public void Missing_manifest_is_rejected()
        => Assert.Equal("manifest_missing", ErrorOf(TestBundles.Default().WithoutEntry(BundleEntryNames.Manifest)));

    [Fact]
    public void Wrong_format_version_is_rejected()
        => Assert.Equal("format_version", ErrorOf(TestBundles.Default().WithManifest(m => m with { FormatVersion = 2 })));

    [Fact]
    public void Invalid_report_id_is_rejected()
        => Assert.Equal("report_id_invalid",
            ErrorOf(TestBundles.Default().WithManifest(m => m with { ReportId = "0192f1c2a3b46c4d8e9f0a1b2c3d4e5f" })));

    [Fact]
    public void Description_limit_is_exactly_4000()
    {
        Assert.Null(ErrorOf(TestBundles.Default().WithManifest(m => m with { Description = new string('x', 4000) })));
        Assert.Equal("description_invalid",
            ErrorOf(TestBundles.Default().WithManifest(m => m with { Description = new string('x', 4001) })));
        Assert.Equal("description_invalid", ErrorOf(TestBundles.Default().WithManifest(m => m with { Description = "   " })));
    }

    [Fact]
    public void Size_or_hash_mismatch_is_rejected()
    {
        Assert.Equal("size_mismatch: " + Build, ErrorOf(TestBundles.Default().WithManifest(m => m with
        {
            Entries = m.Entries.Select(e => e.Name == Build ? e with { Size = e.Size + 1 } : e).ToList(),
        })));
        Assert.Equal("hash_mismatch: " + Build, ErrorOf(TestBundles.Default().WithManifest(m => m with
        {
            Entries = m.Entries.Select(e => e.Name == Build ? e with { Sha256 = new string('a', 64) } : e).ToList(),
        })));
    }

    [Fact]
    public void Listed_but_absent_and_present_but_unlisted_are_mismatches()
    {
        const string Extra = "sections/extra.json";
        Assert.Equal("entry_mismatch: " + Extra, ErrorOf(TestBundles.Default().WithManifest(m => m with
        {
            Entries = [.. m.Entries, new BundleEntry(Extra, 2, new string('a', 64))],
        })));
        Assert.Equal("entry_mismatch: " + Build, ErrorOf(TestBundles.Default().WithManifest(m => m with
        {
            Entries = m.Entries.Where(e => e.Name != Build).ToList(),
        })));
    }

    [Fact]
    public void Png_with_bad_magic_or_2561_edge_is_rejected()
    {
        byte[] badMagic = TestBundles.Png(4, 4);
        badMagic[1] = (byte)'Q';
        Assert.Equal("image_invalid: screenshot.png", ErrorOf(TestBundles.Default().WithEntry(BundleEntryNames.ScreenshotPng, badMagic)));
        Assert.Equal("image_invalid: screenshot.png",
            ErrorOf(TestBundles.Default().WithEntry(BundleEntryNames.ScreenshotPng, TestBundles.Png(2561, 4))));
        Assert.Null(ErrorOf(TestBundles.Default().WithEntry(BundleEntryNames.ScreenshotPng, TestBundles.Png(2560, 1440))));
    }

    [Fact]
    public void Jpeg_without_a_frame_header_is_rejected()
        => Assert.Equal("image_invalid: clip/000.jpg", ErrorOf(TestBundles.Default().WithEntry("clip/000.jpg", TestBundles.JpegWithoutFrame())));

    [Fact]
    public void Section_that_is_not_a_json_object_is_rejected()
        => Assert.Equal("section_invalid: " + Build, ErrorOf(TestBundles.Default().WithEntry(Build, Encoding.UTF8.GetBytes("[1]"))));

    [Fact]
    public void Clip_frames_without_index_or_with_decreasing_offsets_are_rejected()
    {
        Assert.Equal("clip_index_invalid", ErrorOf(TestBundles.Default().WithoutEntry(BundleEntryNames.ClipIndex)));
        byte[] decreasing = TestBundles.ClipIndexJson(
            new ClipIndexFrame(100, 16.5, 4, 4, "clip/000.jpg"),
            new ClipIndexFrame(50, 16.5, 4, 4, "clip/001.jpg"));
        Assert.Equal("clip_index_invalid", ErrorOf(TestBundles.Default().WithEntry(BundleEntryNames.ClipIndex, decreasing)));
    }

    [Fact]
    public void Index_dimensions_must_match_the_jpeg()
    {
        byte[] index = TestBundles.ClipIndexJson(
            new ClipIndexFrame(0, 16.5, 8, 8, "clip/000.jpg"),
            new ClipIndexFrame(83.25, 41, 8, 8, "clip/001.jpg"));
        Assert.Equal("clip_index_invalid", ErrorOf(TestBundles.Default().WithEntry(BundleEntryNames.ClipIndex, index)));
    }

    [Fact]
    public void Clip_mp4_with_index_entries_naming_it_is_accepted()
    {
        byte[] mp4 = [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];
        TestBundles bundle = TestBundles.Default()
            .WithoutEntry("clip/000.jpg")
            .WithoutEntry("clip/001.jpg")
            .WithEntry(BundleEntryNames.ClipMp4, mp4)
            .WithEntry(BundleEntryNames.ClipIndex, TestBundles.ClipIndexJson(
                new ClipIndexFrame(0, 16.5, 960, 540, BundleEntryNames.ClipMp4),
                new ClipIndexFrame(83.25, 41, 960, 540, BundleEntryNames.ClipMp4)));
        BundleValidationResult result = Validate(bundle);
        Assert.Null(result.Error);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Null_manifest_entry_is_rejected()
        => Assert.Equal("manifest_invalid",
            ErrorOf(TestBundles.Default().WithManifestJson(j => j.Replace("\"entries\":[", "\"entries\":[null,", StringComparison.Ordinal))));

    [Fact]
    public void Null_clip_frame_is_rejected()
        => Assert.Equal("clip_index_invalid",
            ErrorOf(TestBundles.Default().WithEntry(BundleEntryNames.ClipIndex, Encoding.UTF8.GetBytes("{\"frames\":[null]}"))));

    [Fact]
    public void Malformed_captured_at_is_manifest_invalid()
        => Assert.Equal("manifest_invalid",
            ErrorOf(TestBundles.Default().WithManifestJson(j => j.Replace("2026-10-17T03:04:05.678+00:00", "yesterday", StringComparison.Ordinal))));

    [Fact]
    public void Truncated_zip_bytes_never_throw()
    {
        byte[] bytes = TestBundles.Valid().ToArray();
        for (int length = 0; length < bytes.Length; length += 97)
        {
            BundleValidationResult result = BundleValidator.Validate(new MemoryStream(bytes, 0, length));
            Assert.NotNull(result);
            Assert.False(result.IsValid);
            Assert.NotNull(result.Error);
        }
    }

    // Yields zeros and refuses Seek and Length, like a request body.
    sealed class ZeroStream(long length) : Stream
    {
        long remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, n);
            remaining -= n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
