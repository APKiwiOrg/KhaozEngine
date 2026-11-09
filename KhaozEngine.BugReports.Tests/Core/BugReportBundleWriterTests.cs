using System;
using System.Collections.Generic;
using System.Globalization;
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

public sealed partial class BugReportBundleWriterTests
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
        ReportClip? clip = null, string description = "Fell through\nthe floor", string reportId = ReportId,
        string gameId = "grimhollow", BugReportKind kind = BugReportKind.Bug, DateTimeOffset? capturedAt = null,
        string category = "Gameplay") =>
        new(reportId, gameId, kind, capturedAt ?? CapturedAt, sections ?? TwoSections, screenshot, clip)
        {
            Description = description,
            Category = category,
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

    /// <summary>The draft is refused by exactly this rule, on the exact type, keeping the parameter name.</summary>
    static BugReportBundleArgumentException Refused(BugReportDraft draft, BugReportBundleRefusal reason)
    {
        BugReportBundleArgumentException refusal =
            Assert.Throws<BugReportBundleArgumentException>(() => BugReportBundleWriter.Write(draft));
        Assert.IsAssignableFrom<ArgumentException>(refusal);
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal("draft", refusal.ParamName);
        return refusal;
    }

    /// <summary>The draft is refused by exactly this bundle limit, on the exact type.</summary>
    static BugReportBundleLimitException Limited(BugReportDraft draft, BugReportBundleRefusal reason)
    {
        BugReportBundleLimitException refusal =
            Assert.Throws<BugReportBundleLimitException>(() => BugReportBundleWriter.Write(draft));
        Assert.IsAssignableFrom<InvalidOperationException>(refusal);
        Assert.Equal(reason, refusal.Reason);
        return refusal;
    }

    /// <summary>Two valid frames, then <paramref name="last"/> as frame 2.</summary>
    static ReportClip ClipEndingWith(ReportClipFrame? last) => new([Frame(0, 16.5), Frame(33, 16.5), last!]);

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

    // A second index read must never replace the frame whose header and dimensions were validated.
    [Fact]
    public void Clip_payload_is_the_validated_frame_when_the_list_changes_on_a_second_read()
    {
        ReportClipFrame validated = Frame(0, 16.5);
        ReportClipFrame replacement = Frame(0, 16.5, 6, 3);
        var frames = new ChangingFrameList(validated, replacement);

        byte[] bundle = BugReportBundleWriter.Write(Draft(clip: new ReportClip(frames)));

        AssertValid(bundle);
        Assert.Equal(validated.Jpeg.ToArray(), Read(Entries(bundle).Single(e => e.FullName == "clip/000.jpg")));
        ClipIndex index = BugReportJson.DeserializeClipIndex(Read(Entries(bundle).Single(e => e.FullName == BundleEntryNames.ClipIndex)));
        Assert.Equal(new[] { new ClipIndexFrame(0, 16.5, 4, 4, "clip/000.jpg") }, index.Frames);
    }

    sealed class ChangingFrameList(ReportClipFrame first, ReportClipFrame replacement) : IReadOnlyList<ReportClipFrame>
    {
        bool read;
        public int Count => 1;
        public ReportClipFrame this[int index]
        {
            get
            {
                if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
                ReportClipFrame frame = read ? replacement : first;
                read = true;
                return frame;
            }
        }
        public IEnumerator<ReportClipFrame> GetEnumerator() { yield return this[0]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    [InlineData(null)]
    public void Blank_description_throws(string? description)
    {
        BugReportBundleArgumentException refusal = Refused(Draft(description: description!), BugReportBundleRefusal.DescriptionBlank);
        Assert.Equal("Description", refusal.FieldName);

        AssertValid(BugReportBundleWriter.Write(Draft(description: "a")));
    }

    // The limit applies after trailing whitespace is dropped, so the same whitespace leaves the control at the limit.
    [Theory]
    [InlineData("")]
    [InlineData(" \n")]
    public void Overlong_description_throws(string trailing)
    {
        BugReportBundleArgumentException refusal =
            Refused(Draft(description: new string('x', 4001) + trailing), BugReportBundleRefusal.DescriptionTooLong);
        Assert.Equal(4001, refusal.ActualCharacters);
        Assert.Equal(4000, refusal.MaximumCharacters);

        AssertValid(BugReportBundleWriter.Write(Draft(description: new string('x', 4000) + trailing)));
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

    [Theory]
    [InlineData("Description")]
    [InlineData("Category")]
    public void Text_with_a_lone_surrogate_throws(string field)
    {
        BugReportDraft Carrying(string text) =>
            field == "Description" ? Draft(description: text) : Draft(category: text);

        BugReportBundleArgumentException refusal = Refused(Carrying("bad \uD800 text"), BugReportBundleRefusal.TextMalformed);
        Assert.Equal(field, refusal.FieldName);

        AssertValid(BugReportBundleWriter.Write(Carrying("good \uD83D\uDE00 text")));
    }

    [Fact]
    public void More_than_120_clip_frames_throws()
    {
        BugReportBundleArgumentException refusal =
            Refused(Draft(clip: Clip(BundleEntryNames.MaxClipFrames + 1)), BugReportBundleRefusal.ClipFrameCountExceeded);
        Assert.Equal(121, refusal.ActualCount);
        Assert.Equal(120, refusal.MaximumCount);

        AssertValid(BugReportBundleWriter.Write(Draft(clip: Clip(BundleEntryNames.MaxClipFrames))));
    }

    [Fact]
    public void Unknown_media_type_throws()
    {
        BugReportBundleArgumentException refusal = Refused(
            Draft(screenshot: new ReportImage("image/gif", 8, 6, TestBundles.Png(8, 6))), BugReportBundleRefusal.ScreenshotMediaUnsupported);
        Assert.Equal("image/gif", refusal.MediaType);

        AssertValid(BugReportBundleWriter.Write(Draft(screenshot: new ReportImage("image/png", 8, 6, TestBundles.Png(8, 6)))));
    }

    // Each refused image carries a header that matches its record, so only the edge rule can refuse it.
    [Theory]
    [InlineData("screenshot", 0, 6, 1, 6)]
    [InlineData("screenshot", 8, 0, 8, 1)]
    [InlineData("screenshot", 2561, 6, 2560, 6)]
    [InlineData("clip", 0, 4, 1, 4)]
    [InlineData("clip", 4, 2561, 4, 2560)]
    public void Image_edge_outside_the_limit_throws(string image, int width, int height, int limitWidth, int limitHeight)
    {
        BugReportDraft Sized(int w, int h) =>
            image == "screenshot" ? Draft(screenshot: Png(w, h)) : Draft(clip: ClipEndingWith(Frame(49.5, 16.5, w, h)));

        BugReportBundleArgumentException refusal = Refused(Sized(width, height), BugReportBundleRefusal.ImageDimensionsOutsideRange);
        Assert.Equal(image == "screenshot" ? "screenshot.png" : "clip/002.jpg", refusal.EntryName);
        Assert.Equal(width, refusal.Width);
        Assert.Equal(height, refusal.Height);
        Assert.Equal(1, refusal.MinimumPixels);
        Assert.Equal(2560, refusal.MaximumPixels);

        AssertValid(BugReportBundleWriter.Write(Sized(limitWidth, limitHeight)));
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
        BugReportBundleArgumentException refusal = Refused(
            Draft(screenshot: new ReportImage("image/png", 8, 6, ReadOnlyMemory<byte>.Empty)), BugReportBundleRefusal.ImageHeaderUnreadable);
        Assert.Equal("screenshot.png", refusal.EntryName);
        Assert.Equal("image/png", refusal.MediaType);

        AssertValid(BugReportBundleWriter.Write(Draft(screenshot: Png(8, 6))));
    }

    [Fact]
    public void Screenshot_header_that_disagrees_with_its_record_throws()
    {
        BugReportBundleArgumentException refusal = Refused(
            Draft(screenshot: new ReportImage("image/png", 2560, 1440, TestBundles.Png(2561, 1440))),
            BugReportBundleRefusal.ImageHeaderDimensionsMismatch);
        Assert.Equal("screenshot.png", refusal.EntryName);
        Assert.Equal(2560, refusal.Width);
        Assert.Equal(1440, refusal.Height);
        Assert.Equal(2561, refusal.HeaderWidth);
        Assert.Equal(1440, refusal.HeaderHeight);

        AssertValid(BugReportBundleWriter.Write(Draft(screenshot: new ReportImage("image/png", 2560, 1440, TestBundles.Png(2560, 1440)))));
    }

    [Fact]
    public void Jpeg_screenshot_that_is_not_a_jpeg_throws()
    {
        BugReportBundleArgumentException refusal = Refused(
            Draft(screenshot: new ReportImage("image/jpeg", 8, 6, TestBundles.Png(8, 6))), BugReportBundleRefusal.ImageHeaderUnreadable);
        Assert.Equal("screenshot.jpg", refusal.EntryName);
        Assert.Equal("image/jpeg", refusal.MediaType);

        AssertValid(BugReportBundleWriter.Write(Draft(screenshot: new ReportImage("image/jpeg", 8, 6, TestBundles.Jpeg(8, 6)))));
    }

    [Fact]
    public void Clip_frame_header_that_disagrees_with_its_record_throws()
    {
        var mismatched = new ReportClipFrame(TimeSpan.FromMilliseconds(16.5), 16.5, 4, 4, TestBundles.Jpeg(6, 3));

        BugReportBundleArgumentException refusal =
            Refused(Draft(clip: new ReportClip([Frame(0, 16.5), mismatched])), BugReportBundleRefusal.ImageHeaderDimensionsMismatch);
        Assert.Equal("clip/001.jpg", refusal.EntryName);
        Assert.Equal(4, refusal.Width);
        Assert.Equal(4, refusal.Height);
        Assert.Equal(6, refusal.HeaderWidth);
        Assert.Equal(3, refusal.HeaderHeight);

        AssertValid(BugReportBundleWriter.Write(Draft(clip: new ReportClip([Frame(0, 16.5), mismatched with { Width = 6, Height = 3 }]))));
    }

    [Fact]
    public void Null_clip_frame_throws()
    {
        BugReportBundleArgumentException refusal = Refused(Draft(clip: ClipEndingWith(null)), BugReportBundleRefusal.ClipFrameMissing);
        Assert.Equal(2, refusal.Index);

        AssertValid(BugReportBundleWriter.Write(Draft(clip: ClipEndingWith(Frame(49.5, 16.5)))));
    }

    // The first frame may not start before the clip, nor a later one before the frame ahead of it. Equal offsets are written.
    [Theory]
    [InlineData(0, -1, 0)]
    [InlineData(2, 16.5, 33)]
    public void Clip_frame_that_starts_too_early_throws(int index, double offset, double earliest)
    {
        ReportClipFrame[] ahead = [Frame(0, 16.5), Frame(33, 16.5)];
        ReportClip Starting(double at) => new([.. ahead[..index], Frame(at, 16.5)]);

        BugReportBundleArgumentException refusal = Refused(Draft(clip: Starting(offset)), BugReportBundleRefusal.ClipOffsetDecreased);
        Assert.Equal(index, refusal.Index);
        Assert.Equal(offset, refusal.ActualMilliseconds);
        Assert.Equal(earliest, refusal.MinimumMilliseconds);

        AssertValid(BugReportBundleWriter.Write(Draft(clip: Starting(earliest))));
    }

    [Fact]
    public void Clip_frame_that_starts_past_the_limit_throws()
    {
        BugReportBundleArgumentException refusal =
            Refused(Draft(clip: ClipEndingWith(Frame(600001, 16.5))), BugReportBundleRefusal.ClipOffsetExceeded);
        Assert.Equal(2, refusal.Index);
        Assert.Equal(600001.0, refusal.ActualMilliseconds);
        Assert.Equal(600000.0, refusal.MaximumMilliseconds);

        AssertValid(BugReportBundleWriter.Write(Draft(clip: ClipEndingWith(Frame(600000, 16.5)))));
    }

    // Infinity is also past the limit, so its row proves the nonfinite branch is judged first.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Render_time_that_is_not_finite_throws(double render)
    {
        BugReportBundleArgumentException refusal =
            Refused(Draft(clip: ClipEndingWith(Frame(49.5, render))), BugReportBundleRefusal.RenderTimeNonfinite);
        Assert.Equal(2, refusal.Index);
        Assert.Equal(render, refusal.ActualMilliseconds);

        AssertValid(BugReportBundleWriter.Write(Draft(clip: ClipEndingWith(Frame(49.5, 16.5)))));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(60001, 60000)]
    public void Render_time_outside_its_range_throws(double render, double limit)
    {
        BugReportBundleArgumentException refusal =
            Refused(Draft(clip: ClipEndingWith(Frame(49.5, render))), BugReportBundleRefusal.RenderTimeOutsideRange);
        Assert.Equal(2, refusal.Index);
        Assert.Equal(render, refusal.ActualMilliseconds);
        Assert.Equal(0.0, refusal.MinimumMilliseconds);
        Assert.Equal(60000.0, refusal.MaximumMilliseconds);

        AssertValid(BugReportBundleWriter.Write(Draft(clip: ClipEndingWith(Frame(49.5, limit)))));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("")]
    [InlineData("Bad Key")]
    [InlineData(null)]
    [InlineData("kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk")]
    public void Invalid_section_keys_throw(string? key)
    {
        BugReportBundleArgumentException refusal =
            Refused(Draft(sections: [Section("build", "{}"), Section(key!, "{}")]), BugReportBundleRefusal.SectionKeyInvalid);
        Assert.Equal(1, refusal.Index);
        Assert.Equal(key, refusal.SectionKey);

        AssertValid(BugReportBundleWriter.Write(Draft(sections: [Section("build", "{}"), Section(new string('k', 48), "{}")])));
    }

    [Fact]
    public void Duplicate_section_keys_throw()
    {
        BugReportBundleArgumentException refusal = Refused(
            Draft(sections: [Section("build", "{}"), Section("log", "{}"), Section("build", "{\"a\":1}")]),
            BugReportBundleRefusal.SectionKeyDuplicate);
        Assert.Equal(2, refusal.Index);
        Assert.Equal("build", refusal.SectionKey);

        AssertValid(BugReportBundleWriter.Write(
            Draft(sections: [Section("build", "{}"), Section("log", "{}"), Section("other", "{\"a\":1}")])));
    }

    [Fact]
    public void Null_section_list_throws()
    {
        // Draft() substitutes TwoSections for a null argument, so both drafts are built here the same way and
        // differ only by the section list.
        static BugReportDraft WithSections(IReadOnlyList<CapturedSection> sections) =>
            new(ReportId, "grimhollow", BugReportKind.Bug, CapturedAt, sections, null, null)
            {
                Description = "Fell through\nthe floor",
                Category = "Gameplay",
            };

        BugReportBundleArgumentException refusal = Refused(WithSections(null!), BugReportBundleRefusal.SectionListMissing);
        Assert.Equal("Sections", refusal.FieldName);

        AssertValid(BugReportBundleWriter.Write(WithSections(TwoSections)));
    }

    [Fact]
    public void Null_section_throws()
    {
        BugReportBundleArgumentException refusal =
            Refused(Draft(sections: [Section("build", "{}"), null!]), BugReportBundleRefusal.SectionMissing);
        Assert.Equal(1, refusal.Index);

        AssertValid(BugReportBundleWriter.Write(Draft(sections: [Section("build", "{}"), Section("log", "{}")])));
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
        BugReportBundleArgumentException refusal = Refused(Draft(sections: [Section("build", json)]), BugReportBundleRefusal.SectionJsonInvalid);
        Assert.Equal("build", refusal.SectionKey);
        Assert.Equal(64, refusal.MaximumDepth);

        AssertValid(BugReportBundleWriter.Write(Draft(sections: [Section("build", "{}")])));
    }

    [Fact]
    public void Section_nested_past_depth_64_throws()
    {
        static string Nested(int depth) =>
            string.Concat(Enumerable.Repeat("{\"a\":", depth - 1)) + "{}" + new string('}', depth - 1);

        BugReportBundleArgumentException refusal =
            Refused(Draft(sections: [Section("build", Nested(65))]), BugReportBundleRefusal.SectionJsonInvalid);
        Assert.Equal("build", refusal.SectionKey);
        Assert.Equal(64, refusal.MaximumDepth);

        AssertValid(BugReportBundleWriter.Write(Draft(sections: [Section("build", Nested(64))])));
    }

    // ActualBytes is the running total through the section that crossed the limit, so a later section is not counted.
    [Fact]
    public void Sections_over_one_mebibyte_throw()
    {
        static CapturedSection Sized(string key, int bytes) => Section(key, "{\"a\":\"" + new string('x', bytes - 8) + "\"}");

        BugReportBundleArgumentException refusal = Refused(
            Draft(sections: [Sized("one", 524288), Sized("two", 524289), Sized("three", 64)]),
            BugReportBundleRefusal.SectionBytesExceeded);
        Assert.Equal(1048577L, refusal.ActualBytes);
        Assert.Equal(1048576L, refusal.MaximumBytes);

        AssertValid(BugReportBundleWriter.Write(Draft(sections: [Sized("one", 524288), Sized("two", 524288)])));
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData(null)]
    public void Report_id_that_is_not_canonical_throws(string? reportId)
    {
        BugReportBundleArgumentException refusal = Refused(Draft(reportId: reportId!), BugReportBundleRefusal.ReportIdInvalid);
        Assert.Equal("ReportId", refusal.FieldName);

        AssertValid(BugReportBundleWriter.Write(Draft(reportId: ReportId)));
    }

    [Theory]
    [InlineData("uppercase")]
    [InlineData("leading-hyphen")]
    [InlineData("too-long")]
    [InlineData("null")]
    public void Game_id_outside_its_pattern_throws(string fault)
    {
        string gameId = fault switch
        {
            "uppercase" => "Grimhollow",
            "leading-hyphen" => "-grimhollow",
            "too-long" => new string('g', 33),
            _ => null!,
        };

        BugReportBundleArgumentException refusal = Refused(Draft(gameId: gameId), BugReportBundleRefusal.GameIdInvalid);
        Assert.Equal("GameId", refusal.FieldName);
        Assert.Equal(32, refusal.MaximumCharacters);

        AssertValid(BugReportBundleWriter.Write(Draft(gameId: new string('g', 32))));
    }

    [Fact]
    public void Undefined_report_kind_throws()
    {
        BugReportBundleArgumentException refusal = Refused(Draft(kind: (BugReportKind)2), BugReportBundleRefusal.ReportKindUndefined);
        Assert.Equal((BugReportKind)2, refusal.ActualKind);

        AssertValid(BugReportBundleWriter.Write(Draft(kind: BugReportKind.Crash)));
    }

    // The zip range is judged in UTC, so the second row is 2107 on the local clock and 2108 in UTC.
    [Theory]
    [InlineData("1979-12-31T23:59:59+00:00", 1979, "1980-01-01T00:00:00+00:00")]
    [InlineData("2107-12-31T23:00:00-05:00", 2108, "2107-12-31T18:59:59-05:00")]
    public void Capture_time_outside_the_zip_range_throws(string refused, int actualYear, string limit)
    {
        BugReportBundleArgumentException refusal = Refused(
            Draft(capturedAt: DateTimeOffset.Parse(refused, CultureInfo.InvariantCulture)),
            BugReportBundleRefusal.CaptureYearOutsideZipRange);
        Assert.Equal(actualYear, refusal.ActualYear);
        Assert.Equal(1980, refusal.MinimumYear);
        Assert.Equal(2107, refusal.MaximumYear);

        AssertValid(BugReportBundleWriter.Write(Draft(capturedAt: DateTimeOffset.Parse(limit, CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void Null_category_throws()
    {
        BugReportBundleArgumentException refusal = Refused(Draft(category: null!), BugReportBundleRefusal.CategoryMissing);
        Assert.Equal("Category", refusal.FieldName);

        AssertValid(BugReportBundleWriter.Write(Draft(category: "Other")));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(65, 64)]
    public void Category_outside_its_length_range_throws(int refused, int limit)
    {
        BugReportBundleArgumentException refusal =
            Refused(Draft(category: new string('c', refused)), BugReportBundleRefusal.CategoryLengthOutsideRange);
        Assert.Equal("Category", refusal.FieldName);
        Assert.Equal(refused, refusal.ActualCharacters);
        Assert.Equal(1, refusal.MinimumCharacters);
        Assert.Equal(64, refusal.MaximumCharacters);

        AssertValid(BugReportBundleWriter.Write(Draft(category: new string('c', limit))));
    }

    [Theory]
    [InlineData("null-frames")]
    [InlineData("null-media-type")]
    public void Null_media_fields_throw(string field)
    {
        (BugReportDraft refused, BugReportBundleRefusal reason, BugReportDraft control) = field == "null-frames"
            ? (Draft(clip: new ReportClip(null!)), BugReportBundleRefusal.ClipFrameListMissing, Draft(clip: ThreeFrames()))
            : (Draft(screenshot: new ReportImage(null!, 8, 6, TestBundles.Png(8, 6))), BugReportBundleRefusal.ScreenshotMediaUnsupported,
                Draft(screenshot: Png(8, 6)));

        BugReportBundleArgumentException refusal = Refused(refused, reason);
        if (field == "null-media-type") Assert.Null(refusal.MediaType);

        AssertValid(BugReportBundleWriter.Write(control));
    }

    [Fact]
    public void More_than_128_entries_throws()
    {
        CapturedSection[] sections = [.. Enumerable.Range(0, 7).Select(i => Section("s" + i, "{}"))];

        BugReportBundleLimitException refusal =
            Limited(Draft(sections: sections, clip: Clip(BundleEntryNames.MaxClipFrames)), BugReportBundleRefusal.EntryCountExceeded);
        Assert.Equal(129, refusal.ActualCount);
        Assert.Equal(128, refusal.MaximumCount);

        AssertValid(BugReportBundleWriter.Write(Draft(sections: sections[..6], clip: Clip(BundleEntryNames.MaxClipFrames))));
    }

    // Padding changes no clip index row, so the unpadded bundle's listed sizes plus the padding are the refused payload.
    [Fact]
    public void Bundle_over_16_MiB_throws()
    {
        int padding = (int)(BugReportLimits.MaxBundleBytes / BundleEntryNames.MaxClipFrames) + 1024;

        BugReportBundleLimitException refusal =
            Limited(Draft(clip: Clip(BundleEntryNames.MaxClipFrames, padding)), BugReportBundleRefusal.PayloadBytesExceeded);
        Assert.Equal(16777216L, refusal.MaximumBytes);

        byte[] unpadded = BugReportBundleWriter.Write(Draft(clip: Clip(BundleEntryNames.MaxClipFrames)));
        AssertValid(unpadded);
        Assert.Equal(Manifest(unpadded).Entries.Sum(e => e.Size) + 120L * padding, refusal.ActualBytes);
    }

    // The payloads total exactly the limit, which the payload rule admits, so only the zip framing pushes the archive over
    // it. Shrinking the screenshot by the reported excess leaves an archive of exactly the limit, which is written.
    [Fact]
    public void Archive_over_16_MiB_throws_when_the_payloads_alone_fit()
    {
        const int Limit = 16777216;
        CapturedSection[] sections = [Section("build", "{}")];
        byte[] header = TestBundles.Png(8, 6);
        BugReportDraft Carrying(int pngBytes)
        {
            byte[] png = [.. header, .. new byte[pngBytes - header.Length]];
            return Draft(sections: sections, screenshot: new ReportImage("image/png", 8, 6, png));
        }

        BugReportBundleLimitException refusal = Limited(Carrying(Limit - 2), BugReportBundleRefusal.ArchiveBytesExceeded);
        Assert.Equal(16777216L, refusal.MaximumBytes);
        long excess = refusal.ActualBytes!.Value - Limit;
        Assert.True(excess > 0);

        byte[] bundle = BugReportBundleWriter.Write(Carrying(Limit - 2 - (int)excess));
        Assert.Equal(Limit, bundle.Length);
        AssertValid(bundle);
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
