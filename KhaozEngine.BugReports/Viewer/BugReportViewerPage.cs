using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports;

/// <summary>
/// Renders the frame viewer: one self-contained HTML page with the screenshot, a clip player, a frame time graph with
/// hitches marked, the sections and a log tail. Media load by relative path from the folder beside the page.
/// </summary>
/// <remarks>
/// Every byte of report data is treated as untrusted, because a relay renders this page from uploaded bundles. The
/// data sits in one <c>application/json</c> block that is written token by token and has every <c>&lt;</c> escaped,
/// so it cannot end its element. The page script is fixed template text allowed by a CSP hash, nothing loads from
/// another origin, and all text reaches the page through <c>textContent</c>.
/// </remarks>
public static class BugReportViewerPage
{
    const string ResourceName = "KhaozEngine.BugReports.Viewer.html";
    const string UnreadableSection = "unreadable section";

    // A server snapshot nests each section two levels down, under its sections object and the section key.
    const int SnapshotDepth = BugReportLimits.MaxSectionDepth + 2;

    // The relaxed encoder leaves '<' literal, and Render then replaces every '<' in the data with <. JSON can
    // only hold '<' inside strings, so the replacement keeps the JSON equal and leaves nothing the HTML parser can
    // read as a tag, a closing tag or a comment opener inside the data block. No other character matters there.
    static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static readonly string Template = LoadTemplate();
    static readonly string ScriptHash = ElementHash("script");
    static readonly string StyleHash = ElementHash("style");

    /// <summary>Renders <paramref name="model"/> as a complete HTML document.</summary>
    /// <remarks>
    /// A section, or the server snapshot, that is not a readable JSON object shows as
    /// <c>{"error":"unreadable section"}</c> and the rest of the page still renders.
    /// </remarks>
    /// <exception cref="ArgumentException">The manifest's report id is not a canonical version 7 id.</exception>
    public static string Render(BugReportViewerModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(model.Manifest);
        string title = WebUtility.HtmlEncode("Bug report " + ReportIds.ReferenceCode(model.Manifest.ReportId));
        string data = ReportJson(model).Replace("<", "\\u003c", StringComparison.Ordinal);

        // The data goes in last, so placeholder text inside it is never replaced.
        return Template
            .Replace("{{TITLE}}", title, StringComparison.Ordinal)
            .Replace("{{SCRIPT_HASH}}", ScriptHash, StringComparison.Ordinal)
            .Replace("{{STYLE_HASH}}", StyleHash, StringComparison.Ordinal)
            .Replace("{{REPORT_JSON}}", data, StringComparison.Ordinal);
    }

    static string ReportJson(BugReportViewerModel model)
    {
        BugReportManifest manifest = model.Manifest;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("manifest");
            Copy(BugReportJson.SerializeManifest(manifest), BugReportLimits.MaxSectionDepth, writer);
            writer.WritePropertyName("clip");
            if (model.Clip is null) writer.WriteNullValue();
            else Copy(BugReportJson.SerializeClipIndex(model.Clip), BugReportLimits.MaxSectionDepth, writer);

            (double median, IReadOnlyList<int> hitches) = model.Clip is null ? (0, []) : ClipHitches.Find(model.Clip.Frames);
            writer.WriteNumber("medianRenderMilliseconds", median);
            writer.WriteStartArray("hitches");
            foreach (int hitch in hitches) writer.WriteNumberValue(hitch);
            writer.WriteEndArray();

            writer.WriteStartObject("media");
            writer.WriteString("screenshot", Lists(manifest, BundleEntryNames.ScreenshotPng) ? BundleEntryNames.ScreenshotPng
                : Lists(manifest, BundleEntryNames.ScreenshotJpeg) ? BundleEntryNames.ScreenshotJpeg : null);
            writer.WriteString("video", Lists(manifest, BundleEntryNames.ClipMp4) ? BundleEntryNames.ClipMp4 : null);
            writer.WriteEndObject();

            writer.WriteStartObject("sections");
            foreach (CapturedSection section in model.Sections)
            {
                writer.WritePropertyName(section.Key);
                CopyOrError(section.Json.Span, BugReportLimits.MaxSectionDepth, writer);
            }
            writer.WriteEndObject();

            writer.WritePropertyName("reporter");
            if (model.Reporter is null) writer.WriteNullValue();
            else Copy(JsonSerializer.SerializeToUtf8Bytes(model.Reporter, BugReportJson.Options), BugReportLimits.MaxSectionDepth, writer);

            writer.WritePropertyName("server");
            if (model.ServerSnapshotJson is not { } server) writer.WriteNullValue();
            else CopyOrError(server.Span, SnapshotDepth, writer);

            writer.WriteStartArray("logTail");
            foreach (string line in model.LogTail) writer.WriteStringValue(line);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static bool Lists(BugReportManifest manifest, string name)
    {
        foreach (BundleEntry entry in manifest.Entries)
            if (entry.Name == name) return true;
        return false;
    }

    // Checks the whole document first, so a failure never leaves half an object in the page data.
    static void CopyOrError(ReadOnlySpan<byte> json, int maxDepth, Utf8JsonWriter writer)
    {
        try
        {
            Copy(json, maxDepth, writer: null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            writer.WriteStartObject();
            writer.WriteString("error", UnreadableSection);
            writer.WriteEndObject();
            return;
        }
        Copy(json, maxDepth, writer);
    }

    // Rewrites one JSON object token by token. Strings and property names are decoded and written again, so raw
    // input bytes never reach the page. Numbers keep the text the reader validated, which keeps 64-bit integers
    // exact. With no writer it only reads, which throws JsonException for malformed JSON and
    // InvalidOperationException for a string that does not decode.
    static void Copy(ReadOnlySpan<byte> json, int maxDepth, Utf8JsonWriter? writer)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = maxDepth });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a JSON object.");
        do
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: writer?.WriteStartObject(); break;
                case JsonTokenType.EndObject: writer?.WriteEndObject(); break;
                case JsonTokenType.StartArray: writer?.WriteStartArray(); break;
                case JsonTokenType.EndArray: writer?.WriteEndArray(); break;
                case JsonTokenType.PropertyName:
                    string name = reader.GetString()!;
                    writer?.WritePropertyName(name);
                    break;
                case JsonTokenType.String:
                    string value = reader.GetString()!;
                    writer?.WriteStringValue(value);
                    break;
                case JsonTokenType.Number: writer?.WriteRawValue(reader.ValueSpan); break;
                case JsonTokenType.True: writer?.WriteBooleanValue(true); break;
                case JsonTokenType.False: writer?.WriteBooleanValue(false); break;
                case JsonTokenType.Null: writer?.WriteNullValue(); break;
                default: throw new JsonException("Unexpected JSON token " + reader.TokenType + ".");
            }
        }
        while (reader.Read());
    }

    // Line endings are normalised because the HTML parser turns CRLF into LF before the browser hashes a script.
    static string LoadTemplate()
    {
        using Stream stream = typeof(BugReportViewerPage).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The viewer template " + ResourceName + " is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    // The base64 SHA-256 of the one attribute-free element with this tag, as a CSP hash source needs it.
    static string ElementHash(string tag)
    {
        string open = "<" + tag + ">";
        int start = Template.IndexOf(open, StringComparison.Ordinal);
        if (start < 0 || Template.IndexOf(open, start + open.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The viewer template must hold exactly one " + open + " element.");
        start += open.Length;
        int end = Template.IndexOf("</" + tag + ">", start, StringComparison.Ordinal);
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Template[start..end])));
    }
}
