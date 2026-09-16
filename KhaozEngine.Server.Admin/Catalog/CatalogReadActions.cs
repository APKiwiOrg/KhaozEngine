using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// The five READ actions of spec 10.3, 10.4 and 10.7: <c>catalog-schema</c>, <c>catalog-list</c>,
/// <c>catalog-get</c>, <c>catalog-draft</c> and <c>catalog-versions</c>.
/// <para>
/// <b>Every handler runs on the HTTP REQUEST THREAD and never touches simulation state</b>, which these are
/// compliant with by construction: they read the AUTHORING STORE, which is a database the tick loop never
/// reads, and they never reach the runtime the tick loop does read.
/// </para>
/// <para>
/// <b>A refusal is a 400 carrying a reason, never a throw.</b> An operator typing a type key by hand is the
/// ordinary case, so a request naming a type that is not registered gets a message naming it rather than a
/// 500 with a stack trace behind it.
/// </para>
/// </summary>
/// <param name="store">The authoring store every read goes through.</param>
/// <param name="registry">The registry the type keys and the schemas come from. Per instance, never ambient.</param>
internal sealed class CatalogReadActions(IContentAuthoringStore store, ContentTypeRegistry registry)
{
    /// <summary>Registers the five names on the admin surface.</summary>
    /// <param name="admin">The surface to register on.</param>
    public void Register(ServerAdmin admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.RegisterAction(CatalogAdminActions.SchemaAction, (_, _) => Task.FromResult(Schema()));
        admin.RegisterAction(CatalogAdminActions.ListAction, ListAsync);
        admin.RegisterAction(CatalogAdminActions.GetAction, GetAsync);
        admin.RegisterAction(CatalogAdminActions.DraftAction, DraftAsync);
        admin.RegisterAction(CatalogAdminActions.VersionsAction, VersionsAsync);
    }

    /// <summary>
    /// The whole registered schema, which is what a generic editor is built on: a type gets its editor by
    /// REGISTERING, so there is no such thing as a content type with no page.
    /// </summary>
    AdminActionResult Schema()
    {
        IReadOnlyList<ContentTypeRegistration> registrations = registry.ByTypeId;
        var types = new List<CatalogSchemaType>(registrations.Count);
        foreach (ContentTypeRegistration registration in registrations)
        {
            IReadOnlyList<ContentFieldEntry> schema = registration.Schema.Fields;
            var fields = new List<CatalogSchemaField>(schema.Count);
            foreach (ContentFieldEntry field in schema)
            {
                fields.Add(new CatalogSchemaField(
                    field.Name,
                    field.Kind.ToString(),
                    field.ReferenceTarget,
                    field.Visibility.ToString(),
                    field.Required,
                    field.Scale,
                    field.IsDerivedMarker));
            }

            types.Add(new CatalogSchemaType(
                registration.Type.Value,
                registration.TypeKey,
                registration.DefaultVisibility.ToString(),
                registration.ChunkSlots,
                registration.MaxRowBytes,
                registration.MaxDefinitionId,
                fields));
        }

        return AdminActionResult.Ok(new CatalogSchemaPayload(ContentPackFormat.Generation, types));
    }

    /// <summary>
    /// One page of a type's rows. <c>version</c> 0 means the current live set, and the response says which
    /// version it read at, so a console never has to assume which number 0 resolved to.
    /// </summary>
    async Task<AdminActionResult> ListAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        if (payload is not JsonElement body)
        {
            return Refuse(CatalogAdminActions.ListAction + " needs a JSON body naming 'typeKey'.");
        }

        if (!TryType(body, out ContentTypeRegistration? type, out string? refusal)
            || !TryVersion(body, out int version, out refusal)
            || !TryCount(body, "skip", 0, out int skip, out refusal)
            || !TryCount(body, "take", CatalogAdminActions.DefaultPageSize, out int take, out refusal)
            || !TryOptionalString(body, "keyPrefix", out string? keyPrefix, out refusal)
            || !TryFlag(body, "includeRetired", out bool includeRetired, out refusal))
        {
            return Refuse(refusal);
        }

        if (take < 1)
        {
            return Refuse("'take' asks for at least 1 row.");
        }

        // The cap is SERVER side, and the echoed take reports it, which is what makes a console page rather
        // than conclude the catalog holds what one page happened to carry.
        take = Math.Min(take, CatalogAdminActions.MaxPageSize);

        ContentRowPage page = await store
            .ListRowsAsync(type.Type, version, keyPrefix, includeRetired, skip, take, cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<CatalogRowPayload>(page.Rows.Count);
        foreach (ContentRow row in page.Rows)
        {
            rows.Add(Row(type, row));
        }

        return AdminActionResult.Ok(new CatalogListPayload(page.VersionNumber, page.Total, skip, take, rows));
    }

    /// <summary>
    /// One row plus its full version HISTORY, by id or by key. An operator reads a key off a design document
    /// and an id off a log line, so both reach the row.
    /// </summary>
    async Task<AdminActionResult> GetAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        if (payload is not JsonElement body)
        {
            return Refuse(CatalogAdminActions.GetAction + " needs a JSON body naming 'typeKey' and an 'id' or a 'key'.");
        }

        if (!TryType(body, out ContentTypeRegistration? type, out string? refusal)
            || !TryCount(body, "id", 0, out int id, out refusal)
            || !TryOptionalString(body, "key", out string? key, out refusal)
            || !TryFlag(body, "includeAudit", out bool includeAudit, out refusal))
        {
            return Refuse(refusal);
        }

        if (id < 1 && key is null)
        {
            return Refuse(CatalogAdminActions.GetAction + " names a row by 'id' or by 'key', and this request named neither.");
        }

        if (id < 1)
        {
            ContentRow? found = await FindByKeyAsync(type, key!, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                return Refuse(FormattableString.Invariant(
                    $"No row of type '{type.TypeKey}' carries the key '{key}'."));
            }

            id = found.Id;
        }

        IReadOnlyList<ContentRowRevision> revisions = await store
            .GetRowHistoryAsync(type.Type, id, cancellationToken)
            .ConfigureAwait(false);

        if (revisions.Count == 0)
        {
            return Refuse(FormattableString.Invariant(
                $"No row of type '{type.TypeKey}' carries the id {id.ToString(CultureInfo.InvariantCulture)}."));
        }

        var history = new List<CatalogRowRevisionPayload>(revisions.Count);
        ContentRowRevision live = revisions[revisions.Count - 1];
        foreach (ContentRowRevision revision in revisions)
        {
            if (revision.ReplacedInVersion is null)
            {
                live = revision;
            }

            history.Add(new CatalogRowRevisionPayload(
                revision.ValidFromVersion,
                revision.ReplacedInVersion,
                revision.FamilyId,
                revision.Row.IsRetired,
                CatalogFieldRendering.Fields(type, revision.Row)));
        }

        IReadOnlyList<CatalogAuditPayload>? audit = includeAudit
            ? await AuditAsync(type, id, cancellationToken).ConfigureAwait(false)
            : null;

        return AdminActionResult.Ok(new CatalogGetPayload(
            type.TypeKey,
            id,
            live.Row.Key.ToString(),
            Row(type, live.Row),
            history,
            audit));
    }

    /// <summary>
    /// The open draft with its edits EXPANDED, so a console can show a pending-changes panel. A store with no
    /// draft open answers a null draft rather than a 404, because "nothing pending" is an answer.
    /// </summary>
    async Task<AdminActionResult> DraftAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        ContentDraft? draft = await store.GetOpenDraftAsync(cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return AdminActionResult.Ok(new CatalogDraftPayload(null, []));
        }

        IReadOnlyList<ContentEdit> pending = draft.Changes.Edits;
        var edits = new List<CatalogDraftEditPayload>(pending.Count);
        foreach (ContentEdit edit in pending)
        {
            edits.Add(new CatalogDraftEditPayload(
                OperationName(edit.Operation),
                registry.TryGet(edit.Type, out ContentTypeRegistration? type) ? type.TypeKey : string.Empty,
                edit.DefinitionId,
                edit.Key.ToString(),
                CatalogFieldRendering.Fields(edit),
                PolicyName(edit.RetirePolicy),
                edit.ReplacementId,
                edit.ForkKey.IsEmpty ? null : edit.ForkKey.ToString(),
                edit.ForkFlagField,
                edit.FamilyId));
        }

        return AdminActionResult.Ok(new CatalogDraftPayload(
            new CatalogDraftHeader(
                draft.BaseVersion,
                draft.EditCount,
                draft.OpenedBy,
                draft.OpenedAtUtc,
                draft.Note,
                draft.IsFrozen,
                draft.FrozenForBaseVersion),
            edits));
    }

    /// <summary>The active version, the operator's hold and every version record, newest first.</summary>
    async Task<AdminActionResult> VersionsAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        int active = await store.GetActiveVersionAsync(cancellationToken).ConfigureAwait(false);
        int? pinned = await store.GetPinnedVersionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ContentVersionRecord> records = await store
            .ListVersionsAsync(cancellationToken)
            .ConfigureAwait(false);

        var versions = new List<CatalogVersionPayload>(records.Count);
        foreach (ContentVersionRecord record in records)
        {
            versions.Add(new CatalogVersionPayload(
                record.VersionNumber,
                record.ServerManifestHash,
                record.ClientManifestHash,
                record.MinimumServerBuild,
                record.MinimumClientBuild,
                record.FormatGeneration,
                record.BaseVersion,
                record.PublishedBy,
                record.Note,
                record.PublishedAtUtc));
        }

        return AdminActionResult.Ok(new CatalogVersionsPayload(active, pinned, versions));
    }

    /// <summary>
    /// The row one key names, found through the seam's own prefix filter. The prefix can match siblings (a
    /// key is a prefix of every longer key), so the walk pages until it finds the EXACT key or runs out.
    /// </summary>
    async Task<ContentRow?> FindByKeyAsync(
        ContentTypeRegistration type,
        string key,
        CancellationToken cancellationToken)
    {
        var wanted = new ContentKey(key);
        int skip = 0;
        while (true)
        {
            ContentRowPage page = await store
                .ListRowsAsync(type.Type, 0, key, true, skip, CatalogAdminActions.MaxPageSize, cancellationToken)
                .ConfigureAwait(false);

            foreach (ContentRow row in page.Rows)
            {
                if (row.Key.Equals(wanted))
                {
                    return row;
                }
            }

            skip += page.Rows.Count;
            if (page.Rows.Count == 0 || skip >= page.Total)
            {
                return null;
            }
        }
    }

    /// <summary>One row's audit entries, newest first, FILTERED to that row.</summary>
    async Task<IReadOnlyList<CatalogAuditPayload>> AuditAsync(
        ContentTypeRegistration type,
        int id,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentAuditEntry> entries = await store
            .ListAuditAsync(type.Type, id, 0, CatalogAdminActions.MaxPageSize, cancellationToken)
            .ConfigureAwait(false);

        var audit = new List<CatalogAuditPayload>(entries.Count);
        foreach (ContentAuditEntry entry in entries)
        {
            audit.Add(new CatalogAuditPayload(
                entry.AuditId,
                entry.OccurredAtUtc,
                entry.Actor,
                entry.Operator,
                entry.Action,
                registry.TryGet(entry.Type, out ContentTypeRegistration? entryType) ? entryType.TypeKey : string.Empty,
                entry.DefinitionId,
                entry.Key.ToString(),
                entry.FieldName,
                entry.BeforeValue,
                entry.AfterValue,
                entry.VersionNumber,
                entry.Note));
        }

        return audit;
    }

    static CatalogRowPayload Row(ContentTypeRegistration type, ContentRow row)
        => new(row.Id, row.Key.ToString(), row.IsRetired, CatalogFieldRendering.Fields(type, row));

    /// <summary>The lower-case operation vocabulary an edit REQUEST uses, so a draft reads back in it.</summary>
    static string OperationName(ContentEditOperation operation) => operation switch
    {
        ContentEditOperation.Add => "add",
        ContentEditOperation.Update => "update",
        ContentEditOperation.Retire => "retire",
        ContentEditOperation.Fork => "fork",
        _ => "unknown",
    };

    static string? PolicyName(ContentRetirePolicy policy) => policy switch
    {
        ContentRetirePolicy.Placeholder => "placeholder",
        ContentRetirePolicy.Replacement => "replacement",
        _ => null,
    };

    static AdminActionResult Refuse(string? reason)
        => AdminActionResult.BadRequest(reason ?? "the request was refused.");

    /// <summary>The registered type a request names, or a refusal naming the key that did not resolve.</summary>
    bool TryType(JsonElement body, out ContentTypeRegistration type, out string? refusal)
    {
        type = null!;
        if (!TryOptionalString(body, "typeKey", out string? typeKey, out refusal))
        {
            return false;
        }

        if (typeKey is null)
        {
            refusal = "'typeKey' names the content type to read, and this request carries none.";
            return false;
        }

        if (!registry.TryGetByKey(typeKey, out ContentTypeRegistration? found))
        {
            refusal = FormattableString.Invariant($"No content type is registered under the type key '{typeKey}'.");
            return false;
        }

        type = found;
        return true;
    }

    static bool TryVersion(JsonElement body, out int version, out string? refusal)
    {
        if (!TryCount(body, "version", 0, out version, out refusal))
        {
            return false;
        }

        // 0 is the caller asking for the current live set, so only a negative number is wrong.
        return true;
    }

    /// <summary>A non-negative integer property, its default when absent, or a refusal naming it.</summary>
    static bool TryCount(JsonElement body, string name, int fallback, out int value, out string? refusal)
    {
        refusal = null;
        value = fallback;
        if (!body.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out value))
        {
            value = fallback;
            refusal = FormattableString.Invariant($"'{name}' is a whole number, and this request carries something else.");
            return false;
        }

        if (value < 0)
        {
            refusal = FormattableString.Invariant($"'{name}' may not be negative.");
            return false;
        }

        return true;
    }

    static bool TryOptionalString(JsonElement body, string name, out string? value, out string? refusal)
    {
        refusal = null;
        value = null;
        if (!body.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            refusal = FormattableString.Invariant($"'{name}' is a string, and this request carries something else.");
            return false;
        }

        value = property.GetString();
        return true;
    }

    static bool TryFlag(JsonElement body, string name, out bool value, out string? refusal)
    {
        refusal = null;
        value = false;
        if (!body.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            refusal = FormattableString.Invariant($"'{name}' is true or false, and this request carries something else.");
            return false;
        }

        value = property.GetBoolean();
        return true;
    }
}
