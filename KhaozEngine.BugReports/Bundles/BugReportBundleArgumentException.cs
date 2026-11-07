using System;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports;

/// <summary>A structured refusal of a draft field or section. Existing <see cref="ArgumentException"/> catches remain
/// valid, and <see cref="ArgumentException.ParamName"/> is <c>draft</c>.</summary>
public sealed class BugReportBundleArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its required reason, retaining the parameter name.</summary>
    /// <param name="reason">The rule that refused the draft.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    /// <param name="paramName">The refused parameter.</param>
    public BugReportBundleArgumentException(BugReportBundleRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the draft.</summary>
    public BugReportBundleRefusal Reason { get; }

    /// <summary>The refused <see cref="BugReportDraft"/> property, for a reason that names one field or that several
    /// fields share: <c>ReportId</c>, <c>GameId</c>, <c>Category</c>, <c>Description</c> or <c>Sections</c>.</summary>
    public string? FieldName { get; init; }

    /// <summary>The UTC year of the capture time, for
    /// <see cref="BugReportBundleRefusal.CaptureYearOutsideZipRange"/>.</summary>
    public int? ActualYear { get; init; }

    /// <summary>The first year the zip date range holds, for
    /// <see cref="BugReportBundleRefusal.CaptureYearOutsideZipRange"/>.</summary>
    public int? MinimumYear { get; init; }

    /// <summary>The last year the zip date range holds, for
    /// <see cref="BugReportBundleRefusal.CaptureYearOutsideZipRange"/>.</summary>
    public int? MaximumYear { get; init; }

    /// <summary>The refused length in UTF-16 units, for <see cref="BugReportBundleRefusal.CategoryLengthOutsideRange"/>,
    /// or after trailing whitespace is dropped for <see cref="BugReportBundleRefusal.DescriptionTooLong"/>.</summary>
    public int? ActualCharacters { get; init; }

    /// <summary>The shortest accepted length, for <see cref="BugReportBundleRefusal.CategoryLengthOutsideRange"/>.</summary>
    public int? MinimumCharacters { get; init; }

    /// <summary>The longest accepted length, for <see cref="BugReportBundleRefusal.GameIdInvalid"/>,
    /// <see cref="BugReportBundleRefusal.CategoryLengthOutsideRange"/> and
    /// <see cref="BugReportBundleRefusal.DescriptionTooLong"/>.</summary>
    public int? MaximumCharacters { get; init; }

    /// <summary>The undefined kind, for <see cref="BugReportBundleRefusal.ReportKindUndefined"/>.</summary>
    public BugReportKind? ActualKind { get; init; }

    /// <summary>The zero-based position in the section list, for <see cref="BugReportBundleRefusal.SectionMissing"/>,
    /// <see cref="BugReportBundleRefusal.SectionKeyInvalid"/> and
    /// <see cref="BugReportBundleRefusal.SectionKeyDuplicate"/>, where it is the later of the two sections.</summary>
    public int? Index { get; init; }

    /// <summary>The key as the section holds it, for <see cref="BugReportBundleRefusal.SectionKeyDuplicate"/> and
    /// <see cref="BugReportBundleRefusal.SectionJsonInvalid"/>, or for
    /// <see cref="BugReportBundleRefusal.SectionKeyInvalid"/>, where it is null when the key is null.</summary>
    public string? SectionKey { get; init; }

    /// <summary>The running total through the section that crossed the limit, for
    /// <see cref="BugReportBundleRefusal.SectionBytesExceeded"/>. Later sections are not counted.</summary>
    public long? ActualBytes { get; init; }

    /// <summary>The inclusive section byte limit, for <see cref="BugReportBundleRefusal.SectionBytesExceeded"/>.</summary>
    public long? MaximumBytes { get; init; }

    /// <summary>The deepest accepted nesting, for <see cref="BugReportBundleRefusal.SectionJsonInvalid"/>.</summary>
    public int? MaximumDepth { get; init; }
}
