using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Contracts;

public sealed class BugReportJsonTests
{
    const string Id = "0192f1c2a3b47c4d8e9f0a1b2c3d4e5f";
    const string ManifestGolden = "{\"formatVersion\":1,\"reportId\":\"0192f1c2a3b47c4d8e9f0a1b2c3d4e5f\",\"gameId\":\"grimhollow\",\"kind\":\"bug\",\"category\":\"Gameplay\",\"description\":\"Fell through\\nthe floor\",\"capturedAt\":\"2026-10-17T03:04:05.678+00:00\",\"entries\":[{\"name\":\"sections/build.json\",\"size\":42,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}";
    const string ClipGolden = "{\"frames\":[{\"offsetMilliseconds\":0,\"renderMilliseconds\":16.5,\"width\":960,\"height\":540,\"entry\":\"clip/000.jpg\"},{\"offsetMilliseconds\":83.25,\"renderMilliseconds\":41,\"width\":960,\"height\":540,\"entry\":\"clip/001.jpg\"}]}";

    static BugReportManifest Manifest() => new(1, Id, "grimhollow", BugReportKind.Bug, "Gameplay",
        "Fell through\nthe floor", new DateTimeOffset(2026, 10, 17, 3, 4, 5, 678, TimeSpan.Zero),
        new[] { new BundleEntry("sections/build.json", 42, new string('a', 64)) });

    [Fact]
    public void Manifest_matches_the_golden_bytes()
    {
        Assert.Equal(Encoding.UTF8.GetBytes(ManifestGolden), BugReportJson.SerializeManifest(Manifest()));
        BugReportManifest value = BugReportJson.DeserializeManifest(Encoding.UTF8.GetBytes(ManifestGolden));
        BugReportManifest expected = Manifest();
        Assert.Equal(expected, value with { Entries = expected.Entries });
        Assert.Equal(expected.Entries, value.Entries);
    }

    [Fact]
    public void Options_serialize_the_manifest_to_the_golden_bytes()
        => Assert.Equal(Encoding.UTF8.GetBytes(ManifestGolden),
            JsonSerializer.SerializeToUtf8Bytes(Manifest(), BugReportJson.Options));

    [Fact]
    public void Manifest_capturedAt_is_normalised_to_utc()
    {
        BugReportManifest manifest = Manifest();
        manifest = manifest with { CapturedAt = manifest.CapturedAt.ToOffset(TimeSpan.FromHours(10)) };
        Assert.Equal(Encoding.UTF8.GetBytes(ManifestGolden), BugReportJson.SerializeManifest(manifest));
        Assert.Equal(Encoding.UTF8.GetBytes(ManifestGolden),
            JsonSerializer.SerializeToUtf8Bytes(manifest, BugReportJson.Options));
    }

    [Fact]
    public void ClipIndex_matches_the_golden_bytes()
    {
        var index = new ClipIndex(new[]
        {
            new ClipIndexFrame(0, 16.5, 960, 540, "clip/000.jpg"),
            new ClipIndexFrame(83.25, 41, 960, 540, "clip/001.jpg"),
        });
        Assert.Equal(Encoding.UTF8.GetBytes(ClipGolden), BugReportJson.SerializeClipIndex(index));
        ClipIndex value = BugReportJson.DeserializeClipIndex(Encoding.UTF8.GetBytes(ClipGolden));
        Assert.Equal(index.Frames, value.Frames);
    }

    [Fact]
    public void Kind_round_trips_as_camel_case_strings()
    {
        byte[] json = Encoding.UTF8.GetBytes(ManifestGolden.Replace("\"bug\"", "\"crash\"", StringComparison.Ordinal));
        BugReportManifest manifest = BugReportJson.DeserializeManifest(json);
        Assert.Equal(BugReportKind.Crash, manifest.Kind);
        Assert.Equal(json, BugReportJson.SerializeManifest(manifest));
    }

    [Fact]
    public void Non_ascii_description_stays_literal_and_html_characters_are_escaped()
    {
        string description = "Café <b> & 'quoted' \"text\"";
        byte[] json = BugReportJson.SerializeManifest(Manifest() with { Description = description });
        string text = Encoding.UTF8.GetString(json);
        Assert.Contains("Café", text, StringComparison.Ordinal);
        Assert.Contains("\\u003Cb\\u003E", text, StringComparison.Ordinal);
        Assert.Contains("\\u0026", text, StringComparison.Ordinal);
        Assert.Contains("\\u0027quoted\\u0027", text, StringComparison.Ordinal);
        Assert.Contains("\\u0022text\\u0022", text, StringComparison.Ordinal);
        Assert.Equal(description, BugReportJson.DeserializeManifest(json).Description);
    }

    [Fact]
    public void Wire_records_round_trip()
    {
        var accepted = new IntakeAccepted(Id, "R-P3TKJZ");
        Assert.Equal(accepted, BugReportJson.DeserializeIntakeAccepted(BugReportJson.SerializeIntakeAccepted(accepted)));
        Assert.Equal("{\"reportId\":\"" + Id + "\",\"reference\":\"R-P3TKJZ\"}",
            Encoding.UTF8.GetString(BugReportJson.SerializeIntakeAccepted(accepted)));

        var error = new RelayError("invalid_bundle");
        Assert.Equal(error, BugReportJson.DeserializeRelayError(BugReportJson.SerializeRelayError(error)));
        Assert.Equal("{\"error\":\"invalid_bundle\"}", Encoding.UTF8.GetString(BugReportJson.SerializeRelayError(error)));

        using JsonDocument section = JsonDocument.Parse("{\"version\":\"1.0.0\"}");
        var snapshot = new ServerSnapshotWire(1, Id, "player-1", Manifest().CapturedAt.ToOffset(TimeSpan.FromHours(10)),
            new Dictionary<string, JsonElement> { ["build"] = section.RootElement.Clone() });
        byte[] snapshotJson = BugReportJson.SerializeServerSnapshot(snapshot);
        ServerSnapshotWire value = BugReportJson.DeserializeServerSnapshot(snapshotJson);
        Assert.Equal(1, value.FormatVersion);
        Assert.Equal(Id, value.ReportId);
        Assert.Equal("player-1", value.Subject);
        Assert.Equal(Manifest().CapturedAt, value.CapturedAt);
        Assert.Equal(TimeSpan.Zero, value.CapturedAt.Offset);
        Assert.Equal("1.0.0", Assert.Single(value.Sections).Value.GetProperty("version").GetString());
        Assert.Equal(snapshotJson, BugReportJson.SerializeServerSnapshot(value));
    }

    [Fact]
    public void Options_include_the_reporter_identity_shape()
    {
        var identity = new ReporterIdentity("player-1", "Café", null);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(identity, BugReportJson.Options);
        Assert.Equal("{\"subject\":\"player-1\",\"displayName\":\"Café\",\"handle\":null}", Encoding.UTF8.GetString(json));
        Assert.Equal(identity, JsonSerializer.Deserialize<ReporterIdentity>(json, BugReportJson.Options));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"entries\":null}")]
    public void Deserializers_throw_JsonException_on_bad_input(string input)
    {
        byte[] json = Encoding.UTF8.GetBytes(input);
        Assert.Throws<JsonException>(() => BugReportJson.DeserializeManifest(json));
        Assert.Throws<JsonException>(() => BugReportJson.DeserializeClipIndex(json));
        Assert.Throws<JsonException>(() => BugReportJson.DeserializeIntakeAccepted(json));
        Assert.Throws<JsonException>(() => BugReportJson.DeserializeRelayError(json));
        Assert.Throws<JsonException>(() => BugReportJson.DeserializeServerSnapshot(json));
    }

    [Theory]
    [InlineData(",\"entries\":[{\"name\":\"sections/build.json\",\"size\":42,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]", "")]
    [InlineData("[{\"name\":\"sections/build.json\",\"size\":42,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]", "null")]
    [InlineData(",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "")]
    [InlineData("\"gameId\":\"grimhollow\",", "")]
    [InlineData("\"gameId\":\"grimhollow\"", "\"gameId\":null")]
    public void Manifest_rejects_missing_or_null_fields(string field, string replacement)
    {
        byte[] json = Encoding.UTF8.GetBytes(Replace(ManifestGolden, field, replacement));
        Assert.Throws<JsonException>(() => BugReportJson.DeserializeManifest(json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BugReportManifest>(json, BugReportJson.Options));
    }

    [Theory]
    [InlineData("\"kind\":\"bug\"", "\"kind\":1")]
    [InlineData("\"kind\":\"bug\"", "\"kind\":0")]
    [InlineData("\"kind\":\"bug\"", "\"kind\":\"1\"")]
    public void Manifest_rejects_integer_kinds(string field, string replacement)
    {
        byte[] json = Encoding.UTF8.GetBytes(Replace(ManifestGolden, field, replacement));
        Assert.Throws<JsonException>(() => BugReportJson.DeserializeManifest(json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BugReportManifest>(json, BugReportJson.Options));
    }

    [Theory]
    [InlineData(",\"entry\":\"clip/001.jpg\"", "")]
    [InlineData("\"entry\":\"clip/001.jpg\"", "\"entry\":null")]
    [InlineData("\"width\":960,", "")]
    public void ClipIndex_rejects_missing_or_null_frame_fields(string field, string replacement)
    {
        byte[] json = Encoding.UTF8.GetBytes(Replace(ClipGolden, field, replacement));
        Assert.Throws<JsonException>(() => BugReportJson.DeserializeClipIndex(json));
    }

    [Fact]
    public void ClipIndex_rejects_null_frames()
        => Assert.Throws<JsonException>(() => BugReportJson.DeserializeClipIndex("{\"frames\":null}"u8));

    [Theory]
    [InlineData("{\"reportId\":\"0192f1c2a3b47c4d8e9f0a1b2c3d4e5f\"}")]
    [InlineData("{\"reportId\":\"0192f1c2a3b47c4d8e9f0a1b2c3d4e5f\",\"reference\":null}")]
    public void IntakeAccepted_rejects_missing_or_null_fields(string input)
        => Assert.Throws<JsonException>(() => BugReportJson.DeserializeIntakeAccepted(Encoding.UTF8.GetBytes(input)));

    [Fact]
    public void RelayError_rejects_a_null_error()
        => Assert.Throws<JsonException>(() => BugReportJson.DeserializeRelayError("{\"error\":null}"u8));

    [Theory]
    [InlineData(",\"sections\":{\"build\":{\"version\":\"1.0.0\"}}", "")]
    [InlineData("{\"build\":{\"version\":\"1.0.0\"}}", "null")]
    [InlineData("\"subject\":\"player-1\",", "")]
    [InlineData("\"formatVersion\":1,", "")]
    public void ServerSnapshot_rejects_missing_or_null_fields(string field, string replacement)
    {
        byte[] json = Encoding.UTF8.GetBytes(Replace(SnapshotJson, field, replacement));
        Assert.Throws<JsonException>(() => BugReportJson.DeserializeServerSnapshot(json));
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"a\":[1,{\"b\":\"\\uD83D\\uDE00\"}]}", true)]
    [InlineData("", false)]
    [InlineData("[1]", false)]
    [InlineData("{} {}", false)]
    [InlineData("{\"a\":1,\"a\":2}", false)]
    [InlineData("{\"a\":\"\\uD800\"}", false)]
    [InlineData("{\"\\uDC00\":1}", false)]
    [InlineData("{\"a\":1} // note", false)]
    public void IsValidSection_matches_the_section_rules(string json, bool valid)
        => Assert.Equal(valid, BugReportJson.IsValidSection(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void IsValidSection_bounds_depth()
    {
        string Nested(int depth) => string.Concat(Enumerable.Repeat("{\"a\":", depth - 1)) + "{}" + new string('}', depth - 1);
        Assert.True(BugReportJson.IsValidSection(Encoding.UTF8.GetBytes(Nested(BugReportLimits.MaxSectionDepth))));
        Assert.False(BugReportJson.IsValidSection(Encoding.UTF8.GetBytes(Nested(BugReportLimits.MaxSectionDepth + 1))));
    }

    [Theory]
    [InlineData("{\"version\":\"1.0.0\"}", "{\"version\":\"\\uD800\"}")]
    [InlineData("{\"version\":\"1.0.0\"}", "{\"\\uDC00\":\"1.0.0\"}")]
    [InlineData("{\"build\":", "{\"b\\uD800\":")]
    [InlineData("\"player-1\"", "\"\\uD800\"")]
    public void ServerSnapshot_rejects_undecodable_strings(string field, string replacement)
    {
        byte[] json = Encoding.UTF8.GetBytes(Replace(SnapshotJson, field, replacement));
        Assert.Throws<JsonException>(() => BugReportJson.DeserializeServerSnapshot(json));
    }

    [Theory]
    [InlineData("{\"subject\":\"player-1\",\"handle\":null}")]
    [InlineData("{\"subject\":\"player-1\",\"displayName\":null,\"handle\":null}")]
    public void ReporterIdentity_rejects_missing_or_null_fields(string input)
        => Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ReporterIdentity>(Encoding.UTF8.GetBytes(input), BugReportJson.Options));

    const string SnapshotJson = "{\"formatVersion\":1,\"reportId\":\"0192f1c2a3b47c4d8e9f0a1b2c3d4e5f\",\"subject\":\"player-1\",\"capturedAt\":\"2026-10-17T03:04:05.678+00:00\",\"sections\":{\"build\":{\"version\":\"1.0.0\"}}}";

    [Fact]
    public void Snapshot_fixture_is_a_valid_document()
        => Assert.Equal("player-1", BugReportJson.DeserializeServerSnapshot(Encoding.UTF8.GetBytes(SnapshotJson)).Subject);

    [Fact]
    public void Duplicate_keys_are_rejected_in_every_wire_document()
    {
        var cases = new (string Document, string Json, Func<byte[], object> Read)[]
        {
            ("manifest", Replace(ManifestGolden, "\"gameId\":\"grimhollow\",", "\"gameId\":\"grimhollow\",\"gameId\":\"other\","),
                json => BugReportJson.DeserializeManifest(json)),
            ("manifest entry", Replace(ManifestGolden, "\"size\":42,", "\"size\":42,\"size\":43,"),
                json => BugReportJson.DeserializeManifest(json)),
            ("clip index", Replace(ClipGolden, "{\"frames\":", "{\"frames\":[],\"frames\":"),
                json => BugReportJson.DeserializeClipIndex(json)),
            ("clip frame", Replace(ClipGolden, "\"width\":960,", "\"width\":960,\"width\":961,"),
                json => BugReportJson.DeserializeClipIndex(json)),
            ("intake accepted", "{\"reportId\":\"" + Id + "\",\"reportId\":\"" + Id + "\",\"reference\":\"R-P3TKJZ\"}",
                json => BugReportJson.DeserializeIntakeAccepted(json)),
            ("relay error", "{\"error\":\"a\",\"error\":\"b\"}", json => BugReportJson.DeserializeRelayError(json)),
            ("server snapshot", Replace(SnapshotJson, "\"subject\":\"player-1\",", "\"subject\":\"player-1\",\"subject\":\"player-2\","),
                json => BugReportJson.DeserializeServerSnapshot(json)),
            ("snapshot section key", Replace(SnapshotJson, "{\"build\":{", "{\"build\":{},\"build\":{"),
                json => BugReportJson.DeserializeServerSnapshot(json)),
            ("snapshot section value", Replace(SnapshotJson, "{\"version\":\"1.0.0\"}", "{\"version\":\"1\",\"version\":\"2\"}"),
                json => BugReportJson.DeserializeServerSnapshot(json)),
            ("reporter identity", "{\"subject\":\"a\",\"subject\":\"b\",\"displayName\":\"x\",\"handle\":null}",
                json => JsonSerializer.Deserialize<ReporterIdentity>(json, BugReportJson.Options)!),
        };
        foreach ((string document, string json, Func<byte[], object> read) in cases)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            Exception? thrown = Record.Exception(() => read(bytes));
            Assert.True(thrown is JsonException, document + " accepted a duplicate key");
        }
    }

    [Fact]
    public void Section_document_options_use_the_shared_bounds()
    {
        Assert.Equal(64, BugReportLimits.MaxSectionDepth);
        Assert.Equal(64, BugReportLimits.MaxCategoryChars);
        Assert.Equal(32, BugReportLimits.MaxGameIdChars);
        Assert.Equal(600000, BugReportLimits.MaxClipOffsetMilliseconds);
        Assert.Equal(60000, BugReportLimits.MaxRenderMilliseconds);
        JsonDocumentOptions options = BugReportJson.SectionDocumentOptions;
        Assert.Equal(BugReportLimits.MaxSectionDepth, options.MaxDepth);
        Assert.False(options.AllowDuplicateProperties);
        Assert.False(options.AllowTrailingCommas);
        Assert.Equal(JsonCommentHandling.Disallow, options.CommentHandling);
    }

    static string Replace(string golden, string field, string replacement)
    {
        Assert.Contains(field, golden, StringComparison.Ordinal);
        return golden.Replace(field, replacement, StringComparison.Ordinal);
    }
}
