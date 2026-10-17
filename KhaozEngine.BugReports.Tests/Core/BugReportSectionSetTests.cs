using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class BugReportSectionSetTests
{
    const string ErrorPrefix = "System.InvalidOperationException: ";

    static readonly BugReportContext Context = new("0192f1c2a3b47c4d8e9f0a1b2c3d4e5f",
        new DateTimeOffset(2026, 10, 17, 3, 4, 5, TimeSpan.Zero));

    static readonly ReportRedactor Redactor = new("/Users/ann");

    static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    /// <summary>A budget that never expires, so a slow runner cannot mask strings in a test that is not about the budget.</summary>
    static readonly TimeSpan Unclocked = TimeSpan.MaxValue;

    static IReadOnlyList<CapturedSection> Finish(BugReportSectionSet set, TimeSpan? budget = null) =>
        BugReportSectionSet.Finish(set.CaptureAll(Context), Redactor, budget ?? Unclocked);

    static JsonDocument Parse(ReadOnlyMemory<byte> json) => JsonDocument.Parse(json, Strict);

    static string Text(ReadOnlyMemory<byte> json) => Encoding.UTF8.GetString(json.Span);

    static string[] Lines(int count, int length) =>
        Enumerable.Range(0, count).Select(i => ("line-" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + "-").PadRight(length, 'x')).ToArray();

    static Action<Utf8JsonWriter> WriteLines(string[] lines, string? first = null) => writer =>
    {
        writer.WriteStartObject();
        if (first is not null) writer.WriteString("truncated", first);
        writer.WriteStartArray("lines");
        foreach (string line in lines) writer.WriteStringValue(line);
        writer.WriteEndArray();
        writer.WriteEndObject();
    };

    static Action<Utf8JsonWriter> WriteObject(string name, string value) => writer =>
    {
        writer.WriteStartObject();
        writer.WriteString(name, value);
        writer.WriteEndObject();
    };

    static string ErrorText(ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = Parse(json);
        return document.RootElement.GetProperty("error").GetString()!;
    }

    [Fact]
    public void Sections_capture_in_registration_order()
    {
        var set = new BugReportSectionSet();
        foreach (string key in new[] { "build", "a.log", "zone" })
            set.Add(new TestSection(key, 1024, WriteObject("key", key)));

        IReadOnlyList<RawSection> raw = set.CaptureAll(Context);
        IReadOnlyList<CapturedSection> finished = BugReportSectionSet.Finish(raw, Redactor, BugReportSectionSet.DefaultFinishBudget);

        Assert.Equal(new[] { "build", "a.log", "zone" }, set.Keys);
        Assert.Equal(new[] { "build", "a.log", "zone" }, raw.Select(section => section.Key));
        Assert.Equal(new[] { "build", "a.log", "zone" }, finished.Select(section => section.Key));
        Assert.Equal("{\"key\":\"a.log\"}", Text(finished[1].Json));
    }

    [Fact]
    public void Throwing_section_writes_error_and_the_rest_continue()
    {
        var set = new BugReportSectionSet();
        set.Add(new TestSection("broken", 1024, _ => throw new InvalidOperationException("boom at /Users/ann/x")));
        set.Add(new TestSection("next", 1024, WriteObject("ok", "yes")));

        IReadOnlyList<RawSection> raw = set.CaptureAll(Context);
        IReadOnlyList<CapturedSection> finished = BugReportSectionSet.Finish(raw, Redactor, Unclocked);

        Assert.True(raw[0].IsError);
        Assert.False(raw[1].IsError);
        Assert.Equal("{\"error\":\"System.InvalidOperationException: boom at ~/x\"}", Text(finished[0].Json));
        Assert.Equal("{\"ok\":\"yes\"}", Text(finished[1].Json));
    }

    [Fact]
    public void Unclosed_or_non_object_root_is_an_error()
    {
        var set = new BugReportSectionSet();
        set.Add(new TestSection("open", 1024, writer => writer.WriteStartArray()));
        set.Add(new TestSection("bare", 1024, writer => writer.WriteNumberValue(3)));
        set.Add(new TestSection("empty", 1024, _ => { }));

        IReadOnlyList<RawSection> raw = set.CaptureAll(Context);
        IReadOnlyList<CapturedSection> finished = BugReportSectionSet.Finish(raw, Redactor, Unclocked);

        Assert.All(raw, section => Assert.True(section.IsError));
        Assert.All(finished, section => Assert.StartsWith(ErrorPrefix, ErrorText(section.Json), StringComparison.Ordinal));
    }

    [Fact]
    public void Every_string_and_property_name_is_redacted()
    {
        var set = new BugReportSectionSet();
        set.Add(new TestSection("secrets", 1024, WriteObject("password=x", "Bearer abcdefgh12")));

        CapturedSection section = Assert.Single(Finish(set));

        using JsonDocument document = Parse(section.Json);
        JsonProperty property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("password=[redacted]", property.Name);
        Assert.Equal("Bearer [redacted]", property.Value.GetString());
    }

    [Fact]
    public void Oversized_section_is_truncated_to_valid_json_within_max_bytes()
    {
        string[] lines = Lines(100, 20);
        var set = new BugReportSectionSet();
        set.Add(new TestSection("log", 200, WriteLines(lines)));

        CapturedSection section = Assert.Single(Finish(set));

        Assert.True(section.Json.Length <= 200);
        using JsonDocument document = Parse(section.Json);
        JsonProperty first = document.RootElement.EnumerateObject().First();
        Assert.Equal("truncated", first.Name);
        Assert.True(first.Value.GetBoolean());
        string[] kept = document.RootElement.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray();
        Assert.NotEmpty(kept);
        Assert.Equal(lines.Take(kept.Length), kept);
    }

    [Fact]
    public void Truncation_keeps_the_largest_fitting_prefix()
    {
        string[] lines = Lines(100, 20);
        var set = new BugReportSectionSet();
        set.Add(new TestSection("log", 200, WriteLines(lines)));

        CapturedSection section = Assert.Single(Finish(set));

        using JsonDocument document = Parse(section.Json);
        int kept = document.RootElement.GetProperty("lines").GetArrayLength();
        Assert.Equal(TruncatedLines(lines, kept), section.Json.ToArray());
        Assert.True(TruncatedLines(lines, kept + 1).Length > 200);
    }

    [Fact]
    public void Total_state_is_capped_at_one_mebibyte()
    {
        string[] lines = Lines(512, 997);
        var set = new BugReportSectionSet();
        foreach (string key in new[] { "one", "two", "three" })
            set.Add(new TestSection(key, 512 * 1024, WriteLines(lines)));

        IReadOnlyList<CapturedSection> finished = Finish(set);

        Assert.Equal(3, finished.Count);
        Assert.True(finished.Sum(section => section.Json.Length) <= BugReportLimits.MaxSectionBytesTotal);
        Assert.Equal(lines.Length, LinesOf(finished[1].Json, out bool secondTruncated));
        Assert.False(secondTruncated);
        Assert.True(LinesOf(finished[2].Json, out bool thirdTruncated) < lines.Length);
        Assert.True(thirdTruncated);
    }

    [Fact]
    public void Finish_truncates_and_drops_against_a_smaller_total_budget()
    {
        RawSection[] raw =
        [
            new("one", 1024, Encoding.UTF8.GetBytes("{\"ok\":1}"), IsError: false),
            new("two", 1024, Encoding.UTF8.GetBytes("{\"keep\":" + new string('1', 94)
                + ",\"overflow\":" + new string('1', 80) + "}"), IsError: false),
            new("three", 1024, Encoding.UTF8.GetBytes("{\"late\":true}"), IsError: false),
        ];

        IReadOnlyList<CapturedSection> finished = BugReportSectionSet.Finish(raw, Redactor, Unclocked, totalBytes: 128);

        Assert.Equal(new[] { "one", "two" }, finished.Select(section => section.Key));
        Assert.Equal(128, finished.Sum(section => section.Json.Length));
        Assert.Equal(raw[0].Json.ToArray(), finished[0].Json.ToArray());
        Assert.Equal("{\"truncated\":true,\"keep\":" + new string('1', 94) + "}", Text(finished[1].Json));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(BugReportSectionSet.MinSectionBytes - 1)]
    public void Finish_rejects_a_total_budget_below_the_section_minimum(int totalBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>("totalBytes", () =>
            BugReportSectionSet.Finish([], Redactor, Unclocked, totalBytes));
    }

    [Fact]
    public void Finish_accepts_the_minimum_total_budget()
    {
        var set = new BugReportSectionSet();
        set.Add(new TestSection("ok", 1024, WriteObject("ok", "yes")));

        CapturedSection finished = Assert.Single(BugReportSectionSet.Finish(
            set.CaptureAll(Context), Redactor, Unclocked, totalBytes: BugReportSectionSet.MinSectionBytes));

        Assert.Equal("{\"ok\":\"yes\"}", Text(finished.Json));
    }

    [Fact]
    public void Section_with_budget_under_64_bytes_is_dropped()
    {
        // {"lines":[ ... ]} costs 11 bytes and each string its length plus 3 for quotes and a comma. Strings stay
        // short so no redaction rule reaches its match timeout.
        const int Fill = BugReportLimits.MaxSectionBytesTotal - 40;
        string[] lines = Lines(1000, 1000);
        lines[^1] = lines[^1].PadRight(1000 + Fill - (11 + 1000 * 1003), 'x');
        var set = new BugReportSectionSet();
        set.Add(new TestSection("big", BugReportLimits.MaxSectionBytesTotal, WriteLines(lines)));
        set.Add(new TestSection("late", 1024, WriteObject("ok", "yes")));

        IReadOnlyList<RawSection> raw = set.CaptureAll(Context);
        IReadOnlyList<CapturedSection> finished = BugReportSectionSet.Finish(raw, Redactor, Unclocked);

        Assert.Equal(Fill, raw[0].Json.Length);
        CapturedSection only = Assert.Single(finished);
        Assert.Equal("big", only.Key);
        Assert.Equal(raw[0].Json.ToArray(), only.Json.ToArray());
    }

    [Fact]
    public void Add_rejects_duplicate_or_invalid_keys()
    {
        var set = new BugReportSectionSet();
        set.Add(new TestSection("log", BugReportSectionSet.MinSectionBytes, WriteObject("a", "b")));

        Assert.Throws<ArgumentException>(() => set.Add(new TestSection("log", 1024, WriteObject("a", "b"))));
        Assert.Throws<ArgumentException>(() => set.Add(new TestSection("Build", 1024, WriteObject("a", "b"))));
        Assert.Throws<ArgumentException>(() => set.Add(new TestSection("a/b", 1024, WriteObject("a", "b"))));
        Assert.Throws<ArgumentException>(() => set.Add(new TestSection("", 1024, WriteObject("a", "b"))));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.Add(new TestSection("small", 63, WriteObject("a", "b"))));
        Assert.Throws<ArgumentNullException>(() => set.Add(null!));
        Assert.Equal(new[] { "log" }, set.Keys);
    }

    [Fact]
    public void CaptureAll_returns_raw_output_without_redaction_or_truncation()
    {
        const string Secret = "{\"password=x\":\"Bearer abcdefgh12\"}";
        var set = new BugReportSectionSet();
        set.Add(new TestSection("secrets", 1024, WriteObject("password=x", "Bearer abcdefgh12")));
        set.Add(new TestSection("long", BugReportSectionSet.MinSectionBytes, WriteObject("s", new string('a', 292))));

        IReadOnlyList<RawSection> raw = set.CaptureAll(Context);

        Assert.Equal(Encoding.UTF8.GetBytes(Secret), raw[0].Json.ToArray());
        Assert.False(raw[0].IsError);
        Assert.Equal(1024, raw[0].MaxBytes);
        Assert.Equal(300, raw[1].Json.Length);
        Assert.False(raw[1].IsError);
        Assert.Equal(BugReportSectionSet.MinSectionBytes, raw[1].MaxBytes);
    }

    [Fact]
    public void Raw_section_over_4_MiB_becomes_an_error_object()
    {
        string chunk = new('a', 1024);
        var set = new BugReportSectionSet();
        set.Add(new TestSection("huge", 1024, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("s");
            for (int i = 0; i < 5 * 1024; i++) writer.WriteStringValue(chunk);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }));
        set.Add(new TestSection("next", 1024, WriteObject("ok", "yes")));

        IReadOnlyList<RawSection> raw = set.CaptureAll(Context);

        Assert.True(raw[0].IsError);
        Assert.Equal("{\"error\":\"System.InvalidOperationException: Section output exceeded 4194304 bytes.\"}", Text(raw[0].Json));
        Assert.False(raw[1].IsError);
        Assert.Equal("{\"ok\":\"yes\"}", Text(raw[1].Json));
    }

    [Fact]
    public void Finish_masks_every_remaining_string_once_the_budget_is_spent()
    {
        var set = new BugReportSectionSet();
        set.Add(new TestSection("plain", 1024, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("a", "plain");
            writer.WriteString("b", "text");
            writer.WriteNumber("n", 3);
            writer.WriteEndObject();
        }));

        CapturedSection section = Assert.Single(Finish(set, TimeSpan.Zero));

        Assert.Equal("{\"[redacted]\":\"[redacted]\",\"[redacted]#2\":\"[redacted]\",\"[redacted]#3\":3}", Text(section.Json));
    }

    [Fact]
    public void Redacted_property_names_stay_unique()
    {
        var set = new BugReportSectionSet();
        set.Add(new TestSection("tokens", 1024, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("token=a", 1);
            writer.WriteNumber("token=b", 2);
            writer.WriteEndObject();
        }));

        CapturedSection section = Assert.Single(Finish(set));

        Assert.Equal("{\"token=[redacted]\":1,\"token=[redacted]#2\":2}", Text(section.Json));
        using JsonDocument document = Parse(section.Json);
        Assert.Equal(2, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void Error_message_is_redacted_before_it_is_cut()
    {
        const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ.c2lnbmF0dXJlLXNpZ25hdHVyZQ";
        string message = new string('x', 500) + " " + Jwt;
        var set = new BugReportSectionSet();
        set.Add(new TestSection("broken", 1024, _ => throw new InvalidOperationException(message)));

        CapturedSection section = Assert.Single(Finish(set));

        string error = ErrorText(section.Json);
        Assert.Contains(ReportRedactor.Mask, error, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_message_is_cut_scalar_safe_at_8192_raw_and_512_finished()
    {
        string message = new string('x', 511) + "\U0001F600" + new string('y', 9000);
        var set = new BugReportSectionSet();
        set.Add(new TestSection("broken", 1024, _ => throw new InvalidOperationException(message)));

        IReadOnlyList<RawSection> raw = set.CaptureAll(Context);
        CapturedSection section = Assert.Single(BugReportSectionSet.Finish(raw, Redactor, Unclocked));

        Assert.Equal(ErrorPrefix + message[..8192], ErrorText(raw[0].Json));
        Assert.Equal(ErrorPrefix + new string('x', 511), ErrorText(section.Json));
    }

    [Fact]
    public void A_root_member_named_truncated_is_renamed_when_truncating()
    {
        string[] lines = Lines(100, 20);
        var set = new BugReportSectionSet();
        set.Add(new TestSection("log", 200, WriteLines(lines, first: "x")));

        CapturedSection section = Assert.Single(Finish(set));

        Assert.True(section.Json.Length <= 200);
        using JsonDocument document = Parse(section.Json);
        JsonProperty[] properties = document.RootElement.EnumerateObject().ToArray();
        Assert.Equal("truncated", properties[0].Name);
        Assert.True(properties[0].Value.GetBoolean());
        Assert.Equal("truncated#2", properties[1].Name);
        Assert.Equal("x", properties[1].Value.GetString());
        Assert.Equal(properties.Length, properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Escaped_lone_surrogate_becomes_an_error_and_the_rest_continue()
    {
        var set = new BugReportSectionSet();
        set.Add(new TestSection("lone", 1024, writer => writer.WriteRawValue("{\"a\":\"\\uD800\"}")));
        set.Add(new TestSection("next", 1024, WriteObject("ok", "yes")));

        IReadOnlyList<RawSection> raw = set.CaptureAll(Context);
        IReadOnlyList<CapturedSection> finished = BugReportSectionSet.Finish(raw, Redactor, Unclocked);

        Assert.False(raw[0].IsError);
        Assert.Equal(ErrorPrefix + UnreadableString, ErrorText(finished[0].Json));
        Assert.Equal("{\"ok\":\"yes\"}", Text(finished[1].Json));
    }

    [Fact]
    public void Invalid_utf8_in_a_raw_section_becomes_an_error_and_the_rest_continue()
    {
        RawSection[] raw =
        [
            new("bad", 1024, new byte[] { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xC3, (byte)'"', (byte)'}' }, IsError: false),
            new("next", 1024, Encoding.UTF8.GetBytes("{\"ok\":\"yes\"}"), IsError: false),
        ];

        IReadOnlyList<CapturedSection> finished = BugReportSectionSet.Finish(raw, Redactor, Unclocked);

        Assert.Equal(ErrorPrefix + UnreadableString, ErrorText(finished[0].Json));
        Assert.Equal("{\"ok\":\"yes\"}", Text(finished[1].Json));
    }

    [Fact]
    public void Hand_built_section_nested_deeper_than_64_becomes_an_error()
    {
        static byte[] Nested(int arrays) =>
            Encoding.UTF8.GetBytes("{\"a\":" + new string('[', arrays) + new string(']', arrays) + "}");
        RawSection[] raw =
        [
            new("deep", 4096, Nested(64), IsError: false),
            new("limit", 4096, Nested(63), IsError: false),
        ];

        IReadOnlyList<CapturedSection> finished = BugReportSectionSet.Finish(raw, Redactor, Unclocked);

        Assert.Equal(ErrorPrefix + "Section output nests deeper than 64 levels.", ErrorText(finished[0].Json));
        Assert.Equal(Encoding.UTF8.GetString(Nested(63)), Text(finished[1].Json));
        Assert.True(BugReportJson.IsValidSection(finished[1].Json));
        Assert.False(BugReportJson.IsValidSection(Nested(64)));
    }

    [Fact]
    public void Malformed_section_still_reports_one_object()
    {
        RawSection[] raw = [new("bad", 1024, Encoding.UTF8.GetBytes("{\"a\":1"), IsError: false)];

        IReadOnlyList<CapturedSection> finished = BugReportSectionSet.Finish(raw, Redactor, Unclocked);

        Assert.Equal(ErrorPrefix + "Section output must be one JSON object.", ErrorText(finished[0].Json));
    }

    const string UnreadableString = "Section output holds a string that is not valid UTF-8 or has a lone surrogate.";

    static int LinesOf(ReadOnlyMemory<byte> json, out bool truncated)
    {
        using JsonDocument document = Parse(json);
        truncated = document.RootElement.TryGetProperty("truncated", out JsonElement marker) && marker.GetBoolean();
        return document.RootElement.GetProperty("lines").GetArrayLength();
    }

    static byte[] TruncatedLines(string[] lines, int count)
    {
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("truncated", true);
            writer.WriteStartArray("lines");
            foreach (string line in lines.Take(count)) writer.WriteStringValue(line);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    sealed class TestSection(string key, int maxBytes, Action<Utf8JsonWriter> write) : IBugReportSection
    {
        public string Key => key;

        public int MaxBytes => maxBytes;

        public void Write(Utf8JsonWriter writer, BugReportContext context) => write(writer);
    }
}
