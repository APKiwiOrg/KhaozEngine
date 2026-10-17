using System;
using System.Collections.Generic;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// Writes a report's bundle so media size never loses the report. The clip is fitted first: the oldest frames go until
/// the entry count and the estimated payload fit <see cref="BugReportLimits"/>. When the writer still refuses a size
/// limit, it retries once without the clip, then once with the sections only.
/// </summary>
internal static class ReportBundleBuilder
{
    /// <summary>What the estimate sets aside for the manifest, the clip index and the zip framing.</summary>
    internal const long OverheadBytes = 64 * 1024;

    /// <summary>The player's text and choices for one bundle.</summary>
    internal sealed record Request(string ReportId, string GameId, DateTimeOffset CapturedAt,
        IReadOnlyList<CapturedSection> Sections, string Description, string Category, ReportImage? Screenshot, ReportClip? Clip);

    /// <summary>Writes the bundle with as much of the requested media as fits.</summary>
    /// <exception cref="ArgumentException">A field is invalid. See <see cref="BugReportBundleWriter.Write"/>.</exception>
    /// <exception cref="InvalidOperationException">Even the sections alone exceed a bundle limit.</exception>
    internal static byte[] Write(Request request)
    {
        ReportClip? clip = request.Clip is { } requested ? FitClip(requested, request.Sections, request.Screenshot) : null;
        var attempts = new List<(ReportImage? Screenshot, ReportClip? Clip)> { (request.Screenshot, clip) };
        if (clip is not null) attempts.Add((request.Screenshot, null));
        if (request.Screenshot is not null) attempts.Add((null, null));

        for (int i = 0; ; i++)
        {
            (ReportImage? screenshot, ReportClip? media) = attempts[i];
            try
            {
                return BugReportBundleWriter.Write(Draft(request, screenshot, media));
            }
            catch (InvalidOperationException exception) when (i + 1 < attempts.Count)
            {
                Log.Warn("Bug report " + request.ReportId + " exceeded a bundle limit and is retried with less media.", exception);
            }
        }
    }

    /// <summary>
    /// The newest frames of <paramref name="clip"/> that fit beside the manifest, the sections, the screenshot and the
    /// clip index, rebased so the first kept frame starts at zero. Null when no frame fits.
    /// </summary>
    internal static ReportClip? FitClip(ReportClip clip, IReadOnlyList<CapturedSection> sections, ReportImage? screenshot)
    {
        if (clip.Frames is not { Count: > 0 } frames) return clip;
        int entries = 1 + sections.Count + (screenshot is null ? 0 : 1) + 1;
        long bytes = OverheadBytes + (screenshot?.Bytes.Length ?? 0);
        foreach (CapturedSection section in sections) bytes += section.Json.Length;

        int first = Math.Max(0, frames.Count - (BugReportLimits.MaxEntries - entries));
        long frameBytes = 0;
        for (int i = first; i < frames.Count; i++) frameBytes += frames[i].Jpeg.Length;
        while (first < frames.Count && bytes + frameBytes > BugReportLimits.MaxBundleBytes)
        {
            frameBytes -= frames[first].Jpeg.Length;
            first++;
        }
        if (first == 0) return clip;
        if (first == frames.Count) return null;

        TimeSpan start = frames[first].Offset;
        var kept = new ReportClipFrame[frames.Count - first];
        for (int i = 0; i < kept.Length; i++) kept[i] = frames[first + i] with { Offset = frames[first + i].Offset - start };
        return new ReportClip(kept);
    }

    static BugReportDraft Draft(Request request, ReportImage? screenshot, ReportClip? clip) =>
        new(request.ReportId, request.GameId, BugReportKind.Bug, request.CapturedAt, request.Sections, screenshot, clip)
        {
            Description = request.Description,
            Category = request.Category,
            IncludeScreenshot = screenshot is not null,
            IncludeClip = clip is not null,
        };
}
