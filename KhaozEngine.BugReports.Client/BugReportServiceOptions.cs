using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Windowing;

namespace KhaozEngine.BugReports.Client;

/// <summary>How a game sets up in-game bug reports: who it is, the trigger key, the categories and the clip.</summary>
public sealed record BugReportServiceOptions
{
    /// <summary>The most category ids a game may list.</summary>
    internal const int MaxCategories = 16;

    /// <summary>
    /// The largest clip budget a report can carry: the bundle limit less the largest PNG screenshot, the section total
    /// and 1 MiB for the manifest and the zip framing. 10 MiB.
    /// </summary>
    internal const int MaxRecordingBytes = (int)(BugReportLimits.MaxBundleBytes - BugReportLimits.MaxPngScreenshotBytes
        - BugReportLimits.MaxSectionBytesTotal - 1024 * 1024);

    /// <summary>
    /// The game id stamped into every bundle: lowercase ASCII letters, digits and hyphens, not starting with a
    /// hyphen, at most 32 characters.
    /// </summary>
    public required string GameId { get; init; }

    /// <summary>The game's name as players know it. Must not be blank.</summary>
    public required string GameDisplayName { get; init; }

    /// <summary>The key bound to the report trigger until the player rebinds it.</summary>
    public Key DefaultKey { get; init; } = Key.F10;

    /// <summary>
    /// The category ids, in display order, 1 to 16 ids of 1 to 64 characters of well-formed text. Null keeps the engine
    /// default list.
    /// </summary>
    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>How the rolling clip records. Its length times its rate is at most 120 frames, and its byte budget is at
    /// most 10 MiB.</summary>
    public FrameHistoryOptions Recording { get; init; } = new();

    /// <summary>Whether the clip records from the start. A settings toggle can change it later.</summary>
    public bool RecordingEnabled { get; init; } = true;

    /// <summary>
    /// The trigger bindings the game saved from <see cref="SaveTriggerBindings"/>, applied over <see cref="DefaultKey"/>.
    /// Text that applies no binding is logged once and leaves <see cref="DefaultKey"/> bound.
    /// </summary>
    public string? TriggerBindingsJson { get; init; }

    /// <summary>Receives the trigger bindings as JSON whenever the player rebinds the trigger.</summary>
    public Action<string>? SaveTriggerBindings { get; init; }

    /// <summary>The session log path <c>SessionLog.Configure</c> returned, when the game keeps one.</summary>
    public string? SessionLogPath { get; init; }

    /// <summary>
    /// Supplies reporter identity terms, such as the current display name and account subject, once per report
    /// on the background finish thread. Must be thread-safe and finish promptly. Null disables identity masking.
    /// Terms are literal, invariant case-insensitive matches replaced by <c>[redacted]</c> in every section's
    /// property names and string values. Null, blank and shorter-than-three-character terms are ignored.
    /// A callback, enumeration or pattern construction failure masks every section string instead.
    /// </summary>
    public Func<IEnumerable<string>>? ReporterIdentityTerms { get; init; }

    /// <summary>Checks every option a report depends on.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or its <see cref="Recording"/> is null.</exception>
    /// <exception cref="ArgumentException">The game id, the display name or a category id is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The category count or a recording option is out of range.</exception>
    internal static void Validate(BugReportServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.GameId is null || !BundleEntryNames.IsValidGameId(options.GameId))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"GameId must match [a-z0-9][a-z0-9-]{{0,{BugReportLimits.MaxGameIdChars - 1}}}."), nameof(options));
        }
        if (string.IsNullOrWhiteSpace(options.GameDisplayName))
            throw new ArgumentException("GameDisplayName must not be blank.", nameof(options));
        if (options.Categories is { } categories) ValidateCategories(categories);
        ArgumentNullException.ThrowIfNull(options.Recording, nameof(options) + "." + nameof(Recording));
        FrameHistoryRecorder.Validate(options.Recording);
        if (options.Recording.MaxBytes > MaxRecordingBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(FrameHistoryOptions.MaxBytes), options.Recording.MaxBytes,
                string.Create(CultureInfo.InvariantCulture,
                    $"Recording.MaxBytes must be at most {MaxRecordingBytes}, so a full clip fits a bundle beside the screenshot and the sections."));
        }
    }

    static void ValidateCategories(IReadOnlyList<string> categories)
    {
        if (categories.Count is < 1 or > MaxCategories)
        {
            throw new ArgumentOutOfRangeException(nameof(Categories), categories.Count, string.Create(
                CultureInfo.InvariantCulture, $"Categories must list 1 to {MaxCategories} ids."));
        }
        foreach (string id in categories)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > BugReportLimits.MaxCategoryChars || !BugReportJson.IsWellFormedText(id))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"Every category id must be 1 to {BugReportLimits.MaxCategoryChars} characters of well-formed text and not blank."),
                    nameof(Categories));
            }
        }
    }
}
