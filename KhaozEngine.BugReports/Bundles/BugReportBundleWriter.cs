using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports;

/// <summary>
/// Writes a draft as a version 1 report bundle: a stored zip with <c>manifest.json</c> first, then sections in
/// draft order, the screenshot, <c>clip/index.json</c> and the clip frames. Before anything is serialized, every
/// field is checked against the rules <see cref="BundleValidator"/> applies, through the same Contracts helpers:
/// <see cref="BundleEntryNames"/>, <see cref="BugReportLimits"/>, <see cref="BugReportJson.SectionDocumentOptions"/>
/// and <see cref="ImageHeaders"/>, which also checks each image header against its record. A bundle that is
/// written therefore passes the relay's validation. Works in memory only.
/// </summary>
public static class BugReportBundleWriter
{
    const int MinZipYear = 1980;
    const int MaxZipYear = 2107;

    // Stored zip framing: a local header and a central record per entry, each carrying the name, then the end record.
    const int LocalHeaderBytes = 30;
    const int CentralRecordBytes = 46;
    const int EndRecordBytes = 22;

    /// <summary>Writes the bundle and returns its bytes.</summary>
    /// <exception cref="ArgumentException">A draft field is null or outside its limit.</exception>
    /// <exception cref="InvalidOperationException">The bundle would exceed 128 entries or 16 MiB.</exception>
    public static byte[] Write(BugReportDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        string description = CheckFields(draft);
        List<(string Name, ReadOnlyMemory<byte> Data)> payloads = CollectPayloads(draft);

        if (payloads.Count + 1 > BugReportLimits.MaxEntries)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"The report needs {payloads.Count + 1} zip entries and a bundle holds at most {BugReportLimits.MaxEntries}."));
        long payloadBytes = 0;
        foreach ((_, ReadOnlyMemory<byte> data) in payloads) payloadBytes += data.Length;
        if (payloadBytes > BugReportLimits.MaxBundleBytes) throw TooLarge(payloadBytes);

        var listed = new List<BundleEntry>(payloads.Count);
        foreach ((string name, ReadOnlyMemory<byte> data) in payloads)
            listed.Add(new BundleEntry(name, data.Length, Convert.ToHexStringLower(SHA256.HashData(data.Span))));
        byte[] manifest = BugReportJson.SerializeManifest(new BugReportManifest(BugReportFormat.Version, draft.ReportId,
            draft.GameId, draft.Kind, draft.Category, description, draft.CapturedAt, listed));

        DateTimeOffset stamp = draft.CapturedAt.ToUniversalTime();
        var output = new MemoryStream(ArchiveCapacity(manifest.Length, payloads));
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, BundleEntryNames.Manifest, manifest, stamp);
            foreach ((string name, ReadOnlyMemory<byte> data) in payloads) AddEntry(archive, name, data, stamp);
        }
        if (output.Length > BugReportLimits.MaxBundleBytes) throw TooLarge(output.Length);
        return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
    }

    // The exact stored archive length when the zip writer adds no extra fields, so the buffer never regrows and is
    // returned without a copy. A larger archive still works, at the cost of one regrowth and a copy.
    static int ArchiveCapacity(int manifestBytes, List<(string Name, ReadOnlyMemory<byte> Data)> payloads)
    {
        long total = EndRecordBytes + Framing(BundleEntryNames.Manifest) + manifestBytes;
        foreach ((string name, ReadOnlyMemory<byte> data) in payloads) total += Framing(name) + data.Length;
        return (int)Math.Min(total, Array.MaxLength);
    }

    static int Framing(string name) => LocalHeaderBytes + CentralRecordBytes + 2 * name.Length;

    // Returns the description as written, with trailing whitespace dropped.
    static string CheckFields(BugReportDraft draft)
    {
        if (!ReportIds.IsValid(draft.ReportId))
            throw Invalid("The report id must be a canonical version 7 id from ReportIds.NewId.");
        if (!BundleEntryNames.IsValidGameId(draft.GameId))
            throw Invalid("The game id must match [a-z0-9][a-z0-9-]{0,31}.");
        if (!Enum.IsDefined(draft.Kind))
            throw Invalid("The report kind is not a defined BugReportKind.");
        int year = draft.CapturedAt.UtcDateTime.Year;
        if (year is < MinZipYear or > MaxZipYear)
            throw Invalid("The capture time must fall in the zip date range, 1980 to 2107 UTC.");
        if (draft.Category is null || draft.Category.Length is < 1 or > BugReportLimits.MaxCategoryChars || !IsWellFormed(draft.Category))
            throw Invalid("The category must be 1 to 64 characters of well-formed text.");

        string description = draft.Description?.TrimEnd() ?? "";
        if (description.Length == 0)
            throw Invalid("The description must not be blank.");
        if (description.Length > BugReportLimits.MaxDescriptionChars)
            throw Invalid(string.Create(CultureInfo.InvariantCulture,
                $"The description holds {description.Length} characters and the limit is {BugReportLimits.MaxDescriptionChars}."));
        if (!IsWellFormed(description))
            throw Invalid("The description contains an unpaired surrogate.");

        CheckSections(draft.Sections);
        return description;
    }

    static void CheckSections(IReadOnlyList<CapturedSection> sections)
    {
        if (sections is null) throw Invalid("The section list must not be null.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (CapturedSection? section in sections)
        {
            if (section is null) throw Invalid("The section list must not hold a null section.");
            string key = section.Key;
            if (!BundleEntryNames.IsValidSectionKey(key))
                throw Invalid("Section key \"" + key + "\" must match [a-z0-9][a-z0-9._-]{0,47}.");
            if (!keys.Add(key)) throw Invalid("Section key \"" + key + "\" appears twice.");
            total += section.Json.Length;
            if (total > BugReportLimits.MaxSectionBytesTotal)
                throw Invalid("The sections total more than " + BugReportLimits.MaxSectionBytesTotal + " bytes.");
            if (!IsJsonObject(section.Json))
                throw Invalid("Section \"" + key + "\" is not one JSON object with unique property names.");
        }
    }

    static bool IsJsonObject(ReadOnlyMemory<byte> json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, BugReportJson.SectionDocumentOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Names and bytes of every entry after the manifest, in zip order. Checks media fields on the way.
    static List<(string Name, ReadOnlyMemory<byte> Data)> CollectPayloads(BugReportDraft draft)
    {
        var payloads = new List<(string, ReadOnlyMemory<byte>)>();
        foreach (CapturedSection section in draft.Sections)
            payloads.Add((BundleEntryNames.Section(section.Key), section.Json));

        if (draft.IncludeScreenshot && draft.Screenshot is { } screenshot)
        {
            string name = screenshot.MediaType switch
            {
                ImageHeaders.PngMediaType => BundleEntryNames.ScreenshotPng,
                ImageHeaders.JpegMediaType => BundleEntryNames.ScreenshotJpeg,
                _ => throw Invalid("Screenshot media type \"" + screenshot.MediaType + "\" must be image/png or image/jpeg."),
            };
            if (!IsValidEdge(screenshot.Width) || !IsValidEdge(screenshot.Height))
                throw Invalid("Screenshot edges must be 1 to " + BugReportLimits.MaxScreenshotLongEdge + " pixels.");
            CheckHeader(name, screenshot.MediaType, screenshot.Bytes, screenshot.Width, screenshot.Height);
            payloads.Add((name, screenshot.Bytes));
        }

        if (draft.IncludeClip && draft.Clip is { } clip)
        {
            if (clip.Frames is null) throw Invalid("The clip frame list must not be null.");
            if (clip.Frames.Count > BundleEntryNames.MaxClipFrames)
                throw Invalid(string.Create(CultureInfo.InvariantCulture,
                    $"The clip holds {clip.Frames.Count} frames and the limit is {BundleEntryNames.MaxClipFrames}."));
            if (clip.Frames.Count > 0) AddClip(payloads, clip.Frames);
        }
        return payloads;
    }

    static void AddClip(List<(string, ReadOnlyMemory<byte>)> payloads, IReadOnlyList<ReportClipFrame> frames)
    {
        var rows = new ClipIndexFrame[frames.Count];
        double previousOffset = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            ReportClipFrame? frame = frames[i];
            if (frame is null) throw Invalid("The clip must not hold a null frame.");
            double offset = frame.Offset.TotalMilliseconds;
            if (offset < previousOffset)
                throw Invalid("Clip frame " + i + " starts before the frame ahead of it or before the clip.");
            if (!double.IsFinite(frame.RenderMilliseconds) || frame.RenderMilliseconds < 0)
                throw Invalid("Clip frame " + i + " needs a finite render time of zero or more.");
            if (!IsValidEdge(frame.Width) || !IsValidEdge(frame.Height))
                throw Invalid("Clip frame " + i + " edges must be 1 to " + BugReportLimits.MaxScreenshotLongEdge + " pixels.");
            string entry = BundleEntryNames.ClipFrame(i);
            CheckHeader(entry, ImageHeaders.JpegMediaType, frame.Jpeg, frame.Width, frame.Height);
            previousOffset = offset;
            rows[i] = new ClipIndexFrame(offset, frame.RenderMilliseconds, frame.Width, frame.Height, entry);
        }

        payloads.Add((BundleEntryNames.ClipIndex, BugReportJson.SerializeClipIndex(new ClipIndex(rows))));
        for (int i = 0; i < frames.Count; i++) payloads.Add((rows[i].Entry, frames[i].Jpeg));
    }

    static void AddEntry(ZipArchive archive, string name, ReadOnlyMemory<byte> data, DateTimeOffset stamp)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = stamp;
        using Stream stream = entry.Open();
        stream.Write(data.Span);
    }

    // The validator reads the size from the encoded header, so the record must agree with it.
    static void CheckHeader(string entry, string mediaType, ReadOnlyMemory<byte> bytes, int width, int height)
    {
        if (!ImageHeaders.TryReadSize(mediaType, bytes.Span, out int headerWidth, out int headerHeight))
            throw Invalid(entry + " does not start with a readable " + mediaType + " header.");
        if (headerWidth != width || headerHeight != height)
            throw Invalid(string.Create(CultureInfo.InvariantCulture,
                $"{entry} has a {headerWidth}x{headerHeight} header but its record says {width}x{height}."));
    }

    static bool IsValidEdge(int edge) => edge is >= 1 and <= BugReportLimits.MaxScreenshotLongEdge;

    // An unpaired surrogate cannot round-trip through the manifest's JSON, so it is refused here.
    static bool IsWellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 == text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
            i++;
        }
        return true;
    }

    static ArgumentException Invalid(string message) => new(message, "draft");

    static InvalidOperationException TooLarge(long bytes) => new(string.Create(CultureInfo.InvariantCulture,
        $"The report needs {bytes} bytes and a bundle holds at most {BugReportLimits.MaxBundleBytes}."));
}
