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
}
