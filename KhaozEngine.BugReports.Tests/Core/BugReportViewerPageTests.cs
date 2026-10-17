using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class BugReportViewerPageTests
{
    const string DataOpen = "<script type=\"application/json\" id=\"report-data\">";
    const string ScriptClose = "</script>";

    static readonly DateTimeOffset CapturedAt = new(2026, 10, 17, 3, 4, 5, 678, TimeSpan.Zero);

    internal static BugReportManifest Manifest(string description = "Fell through\nthe floor", params string[] entries)
        => new(BugReportFormat.Version, TestBundles.ReportId, "grimhollow", BugReportKind.Bug, "Gameplay", description, CapturedAt,
            entries.Select(name => new BundleEntry(name, 1, new string('a', 64))).ToList());

    static CapturedSection Section(string key, string json) => new(key, Encoding.UTF8.GetBytes(json));

    static BugReportViewerModel Model(BugReportManifest? manifest = null, ClipIndex? clip = null,
        IReadOnlyList<CapturedSection>? sections = null, ReporterIdentity? reporter = null, string? server = null,
        IReadOnlyList<string>? logTail = null)
        => new(manifest ?? Manifest(), clip, sections ?? [Section("build", "{\"version\":\"1.0.0\"}")], reporter,
            server is null ? (ReadOnlyMemory<byte>?)null : Encoding.UTF8.GetBytes(server), logTail ?? []);

    // The data block's JSON text exactly as embedded.
    internal static string DataText(string page)
    {
        int start = page.IndexOf(DataOpen, StringComparison.Ordinal);
        Assert.True(start >= 0, "The page has no report data block.");
        start += DataOpen.Length;
        return page[start..page.IndexOf(ScriptClose, start, StringComparison.Ordinal)];
    }

    internal static JsonDocument Data(string page) => JsonDocument.Parse(DataText(page));

    // How the page shows a section that could not be read.
    const string ErrorText = "{\n  \"error\": \"unreadable section\"\n}";

    // The display text the page carries for one section.
    internal static string SectionText(JsonDocument data, string key)
        => data.RootElement.GetProperty("sections").GetProperty(key).GetString()!;

    static string AppScript(string page)
    {
        int start = page.IndexOf("<script>", StringComparison.Ordinal);
        Assert.True(start >= 0, "The page has no application script.");
        start += "<script>".Length;
        return page[start..page.IndexOf(ScriptClose, start, StringComparison.Ordinal)];
    }

    static string Template()
    {
        using Stream stream = typeof(BugReportViewerPage).Assembly.GetManifestResourceStream("KhaozEngine.BugReports.Viewer.html")
            ?? throw new InvalidOperationException("The viewer template is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    static int Count(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    static string Directive(string page, string name)
    {
        Match csp = Regex.Match(page, "<meta http-equiv=\"Content-Security-Policy\" content=\"([^\"]*)\">");
        Assert.True(csp.Success, "The page has no CSP meta element.");
        return csp.Groups[1].Value.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith(name + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void Script_close_in_user_text_cannot_end_the_data_block()
    {
        const string Hostile = "</script><script>alert(1)</script>";
        string page = BugReportViewerPage.Render(Model(Manifest(Hostile), sections: [Section("build", "{\"a\":\"\\u003c/script>\"}")]));

        Assert.Equal(Count(Template(), ScriptClose), Count(page, ScriptClose));
        Assert.Contains("\\u003c/script", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<", DataText(page), StringComparison.Ordinal);
        using JsonDocument data = Data(page);
        Assert.Equal(Hostile, data.RootElement.GetProperty("manifest").GetProperty("description").GetString());
        Assert.Equal("{\n  \"a\": \"</script>\"\n}", SectionText(data, "build"));
    }

    [Fact]
    public void Raw_markup_inside_section_json_is_reescaped()
    {
        string page = BugReportViewerPage.Render(Model(sections: [Section("build", "{\"a\":\"<b>\"}")]));

        Assert.DoesNotContain("<b>", page, StringComparison.Ordinal);
        using JsonDocument data = Data(page);
        Assert.Equal("{\n  \"a\": \"<b>\"\n}", SectionText(data, "build"));
    }

    [Fact]
    public void Page_loads_nothing_external()
    {
        string page = BugReportViewerPage.Render(Model(clip: new ClipIndex([new ClipIndexFrame(0, 10, 4, 4, "clip/000.jpg")])));

        foreach (string external in new[] { "http://", "https://", "<link", "@import", "src=\"//" })
            Assert.DoesNotContain(external, page, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("default-src 'none'", Directive(page, "default-src"));
        Assert.Equal("img-src 'self' file:", Directive(page, "img-src"));
        Assert.Equal("media-src 'self' file:", Directive(page, "media-src"));
    }

    [Fact]
    public void Script_runs_only_by_hash()
    {
        string page = BugReportViewerPage.Render(Model());

        string hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(AppScript(page))));
        string scriptSrc = Directive(page, "script-src");
        Assert.Equal("script-src 'sha256-" + hash + "'", scriptSrc);
        Assert.DoesNotContain("unsafe-inline", scriptSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", scriptSrc, StringComparison.Ordinal);
        Assert.Equal(1, Count(page, "<script>"));
    }

    [Fact]
    public void Page_never_builds_html_from_strings()
    {
        string page = BugReportViewerPage.Render(Model());

        foreach (string sink in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(" })
            Assert.DoesNotContain(sink, page, StringComparison.Ordinal);
    }

    [Fact]
    public void Video_is_used_when_the_manifest_lists_clip_mp4()
    {
        string withVideo = BugReportViewerPage.Render(Model(Manifest(entries: [BundleEntryNames.ClipIndex, BundleEntryNames.ClipMp4])));
        string withFrames = BugReportViewerPage.Render(Model(Manifest(entries: [BundleEntryNames.ClipIndex, "clip/000.jpg"])));

        using JsonDocument video = Data(withVideo);
        using JsonDocument frames = Data(withFrames);
        Assert.Equal(BundleEntryNames.ClipMp4, video.RootElement.GetProperty("media").GetProperty("video").GetString());
        Assert.Equal(JsonValueKind.Null, frames.RootElement.GetProperty("media").GetProperty("video").ValueKind);
    }

    [Fact]
    public void Referrer_is_suppressed()
        => Assert.Contains("<meta name=\"referrer\" content=\"no-referrer\">", BugReportViewerPage.Render(Model()), StringComparison.Ordinal);

    [Fact]
    public void Unreadable_section_renders_as_an_error_and_the_rest_survive()
    {
        string page = BugReportViewerPage.Render(Model(
            sections: [Section("bad", "{\"a\":\"\\uD800\"}"), Section("name", "{\"\\uDC00\":1}"), Section("torn", "{\"a\":"),
                Section("build", "{\"version\":\"1.0.0\"}")],
            server: "{\"sections\":{\"x\":{\"a\":\"\\uD800\"}}}"));

        using JsonDocument data = Data(page);
        foreach (string key in new[] { "bad", "name", "torn" })
            Assert.Equal(ErrorText, SectionText(data, key));
        Assert.Equal("{\n  \"version\": \"1.0.0\"\n}", SectionText(data, "build"));
        Assert.Equal(ErrorText, data.RootElement.GetProperty("server").GetString());
    }

    [Fact]
    public void Numbers_keep_their_exact_text()
    {
        string page = BugReportViewerPage.Render(Model(sections: [Section("build", "{\"n\":9007199254740993,\"f\":1.50e3}")]));

        using JsonDocument data = Data(page);
        Assert.Equal("{\n  \"n\": 9007199254740993,\n  \"f\": 1.50e3\n}", SectionText(data, "build"));
    }

    [Fact]
    public void Display_text_keeps_empty_containers_on_one_line()
    {
        string page = BugReportViewerPage.Render(Model(sections: [Section("build", "{\"e\":{},\"l\":[],\"n\":[[],{\"a\":false}]}")]));

        using JsonDocument data = Data(page);
        Assert.Equal("{\n  \"e\": {},\n  \"l\": [],\n  \"n\": [\n    [],\n    {\n      \"a\": false\n    }\n  ]\n}",
            SectionText(data, "build"));
    }

    [Fact]
    public void Display_text_escapes_quotes_backslashes_and_control_characters()
    {
        string page = BugReportViewerPage.Render(Model(sections: [Section("build", "{\"s\":\"q\\\"b\\\\c\\u0001\"}")]));

        using JsonDocument data = Data(page);
        Assert.Equal("{\n  \"s\": \"q\\\"b\\\\c\\u0001\"\n}", SectionText(data, "build"));
    }

    [Fact]
    public void Display_indent_stops_at_sixteen_levels()
    {
        string nested = string.Concat(Enumerable.Repeat("{\"a\":", 19)) + "1" + new string('}', 19);
        // The padding keeps the indented text inside the growth bound, so the text stays indented.
        string section = "{\"x\":" + nested + ",\"pad\":\"" + new string('p', 2000) + "\"}";
        string page = BugReportViewerPage.Render(Model(sections: [Section("build", section)]));

        using JsonDocument data = Data(page);
        string[] lines = SectionText(data, "build").Split('\n');
        Assert.Contains(new string(' ', 32) + "\"a\": 1", lines);
        Assert.All(lines, line => Assert.False(line.StartsWith(new string(' ', 33), StringComparison.Ordinal)));
    }

    [Fact]
    public void Deep_wide_sections_fall_back_to_compact_text()
    {
        // 63 nested arrays inside the section object, then many one-byte numbers: indented, every number would
        // carry a newline and 32 spaces.
        string section = "{\"a\":" + new string('[', 63) + string.Concat(Enumerable.Repeat("0,", 5000)) + "9007199254740993"
            + new string(']', 63) + "}";
        string page = BugReportViewerPage.Render(Model(sections: [Section("build", section)],
            server: "{\"sections\":{\"x\":" + section + "}}"));

        using JsonDocument data = Data(page);
        string text = SectionText(data, "build");
        Assert.True(text.Length <= 4 * section.Length + 64, "display text is " + text.Length + " characters");
        Assert.Equal(section, text);
        using JsonDocument parsed = JsonDocument.Parse(text);
        Assert.Equal("{\"sections\":{\"x\":" + section + "}}", data.RootElement.GetProperty("server").GetString());
    }

    [Fact]
    public void Data_carries_every_part_of_the_report()
    {
        var clip = new ClipIndex([new ClipIndexFrame(0, 10, 4, 4, "clip/000.jpg"), new ClipIndexFrame(100, 30, 4, 4, "clip/001.jpg"),
            new ClipIndexFrame(200, 10, 4, 4, "clip/002.jpg")]);
        string page = BugReportViewerPage.Render(Model(
            Manifest(entries: [BundleEntryNames.ScreenshotJpeg, BundleEntryNames.ClipIndex, "clip/000.jpg"]), clip,
            reporter: new ReporterIdentity("player-1", "Café <3", null),
            server: "{\"formatVersion\":1,\"sections\":{\"world\":{\"tick\":7,\"ok\":true,\"none\":null,\"list\":[1,\"two\"]}}}",
            logTail: ["first", "<last>"]));

        Assert.Contains("<title>Bug report " + ReportIds.ReferenceCode(TestBundles.ReportId) + "</title>", page, StringComparison.Ordinal);
        using JsonDocument data = Data(page);
        JsonElement root = data.RootElement;
        Assert.Equal(TestBundles.ReportId, root.GetProperty("manifest").GetProperty("reportId").GetString());
        Assert.Equal(3, root.GetProperty("clip").GetProperty("frames").GetArrayLength());
        Assert.Equal(10, root.GetProperty("medianRenderMilliseconds").GetDouble());
        Assert.Equal("[1]", root.GetProperty("hitches").GetRawText());
        Assert.Equal(BundleEntryNames.ScreenshotJpeg, root.GetProperty("media").GetProperty("screenshot").GetString());
        Assert.Equal("Café <3", root.GetProperty("reporter").GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reporter").GetProperty("handle").ValueKind);
        Assert.Equal("{\n  \"formatVersion\": 1,\n  \"sections\": {\n    \"world\": {\n      \"tick\": 7,\n      \"ok\": true,\n" +
            "      \"none\": null,\n      \"list\": [\n        1,\n        \"two\"\n      ]\n    }\n  }\n}", root.GetProperty("server").GetString());
        Assert.Equal(["first", "<last>"], root.GetProperty("logTail").EnumerateArray().Select(e => e.GetString()!));
    }

    [Fact]
    public void Missing_parts_embed_as_null_or_empty()
    {
        string page = BugReportViewerPage.Render(Model(sections: []));

        using JsonDocument data = Data(page);
        JsonElement root = data.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("clip").ValueKind);
        Assert.Equal(0, root.GetProperty("medianRenderMilliseconds").GetDouble());
        Assert.Equal(0, root.GetProperty("hitches").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("media").GetProperty("screenshot").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reporter").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("server").ValueKind);
        Assert.Equal("{}", root.GetProperty("sections").GetRawText());
        Assert.Equal(0, root.GetProperty("logTail").GetArrayLength());
    }
}
