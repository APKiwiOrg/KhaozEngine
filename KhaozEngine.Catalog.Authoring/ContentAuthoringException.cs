using System;
using System.Collections.Generic;

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
/// </summary>
public sealed class ContentAuthoringException : Exception
{
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
    /// publish the same draft: the second one's expectation is stale and it is refused with both numbers
    /// named, which turns a race into an error message.
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
    /// A version's pack could not be read back: an absent manifest, a chunk the store no longer holds, or
    /// bytes that do not digest to the address they were filed under. The pack reader's own reason token is
    /// in the message.
    /// </summary>
    public const string PackUnreadableReason = "pack-unreadable";

    /// <summary>
    /// There is no usable pack target: either the store was built with none, or the one it was handed writes
    /// no version pointer and a published version could therefore never be enumerated out of it. Publishing
    /// writes files before it writes rows, so the target is not optional and neither half of it is.
    /// </summary>
    public const string NoPackStoreReason = "no-pack-store";

    /// <summary>A bundle document could not be read: a format version this build does not know, or a malformed member.</summary>
    public const string BundleFormatReason = "bundle-format";

    /// <summary>
    /// A provider's database is absent under <see cref="ContentAuthoringSchemaMode.ValidateOnly"/>, carries a
    /// schema version the build does not support, or holds an object that does not match the shape its
    /// migration declares. The message names the object and the migration, because those are the two facts an
    /// operator can act on.
    /// </summary>
    public const string SchemaMismatchReason = "schema-mismatch";

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
        : base(message)
    {
        Type = type;
        Id = id;
        Reason = reason;
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
        : this(message, type, id, reason)
    {
        ArgumentNullException.ThrowIfNull(findings);
        Findings = findings;
    }

    /// <summary>The content type the refusal is about. The default type id 0 means none.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The definition id the refusal is about, or 0 when it is about the store as a whole.</summary>
    public int Id { get; }

    /// <summary>The stable reason token, or null. A log line and a counter key on this, not on the message.</summary>
    public string? Reason { get; }

    /// <summary>
    /// Every finding behind the refusal, EMPTY when the refusal is a plain one. A publish refused by the
    /// validator and a rollback blocked by a retire both carry theirs here, so the API boundary renders one
    /// body shape whichever refused.
    /// </summary>
    public IReadOnlyList<ContentFinding> Findings { get; } = [];
}
