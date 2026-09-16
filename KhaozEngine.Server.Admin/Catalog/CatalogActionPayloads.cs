using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// One schema field as a console receives it (spec 10.3). <see cref="Derived"/> is the property that makes a
/// GENERIC editor possible: it says the value is not the console's to set without the console having to know
/// which kind names are derived, so the cell renders read only whatever the kind turns out to be.
/// </summary>
/// <param name="Name">The field name, which is also the segment its localization key is derived from.</param>
/// <param name="Kind">The <see cref="ContentFieldKind"/> name, which is what picks the editor's cell renderer.</param>
/// <param name="Target">The content type key a key reference or a tag list points at, null for every other kind.</param>
/// <param name="Visibility">Whether a client ever sees the value.</param>
/// <param name="Required">Whether a live row must carry a value.</param>
/// <param name="Scale">The fixed scale of a scaled int, so a console divides the stored integer by it. Every other kind carries 1.</param>
/// <param name="Derived">Whether the value is DERIVED from the row rather than authored, which is read only.</param>
public sealed record CatalogSchemaField(
    string Name,
    string Kind,
    string? Target,
    string Visibility,
    bool Required,
    int Scale,
    bool Derived);

/// <summary>One registered content type with its whole field list, in declared order.</summary>
/// <param name="TypeId">The stable numeric type id.</param>
/// <param name="TypeKey">The stable string type key, which every other action names the type by.</param>
/// <param name="Visibility">The visibility a field of this type inherits when it declares none.</param>
/// <param name="ChunkSlots">Id slots per chunk, so a chunk boundary is the id divided by this number.</param>
/// <param name="MaxRowBytes">This type's own row cap.</param>
/// <param name="MaxDefinitionId">The per-type id ceiling, or null when its only ceiling is the format's.</param>
/// <param name="Fields">The fields in declared order, which is the order a row's values are parallel to.</param>
public sealed record CatalogSchemaType(
    int TypeId,
    string TypeKey,
    string Visibility,
    int ChunkSlots,
    int MaxRowBytes,
    int? MaxDefinitionId,
    IReadOnlyList<CatalogSchemaField> Fields);

/// <summary>
/// The whole registered schema (<c>catalog-schema</c>), which is what makes ONE editor render a type the
/// console has never heard of. Types come back sorted ascending by type id, always, because nothing anywhere
/// derives an ordinal from registration order.
/// </summary>
/// <param name="Generation">The engine's pack format generation.</param>
/// <param name="Types">Every registered type, ascending by type id.</param>
public sealed record CatalogSchemaPayload(int Generation, IReadOnlyList<CatalogSchemaType> Types);

/// <summary>
/// One row as a console receives it: its id, its key, its retired bit and its field values keyed by field
/// name.
/// <para>
/// <b>The int id is shown to authors, read only beside the key.</b> An operator reading a quarantine reason
/// or a log line needs to be able to look the id up.
/// </para>
/// </summary>
/// <param name="Id">The definition id, unique and never reused within its type.</param>
/// <param name="Key">The row key, immutable once published.</param>
/// <param name="Retired">Whether the row is retired.</param>
/// <param name="Fields">The values keyed by field name, rendered by <see cref="CatalogFieldRendering"/>.</param>
public sealed record CatalogRowPayload(
    int Id,
    string Key,
    bool Retired,
    IReadOnlyDictionary<string, object?> Fields);

/// <summary>
/// ONE row version out of a definition's history, which is the temporal model's payoff: "when did this price
/// change and what was it before" is answered from the row table rather than reconstructed from an audit.
/// </summary>
/// <param name="ValidFrom">The version the row became valid in.</param>
/// <param name="ReplacedIn">The version it was replaced in, or null while it is still live.</param>
/// <param name="FamilyId">The family its id was allocated from, or null.</param>
/// <param name="Retired">Whether the row was retired at this revision.</param>
/// <param name="Fields">The values as they stood.</param>
public sealed record CatalogRowRevisionPayload(
    int ValidFrom,
    int? ReplacedIn,
    long? FamilyId,
    bool Retired,
    IReadOnlyDictionary<string, object?> Fields);

/// <summary>
/// One audited FIELD change. The unit is one field, so an update that changed three fields is three entries
/// sharing an occurred-at, an actor, an operator and a note.
/// </summary>
/// <param name="AuditId">The store's own monotonic row id.</param>
/// <param name="OccurredAtUtc">When the change was written.</param>
/// <param name="Actor">What the engine AUTHENTICATED, which is the bearer token's holder.</param>
/// <param name="Operator">What the console ASSERTED, which the engine does not verify.</param>
/// <param name="Action">One of the seven audit action names.</param>
/// <param name="TypeKey">The content type's key, or empty for a store-level action.</param>
/// <param name="Id">The row's id, or 0 for a store-level action.</param>
/// <param name="Key">The row's key, or empty for a store-level action.</param>
/// <param name="Field">The schema field name, empty for a row-level action such as a retire.</param>
/// <param name="Before">The old value rendered, null for absent.</param>
/// <param name="After">The new value rendered, null for absent.</param>
/// <param name="Version">0 for a draft edit, the published number for a publish.</param>
/// <param name="Note">The operator's note, empty when none.</param>
public sealed record CatalogAuditPayload(
    long AuditId,
    DateTimeOffset OccurredAtUtc,
    string Actor,
    string Operator,
    string Action,
    string TypeKey,
    int Id,
    string Key,
    string Field,
    string? Before,
    string? After,
    int Version,
    string Note);

/// <summary>
/// One page of a type's rows (<c>catalog-list</c>). The TOTAL is carried beside the page because a console
/// that cannot see how many rows it did not get shows the first screenful and says nothing.
/// </summary>
/// <param name="Version">The version the rows were read at, which resolves the caller's 0.</param>
/// <param name="Total">How many rows matched, before skip and take.</param>
/// <param name="Skip">The skip that was applied.</param>
/// <param name="Take">The take that was applied AFTER the server-side cap, so a clamped ask says so.</param>
/// <param name="Rows">The page, ordered by definition id.</param>
public sealed record CatalogListPayload(
    int Version,
    int Total,
    int Skip,
    int Take,
    IReadOnlyList<CatalogRowPayload> Rows);

/// <summary>
/// One row plus its full version history (<c>catalog-get</c>), and its audit trail when the request asked
/// for one.
/// </summary>
/// <param name="TypeKey">The content type's key.</param>
/// <param name="Id">The definition id, resolved from the key when the request named one.</param>
/// <param name="Key">The row key.</param>
/// <param name="Row">The row as it stands now.</param>
/// <param name="History">Every revision, oldest first.</param>
/// <param name="Audit">The row's audit entries newest first, or null when the request did not ask for them.</param>
public sealed record CatalogGetPayload(
    string TypeKey,
    int Id,
    string Key,
    CatalogRowPayload Row,
    IReadOnlyList<CatalogRowRevisionPayload> History,
    IReadOnlyList<CatalogAuditPayload>? Audit);

/// <summary>The open draft's header, which is what a pending-changes panel shows above its edit list.</summary>
/// <param name="BaseVersion">The published version the edits are against, 0 on a database that has published none.</param>
/// <param name="EditCount">How many edits are pending.</param>
/// <param name="OpenedBy">The identity that opened it.</param>
/// <param name="OpenedAtUtc">When it was opened.</param>
/// <param name="Note">The operator's note, empty when none.</param>
/// <param name="Frozen">True while a publish holds the draft, which is when every write to it is refused.</param>
/// <param name="FrozenForBaseVersion">The base version the publish in flight froze it for, or null.</param>
public sealed record CatalogDraftHeader(
    int BaseVersion,
    int EditCount,
    string OpenedBy,
    DateTimeOffset OpenedAtUtc,
    string Note,
    bool Frozen,
    int? FrozenForBaseVersion)
{
    /// <summary>One draft's header, which every action that touches the draft answers with the same shape of.</summary>
    /// <param name="draft">The open draft.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draft"/> is null.</exception>
    public static CatalogDraftHeader Of(ContentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new CatalogDraftHeader(
            draft.BaseVersion,
            draft.EditCount,
            draft.OpenedBy,
            draft.OpenedAtUtc,
            draft.Note,
            draft.IsFrozen,
            draft.FrozenForBaseVersion);
    }
}

/// <summary>
/// One pending edit, expanded. An ADD carries id 0, because ids are allocated at publish and reporting one
/// from a draft would be reporting a number that does not exist yet.
/// </summary>
/// <param name="Op">The operation, in the same lower-case vocabulary an edit request uses.</param>
/// <param name="TypeKey">The content type's key.</param>
/// <param name="Id">The row the edit targets, or 0 for an add.</param>
/// <param name="Key">The row's key.</param>
/// <param name="Fields">The CHANGED fields only, keyed by field name.</param>
/// <param name="RetirePolicy">A retire's policy, or null on every other operation.</param>
/// <param name="ReplacementId">A replacement-policy retire's destination id, 0 otherwise.</param>
/// <param name="ForkKey">A fork's copy key, or null on every other operation.</param>
/// <param name="FlagField">The Bool field a fork sets on the copy, or null.</param>
/// <param name="FamilyId">The family an add allocates its id from, or null.</param>
public sealed record CatalogDraftEditPayload(
    string Op,
    string TypeKey,
    int Id,
    string Key,
    IReadOnlyDictionary<string, object?> Fields,
    string? RetirePolicy,
    int ReplacementId,
    string? ForkKey,
    string? FlagField,
    long? FamilyId);

/// <summary>The open draft with its edits expanded (<c>catalog-draft</c>), or a null draft when none is open.</summary>
/// <param name="Draft">The draft header, or null when no draft is open.</param>
/// <param name="Edits">The pending edits, empty when no draft is open.</param>
public sealed record CatalogDraftPayload(CatalogDraftHeader? Draft, IReadOnlyList<CatalogDraftEditPayload> Edits);

/// <summary>One published version's record, carrying both manifest hashes because a pack has two sides.</summary>
/// <param name="Version">The version number.</param>
/// <param name="ServerManifestHash">The server manifest digest, lower hex.</param>
/// <param name="ClientManifestHash">The client manifest digest, lower hex, which the connect door carries.</param>
/// <param name="MinimumServerBuild">Consumer supplied, compared and never interpreted.</param>
/// <param name="MinimumClientBuild">Consumer supplied, compared and never interpreted.</param>
/// <param name="FormatGeneration">The pack format generation this version was written at.</param>
/// <param name="BaseVersion">The version it was published from, 0 for the first.</param>
/// <param name="PublishedBy">The identity that published it.</param>
/// <param name="Note">The publisher's note, empty when none.</param>
/// <param name="PublishedAtUtc">When the publish transaction committed.</param>
public sealed record CatalogVersionPayload(
    int Version,
    string ServerManifestHash,
    string ClientManifestHash,
    int MinimumServerBuild,
    int MinimumClientBuild,
    int FormatGeneration,
    int BaseVersion,
    string PublishedBy,
    string Note,
    DateTimeOffset PublishedAtUtc);

/// <summary>
/// The version line (<c>catalog-versions</c>): the active version, the operator's hold and every record,
/// newest first. It is the read a pin and a rollback are both decided from.
/// </summary>
/// <param name="ActiveVersion">The version the last publish committed, 0 when the store has published none.</param>
/// <param name="PinnedVersion">The operator's hold, or null for the ordinary no-pin state.</param>
/// <param name="Versions">Every published version, newest first.</param>
public sealed record CatalogVersionsPayload(
    int ActiveVersion,
    int? PinnedVersion,
    IReadOnlyList<CatalogVersionPayload> Versions);

/// <summary>
/// How a stored field value becomes JSON, in ONE place, so every action renders a row the same way and a
/// console writes one set of cell renderers.
/// <para>
/// A NUMBER stays a number and is the STORED integer, which for a scaled int is the value times the schema's
/// scale. The scale is on the schema, so a console divides rather than being handed a rounded number it
/// cannot get back. Bytes render as lower hex, the same rendering the audit ledger uses, so the two agree.
/// An absent field is null rather than a sentinel, and a DERIVED marker renders as its derived key and is
/// never a stored value.
/// </para>
/// </summary>
public static class CatalogFieldRendering
{
    /// <summary>Every field of one row, keyed by field name, in the schema's declared order.</summary>
    /// <param name="registration">The row's type, whose schema the values are parallel to.</param>
    /// <param name="row">The row.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyDictionary<string, object?> Fields(ContentTypeRegistration registration, ContentRow row)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(row);

        IReadOnlyList<ContentFieldEntry> fields = registration.Schema.Fields;
        var rendered = new Dictionary<string, object?>(fields.Count, StringComparer.Ordinal);
        for (int i = 0; i < fields.Count; i++)
        {
            ContentFieldEntry field = fields[i];
            ContentFieldValue value = i < row.Fields.Count
                ? row.Fields[i]
                : ContentFieldValue.Absent(field.Kind);

            rendered[field.Name] = field.IsDerivedMarker
                ? ContentTextKey.Derive(registration.TypeKey, row.Key.Utf8, field.Name)
                : Value(value);
        }

        return rendered;
    }

    /// <summary>The CHANGED fields of one draft edit, keyed by field name, in authored order.</summary>
    /// <param name="edit">The edit.</param>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is null.</exception>
    public static IReadOnlyDictionary<string, object?> Fields(ContentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var rendered = new Dictionary<string, object?>(edit.Fields.Count, StringComparer.Ordinal);
        for (int i = 0; i < edit.Fields.Count; i++)
        {
            ContentFieldEdit field = edit.Fields[i];
            rendered[field.Name] = Value(field.Value);
        }

        return rendered;
    }

    /// <summary>One value rendered through its kind, or null when the row carries none.</summary>
    /// <param name="value">The field value.</param>
    public static object? Value(ContentFieldValue value)
    {
        if (value.IsAbsent)
        {
            return null;
        }

        return value.Kind switch
        {
            ContentFieldKind.Bool => value.Number != 0,
            ContentFieldKind.TagList => TagIds(value.Bytes.Span),
            ContentFieldKind.OpaqueBytes => Convert.ToHexString(value.Bytes.Span).ToLowerInvariant(),
            ContentFieldKind.LocalizedTextKey => null,
            _ => value.Number,
        };
    }

    /// <summary>
    /// A tag list's ids in AUTHORED order, which is the order they were written in and is never sorted. The
    /// bytes are varint ids with no count of their own, because the count is structure the row walk owns.
    /// </summary>
    /// <param name="bytes">The tag list's bytes.</param>
    public static int[] TagIds(ReadOnlySpan<byte> bytes)
    {
        var ids = new List<int>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (!ContentVarint.TryRead(bytes, ref offset, out uint raw, out _))
            {
                // A list this walk cannot read is a codec defect the validator reports, so it stops here
                // rather than guessing at the remaining bytes.
                break;
            }

            ids.Add(unchecked((int)raw));
        }

        return ids.ToArray();
    }
}
