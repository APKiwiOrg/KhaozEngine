namespace KhaozEngine.BugReports;

/// <summary>Rules that refuse a draft <see cref="BugReportBundleWriter.Write"/> cannot turn into a valid bundle.</summary>
public enum BugReportBundleRefusal
{
    // Draft fields.

    /// <summary>Refuses a report id that is not a canonical version 7 id, null included.</summary>
    ReportIdInvalid,

    /// <summary>Refuses a game id outside its pattern and length limit, null included.</summary>
    GameIdInvalid,

    /// <summary>Refuses a report kind that is not a defined <see cref="Contracts.BugReportKind"/>.</summary>
    ReportKindUndefined,

    /// <summary>Refuses a capture time whose UTC year falls outside the zip date range.</summary>
    CaptureYearOutsideZipRange,

    /// <summary>Refuses a null category.</summary>
    CategoryMissing,

    /// <summary>Refuses a category outside its length range, empty included.</summary>
    CategoryLengthOutsideRange,

    /// <summary>Refuses a description that is null, empty or only whitespace.</summary>
    DescriptionBlank,

    /// <summary>Refuses a description over its limit once trailing whitespace is dropped.</summary>
    DescriptionTooLong,

    /// <summary>Refuses a category or description holding an unpaired surrogate.</summary>
    TextMalformed,

    // Sections.

    /// <summary>Refuses a null section list.</summary>
    SectionListMissing,

    /// <summary>Refuses a null entry in the section list.</summary>
    SectionMissing,

    /// <summary>Refuses a section key outside its pattern and length limit, null included.</summary>
    SectionKeyInvalid,

    /// <summary>Refuses a section key an earlier section already uses.</summary>
    SectionKeyDuplicate,

    /// <summary>Refuses sections whose running total passes the section byte limit.</summary>
    SectionBytesExceeded,

    /// <summary>Refuses a section that is not one JSON object within the depth limit, with unique property names and
    /// decodable strings.</summary>
    SectionJsonInvalid,

    // Screenshot and clip media.

    /// <summary>Refuses a screenshot whose media type is not <c>image/png</c> or <c>image/jpeg</c>, null included.</summary>
    ScreenshotMediaUnsupported,

    /// <summary>Refuses a screenshot or clip frame whose recorded width or height is outside the edge range.</summary>
    ImageDimensionsOutsideRange,

    /// <summary>Refuses a screenshot or clip frame whose bytes do not start with a readable header of its media type.</summary>
    ImageHeaderUnreadable,

    /// <summary>Refuses a screenshot or clip frame whose header size differs from its recorded size.</summary>
    ImageHeaderDimensionsMismatch,

    /// <summary>Refuses a clip whose frame list is null.</summary>
    ClipFrameListMissing,

    /// <summary>Refuses a clip holding more frames than the clip frame limit.</summary>
    ClipFrameCountExceeded,

    /// <summary>Refuses a null entry in the clip frame list.</summary>
    ClipFrameMissing,

    /// <summary>Refuses a clip frame that starts before the frame ahead of it, or before the clip.</summary>
    ClipOffsetDecreased,

    /// <summary>Refuses a clip frame that starts past the clip offset limit.</summary>
    ClipOffsetExceeded,

    /// <summary>Refuses a clip frame whose render time is NaN or infinite.</summary>
    RenderTimeNonfinite,

    /// <summary>Refuses a clip frame whose finite render time is negative or over the render time limit.</summary>
    RenderTimeOutsideRange,

    // Bundle limits, thrown as BugReportBundleLimitException.

    /// <summary>Refuses a bundle that would need more zip entries than the entry limit, the manifest included.</summary>
    EntryCountExceeded,

    /// <summary>Refuses a bundle whose entries after the manifest total more bytes than the bundle byte limit.</summary>
    PayloadBytesExceeded,

    /// <summary>Refuses a bundle whose written archive is longer than the bundle byte limit.</summary>
    ArchiveBytesExceeded,
}
