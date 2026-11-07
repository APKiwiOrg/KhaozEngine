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

    /// <summary>Refuses a negative source version. Carries <c>JsonPath</c>, <c>ActualInteger</c> and
    /// <c>MinimumInteger</c> (0).</summary>
    BundleSourceVersionNegative,

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

    // Bundle values a lower constructor refuses, mapped from its typed finding. Each carries the bundle-format
    // token, and the constructor's own argument exception is the inner exception.

    /// <summary>Refuses a family key that is empty or white space. Carries <c>JsonPath</c>.</summary>
    BundleFamilyKeyBlank,

    /// <summary>Refuses a family block size outside 16 to 65,536. Carries <c>FamilyKey</c>, <c>ActualBlockSize</c>,
    /// <c>MinimumBlockSize</c> (16) and <c>MaximumBlockSize</c> (65,536), and <c>JsonPath</c> for a bundle.</summary>
    FamilyBlockSizeOutsideRange,

    /// <summary>Refuses a family block size inside 16 to 65,536 that is not a power of two. Carries
    /// <c>FamilyKey</c> and <c>ActualBlockSize</c>, and <c>JsonPath</c> for a bundle.</summary>
    FamilyBlockSizeNotPowerOfTwo,

    /// <summary>Refuses a family block whose base id is not a multiple of the family's block size. Carries
    /// <c>JsonPath</c> and the base id as <c>ActualInteger</c>.</summary>
    BundleFamilyBlockAlignmentInvalid,

    /// <summary>Refuses a schema field whose name is empty or white space. Carries <c>JsonPath</c>.</summary>
    BundleFieldNameBlank,

    /// <summary>Refuses a schema field whose name an earlier field of the type already declares. Carries
    /// <c>JsonPath</c> of the later field's name and <c>FieldName</c>.</summary>
    BundleFieldNameDuplicate,

    /// <summary>Refuses a key reference field that names no content type key. Carries <c>JsonPath</c> and
    /// <c>FieldName</c>.</summary>
    BundleReferenceTargetMissing,

    /// <summary>Refuses a tag list field whose reference target is not <c>tag</c>. Carries <c>JsonPath</c> and
    /// <c>FieldName</c>.</summary>
    BundleTagReferenceTargetInvalid,

    /// <summary>Refuses a reference target on a field kind other than a key reference or a tag list. Carries
    /// <c>JsonPath</c>, <c>FieldName</c> and <c>ActualKind</c>.</summary>
    BundleUnexpectedReferenceTarget,

    /// <summary>Refuses a scaled int field whose scale is below 1. Carries <c>JsonPath</c>, <c>FieldName</c>,
    /// <c>ActualInteger</c> and <c>MinimumInteger</c> (1).</summary>
    BundleScaledIntegerScaleInvalid,

    /// <summary>Refuses a field of any other kind whose scale is not 1. Carries <c>JsonPath</c>, <c>FieldName</c>,
    /// <c>ActualKind</c>, <c>ActualInteger</c>, <c>MinimumInteger</c> (1) and <c>MaximumInteger</c> (1).</summary>
    BundleUnscaledKindScaleInvalid,

    /// <summary>Refuses a row field of the byte-free <see cref="ContentFieldKind.LocalizedTextKey"/> kind that
    /// carries a value instead of being absent. Carries <c>JsonPath</c>.</summary>
    BundleMarkerValueAuthored,

    /// <summary>Refuses a remap rule's <c>sequence</c>, <c>introducedIn</c>, <c>fromId</c> or <c>toId</c> below
    /// 0. Carries <c>JsonPath</c>, <c>ActualInteger</c> and <c>MinimumInteger</c> (0).</summary>
    BundleRuleIntegerNegative,

    /// <summary>Refuses a remap rule payload that is not the shape its kind declares. Carries <c>JsonPath</c>,
    /// <c>RuleKind</c> and the payload length as <c>ActualBytes</c>.</summary>
    BundleRulePayloadMalformed,

    // Bundle text section and the text constructors it wraps.

    /// <summary>Refuses a repeated canonical language identity. Carries <c>JsonPath</c> and <c>Language</c>.</summary>
    BundleLanguageIdentityDuplicate,

    /// <summary>Refuses a tag outside the ASCII language grammar of 1 to 35 bytes. Carries <c>JsonPath</c>
    /// and the submitted <c>LanguageTag</c>.</summary>
    BundleLanguageTagInvalid,

    /// <summary>Refuses a wire tag that normalizes to another identity. Carries <c>JsonPath</c>, the canonical
    /// <c>Language</c> and the submitted <c>WireTag</c>.</summary>
    BundleLanguageWireTagMismatch,

    /// <summary>Refuses a text value in an undeclared language. Carries <c>JsonPath</c> and <c>Language</c>.</summary>
    BundleTextLanguageUndeclared,

    /// <summary>Refuses a repeated canonical text target. Carries <c>JsonPath</c> and <c>TextTarget</c>.</summary>
    BundleTextTargetDuplicate,

    /// <summary>Refuses a text target with a blank marker field. Carries <c>JsonPath</c>.</summary>
    BundleTextTargetFieldBlank,

    /// <summary>Refuses a text target with an empty content key. Carries <c>JsonPath</c>.</summary>
    BundleTextTargetKeyEmpty,

    /// <summary>Refuses text with an unpaired surrogate. A bundle carries <c>JsonPath</c>.</summary>
    TextMalformedUnicode,

    /// <summary>Refuses a text value above 8,192 strict UTF-8 bytes. A bundle carries <c>JsonPath</c>,
    /// <c>ActualBytes</c> and <c>MaximumBytes</c> (8,192).</summary>
    TextValueBytesExceeded,
}
