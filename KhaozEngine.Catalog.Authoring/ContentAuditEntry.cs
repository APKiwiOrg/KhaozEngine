using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The <c>action</c> vocabulary a <see cref="ContentAuditEntry"/> carries (spec 4.6). Eight names, fixed, so
/// a counter, an operator runbook and a provider's <c>CHECK</c> can all key on the same token.
/// </summary>
public static class ContentAuditActions
{
    /// <summary>An edit applied to the open draft.</summary>
    public const string DraftEdit = "draft-edit";

    /// <summary>The open draft discarded, carrying the edit count so a discard leaves a trace.</summary>
    public const string DraftDiscard = "draft-discard";

    /// <summary>A version published.</summary>
    public const string Publish = "publish";

    /// <summary>The operator's version hold written or cleared.</summary>
    public const string Pin = "pin";

    /// <summary>A rollback draft built from an earlier version.</summary>
    public const string Rollback = "rollback";

    /// <summary>A bundle imported into an empty database.</summary>
    public const string BulkImport = "bulk-import";

    /// <summary>A family created with its first reserved block.</summary>
    public const string FamilyCreate = "family-create";

    /// <summary>
    /// A pack sweep that DELETED something, carrying the count. A sweep that deleted nothing writes no row,
    /// because an audit an operator has to page through to find the deletions is worse than one that only
    /// holds them, and a no-op sweep is what a monitoring script runs on a timer.
    /// </summary>
    public const string Sweep = "sweep";

    /// <summary>
    /// A content upgrade recorded in the ledger. The row carries the disposition as its field name, the
    /// upgrade id as its after value, and the version the upgrade published or was recorded against.
    /// </summary>
    public const string ContentUpgrade = "content-upgrade";
}

/// <summary>
/// ONE audited field change (spec 4.6). The unit of an audit row is one FIELD, not one row and not one
/// request, so an update that changes three fields writes three entries sharing an occurred-at, an actor, an
/// operator and a note.
/// <para>
/// <b>The append is IN the edit's transaction, never best effort.</b> A content edit with no audit row is
/// indistinguishable from no edit, and the whole reason this store exists is that an owner authors values a
/// player's economy depends on. If the audit insert fails, the edit fails.
/// </para>
/// <para>
/// <b><see cref="Actor"/> and <see cref="Operator"/> are two different facts</b> (spec 10.10). The actor is
/// what the engine AUTHENTICATED, which is the bearer token's holder and is one token rather than an
/// identity. The operator is what the console ASSERTED and the engine does not verify. Both columns stay so
/// the row can say which is which, and the operator is documented as taking a STABLE identity: a console
/// passing a display name gets an audit trail that breaks when someone changes their name.
/// </para>
/// </summary>
/// <param name="AuditId">The store's own monotonic row id.</param>
/// <param name="OccurredAtUtc">When the change was written.</param>
/// <param name="Actor">What the engine authenticated, at most 128 characters.</param>
/// <param name="Operator">What the console forwarded, empty when it forwarded none, at most 128 characters.</param>
/// <param name="Action">One of <see cref="ContentAuditActions"/>.</param>
/// <param name="Type">The content type, or the default type id 0 for a store-level action.</param>
/// <param name="DefinitionId">The row's id, or 0 for a store-level action.</param>
/// <param name="Key">The row's key, or the default key for a store-level action.</param>
/// <param name="FieldName">The schema field name, empty for a row-level action such as a retire.</param>
/// <param name="BeforeValue">The old value rendered through the field's kind, invariant culture, null for absent.</param>
/// <param name="AfterValue">The new value rendered the same way, null for absent.</param>
/// <param name="VersionNumber">0 for a draft edit, the published number for a publish.</param>
/// <param name="Note">The operator's note, empty when none.</param>
public sealed record ContentAuditEntry(
    long AuditId,
    DateTimeOffset OccurredAtUtc,
    string Actor,
    string Operator,
    string Action,
    ContentTypeId Type,
    int DefinitionId,
    ContentKey Key,
    string FieldName,
    string? BeforeValue,
    string? AfterValue,
    int VersionNumber,
    string Note)
{
    /// <summary>
    /// The cap a rendered value is written under, matching <c>catalog_audit.before_value</c> and
    /// <c>after_value</c> (spec 4.6). It is 4,096 and not lower because the audit insert shares the edit's
    /// transaction, so a value the COLUMN refuses is an EDIT the database refuses, arriving as a constraint
    /// error rather than as a finding. A rendering that still would not fit is abbreviated VISIBLY, with a
    /// trailing marker, so a reader can never take an abbreviated value for a complete one.
    /// </summary>
    public const int MaxValueLength = 4096;

    /// <summary>The cap an actor or an operator identity is written under.</summary>
    public const int MaxIdentityLength = 128;
}
