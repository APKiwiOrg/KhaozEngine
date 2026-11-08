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

    /// <summary>Refuses a field kind that is not a defined <see cref="ContentFieldKind"/> value. Carries
    /// <c>JsonPath</c> and <c>ActualInteger</c>.</summary>
    BundleFieldKindUnknown = 11,

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

    /// <summary>Refuses a bundle text value in an undeclared language. Carries <c>JsonPath</c> and <c>Language</c>.</summary>
    BundleTextLanguageUndeclared,

    /// <summary>Refuses a repeated canonical text target. Carries <c>JsonPath</c> and <c>TextTarget</c>.</summary>
    BundleTextTargetDuplicate,

    /// <summary>Refuses a text target with a blank marker field. Carries <c>JsonPath</c>.</summary>
    BundleTextTargetFieldBlank,

    /// <summary>Refuses a text target with an empty content key. Carries <c>JsonPath</c>.</summary>
    BundleTextTargetKeyEmpty,

    /// <summary>Refuses a bundle text string with an unpaired surrogate. Carries <c>JsonPath</c>.</summary>
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
    /// and <c>EditOrdinal</c>.</summary>
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

    /// <summary>Refuses text binding without a single live row for its key. Carries <c>Type</c>,
    /// <c>Id</c> 0 and <c>RowKey</c>.</summary>
    TextBindingRowUnknown,

    /// <summary>Refuses text whose final row is absent. Carries <c>Type</c>, <c>Id</c>,
    /// <c>FieldName</c> and <c>Language</c>.</summary>
    TextFinalRowUnknown,

    /// <summary>Refuses publication text in an undeclared language. Carries <c>Type</c>, <c>Id</c>,
    /// <c>FieldName</c> and <c>Language</c>.</summary>
    TextLanguageUndeclared,

    /// <summary>Refuses an unpaired surrogate in a text producer component. Carries <c>Type</c>, <c>Id</c>,
    /// <c>FieldName</c>, <c>Language</c>, <c>MemberName</c> and zero-based UTF-16 <c>Index</c>.</summary>
    TextProducerMalformedUnicode,

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

    // Operator policy binding.

    /// <summary>Refuses an operator import whose store cannot bind the host policy. Carries
    /// <c>RequiredCapability</c> (<see cref="IContentPolicyImportStore"/>).</summary>
    PolicyImportCapabilityMissing,

    // Store admission and commit confirmation.

    /// <summary>Refuses a store family declaration outside the inclusive block-size range. Carries Type, FamilyKey, ActualBlockSize, MinimumBlockSize (16) and MaximumBlockSize (65,536).</summary>
    FamilyDeclarationBlockSizeOutsideRange,

    /// <summary>Refuses an in-range store family block size that is not a power of two. Carries Type, FamilyKey and ActualBlockSize.</summary>
    FamilyDeclarationBlockSizeNotPowerOfTwo,

    /// <summary>Refuses a family key already declared for its type. Carries Type and FamilyKey.</summary>
    FamilyKeyDuplicate,

    /// <summary>Refuses a row edit authoring a derived marker. Carries Type, Id, TypeKey, FieldName and EditOrdinal.</summary>
    DerivedMarkerNotAuthorable,

    /// <summary>Refuses a bundle declaration that disagrees with the registry. Carries Type, ActualTypeKey, RegisteredTypeKey, ActualChunkSlots and RegisteredChunkSlots.</summary>
    BundleTypeDeclarationMismatch,

    /// <summary>Refuses an imported row naming no carried family. Carries Type, Id, RowKey and FamilyKey.</summary>
    BundleFamilyUnknown,

    /// <summary>Refuses import into a catalog that already published. Carries ActiveVersion.</summary>
    CatalogNotEmpty,

    /// <summary>Refuses a plan whose number is not next after the highest version. Carries RequestedVersion, HighestVersion and NextVersion.</summary>
    NextVersionMismatch,

    /// <summary>Refuses an operation requiring an absent pack store. Carries MemberName.</summary>
    PackStoreMissing,

    /// <summary>Refuses a version whose pack reader failed. Carries VersionNumber, ObjectHash and the stable PackReaderReason token.</summary>
    PackUnreadable,

    /// <summary>Refuses a draft write while publication holds its freeze. Carries MemberName and FrozenForBaseVersion.</summary>
    PublishInProgress,

    /// <summary>Refuses publication of an upgrade already in the ledger. Carries UpgradeId.</summary>
    UpgradeAlreadyRecorded,

    /// <summary>Refuses a fork whose row text a row-only route cannot copy. Carries MemberName, TextRowType and TextRowId. Legacy Type and Id stay default and 0.</summary>
    TextForkOnRowOnlyRoute,

    /// <summary>Refuses a row-only route over recorded text. Carries MemberName, VersionNumber, RecordedLanguageCount and RecordedValueCount.</summary>
    TextBaseOnRowOnlyRoute,

    /// <summary>Refuses a row-only plan naming different text chunks from its baseline. Carries MemberName, VersionNumber, ActualCount and ExpectedCount.</summary>
    TextManifestOnRowOnlyRoute,

    /// <summary>Refuses a text plan frozen in another store. Carries ExpectedEpoch from the plan and ActualEpoch from the store.</summary>
    TextPlanEpochMismatch,

    /// <summary>Refuses a text plan whose draft freeze does not match the active base. Carries ExpectedBaseVersion and nullable ActualBaseVersion from the freeze marker.</summary>
    TextPlanFreezeMismatch,

    /// <summary>Refuses a text plan whose complete frozen draft differs from the store. Carries ExpectedBaseVersion.</summary>
    TextPlanDraftMismatch,

    /// <summary>Refuses a text plan whose baseline text differs from the store. Carries ExpectedBaseVersion.</summary>
    TextPlanBaselineMismatch,

    /// <summary>Refuses a text close absent from the store. Carries TextRowType, TextRowId, FieldName, Language and FromVersion. Legacy Type and Id stay default and 0.</summary>
    TextCloseRevisionMissing,

    /// <summary>Refuses a text commit revision without a live plan row. Carries TextRowType, TextRowId, FieldName and Language. Legacy Type and Id stay default and 0.</summary>
    TextPlanRowUnknown,

    // Defensive candidate and text producer admission.

    /// <summary>Refuses a candidate edit operation outside the four supported operations. Carries Type, Id,
    /// TypeKey, ActualOperation and EditOrdinal.</summary>
    CandidateEditOperationUnsupported,

    /// <summary>Refuses a candidate fork flag absent from its registered schema or not Bool. Carries Type,
    /// Id, TypeKey, FieldName and EditOrdinal.</summary>
    CandidateForkFlagInvalid,

    /// <summary>Refuses a text producer wire tag above 35 ASCII bytes. Carries Language, WireTag,
    /// ActualBytes and MaximumBytes (35). Legacy Type and Id stay default and 0.</summary>
    TextProducerWireTagBytesExceeded,

    /// <summary>Refuses a derived text key whose re-encoded length differs from its measurement.
    /// Carries Type, Id, FieldName and Language.</summary>
    TextDerivedKeyByteCountMismatch,

    // Durable store metadata and identity.

    /// <summary>Refuses a store read when the catalog_metadata singleton is absent. Carries no identifiers.</summary>
    SchemaMetadataMissing,

    /// <summary>Refuses a registry pairing that disagrees with a stored type id or key. Carries Type,
    /// StoredTypeId (full persisted Int64), StoredTypeKey and RegisteredTypeKey.</summary>
    TypeIdentityMismatch,

    /// <summary>Refuses a draft singleton removed while a transaction is opening or writing it. Carries no identifiers.</summary>
    DraftRowDisappeared,

    /// <summary>Refuses a persisted edit operation outside the four supported operations. Carries Type,
    /// Id, ActualOperation and StoredEditOrdinal.</summary>
    StoredEditOperationUnknown,

    /// <summary>Refuses a non-null stored scalar of the wrong CLR type. Carries ExpectedScalarType and ActualScalarType.</summary>
    StoredScalarTypeMismatch,

    /// <summary>Refuses a SQL Server draft-edit insert that returns no identity. Carries Type and Id.</summary>
    DraftEditIdentityMissing,

    /// <summary>Refuses a SQL Server family insert that returns no identity. Carries Type and FamilyKey.</summary>
    FamilyIdentityMissing,

    /// <summary>Refuses a SQL Server transaction contended by another writer. Carries SqlErrorNumber (1205 or 3960).</summary>
    TransactionContended,

    // Durable schema validation and reset.

    /// <summary>Refuses a negative SQL Server application lock result. Carries ReturnCode and RequiredMigration.</summary>
    SchemaApplicationLockNotGranted,

    /// <summary>Refuses validation of an absent catalog. Carries RequiredMigration.</summary>
    SchemaMissing,

    /// <summary>Refuses schema validation or reset with a missing required metadata value. Carries RequiredMigration.</summary>
    SchemaValidationMetadataMissing,

    /// <summary>Refuses schema metadata that the provider cannot read. Carries RequiredMigration and the provider InnerException.</summary>
    SchemaMetadataUnreadable,

    /// <summary>Refuses a noninteger schema version. Carries RequiredMigration and MetadataType.</summary>
    SchemaVersionMalformed,

    /// <summary>Refuses a schema version this mode cannot use. Carries ActualVersion, SupportedVersions and RequiredMigration.</summary>
    SchemaVersionUnsupported,

    /// <summary>Refuses validation with foreign key enforcement disabled. Carries RequiredMigration.</summary>
    SchemaForeignKeysDisabled,

    /// <summary>Refuses a missing schema object or required row time shape. Carries ObjectName, ExpectedVersion and RequiredMigration.</summary>
    SchemaObjectMissing,

    /// <summary>Refuses a schema object with the wrong definition. Carries ObjectName, ExpectedVersion and RequiredMigration.</summary>
    SchemaObjectDefinitionMismatch,

    /// <summary>Refuses a schema object or row time shape its version does not declare. Carries ObjectName, ExpectedVersion and RequiredMigration.</summary>
    SchemaObjectUnexpected,

    /// <summary>Refuses reset of an incomplete catalog with a readable version. Carries StandingTableCount, ExpectedTableCount, ActualVersion and ForceRequired.</summary>
    ResetTablesPartial,

    /// <summary>Refuses reset without a readable version. Carries StandingTableCount, ExpectedTableCount and ForceRequired.</summary>
    ResetVersionUnreadable,

    /// <summary>Refuses reset to an older schema. Carries ActualVersion and MaximumVersion.</summary>
    ResetVersionNewer,

    /// <summary>Refuses reset while a draft stands. Carries DraftCount.</summary>
    ResetDraftOpen,

    /// <summary>Refuses reset whose host foreign key delete actions write host rows. Carries HostObjects.</summary>
    HostForeignKeyActionUnsafe,

    /// <summary>Refuses a text producer value above 8192 strict UTF-8 bytes. Carries Type, Id,
    /// FieldName, Language, ActualBytes and MaximumBytes (8192).</summary>
    TextProducerValueBytesExceeded,

    /// <summary>Refuses a lower bundle constructor rule with no mapped reader finding.
    /// Carries no identifiers. The constructor's exception is the inner cause.</summary>
    BundleConstructorRuleUnmapped,

}
