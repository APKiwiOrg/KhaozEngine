using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class BugReportBundleWriterTests
{
    const string ReportId = TestBundles.ReportId;

    static readonly DateTimeOffset CapturedAt = new(2026, 10, 17, 3, 4, 5, 678, TimeSpan.Zero);

    static CapturedSection Section(string key, string json) => new(key, Encoding.UTF8.GetBytes(json));

    static readonly CapturedSection[] TwoSections =
    [
        Section("build", "{\"version\":\"1.2.3\"}"),
        Section("log", "{\"lines\":[\"started\",\"loaded\"]}"),
    ];

    static ReportImage Png(int width = 8, int height = 6) => new("image/png", width, height, TestBundles.Png(width, height));

    static ReportImage Jpeg(int width = 8, int height = 6) => new("image/jpeg", width, height, TestBundles.Jpeg(width, height));

    static ReportClipFrame Frame(double offsetMilliseconds, double renderMilliseconds, int width = 4, int height = 4, int padding = 0)
    {
        byte[] jpeg = [.. TestBundles.Jpeg(width, height), .. new byte[padding]];
        return new ReportClipFrame(TimeSpan.FromMilliseconds(offsetMilliseconds), renderMilliseconds, width, height, jpeg);
    }

    static ReportClip Clip(int count, int padding = 0) =>
        new([.. Enumerable.Range(0, count).Select(i => Frame(i * 16.5, 16.5, padding: padding))]);

    static ReportClip ThreeFrames() => new([Frame(0, 16.5), Frame(83.25, 41), Frame(166.5, 12.25, 6, 3)]);

    static BugReportDraft Draft(IReadOnlyList<CapturedSection>? sections = null, ReportImage? screenshot = null,
        ReportClip? clip = null, string description = "Fell through\nthe floor") =>
        new(ReportId, "grimhollow", BugReportKind.Bug, CapturedAt, sections ?? TwoSections, screenshot, clip)
        {
            Description = description,
            Category = "Gameplay",
        };

    static BundleValidationResult Validate(byte[] bundle)
    {
        using var stream = new MemoryStream(bundle);
        return BundleValidator.Validate(stream);
    }

    static void AssertValid(byte[] bundle)
    {
        BundleValidationResult result = Validate(bundle);
        Assert.True(result.IsValid, result.Error);
    }

    static List<ZipArchiveEntry> Entries(byte[] bundle) =>
        [.. new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read).Entries];

    static string[] Names(byte[] bundle) => [.. Entries(bundle).Select(e => e.FullName)];

    static byte[] Read(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    static BugReportManifest Manifest(byte[] bundle) =>
        BugReportJson.DeserializeManifest(Read(Entries(bundle).Single(e => e.FullName == BundleEntryNames.Manifest)));

    /// <summary>Builds sections through the real capture and finish path, as a client would.</summary>
    static IReadOnlyList<CapturedSection> FinishedSections()
    {
        var set = new BugReportSectionSet();
        set.Add(new TestSection("build", writer => writer.WriteString("version", "1.2.3")));
        set.Add(new TestSection("platform", writer => writer.WriteString("os", "macOS")));
        IReadOnlyList<RawSection> raw = set.CaptureAll(new BugReportContext(ReportId, CapturedAt));
        return BugReportSectionSet.Finish(raw, new ReportRedactor("/Users/ann"), TimeSpan.MaxValue);
    }

    [Fact]
    public void Default_categories_are_the_six_ids_in_order()
    {
        Assert.Equal(new[] { "Gameplay", "Visual", "Audio", "Performance", "Interface", "Other" }, BugReportDraft.DefaultCategories);
    }

    [Fact]
    public void New_draft_has_the_contract_defaults()
    {
        var draft = new BugReportDraft(ReportId, "grimhollow", BugReportKind.Crash, CapturedAt, TwoSections, null, null);

        Assert.Equal("", draft.Description);
        Assert.Equal("Other", draft.Category);
        Assert.True(draft.IncludeScreenshot);
        Assert.True(draft.IncludeClip);
    }

    [Theory]
    [InlineData("png-and-clip")]
    [InlineData("jpeg-without-clip")]
    [InlineData("sections-only")]
    public void Written_bundle_passes_the_validator(string shape)
    {
        BugReportDraft draft = shape switch
        {
            "png-and-clip" => Draft(screenshot: Png(), clip: ThreeFrames()),
            "jpeg-without-clip" => Draft(screenshot: Jpeg()),
            _ => Draft(sections: FinishedSections()),
        };

        BundleValidationResult result = Validate(BugReportBundleWriter.Write(draft));

        Assert.True(result.IsValid, result.Error);
        BugReportManifest manifest = result.Manifest!;
        Assert.Equal(BugReportFormat.Version, manifest.FormatVersion);
        Assert.Equal(ReportId, manifest.ReportId);
        Assert.Equal("grimhollow", manifest.GameId);
        Assert.Equal(BugReportKind.Bug, manifest.Kind);
        Assert.Equal("Gameplay", manifest.Category);
        Assert.Equal("Fell through\nthe floor", manifest.Description);
        Assert.Equal(CapturedAt, manifest.CapturedAt);
    }

    [Fact]
    public void Manifest_is_first_and_lists_entries_in_zip_order()
    {
        byte[] bundle = BugReportBundleWriter.Write(Draft(screenshot: Png(), clip: ThreeFrames()));

        List<ZipArchiveEntry> entries = Entries(bundle);
        Assert.Equal(BundleEntryNames.Manifest, entries[0].FullName);
        string[] expected =
        [
            "sections/build.json", "sections/log.json", BundleEntryNames.ScreenshotPng, BundleEntryNames.ClipIndex,
            "clip/000.jpg", "clip/001.jpg", "clip/002.jpg",
        ];
        Assert.Equal(expected, entries.Skip(1).Select(e => e.FullName));

        BugReportManifest manifest = Manifest(bundle);
        Assert.Equal(expected, manifest.Entries.Select(e => e.Name));
        foreach ((BundleEntry listed, ZipArchiveEntry entry) in manifest.Entries.Zip(entries.Skip(1)))
        {
            byte[] data = Read(entry);
            Assert.Equal(data.Length, listed.Size);
            Assert.Equal(TestBundles.Sha256(data), listed.Sha256);
        }
        Assert.Equal(TwoSections[0].Json.ToArray(), Read(entries[1]));
    }

    [Fact]
    public void Screenshot_and_clip_toggles_omit_their_entries()
    {
        BugReportDraft noScreenshot = Draft(screenshot: Png(), clip: ThreeFrames());
        noScreenshot.IncludeScreenshot = false;
        byte[] withoutScreenshot = BugReportBundleWriter.Write(noScreenshot);
        Assert.DoesNotContain(Names(withoutScreenshot), name => name.StartsWith("screenshot.", StringComparison.Ordinal));
        Assert.Contains(BundleEntryNames.ClipIndex, Names(withoutScreenshot));
        AssertValid(withoutScreenshot);

        BugReportDraft noClip = Draft(screenshot: Png(), clip: ThreeFrames());
        noClip.IncludeClip = false;
        byte[] withoutClip = BugReportBundleWriter.Write(noClip);
        Assert.DoesNotContain(Names(withoutClip), name => name.StartsWith("clip", StringComparison.Ordinal));
        Assert.Contains(BundleEntryNames.ScreenshotPng, Names(withoutClip));
        AssertValid(withoutClip);
    }

    [Fact]
    public void Empty_clip_writes_no_clip_entries()
    {
        byte[] bundle = BugReportBundleWriter.Write(Draft(clip: new ReportClip([])));

        Assert.DoesNotContain(Names(bundle), name => name.StartsWith("clip", StringComparison.Ordinal));
        AssertValid(bundle);
    }

    [Fact]
    public void Jpeg_screenshot_uses_the_jpg_name()
    {
        string[] names = Names(BugReportBundleWriter.Write(Draft(screenshot: Jpeg())));

        Assert.Contains(BundleEntryNames.ScreenshotJpeg, names);
        Assert.DoesNotContain(BundleEntryNames.ScreenshotPng, names);
    }

    [Fact]
    public void Clip_offsets_and_render_times_reach_the_index()
    {
        byte[] bundle = BugReportBundleWriter.Write(Draft(clip: ThreeFrames()));

        ClipIndex index = BugReportJson.DeserializeClipIndex(Read(Entries(bundle).Single(e => e.FullName == BundleEntryNames.ClipIndex)));
        Assert.Equal(
            new[]
            {
                new ClipIndexFrame(0, 16.5, 4, 4, "clip/000.jpg"),
                new ClipIndexFrame(83.25, 41, 4, 4, "clip/001.jpg"),
                new ClipIndexFrame(166.5, 12.25, 6, 3, "clip/002.jpg"),
            },
            index.Frames);
    }

    [Fact]
    public void Entries_are_stored_not_compressed()
    {
        List<ZipArchiveEntry> entries = Entries(BugReportBundleWriter.Write(Draft(screenshot: Png(), clip: ThreeFrames())));

        Assert.All(entries, entry => Assert.Equal(entry.Length, entry.CompressedLength));
    }

    [Fact]
    public void Entries_carry_the_capture_time_and_the_same_draft_writes_the_same_bytes()
    {
        BugReportDraft draft = Draft(screenshot: Png(), clip: ThreeFrames());

        byte[] first = BugReportBundleWriter.Write(draft);

        // DOS times keep even seconds, so 05.678 reads back as 04.
        Assert.All(Entries(first), entry => Assert.Equal(new DateTime(2026, 10, 17, 3, 4, 4), entry.LastWriteTime.DateTime));
        Assert.Equal(first, BugReportBundleWriter.Write(draft));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    [InlineData(null)]
    public void Blank_description_throws(string? description)
    {
        Assert.Throws<ArgumentException>(() => BugReportBundleWriter.Write(Draft(description: description!)));
    }

    [Fact]
    public void Overlong_description_throws()
    {
        Assert.Throws<ArgumentException>(
            () => BugReportBundleWriter.Write(Draft(description: new string('x', BugReportLimits.MaxDescriptionChars + 1))));
    }

    [Fact]
    public void Description_limit_is_exactly_4000()
    {
        string description = new('x', BugReportLimits.MaxDescriptionChars);

        byte[] bundle = BugReportBundleWriter.Write(Draft(description: description));

        AssertValid(bundle);
        Assert.Equal(description, Manifest(bundle).Description);
    }

    [Fact]
    public void Description_is_trimmed_at_the_end_only()
    {
        Assert.Equal("  a", Manifest(BugReportBundleWriter.Write(Draft(description: "  a\n\n"))).Description);
    }

    [Fact]
    public void Description_keeps_inner_crlf_and_drops_a_trailing_one()
    {
        byte[] bundle = BugReportBundleWriter.Write(Draft(description: "one\r\ntwo\r\n"));

        AssertValid(bundle);
        Assert.Equal("one\r\ntwo", Manifest(bundle).Description);
    }

    [Fact]
    public void Description_with_a_lone_surrogate_throws()
    {
        Assert.Throws<ArgumentException>(() => BugReportBundleWriter.Write(Draft(description: "bad \uD800 text")));
    }

    [Fact]
    public void More_than_120_clip_frames_throws()
    {
        AssertValid(BugReportBundleWriter.Write(Draft(clip: Clip(BundleEntryNames.MaxClipFrames))));

        Assert.Throws<ArgumentException>(() => BugReportBundleWriter.Write(Draft(clip: Clip(BundleEntryNames.MaxClipFrames + 1))));
    }

    [Fact]
    public void Unknown_media_type_throws()
    {
        var gif = new ReportImage("image/gif", 8, 6, TestBundles.Png(8, 6));

        Assert.Throws<ArgumentException>(() => BugReportBundleWriter.Write(Draft(screenshot: gif)));
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(8, 0)]
    [InlineData(2561, 6)]
    public void Screenshot_size_outside_the_edge_limit_throws(int width, int height)
    {
        var image = new ReportImage("image/png", width, height, TestBundles.Png(width, height));

        Assert.Throws<ArgumentException>(() => BugReportBundleWriter.Write(Draft(screenshot: image)));
    }

    [Fact]
    public void Edges_of_exactly_2560_are_written()
    {
        int edge = BugReportLimits.MaxScreenshotLongEdge;
        var clip = new ReportClip([Frame(0, 16.5, edge, 1440), Frame(16.5, 16.5, 1440, edge)]);

        byte[] bundle = BugReportBundleWriter.Write(Draft(screenshot: Png(edge, 1440), clip: clip));

        AssertValid(bundle);
        AssertValid(BugReportBundleWriter.Write(Draft(screenshot: Jpeg(1440, edge))));
    }

    [Fact]
    public void Png_screenshot_with_empty_bytes_throws()
    {
        var empty = new ReportImage("image/png", 8, 6, ReadOnlyMemory<byte>.Empty);

        AssertRejected(Draft(screenshot: empty), BundleEntryNames.ScreenshotPng);
    }

    [Fact]
    public void Screenshot_header_that_disagrees_with_its_record_throws()
    {
        var image = new ReportImage("image/png", 2560, 1440, TestBundles.Png(2561, 1440));

        AssertRejected(Draft(screenshot: image), BundleEntryNames.ScreenshotPng);
    }

    [Fact]
    public void Jpeg_screenshot_that_is_not_a_jpeg_throws()
    {
        var image = new ReportImage("image/jpeg", 8, 6, TestBundles.Png(8, 6));

        AssertRejected(Draft(screenshot: image), BundleEntryNames.ScreenshotJpeg);
    }

    [Fact]
    public void Clip_frame_header_that_disagrees_with_its_record_throws()
    {
        var mismatched = new ReportClipFrame(TimeSpan.FromMilliseconds(16.5), 16.5, 4, 4, TestBundles.Jpeg(6, 3));
        var clip = new ReportClip([Frame(0, 16.5), mismatched]);

        AssertRejected(Draft(clip: clip), "clip/001.jpg");
    }

    [Theory]
    [InlineData("negative-offset")]
    [InlineData("decreasing-offset")]
    [InlineData("nan-render")]
    [InlineData("negative-render")]
    [InlineData("zero-width")]
    [InlineData("tall-frame")]
    [InlineData("null-frame")]
    [InlineData("render-over-limit")]
    [InlineData("offset-over-limit")]
    public void Invalid_clip_frame_throws(string fault)
    {
        ReportClipFrame?[] frames = [Frame(0, 16.5), Frame(33, 16.5), Frame(49.5, 16.5)];
        frames[2] = fault switch
        {
            "negative-offset" => Frame(-1, 16.5),
            "decreasing-offset" => Frame(16.5, 16.5),
            "nan-render" => Frame(49.5, double.NaN),
            "negative-render" => Frame(49.5, -1),
            "render-over-limit" => Frame(49.5, BugReportLimits.MaxRenderMilliseconds + 1),
            "offset-over-limit" => Frame(BugReportLimits.MaxClipOffsetMilliseconds + 1, 16.5),
            "zero-width" => frames[2]! with { Width = 0 },
            "tall-frame" => frames[2]! with { Height = 2561 },
            _ => null,
        };
        var clip = new ReportClip(frames!);

        Assert.Throws<ArgumentException>(() => BugReportBundleWriter.Write(Draft(clip: clip)));
    }

    [Fact]
    public void Clip_timing_at_the_limits_is_written_and_valid()
    {
        var clip = new ReportClip([Frame(0, BugReportLimits.MaxRenderMilliseconds), Frame(BugReportLimits.MaxClipOffsetMilliseconds, 16.5)]);

        AssertValid(BugReportBundleWriter.Write(Draft(clip: clip)));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("")]
    [InlineData("Bad Key")]
    public void Invalid_section_keys_throw(string key)
    {
        ArgumentException invalid = Assert.Throws<ArgumentException>(
            () => BugReportBundleWriter.Write(Draft(sections: [Section(key, "{}")])));
        Assert.Contains("\"" + key + "\"", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_section_keys_throw()
    {
        ArgumentException duplicate = Assert.Throws<ArgumentException>(
            () => BugReportBundleWriter.Write(Draft(sections: [Section("build", "{}"), Section("build", "{\"a\":1}")])));
        Assert.Contains("\"build\"", duplicate.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("{\"a\":")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("")]
    [InlineData("{\"a\":\"\\uD800\"}")]
    [InlineData("{\"\\uD800\":1}")]
    [InlineData("{} {}")]
    public void Section_that_is_not_one_json_object_throws(string json)
    {
        Assert.Throws<ArgumentException>(() => BugReportBundleWriter.Write(Draft(sections: [Section("build", json)])));
    }

    [Fact]
    public void Sections_over_one_mebibyte_throw()
    {
        string half = "{\"a\":\"" + new string('x', BugReportLimits.MaxSectionBytesTotal / 2) + "\"}";

        Assert.Throws<ArgumentException>(
            () => BugReportBundleWriter.Write(Draft(sections: [Section("one", half), Section("two", half)])));
    }

    [Theory]
    [InlineData("empty-category")]
    [InlineData("long-category")]
    [InlineData("null-category")]
    [InlineData("report-id")]
    [InlineData("game-id")]
    [InlineData("kind")]
    [InlineData("captured-at")]
    [InlineData("captured-after-2107")]
    [InlineData("null-sections")]
    [InlineData("null-section")]
    [InlineData("null-frames")]
    [InlineData("null-media-type")]
    public void Null_or_invalid_draft_fields_throw_argument_exceptions(string field)
    {
        BugReportDraft draft = field switch
        {
            "report-id" => new BugReportDraft("not-an-id", "grimhollow", BugReportKind.Bug, CapturedAt, TwoSections, null, null),
            "game-id" => new BugReportDraft(ReportId, "Grimhollow", BugReportKind.Bug, CapturedAt, TwoSections, null, null),
            "kind" => new BugReportDraft(ReportId, "grimhollow", (BugReportKind)7, CapturedAt, TwoSections, null, null),
            "captured-at" => new BugReportDraft(ReportId, "grimhollow", BugReportKind.Bug,
                new DateTimeOffset(1979, 12, 31, 0, 0, 0, TimeSpan.Zero), TwoSections, null, null),
            "captured-after-2107" => new BugReportDraft(ReportId, "grimhollow", BugReportKind.Bug,
                new DateTimeOffset(2107, 12, 31, 23, 0, 0, TimeSpan.FromHours(-5)), TwoSections, null, null),
            "null-sections" => new BugReportDraft(ReportId, "grimhollow", BugReportKind.Bug, CapturedAt, null!, null, null),
            "null-section" => new BugReportDraft(ReportId, "grimhollow", BugReportKind.Bug, CapturedAt, [null!], null, null),
            "null-frames" => Draft(clip: new ReportClip(null!)),
            "null-media-type" => Draft(screenshot: new ReportImage(null!, 8, 6, TestBundles.Png(8, 6))),
            _ => Draft(),
        };
        draft.Description = "A report";
        draft.Category = field switch
        {
            "empty-category" => "",
            "long-category" => new string('c', 65),
            "null-category" => null!,
            _ => "Other",
        };

        Exception thrown = Record.Exception(() => BugReportBundleWriter.Write(draft));

        Assert.IsAssignableFrom<ArgumentException>(thrown);
    }

    [Fact]
    public void Category_of_64_characters_is_written()
    {
        BugReportDraft draft = Draft();
        draft.Category = new string('c', 64);

        AssertValid(BugReportBundleWriter.Write(draft));
    }

    [Fact]
    public void More_than_128_entries_throws()
    {
        CapturedSection[] sections = [.. Enumerable.Range(0, 7).Select(i => Section("s" + i, "{}"))];

        Assert.Throws<InvalidOperationException>(
            () => BugReportBundleWriter.Write(Draft(sections: sections, clip: Clip(BundleEntryNames.MaxClipFrames))));
        AssertValid(BugReportBundleWriter.Write(Draft(sections: sections[..6], clip: Clip(BundleEntryNames.MaxClipFrames))));
    }

    [Fact]
    public void Bundle_over_16_MiB_throws()
    {
        int padding = (int)(BugReportLimits.MaxBundleBytes / BundleEntryNames.MaxClipFrames) + 1024;

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => BugReportBundleWriter.Write(Draft(clip: Clip(BundleEntryNames.MaxClipFrames, padding))));
        Assert.StartsWith("The report payloads need ", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_over_16_MiB_throws_when_the_payloads_alone_fit()
    {
        // The payloads total 10 bytes under the limit, so only the zip headers push the archive over it.
        CapturedSection[] sections = [Section("build", "{}")];
        int pngBytes = (int)BugReportLimits.MaxBundleBytes - 2 - 10;
        byte[] png = [.. TestBundles.Png(8, 6), .. new byte[pngBytes - TestBundles.Png(8, 6).Length]];

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => BugReportBundleWriter.Write(Draft(sections: sections, screenshot: new ReportImage("image/png", 8, 6, png))));
        Assert.StartsWith("The written archive is ", thrown.Message, StringComparison.Ordinal);
    }

    static void AssertRejected(BugReportDraft draft, string entry)
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(() => BugReportBundleWriter.Write(draft));
        Assert.Contains(entry, thrown.Message, StringComparison.Ordinal);
    }

    sealed class TestSection(string key, Action<Utf8JsonWriter> body) : IBugReportSection
    {
        public string Key => key;

        public int MaxBytes => 4096;

        public void Write(Utf8JsonWriter writer, BugReportContext context)
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
    }
}
