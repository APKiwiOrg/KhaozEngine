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

    /// <summary>Refuses reading or writing a format 2 bundle that has lost its text section. Carries
    /// <c>ActualFormatVersion</c>.</summary>
    BundleTextSectionMissing,

    /// <summary>Refuses a document missing a member the format requires. Carries the member's
    /// <c>JsonPath</c>.</summary>
    BundleMemberRequired,

    /// <summary>Refuses a member present with a JSON kind other than the one its path takes. Carries
    /// <c>JsonPath</c> and <c>ActualJsonKind</c>.</summary>
    BundleMemberKindInvalid,

    /// <summary>Refuses a number member not written as a 32-bit integer literal, such as
    /// <c>1.0</c>, <c>1e2</c> or <c>2147483648</c>. Carries <c>JsonPath</c>.</summary>
    BundleInt32Unrepresentable,

    /// <summary>Refuses a number member not written as a 64-bit integer literal. Carries
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

    /// <summary>Refuses a text value in an undeclared language. Carries <c>Language</c> and, for a bundle,
    /// <c>JsonPath</c>, or for publication, <c>Type</c>, <c>Id</c> and <c>FieldName</c>.</summary>
    BundleTextLanguageUndeclared,

    /// <summary>Refuses a repeated canonical text target. Carries <c>JsonPath</c> and <c>TextTarget</c>.</summary>
    BundleTextTargetDuplicate,

    /// <summary>Refuses a text target with a blank marker field. Carries <c>JsonPath</c>.</summary>
    BundleTextTargetFieldBlank,

    /// <summary>Refuses a text target with an empty content key. Carries <c>JsonPath</c>.</summary>
    BundleTextTargetKeyEmpty,

    /// <summary>Refuses text with an unpaired surrogate. A bundle carries <c>JsonPath</c>. A chunk producer
    /// carries <c>Type</c>, <c>Id</c>, <c>FieldName</c>, <c>Language</c>, <c>MemberName</c> and the UTF-16
    /// <c>Index</c>, with the
    /// original argument exception as inner.</summary>
    TextMalformedUnicode,

    /// <summary>Refuses a text value above 8,192 strict UTF-8 bytes. A bundle carries <c>JsonPath</c>,
    /// <c>ActualBytes</c> and <c>MaximumBytes</c> (8,192).</summary>
    TextValueBytesExceeded,

    // Id allocation, family reservations, change sets and diff row identity.

    /// <summary>Refuses a plain id range whose top exceeds the type's inclusive ceiling. Carries
    /// <c>Type</c>, <c>Ceiling</c>, <c>IssuedThrough</c>, <c>ReservedThrough</c> and <c>RequestedCount</c>.</summary>
    AllocationCeilingExceeded,

    /// <summary>Refuses an aligned family block whose highest id exceeds the type's inclusive ceiling. Carries
    /// <c>Type</c>, <c>FamilyKey</c>, <c>Ceiling</c>, <c>BaseId</c>, <c>HighestId</c>, <c>IssuedThrough</c>
    /// and <c>ReservedThrough</c>.</summary>
    FamilyReservationCeilingExceeded,

    /// <summary>Refuses allocation or reservation by an absent family id. Carries <c>FamilyId</c>, with
    /// <c>Type</c> left at its default because the family is unknown.</summary>
    FamilyUnknown,

    /// <summary>Refuses a target held under a different edit operation. Carries <c>Type</c>, <c>Id</c>,
    /// <c>RowKey</c>, <c>StandingOperation</c> and <c>RequestedOperation</c>.</summary>
    EditTargetOperationCollision,

    /// <summary>Refuses a diff, candidate, rollback, pack row or text target whose type the registry does not declare.
    /// Carries <c>Type</c>, retaining the existing <c>Id</c> where known.</summary>
    TypeUnknown,
    // Publication, rollback and row-only pack writes.

    /// <summary>Refuses a stale publish expectation. Carries <c>ExpectedBaseVersion</c> and <c>ActualBaseVersion</c>.</summary>
    BaseVersionMoved,

    /// <summary>Refuses publication without an open draft carrying pending edits.</summary>
    NoPendingDraft,

    /// <summary>Refuses publication that lost its frozen draft. Carries <c>ExpectedBaseVersion</c>.</summary>
    FrozenDraftLost,

    /// <summary>Refuses a candidate rejected by validation. The complete <c>Findings</c> snapshot identifies it.</summary>
    CandidateRejected,

    /// <summary>Refuses a carried definition id already in the candidate. Carries <c>Type</c>, <c>Id</c>,
    /// <c>RowKey</c> and <c>EditOrdinal</c>.</summary>
    DefinitionIdDuplicate,

    /// <summary>Refuses an edit naming an undeclared field. Carries <c>Type</c>, <c>Id</c>, <c>TypeKey</c>,
    /// <c>FieldName</c> and <c>EditOrdinal</c>.</summary>
    FieldUnknown,

    /// <summary>Refuses an edit whose target row is absent. Carries <c>Type</c>, <c>Id</c>, <c>RowKey</c>
    /// and <c>EditOrdinal</c> for a row edit. Text binding carries <c>Type</c> and <c>RowKey</c> with
    /// <c>Id</c> 0, or <c>Type</c>, <c>Id</c>, <c>FieldName</c> and <c>Language</c> for an absent final row.</summary>
    RowUnknown,

    /// <summary>Refuses a pack target without version pointers. Carries <c>StoreType</c> and <c>MemberName</c>.</summary>
    PackPointerStoreRequired,

    /// <summary>Refuses a version the store does not hold. Carries <c>VersionNumber</c>.</summary>
    VersionUnknown,

    /// <summary>Refuses rollback across an irreversible retire (KEC0039). Carries the first blocker's
    /// <c>Type</c>, <c>Id</c>, <c>RowKey</c>, <c>TargetVersion</c>, <c>BlockerCount</c> and complete <c>Findings</c>.</summary>
    RetireIrreversible,

    /// <summary>Refuses a rule list shorter than the held history. Carries <c>ActualCount</c> and <c>MinimumCount</c>.</summary>
    RulePrefixShortened,

    /// <summary>Refuses a different rule at a held sequence. Carries <c>RuleSequence</c>.</summary>
    RulePrefixEntryMismatch,

    /// <summary>Refuses writing the row half of a text plan. Carries <c>MemberName</c> and <c>VersionNumber</c>.</summary>
    TextPlanOnRowOnlyRoute,

    /// <summary>Refuses a row-only write naming an absent text chunk. Carries <c>MemberName</c>,
    /// <c>Language</c> and <c>TextHash</c>.</summary>
    TextChunkMissingOnRowOnlyRoute,

    // Text candidate preflight and commit confirmation.

    /// <summary>Refuses text without a registered CLIENT type and CLIENT localized marker. Carries
    /// <c>Type</c>, <c>Id</c>, <c>FieldName</c>, <c>Language</c> and <c>TypeKey</c> when registered.</summary>
    TextTargetIneligible,

    /// <summary>Refuses a derived text key above 192 strict UTF-8 bytes. Carries <c>Type</c>, <c>Id</c>,
    /// <c>FieldName</c>, <c>Language</c>, <c>ActualBytes</c> and <c>MaximumBytes</c> (192).</summary>
    TextKeyBytesExceeded,

    /// <summary>Refuses invalid UTF-8 in a content key used by text. Carries <c>Type</c>, <c>Id</c>,
    /// <c>FieldName</c> and <c>Language</c>.</summary>
    TextKeyUtf8Invalid,

    /// <summary>Refuses two values deriving one key in a language. Carries <c>DerivedKey</c> and <c>Language</c>.</summary>
    TextKeyCollision,

    /// <summary>Refuses a language body above 16 MiB, including count and length prefixes. Carries
    /// <c>Language</c>, <c>ActualBytes</c> and <c>MaximumBytes</c> (16,777,216).</summary>
    TextChunkBytesExceeded,

    /// <summary>Refuses a chunk hash that differs from its values. Carries <c>Language</c>, the claimed
    /// <c>TextHash</c> and the regenerated <c>ExpectedTextHash</c>.</summary>
    TextChunkHashMismatch,

    /// <summary>Refuses stored bytes that differ from the regenerated file. Carries <c>Language</c> and <c>TextHash</c>.</summary>
    TextChunkBytesMismatch,

    /// <summary>Refuses reuse of a language absent from the baseline. Carries <c>Language</c>, the baseline
    /// <c>VersionNumber</c> and <c>TextHash</c>.</summary>
    TextReusedChunkUnrecorded,

    /// <summary>Refuses reuse of a hash the baseline did not record. Carries <c>Language</c>, the baseline
    /// <c>VersionNumber</c>, the claimed <c>TextHash</c> and the recorded <c>ExpectedTextHash</c>.</summary>
    TextReusedHashMismatch,

    /// <summary>Refuses reuse with another spelling than the baseline recorded. Carries <c>Language</c>,
    /// baseline <c>VersionNumber</c>, <c>TextHash</c>, claimed <c>WireTag</c> and recorded <c>ExpectedWireTag</c>.</summary>
    TextReusedWireTagMismatch,

    /// <summary>Refuses a version whose unknown text cannot be proven empty. Carries <c>VersionNumber</c>.</summary>
    TextProvenanceUnknown,

    // Text route admission and exact-version export.

    /// <summary>Refuses a companion import while a draft holds work. Carries <c>MemberName</c>,
    /// <c>RowEditCount</c>, <c>TextEditCount</c> and <c>LanguageIntroductionCount</c>.</summary>
    DraftOpen,

    /// <summary>Refuses a bundle text target without a carried row. Carries <c>Type</c>, <c>Id</c> 0,
    /// <c>RowKey</c>, <c>FieldName</c>, <c>Language</c> and the value's zero-based <c>Index</c>.</summary>
    BundleTextRowUnknown,

    /// <summary>Refuses export of a text revision without a live row. Carries <c>TextRowType</c>, <c>TextRowId</c>,
    /// <c>FieldName</c>, <c>Language</c>, <c>VersionNumber</c> and <c>MemberName</c>.</summary>
    TextExportRowUnknown,

    /// <summary>Refuses a text target naming an undeclared field. Carries <c>Type</c>, <c>Id</c> 0,
    /// <c>TypeKey</c>, <c>FieldName</c> and <c>Language</c>.</summary>
    TextTargetFieldUnknown,

    /// <summary>Refuses a text target's field name above 64 UTF-16 code units. Carries <c>Type</c>,
    /// <c>Id</c> 0, <c>TypeKey</c>, <c>FieldName</c>, <c>Language</c>, <c>ActualCharacters</c>
    /// and inclusive <c>MaximumCharacters</c> (64).</summary>
    TextFieldNameTooLong,

    /// <summary>Refuses a text-bearing bundle on a row-only route. Carries <c>MemberName</c>.</summary>
    TextBundleOnRowOnlyRoute,

    /// <summary>Refuses a non-format-1 bundle without text on a row-only route. Carries <c>MemberName</c>
    /// and <c>ActualFormatVersion</c>.</summary>
    TextFormatOnRowOnlyRoute,

    /// <summary>Refuses a draft holding text on a row-only route. Carries <c>MemberName</c>,
    /// <c>TextEditCount</c> and <c>LanguageIntroductionCount</c>.</summary>
    TextDraftOnRowOnlyRoute,

    /// <summary>Refuses a planner baseline whose carried text disagrees with its record. Carries <c>VersionNumber</c>,
    /// <c>CarriedLanguageCount</c>, <c>CarriedValueCount</c>, <c>RecordedLanguageCount</c>
    /// and <c>RecordedValueCount</c>.</summary>
    TextBaselineHiddenFromPlanner,
}
