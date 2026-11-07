namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Rules this package refuses, carried as <see cref="ContentAuthoringException.Refusal"/> with the identifiers
/// each member names. The legacy <see cref="ContentAuthoringException.Reason"/> token is set beside it.
/// </summary>
public enum ContentAuthoringRefusal
{
    // Bundle document shape, read by ContentBundleJson. Each carries the bundle-format token.

    /// <summary>Refuses text that is not a well-formed JSON document (comments and trailing commas are accepted).
    /// The reader's <c>JsonException</c> is the inner exception, and <c>JsonPath</c>, <c>LineNumber</c> and
    /// <c>BytePositionInLine</c> repeat what it reports, each null when it reports none.</summary>
    BundleJsonMalformed,

    /// <summary>Refuses a document whose root is not a JSON object. Carries <c>JsonPath</c> <c>$</c> and
    /// <c>ActualJsonKind</c>.</summary>
    BundleRootObjectRequired,

    /// <summary>Refuses a declared format version this build neither reads nor writes. Carries
    /// <c>ActualFormatVersion</c> and <c>SupportedFormatVersions</c>.</summary>
    BundleVersionUnsupported,

    /// <summary>Refuses a format 1 document carrying a <c>text</c> member, which format 1 never has. Carries
    /// <c>JsonPath</c> <c>$.text</c> and <c>ActualFormatVersion</c>.</summary>
    BundleUnexpectedTextSection,

    /// <summary>Refuses writing a format 2 bundle that has lost its text section. Carries
    /// <c>ActualFormatVersion</c>.</summary>
    BundleTextSectionMissing,

    /// <summary>Refuses a document missing a member the format requires. Carries the member's
    /// <c>JsonPath</c>.</summary>
    BundleMemberRequired,

    /// <summary>Refuses a member present with a JSON kind other than the one its path takes. Carries
    /// <c>JsonPath</c> and <c>ActualJsonKind</c>.</summary>
    BundleMemberKindInvalid,

    /// <summary>Refuses a number member that is not an integer a 32-bit signed integer can hold, such as
    /// <c>1.5</c> or <c>2147483648</c>. Carries <c>JsonPath</c>.</summary>
    BundleInt32Unrepresentable,

    /// <summary>Refuses a number member that is not an integer a 64-bit signed integer can hold. Carries
    /// <c>JsonPath</c>.</summary>
    BundleInt64Unrepresentable,

    /// <summary>Refuses a content type id outside 1 to 65,535. Carries <c>JsonPath</c>, <c>ActualInteger</c>,
    /// <c>MinimumInteger</c> (1) and <c>MaximumInteger</c> (65,535).</summary>
    BundleTypeIdOutsideRange,

    /// <summary>Refuses a field kind that is not a defined <see cref="ContentFieldKind"/> value. Carries
    /// <c>JsonPath</c> and <c>ActualInteger</c>.</summary>
    BundleFieldKindUnknown,

    /// <summary>Refuses a visibility that is not a defined <see cref="ContentVisibility"/> value. Carries
    /// <c>JsonPath</c> and <c>ActualInteger</c>.</summary>
    BundleVisibilityUnknown,

    /// <summary>Refuses a remap rule kind that is not a defined <see cref="RemapRuleKind"/> value. Carries
    /// <c>JsonPath</c> and <c>ActualInteger</c>.</summary>
    BundleRemapKindUnknown,

    /// <summary>Refuses a bytes member whose string is not hexadecimal: an odd length or a character that is not
    /// a hex digit. Either letter case is accepted. Carries <c>JsonPath</c>.</summary>
    BundleHexMalformed,
}
