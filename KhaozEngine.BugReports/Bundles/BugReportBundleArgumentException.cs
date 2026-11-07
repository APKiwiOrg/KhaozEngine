using System;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports;

/// <summary>A structured refusal of a draft field, section, screenshot or clip. Existing <see cref="ArgumentException"/>
/// catches remain valid, and <see cref="ArgumentException.ParamName"/> is <c>draft</c>.</summary>
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
    /// <see cref="BugReportBundleRefusal.SectionKeyDuplicate"/>, where it is the later of the two sections. The
    /// zero-based position in the clip frame list, for <see cref="BugReportBundleRefusal.ClipFrameMissing"/>,
    /// <see cref="BugReportBundleRefusal.ClipOffsetDecreased"/>, <see cref="BugReportBundleRefusal.ClipOffsetExceeded"/>,
    /// <see cref="BugReportBundleRefusal.RenderTimeNonfinite"/> and
    /// <see cref="BugReportBundleRefusal.RenderTimeOutsideRange"/>.</summary>
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

    /// <summary>The screenshot's media type, for <see cref="BugReportBundleRefusal.ScreenshotMediaUnsupported"/>, where it
    /// is null when the type is null, or the media type the header was read as, for
    /// <see cref="BugReportBundleRefusal.ImageHeaderUnreadable"/>.</summary>
    public string? MediaType { get; init; }

    /// <summary>The bundle entry the image would be written as, such as <c>screenshot.png</c> or <c>clip/002.jpg</c>, for
    /// <see cref="BugReportBundleRefusal.ImageDimensionsOutsideRange"/>,
    /// <see cref="BugReportBundleRefusal.ImageHeaderUnreadable"/> and
    /// <see cref="BugReportBundleRefusal.ImageHeaderDimensionsMismatch"/>.</summary>
    public string? EntryName { get; init; }

    /// <summary>The recorded width in pixels, for <see cref="BugReportBundleRefusal.ImageDimensionsOutsideRange"/> and
    /// <see cref="BugReportBundleRefusal.ImageHeaderDimensionsMismatch"/>.</summary>
    public int? Width { get; init; }

    /// <summary>The recorded height in pixels, for <see cref="BugReportBundleRefusal.ImageDimensionsOutsideRange"/> and
    /// <see cref="BugReportBundleRefusal.ImageHeaderDimensionsMismatch"/>.</summary>
    public int? Height { get; init; }

    /// <summary>The shortest accepted edge, for <see cref="BugReportBundleRefusal.ImageDimensionsOutsideRange"/>.</summary>
    public int? MinimumPixels { get; init; }

    /// <summary>The longest accepted edge, for <see cref="BugReportBundleRefusal.ImageDimensionsOutsideRange"/>.</summary>
    public int? MaximumPixels { get; init; }

    /// <summary>The width the image header encodes, for
    /// <see cref="BugReportBundleRefusal.ImageHeaderDimensionsMismatch"/>.</summary>
    public int? HeaderWidth { get; init; }

    /// <summary>The height the image header encodes, for
    /// <see cref="BugReportBundleRefusal.ImageHeaderDimensionsMismatch"/>.</summary>
    public int? HeaderHeight { get; init; }

    /// <summary>The frames the clip holds, for <see cref="BugReportBundleRefusal.ClipFrameCountExceeded"/>.</summary>
    public int? ActualCount { get; init; }

    /// <summary>The inclusive clip frame limit, for <see cref="BugReportBundleRefusal.ClipFrameCountExceeded"/>.</summary>
    public int? MaximumCount { get; init; }

    /// <summary>The frame's offset into the clip, for <see cref="BugReportBundleRefusal.ClipOffsetDecreased"/> and
    /// <see cref="BugReportBundleRefusal.ClipOffsetExceeded"/>, or its render time, for
    /// <see cref="BugReportBundleRefusal.RenderTimeNonfinite"/> and
    /// <see cref="BugReportBundleRefusal.RenderTimeOutsideRange"/>.</summary>
    public double? ActualMilliseconds { get; init; }

    /// <summary>The earliest accepted offset, the offset of the frame ahead or zero for the first frame, for
    /// <see cref="BugReportBundleRefusal.ClipOffsetDecreased"/>, or the shortest accepted render time, for
    /// <see cref="BugReportBundleRefusal.RenderTimeOutsideRange"/>.</summary>
    public double? MinimumMilliseconds { get; init; }

    /// <summary>The inclusive offset limit, for <see cref="BugReportBundleRefusal.ClipOffsetExceeded"/>, or the inclusive
    /// render time limit, for <see cref="BugReportBundleRefusal.RenderTimeOutsideRange"/>.</summary>
    public double? MaximumMilliseconds { get; init; }
}
