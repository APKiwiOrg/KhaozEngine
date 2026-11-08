using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The ONE exception type this package throws (spec 2.3), carrying the offending content type, the
/// definition id and a stable reason token beside the human-readable message.
/// <para>
/// It THROWS where the read side returns a reason, and the split is deliberate. A decoder is handed bytes
/// from a remote peer, so it must be total. An authoring refusal happens on the publisher's own machine,
/// against a store the publisher opened, with the caller's own arguments still on the line above, so it
/// belongs on that line. The API boundary of spec 10 turns the reason into a 400 or a 409 with a finding.
/// </para>
/// <para>
/// <see cref="Reason"/> is what an operator's log line and a counter key on, never the message, the same
/// rule the pack decoders' reason tokens follow.
/// </para>
/// <para>
/// <see cref="Refusal"/> names the exact rule where a throw site carries one, with the identifiers that member
/// documents. It sits beside <see cref="Reason"/> and never replaces it.
/// </para>
/// </summary>
public sealed class ContentAuthoringException : Exception
{
    readonly IReadOnlyList<int>? _supportedFormatVersions;

    /// <summary>
    /// A second edit named a target the open draft already holds under a DIFFERENT operation (spec 4.4). A
    /// draft holds one pending intent per row, so the second edit is refused rather than silently flipping
    /// the first one's operation and dropping its fields.
    /// </summary>
    public const string EditTargetCollisionReason = "edit-target-collision";

    /// <summary>
    /// An allocation would have crossed a type's declared id CEILING (spec 4.7). A ceiling is a FORMAT
    /// constraint rather than a preference, and retired rows count toward it because ids are never reused.
    /// </summary>
    public const string IdCeilingReason = "id-ceiling-exceeded";

    /// <summary>An allocation named a family the store does not hold.</summary>
    public const string UnknownFamilyReason = "unknown-family";

    /// <summary>An edit or an allocation named a content type the registry does not carry.</summary>
    public const string UnknownTypeReason = "unknown-type";

    /// <summary>An edit named a field the type's schema does not declare, refused AT THE BOUNDARY.</summary>
    public const string UnknownFieldReason = "unknown-field";

    /// <summary>A family declaration was refused: a taken key, or a block size that is not a legal power of two.</summary>
    public const string FamilyDeclarationReason = "family-declaration";

    /// <summary>
    /// A publish expected a base version the store no longer stands at (spec 6.2). Two consoles cannot both
    /// publish the same draft: the second one's expectation is stale. The row publisher carries both numbers
    /// as ExpectedBaseVersion and ActualBaseVersion beside Refusal BaseVersionMoved.
    /// </summary>
    public const string BaseVersionMovedReason = "base-version-moved";

    /// <summary>
    /// A publish found no open draft with pending edits. A version is a set of changes, so an empty one
    /// would be a number with no content behind it.
    /// </summary>
    public const string NoOpenDraftReason = "no-open-draft";

    /// <summary>
    /// A draft edit or a discard arrived while a publish was in flight (spec 6.2). Step 1 marks the draft
    /// frozen for the base version it is publishing, and every write to the draft is refused until the
    /// publish leaves, so a second actor cannot slip an edit into a change set the pipeline has already read.
    /// <para>
    /// A marker naming a base version the store no longer stands at is STALE: it belongs to a publish that
    /// died, and the next baseline read clears it rather than leaving the draft wedged forever.
    /// </para>
    /// </summary>
    public const string PublishInProgressReason = "publish-in-progress";

    /// <summary>
    /// An update, a retire or a fork named a row the base version carries no live revision of. It is a
    /// refusal rather than a finding, because a candidate cannot be built for an edit with no target.
    /// </summary>
    public const string UnknownRowReason = "unknown-row";

    /// <summary>
    /// A fork named a flag field its type's schema does not declare as a <c>Bool</c>. The field is the
    /// caller's, and the engine checks only that it exists and is the right kind.
    /// </summary>
    public const string ForkFlagFieldReason = "fork-flag-field";

    /// <summary>A draft edit carried an operation outside the four of spec 3.7.</summary>
    public const string UnknownEditOperationReason = "unknown-edit-operation";

    /// <summary>
    /// A publish was refused because its candidate did not validate. The findings are on
    /// <see cref="Findings"/>, which is what the API boundary turns into its 400 body.
    /// </summary>
    public const string CandidateInvalidReason = "candidate-invalid";

    /// <summary>
    /// A rollback would restore a row that has been RETIRED since the target version, which
    /// <c>KEC0039</c> refuses. A retire is never reversible by rollback: the way out is an ordinary add
    /// under a new key carrying the old values, plus a replacement rule when existing references should
    /// move onto it.
    /// </summary>
    public const string RetireIrreversibleReason = "retire-irreversible";

    /// <summary>
    /// An import was refused because the database already holds a published version. A bundle is imported
    /// into an EMPTY database only, which is what makes a seed that cannot run twice against live data.
    /// </summary>
    public const string CatalogNotEmptyReason = "catalog-not-empty";

    /// <summary>A read named a version the store does not hold.</summary>
    public const string UnknownVersionReason = "unknown-version";

    /// <summary>
    /// A provider's catalog RESET was refused because the store carries an open draft. A reset drops every
    /// catalog object, so it would take the draft with it, and an operator who did not know the draft was
    /// there would lose unpublished authoring with nothing to read afterwards that says what it held. The
    /// remedy is to publish or discard the draft, and the escape hatch is the reset's own force flag. A
    /// companion text bundle import is refused with it too, before anything is staged, while an open draft holds
    /// any row, text or language work the import's own draft would otherwise merge with.
    /// </summary>
    public const string DraftOpenReason = "draft-open";

    /// <summary>
    /// A provider's catalog RESET found SOME of the schema's tables standing and not the rest, which is a
    /// half-finished deletion rather than a migration, or found tables standing with no schema version it
    /// could read, all of them included. No store can open either and no read can describe what it holds.
    /// The remedy is not a migration, which is why this is not <see cref="SchemaMismatchReason"/>. The reset
    /// repairs it under its own force flag by dropping what is left and recreating the schema, and the
    /// refusal says so.
    /// </summary>
    public const string CatalogPartialReason = "catalog-partial";

    /// <summary>
    /// A SQLite catalog RESET found a HOST table with a foreign key into a catalog table declared
    /// <c>ON DELETE CASCADE</c>, <c>SET NULL</c> or <c>SET DEFAULT</c>. SQLite's <c>DROP TABLE</c> deletes every
    /// row before it drops the table, and that delete fires the host key's action, so the reset would delete or
    /// rewrite the host's own rows. It is refused before anything is dropped, whatever the force flag says. The
    /// SQL Server reset needs no such check, because SQL Server refuses to drop a table any foreign key
    /// references and the reset rolls back as a whole.
    /// </summary>
    public const string HostForeignKeyReason = "host-foreign-key";

    /// <summary>
    /// A version's pack could not be read back: an absent manifest, a chunk the store no longer holds, or
    /// bytes that do not digest to the address they were filed under. A typed
    /// <see cref="ContentAuthoringRefusal.PackUnreadable"/> carries the pack reader's token in
    /// <see cref="PackReaderReason"/>, with <see cref="VersionNumber"/> and <see cref="ObjectHash"/>.
    /// </summary>
    public const string PackUnreadableReason = "pack-unreadable";

    /// <summary>
    /// There is no usable pack target: either the store was built with none, or the one it was handed writes
    /// no version pointer and a published version could therefore never be enumerated out of it. Publishing
    /// writes files before it writes rows, so the target is not optional and neither half of it is.
    /// </summary>
    public const string NoPackStoreReason = "no-pack-store";

    /// <summary>
    /// A bundle document could not be read: a format version this build does not know, or a malformed member.
    /// A shape rule <c>ContentBundleJson</c> checks itself, and a value a lower constructor refuses, also names its
    /// <see cref="Refusal"/> and JSON path.
    /// </summary>
    public const string BundleFormatReason = "bundle-format";

    /// <summary>
    /// A provider's database is absent under <see cref="ContentAuthoringSchemaMode.ValidateOnly"/>, carries a
    /// schema version the build does not support, or holds an object that does not match the shape its
    /// migration declares. Durable schema validation carries <see cref="ObjectName"/> where applicable and
    /// <see cref="RequiredMigration"/> beside <see cref="Refusal"/>. A newer-schema reset instead carries
    /// <see cref="ActualVersion"/> and <see cref="MaximumVersion"/>.
    /// </summary>
    public const string SchemaMismatchReason = "schema-mismatch";

    /// <summary>
    /// A publish carried a content upgrade id the ledger already holds. The upgrade id is the ledger's primary
    /// key, so the commit is refused whole: the version, its rows, its chunks and the draft are all left
    /// exactly as they were, and the caller re-reads the ledger rather than assuming which run won.
    /// </summary>
    public const string UpgradeAlreadyRecordedReason = "upgrade-already-recorded";

    /// <summary>
    /// A text target names a type or a field that is not CLIENT visible, or a field that is not a localized
    /// text marker. Text needs both levels visible, because a shared manifest carries every language chunk.
    /// </summary>
    public const string TextTargetIneligibleReason = "text-target-ineligible";

    /// <summary>
    /// A text Remove named a language that is neither declared by the base version nor introduced by the open
    /// draft. Removing an absent value in a declared language is idempotent, and a Set is what declares one.
    /// </summary>
    public const string TextLanguageUndeclaredReason = "text-language-undeclared";

    /// <summary>A derived text key, a value or a language tag exceeds its strict UTF-8 byte bound.</summary>
    public const string TextBoundsReason = "text-bounds";

    /// <summary>
    /// A row-only route was refused because the text state it would need to carry cannot be represented on
    /// it: a draft or base holding text, a plan or bundle carrying a text section, or a format that implies
    /// one. The route fails closed rather than publishing, discarding or exporting the row half alone.
    /// </summary>
    public const string TextUnrepresentedReason = "text-unrepresented";

    /// <summary>
    /// A text commit or discard found the store's actual state different from the state its plan or proof
    /// names: another epoch, another base, other frozen rows, other text or other declarations.
    /// </summary>
    public const string TextStateMismatchReason = "text-state-mismatch";

    /// <summary>
    /// Text input reached a store that implements only <see cref="IContentAuthoringStore"/> and no
    /// <see cref="IContentTextAuthoringStore"/> companion. The caller refuses it before anything is applied or
    /// staged rather than running the row half alone. A store with the companion implements every member of it
    /// and never refuses with this token.
    /// </summary>
    public const string TextOperationUnavailableReason = "text-operation-unavailable";

    /// <summary>
    /// A committed version has no complete text record, so its values and language mappings are unknown. An
    /// unknown version is never read as empty, except on a read-only proof that its manifests named no
    /// language.
    /// </summary>
    public const string TextProvenanceUnknownReason = "text-provenance-unknown";

    /// <summary>
    /// A text plan names a chunk hash that the plan's own output values do not regenerate to, or reuses a
    /// chunk the base version did not record. The commit verifies every chunk rather than trusting it.
    /// </summary>
    public const string TextChunkMismatchReason = "text-chunk-mismatch";

    /// <summary>Creates the exception with no message.</summary>
    public ContentAuthoringException()
    {
    }

    /// <summary>Creates the exception, naming what was refused.</summary>
    public ContentAuthoringException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public ContentAuthoringException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception carrying the offending row and the reason it was refused.</summary>
    /// <param name="message">The human-readable refusal, naming both sides of the rule that was broken.</param>
    /// <param name="type">The content type the refusal is about, or the default when it is about none.</param>
    /// <param name="id">The definition id, or 0 when the refusal is about the store as a whole.</param>
    /// <param name="reason">The stable reason token an operator's log line is built from.</param>
    public ContentAuthoringException(string message, ContentTypeId type, int id, string? reason)
        : this(null, message, type, id, reason, null)
    {
    }

    /// <summary>
    /// Creates the exception carrying the FINDINGS that refused it, which is the publish and rollback
    /// shape: a refusal an operator reads as a list of codes rather than as one sentence.
    /// </summary>
    /// <param name="message">The human-readable refusal.</param>
    /// <param name="type">The content type the refusal is about, or the default when it is about none.</param>
    /// <param name="id">The definition id, or 0 when the refusal is about the store as a whole.</param>
    /// <param name="reason">The stable reason token.</param>
    /// <param name="findings">Every finding behind the refusal, in the order they were produced.</param>
    /// <exception cref="ArgumentNullException"><paramref name="findings"/> is null.</exception>
    public ContentAuthoringException(
        string message,
        ContentTypeId type,
        int id,
        string? reason,
        IReadOnlyList<ContentFinding> findings)
        : this(null, message, type, id, reason, null)
    {
        ArgumentNullException.ThrowIfNull(findings);
        Findings = ImmutableArray.Create(findings.ToArray());
    }

    /// <summary>Creates the exception carrying the exact rule it refused beside the legacy reason token.</summary>
    /// <param name="refusal">The rule that was refused.</param>
    /// <param name="message">The human-readable refusal.</param>
    /// <param name="type">The content type the refusal is about, or the default when it is about none.</param>
    /// <param name="id">The definition id, or 0 when the refusal is about the store as a whole.</param>
    /// <param name="reason">The stable legacy reason token.</param>
    public ContentAuthoringException(
        ContentAuthoringRefusal refusal,
        string message,
        ContentTypeId type,
        int id,
        string? reason)
        : this((ContentAuthoringRefusal?)refusal, message, type, id, reason, null)
    {
    }

    /// <summary>Creates a structured refusal carrying every validation or rollback finding as an owned snapshot.</summary>
    public ContentAuthoringException(
        ContentAuthoringRefusal refusal,
        string message,
        ContentTypeId type,
        int id,
        string? reason,
        IReadOnlyList<ContentFinding> findings)
        : this((ContentAuthoringRefusal?)refusal, message, type, id, reason, null)
    {
        ArgumentNullException.ThrowIfNull(findings);
        Findings = ImmutableArray.Create(findings.ToArray());
    }

    /// <summary>Creates the exception carrying the exact rule it refused and the fault that caused it.</summary>
    /// <param name="refusal">The rule that was refused.</param>
    /// <param name="message">The human-readable refusal.</param>
    /// <param name="type">The content type the refusal is about, or the default when it is about none.</param>
    /// <param name="id">The definition id, or 0 when the refusal is about the store as a whole.</param>
    /// <param name="reason">The stable legacy reason token.</param>
    /// <param name="innerException">The fault the refusal reports.</param>
    public ContentAuthoringException(
        ContentAuthoringRefusal refusal,
        string message,
        ContentTypeId type,
        int id,
        string? reason,
        Exception innerException)
        : this((ContentAuthoringRefusal?)refusal, message, type, id, reason, innerException)
    {
    }

    // Legacy consumer instances keep a null refusal without calling a forbidden public constructor.
    ContentAuthoringException(ContentAuthoringRefusal? refusal, string message, ContentTypeId type,
        int id, string? reason, Exception? innerException)
        : base(message, innerException)
    {
        Refusal = refusal;
        Type = type;
        Id = id;
        Reason = reason;
    }

    /// <summary>
    /// The exact rule refused, or null when the instance was built through a constructor that takes no
    /// refusal. Read <see cref="Reason"/> then.
    /// </summary>
    public ContentAuthoringRefusal? Refusal { get; }

    /// <summary>
    /// The rooted JSON path (<c>$</c>, <c>$.types[0].typeId</c>) of the bundle member a document refusal is
    /// about, or null. For <see cref="ContentAuthoringRefusal.BundleJsonMalformed"/> it is the reader's own
    /// path, null when the reader reports none.
    /// </summary>
    public string? JsonPath { get; init; }

    /// <summary>The zero-based line the JSON reader stopped at, for
    /// <see cref="ContentAuthoringRefusal.BundleJsonMalformed"/>, null when the reader reports none.</summary>
    public long? LineNumber { get; init; }

    /// <summary>The zero-based byte offset in that line, for
    /// <see cref="ContentAuthoringRefusal.BundleJsonMalformed"/>, null when the reader reports none.</summary>
    public long? BytePositionInLine { get; init; }

    /// <summary>The JSON kind the refused member actually has, or null.</summary>
    public JsonValueKind? ActualJsonKind { get; init; }

    /// <summary>The integer a refused member actually holds, once it was read as a 32-bit integer, or null.</summary>
    public int? ActualInteger { get; init; }

    /// <summary>The smallest integer the refused member admits, inclusive, or null.</summary>
    public int? MinimumInteger { get; init; }

    /// <summary>The largest integer the refused member admits, inclusive, or null.</summary>
    public int? MaximumInteger { get; init; }

    /// <summary>The bundle format version the refused bundle declares, or null.</summary>
    public int? ActualFormatVersion { get; init; }

    /// <summary>The bundle format versions this build reads and writes, as an owned read-only snapshot, or null.</summary>
    public IReadOnlyList<int>? SupportedFormatVersions
    {
        get => _supportedFormatVersions;
        init => _supportedFormatVersions = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    /// <summary>The schema field name the refused entry declares, or null.</summary>
    public string? FieldName { get; init; }

    /// <summary>The content field kind the refused entry declares, or null.</summary>
    public ContentFieldKind? ActualKind { get; init; }

    /// <summary>The key of the family a block size or reservation refusal is about, or null.</summary>
    public string? FamilyKey { get; init; }

    /// <summary>The refused family block size, or null.</summary>
    public int? ActualBlockSize { get; init; }

    /// <summary>The smallest family block size, 16, inclusive, or null.</summary>
    public int? MinimumBlockSize { get; init; }

    /// <summary>The largest family block size, 65,536, inclusive, or null.</summary>
    public int? MaximumBlockSize { get; init; }

    /// <summary>The kind of the remap rule a payload refusal is about, or null.</summary>
    public RemapRuleKind? RuleKind { get; init; }

    /// <summary>The byte length of the refused value, or null.</summary>
    public long? ActualBytes { get; init; }

    /// <summary>The inclusive byte limit of a refused value, or null.</summary>
    public long? MaximumBytes { get; init; }

    /// <summary>The refused language tag as submitted, before normalization, or null.</summary>
    public string? LanguageTag { get; init; }

    /// <summary>The canonical language identity a refusal is about, or null.</summary>
    public string? Language { get; init; }

    /// <summary>The wire spelling a language declaration submitted, or null.</summary>
    public string? WireTag { get; init; }

    /// <summary>The repeated canonical text target, or null.</summary>
    public ContentTextTarget? TextTarget { get; init; }

    /// <summary>The type's inclusive definition-id ceiling, or null.</summary>
    public int? Ceiling { get; init; }

    /// <summary>The highest id already issued when an allocation was refused, or null.</summary>
    public int? IssuedThrough { get; init; }

    /// <summary>The highest id already reserved when an allocation was refused, or null.</summary>
    public int? ReservedThrough { get; init; }

    /// <summary>The number of plain ids the refused range requested, or null.</summary>
    public int? RequestedCount { get; init; }

    /// <summary>The aligned base id of a refused family block, without 32-bit truncation, or null.</summary>
    public long? BaseId { get; init; }

    /// <summary>The highest id of a refused family block, without 32-bit truncation, or null.</summary>
    public long? HighestId { get; init; }

    /// <summary>The absent family id an allocation or reservation named, or null.</summary>
    public long? FamilyId { get; init; }

    /// <summary>The content key of the target a change-set collision compared, or null.</summary>
    public string? RowKey { get; init; }

    /// <summary>The operation already held for a colliding change-set target, or null.</summary>
    public ContentEditOperation? StandingOperation { get; init; }

    /// <summary>The competing operation a change-set collision refused, or null.</summary>
    public ContentEditOperation? RequestedOperation { get; init; }

    /// <summary>The base version the refused publish expected, or null.</summary>
    public int? ExpectedBaseVersion { get; init; }

    /// <summary>The actual baseline version a stale publish compared against, or null.</summary>
    public int? ActualBaseVersion { get; init; }

    /// <summary>The zero-based ordinal of a refused edit, or null.</summary>
    public int? EditOrdinal { get; init; }

    /// <summary>The registered type key of a field edit or an ineligible text slot, or null when unknown.</summary>
    public string? TypeKey { get; init; }

    /// <summary>The runtime type of a pack target without version pointers, or null.</summary>
    public Type? StoreType { get; init; }

    /// <summary>The interface an operator import requires the store to implement, or null.</summary>
    public Type? RequiredCapability { get; init; }

    /// <summary>The operation refusing a pack write, or the malformed text component (TypeKey or FieldName), or null.</summary>
    public string? MemberName { get; init; }

    /// <summary>The version an absent-version, provenance or row-only-plan refusal names, or the reuse baseline version, or null.</summary>
    public int? VersionNumber { get; init; }

    /// <summary>The rollback target version, or null.</summary>
    public int? TargetVersion { get; init; }

    /// <summary>The number of rows blocking a rollback, or null.</summary>
    public long? BlockerCount { get; init; }

    /// <summary>The submitted rule count when it is shorter than the held history, or null.</summary>
    public long? ActualCount { get; init; }

    /// <summary>The inclusive minimum rule count needed to retain the held history, or null.</summary>
    public long? MinimumCount { get; init; }

    /// <summary>The one-based rule sequence whose prefix entry differs, or null.</summary>
    public int? RuleSequence { get; init; }

    /// <summary>The claimed or missing text chunk's content address, or null.</summary>
    public string? TextHash { get; init; }

    /// <summary>The zero-based UTF-16 offset of an unpaired surrogate, or the bundle text value index
    /// for <see cref="ContentAuthoringRefusal.BundleTextRowUnknown"/>, or null.</summary>
    public int? Index { get; init; }

    /// <summary>The repeated fully derived text key, or null.</summary>
    public string? DerivedKey { get; init; }

    /// <summary>The text hash the regenerated values or recorded baseline require, or null.</summary>
    public string? ExpectedTextHash { get; init; }

    /// <summary>The wire spelling a reused text language recorded in the baseline, or null.</summary>
    public string? ExpectedWireTag { get; init; }

    /// <summary>The number of pending row edits blocking a companion import, or null.</summary>
    public int? RowEditCount { get; init; }

    /// <summary>The number of pending text edits a route cannot carry, or null.</summary>
    public int? TextEditCount { get; init; }

    /// <summary>The number of pending language introductions a route cannot carry, or null.</summary>
    public int? LanguageIntroductionCount { get; init; }

    /// <summary>The refused field name's length in UTF-16 code units, or null.</summary>
    public int? ActualCharacters { get; init; }

    /// <summary>The inclusive field-name limit in UTF-16 code units, or null.</summary>
    public int? MaximumCharacters { get; init; }

    /// <summary>The number of languages carried by a planner's baseline bundle, or null.</summary>
    public int? CarriedLanguageCount { get; init; }

    /// <summary>The number of values carried by a planner's baseline bundle, or null.</summary>
    public int? CarriedValueCount { get; init; }

    /// <summary>The number of languages recorded by the planner's exact baseline version, or null.</summary>
    public int? RecordedLanguageCount { get; init; }

    /// <summary>The number of values recorded by the planner's exact baseline version, or null.</summary>
    public int? RecordedValueCount { get; init; }

    /// <summary>The text row named by an export or store refusal, or null. Legacy Type stays default.</summary>
    public ContentTypeId? TextRowType { get; init; }

    /// <summary>The text row id named by an export or store refusal, or null. Legacy Id stays 0.</summary>
    public int? TextRowId { get; init; }

    /// <summary>The version number the refused plan would publish. Null for other refusals.</summary>
    public int? RequestedVersion { get; init; }

    /// <summary>The highest version the store already holds. Null for other refusals.</summary>
    public int? HighestVersion { get; init; }

    /// <summary>The next publishable version after HighestVersion. Null for other refusals.</summary>
    public int? NextVersion { get; init; }

    /// <summary>The type key the refused bundle declares. Null for other refusals.</summary>
    public string? ActualTypeKey { get; init; }

    /// <summary>The registered type key the bundle must match. Null for other refusals.</summary>
    public string? RegisteredTypeKey { get; init; }

    /// <summary>The chunk slot count the refused bundle declares. Null for other refusals.</summary>
    public int? ActualChunkSlots { get; init; }

    /// <summary>The registered chunk slot count the bundle must match. Null for other refusals.</summary>
    public int? RegisteredChunkSlots { get; init; }

    /// <summary>The active version blocking import into a nonempty catalog. Null for other refusals.</summary>
    public int? ActiveVersion { get; init; }

    /// <summary>The object content address where a pack read failed. Null for other refusals.</summary>
    public string? ObjectHash { get; init; }

    /// <summary>The pack reader's stable refusal token. Null for other refusals.</summary>
    public string? PackReaderReason { get; init; }

    /// <summary>The freeze marker blocking a draft write. Null for other refusals.</summary>
    public int? FrozenForBaseVersion { get; init; }

    /// <summary>The upgrade id already recorded in the ledger. Null for other refusals.</summary>
    public string? UpgradeId { get; init; }

    /// <summary>The recorded text chunk count a row-only plan must match. Null for other refusals.</summary>
    public long? ExpectedCount { get; init; }

    /// <summary>The store epoch the text plan read. Null for other refusals.</summary>
    public string? ExpectedEpoch { get; init; }

    /// <summary>The epoch of the store refusing the text plan. Null for other refusals.</summary>
    public string? ActualEpoch { get; init; }

    /// <summary>The starting version of a text revision the store cannot close. Null for other refusals.</summary>
    public int? FromVersion { get; init; }

    /// <summary>The full persisted Int64 type id whose identity disagrees with registration, or null.</summary>
    public long? StoredTypeId { get; init; }

    /// <summary>The stored type key whose identity disagrees with registration, or null.</summary>
    public string? StoredTypeKey { get; init; }

    /// <summary>The unrecognized candidate or persisted edit operation as an integer, or null.</summary>
    public int? ActualOperation { get; init; }

    /// <summary>The persisted database ordinal of an edit with an unrecognized operation, or null.</summary>
    public long? StoredEditOrdinal { get; init; }

    /// <summary>The required CLR type of a malformed persisted scalar, or null.</summary>
    public Type? ExpectedScalarType { get; init; }

    /// <summary>The actual CLR type of a malformed non-null persisted scalar, or null.</summary>
    public Type? ActualScalarType { get; init; }

    /// <summary>The typed SQL Server error number of a transaction-contention refusal, or null.</summary>
    public int? SqlErrorNumber { get; init; }

    /// <summary>The SQL Server application lock return code of a schema refusal, or null.</summary>
    public int? ReturnCode { get; init; }

    /// <summary>The migration required by a schema validation or reset refusal, or null.</summary>
    public string? RequiredMigration { get; init; }

    /// <summary>The compared provider object identity, such as index:ix_catalog_row_live on SQLite, or null.</summary>
    public string? ObjectName { get; init; }

    /// <summary>The schema version whose object definition was expected, or null.</summary>
    public int? ExpectedVersion { get; init; }

    /// <summary>The schema version read from the refused database, or null when unreadable.</summary>
    public long? ActualVersion { get; init; }

    /// <summary>The newest schema version a reset can recreate, or null.</summary>
    public long? MaximumVersion { get; init; }

    /// <summary>The CLR type of a noninteger schema version, or null.</summary>
    public Type? MetadataType { get; init; }

    /// <summary>The number of catalog tables standing before a refused reset, or null.</summary>
    public int? StandingTableCount { get; init; }

    /// <summary>The number of tables the current schema declares, or null.</summary>
    public int? ExpectedTableCount { get; init; }

    /// <summary>Whether a reset requires explicit force to repair the catalog, or null.</summary>
    public bool? ForceRequired { get; init; }

    /// <summary>The number of draft rows blocking reset, or null.</summary>
    public long? DraftCount { get; init; }

    readonly IReadOnlyList<long>? _supportedVersions;
    /// <summary>The schema versions supported by the refusing mode, as an owned snapshot, or null.</summary>
    public IReadOnlyList<long>? SupportedVersions
    {
        get => _supportedVersions;
        init => _supportedVersions = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    readonly IReadOnlyList<ContentHostForeignKeyFinding>? _hostObjects;
    /// <summary>Every host key whose delete action would fire, as an owned snapshot, or null.</summary>
    public IReadOnlyList<ContentHostForeignKeyFinding>? HostObjects
    {
        get => _hostObjects;
        init => _hostObjects = value is null ? null : ImmutableArray.Create(value.ToArray());
    }

    /// <summary>The content type the refusal is about. The default type id 0 means none.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The definition id the refusal is about, or 0 when it is about the store as a whole.</summary>
    public int Id { get; }

    /// <summary>The stable reason token, or null. A log line and a counter key on this, not on the message.</summary>
    public string? Reason { get; }

    /// <summary>
    /// An owned read-only snapshot of every finding behind the refusal, EMPTY for a plain refusal. A publish refused by the
    /// validator and a rollback blocked by a retire both carry theirs here, so the API boundary renders one
    /// body shape whichever refused.
    /// </summary>
    public IReadOnlyList<ContentFinding> Findings { get; } = ImmutableArray<ContentFinding>.Empty;
}
