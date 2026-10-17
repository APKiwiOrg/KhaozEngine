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
/// draft order, the screenshot, <c>clip/index.json</c> and the clip frames. Every field is checked against the
/// limits <see cref="BundleValidator"/> enforces before anything is serialized, so a bundle that is written
/// passes the relay's validation. Works in memory only.
/// </summary>
public static class BugReportBundleWriter
{
    // The validator's own bounds for fields BugReportLimits does not name.
    const int MaxCategoryLength = 64;
    const int MaxGameIdLength = 32;
    const int MaxSectionDepth = 64;
    const int MinZipYear = 1980;
    const int MaxZipYear = 2107;

    const string PngMediaType = "image/png";
    const string JpegMediaType = "image/jpeg";

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
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, BundleEntryNames.Manifest, manifest, stamp);
            foreach ((string name, ReadOnlyMemory<byte> data) in payloads) AddEntry(archive, name, data, stamp);
        }
        if (output.Length > BugReportLimits.MaxBundleBytes) throw TooLarge(output.Length);
        return output.ToArray();
    }

    // Returns the description as written, with trailing whitespace dropped.
    static string CheckFields(BugReportDraft draft)
    {
        if (!ReportIds.IsValid(draft.ReportId))
            throw Invalid("The report id must be a canonical version 7 id from ReportIds.NewId.");
        if (!IsValidGameId(draft.GameId))
            throw Invalid("The game id must match [a-z0-9][a-z0-9-]{0,31}.");
        if (!Enum.IsDefined(draft.Kind))
            throw Invalid("The report kind is not a defined BugReportKind.");
        int year = draft.CapturedAt.UtcDateTime.Year;
        if (year is < MinZipYear or > MaxZipYear)
            throw Invalid("The capture time must fall in the zip date range, 1980 to 2107 UTC.");
        if (draft.Category is null || draft.Category.Length is < 1 or > MaxCategoryLength || !IsWellFormed(draft.Category))
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
        var options = new JsonDocumentOptions { MaxDepth = MaxSectionDepth, AllowDuplicateProperties = false };
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
            if (!IsJsonObject(section.Json, options))
                throw Invalid("Section \"" + key + "\" is not one JSON object with unique property names.");
        }
    }

    static bool IsJsonObject(ReadOnlyMemory<byte> json, JsonDocumentOptions options)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, options);
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
                PngMediaType => BundleEntryNames.ScreenshotPng,
                JpegMediaType => BundleEntryNames.ScreenshotJpeg,
                _ => throw Invalid("Screenshot media type \"" + screenshot.MediaType + "\" must be image/png or image/jpeg."),
            };
            if (!IsValidEdge(screenshot.Width) || !IsValidEdge(screenshot.Height))
                throw Invalid("Screenshot edges must be 1 to " + BugReportLimits.MaxScreenshotLongEdge + " pixels.");
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
            previousOffset = offset;
            rows[i] = new ClipIndexFrame(offset, frame.RenderMilliseconds, frame.Width, frame.Height, BundleEntryNames.ClipFrame(i));
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

    static bool IsValidGameId(string gameId)
    {
        if (gameId is null || gameId.Length is < 1 or > MaxGameIdLength) return false;
        if (gameId[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9')) return false;
        foreach (char c in gameId)
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return false;
        return true;
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
