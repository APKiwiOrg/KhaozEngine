using System;
using System.Collections.Generic;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports;

/// <summary>
/// A report the player is still editing: what was captured at the trigger plus the description, category and media
/// toggles. Setters do not validate. <see cref="BugReportBundleWriter.Write"/> checks every field.
/// </summary>
public sealed class BugReportDraft
{
    /// <summary>The engine's default category ids, in display order. <c>Other</c> is the default selection.</summary>
    public static IReadOnlyList<string> DefaultCategories { get; } =
        Array.AsReadOnly(["Gameplay", "Visual", "Audio", "Performance", "Interface", "Other"]);

    /// <summary>Creates a draft from what the trigger captured.</summary>
    /// <param name="reportId">A version 7 report id from <see cref="ReportIds.NewId"/>.</param>
    /// <param name="gameId">
    /// The game id: lowercase ASCII letters, digits and hyphens, not starting with a hyphen, at most
    /// <see cref="BugReportLimits.MaxGameIdChars"/> characters. <see cref="BundleEntryNames.IsValidGameId"/> checks it.
    /// </param>
    /// <param name="kind">Whether the player filed this or a crash produced it.</param>
    /// <param name="capturedAt">
    /// When the report was triggered. The manifest keeps it exactly, in UTC. It also stamps every zip entry, as the
    /// UTC time with the zip format's 2 second precision, so it must fall in 1980 to 2107 UTC.
    /// </param>
    /// <param name="sections">Finished sections from <see cref="BugReportSectionSet.Finish"/>, in registration order.</param>
    /// <param name="screenshot">The trigger frame's screenshot, or null when none was taken.</param>
    /// <param name="clip">The frozen clip, or null when recording was off.</param>
    public BugReportDraft(string reportId, string gameId, BugReportKind kind, DateTimeOffset capturedAt,
        IReadOnlyList<CapturedSection> sections, ReportImage? screenshot, ReportClip? clip)
    {
        ReportId = reportId;
        GameId = gameId;
        Kind = kind;
        CapturedAt = capturedAt;
        Sections = sections;
        Screenshot = screenshot;
        Clip = clip;
    }

    public string ReportId { get; }

    public string GameId { get; }

    public BugReportKind Kind { get; }

    public DateTimeOffset CapturedAt { get; }

    public IReadOnlyList<CapturedSection> Sections { get; }

    public ReportImage? Screenshot { get; }

    public ReportClip? Clip { get; }

    /// <summary>The player's description. Trailing whitespace is dropped when written. Must not be blank.</summary>
    public string Description { get; set; } = "";

    /// <summary>The category id, 1 to <see cref="BugReportLimits.MaxCategoryChars"/> characters.</summary>
    public string Category { get; set; } = "Other";

    /// <summary>Whether <see cref="Screenshot"/> goes into the bundle.</summary>
    public bool IncludeScreenshot { get; set; } = true;

    /// <summary>Whether <see cref="Clip"/> goes into the bundle.</summary>
    public bool IncludeClip { get; set; } = true;
}
