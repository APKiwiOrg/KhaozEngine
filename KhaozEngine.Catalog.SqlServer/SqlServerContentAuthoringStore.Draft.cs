using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The ONE open draft and its ordered, deduplicated edit list (spec 3.7): <c>catalog_draft</c> holds at most
/// one row and <c>catalog_draft_edit</c> holds one pending intent per target.
/// <para>
/// <b>Dedup and collision are two different answers to a second edit of an occupied target.</b> A second
/// edit under the SAME operation replaces the standing one in its own slot, so a console that saves the same
/// row twice updates one edit rather than queueing two. A second edit under a DIFFERENT operation is
/// REFUSED, because an update followed by a retire would otherwise flip the first edit's operation and drop
/// its fields. The unique index on <c>(type_id, definition_id, content_key)</c> is that rule in the schema.
/// </para>
/// <para>
/// <b>A whole call lands or none of it does.</b> The schema check runs over every edit before any of them is
/// written, and the writes share one Serializable transaction, so a batch save from a grid is atomic and one
/// refusal leaves the open draft exactly as it was.
/// </para>
/// <para>
/// <b>Both writers refuse while a publish holds the draft.</b> The marker is
/// <c>catalog_draft.frozen_for_base_version</c> and the half that sets and clears it is
/// <c>SqlServerContentAuthoringStore.Freeze.cs</c>. Serializable covers step 10 alone, so the marker rather
/// than a lock is what spans steps 1 to 10.
/// </para>
/// <para>
/// <b>An edit's field rows come back in field-name order rather than in insertion order</b>, which is the one
/// read here that does not reproduce the SQLite provider's ordering: SQLite orders by <c>rowid</c> and SQL
/// Server has no equivalent. It is not observable through the seam, because the candidate builder overlays a
/// change onto the schema BY NAME and an edit cannot carry the same field twice, which the primary key
/// enforces. It is observable in the AUDIT, where an edit touching several fields writes its rows in that
/// order, and an audit read is newest-first over an identity anyway.
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore
{
    /// <inheritdoc />
    public Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
        => ReadAsync((scope, token) => ReadDraftAsync(scope, token), cancellationToken);

    /// <inheritdoc />
    public Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        // Every edit is checked against the schema BEFORE any of them is written, so a refusal costs no
        // rollback and names the edit rather than the statement.
        for (int i = 0; i < edits.Count; i++)
        {
            CheckAgainstSchema(edits[i]);
        }

        return WriteAsync(
            (scope, token) => ApplyEditsInAsync(scope, edits, actor, operatorId, note, token),
            cancellationToken);
    }

    /// <summary>
    /// The body of <see cref="ApplyEditsAsync"/> inside the caller's transaction, which is what lets a rollback
    /// re-check its versions and apply its edits in one step. The held text and introductions are untouched: a
    /// row-only batch never rebuilds a draft from its row intents alone. The edits are already schema checked.
    /// </summary>
    async Task<ContentDraft> ApplyEditsInAsync(
        SqlServerCatalogScope scope,
        IReadOnlyList<ContentEdit> edits,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken)
    {
        // Spec 6.2's refusal, inside this call's OWN Serializable transaction, so the marker cannot be written
        // between reading it and writing the edits it guards.
        await RequireNotFrozenAsync(scope, nameof(ApplyEditsAsync), cancellationToken).ConfigureAwait(false);

        await OpenDraftAsync(scope, actor, note, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < edits.Count; i++)
        {
            ContentEdit edit = edits[i];
            await ApplyOneAsync(scope, edit, actor, cancellationToken).ConfigureAwait(false);
            await AppendEditAuditAsync(scope, edit, actor, operatorId, note, cancellationToken).ConfigureAwait(false);
        }

        return await RequireDraftAsync(scope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The open draft inside the transaction that just opened or wrote it.</summary>
    static async Task<ContentDraft> RequireDraftAsync(SqlServerCatalogScope scope, CancellationToken cancellationToken)
        => await ReadDraftAsync(scope, cancellationToken).ConfigureAwait(false)
            ?? throw new ContentAuthoringException(
                "The draft row went missing inside the transaction that opened it.",
                default,
                0,
                ContentAuthoringException.NoOpenDraftReason);

    /// <inheritdoc />
    public Task DiscardDraftAsync(
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        return WriteAsync(
            async (scope, token) =>
            {
                await RequireNotFrozenAsync(scope, nameof(DiscardDraftAsync), token).ConfigureAwait(false);

                // The row-only discard cannot prove what it would delete of the draft's text, so held text or
                // a held introduction refuses it before anything is deleted or audited.
                ContentTextCompatibility.RequireNoHeldText(
                    await ReadDraftAsync(scope, token).ConfigureAwait(false), nameof(DiscardDraftAsync));
                int discarded = await ReadIntAsync(
                    scope, "SELECT COUNT(*) FROM dbo.catalog_draft_edit;", token).ConfigureAwait(false);
                await DeleteDraftAsync(scope, token).ConfigureAwait(false);
                await AppendAuditAsync(
                    scope,
                    ContentAuditActions.DraftDiscard,
                    actor,
                    operatorId,
                    default,
                    0,
                    default,
                    string.Empty,
                    Render(discarded),
                    null,
                    0,
                    string.Empty,
                    token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// The open draft with its row edits, text intents and introductions expanded, or null. It is always a
    /// COMPLETE draft, whose text state is the backend's own, empty when nothing is held. The caller owns the
    /// scope.
    /// </summary>
    static async Task<ContentDraft?> ReadDraftAsync(
        SqlServerCatalogScope scope,
        CancellationToken cancellationToken)
    {
        int baseVersion;
        string openedBy;
        DateTimeOffset openedAt;
        string note;
        int? frozen;
        await using (SqlCommand command = Command(
            scope,
            """
            SELECT base_version, opened_by, opened_at_utc, note, frozen_for_base_version
            FROM dbo.catalog_draft WHERE draft_key = 1;
            """))
        {
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            baseVersion = reader.GetInt32(0);
            openedBy = reader.GetString(1);
            openedAt = reader.GetDateTimeOffset(2);
            note = reader.GetString(3);
            frozen = reader.IsDBNull(4) ? null : reader.GetInt32(4);
        }

        IReadOnlyList<ContentEdit> edits = await ReadEditsAsync(scope, cancellationToken).ConfigureAwait(false);
        ContentDraftTextState text = await ReadDraftTextAsync(scope, cancellationToken).ConfigureAwait(false);
        return new ContentDraft(text, baseVersion, openedBy, openedAt, note, new ContentChangeSet(edits), frozen);
    }

    /// <summary>Every pending edit in EDIT ORDINAL order, which is the order ids are allocated in.</summary>
    static async Task<IReadOnlyList<ContentEdit>> ReadEditsAsync(
        SqlServerCatalogScope scope,
        CancellationToken cancellationToken)
    {
        Dictionary<long, List<ContentFieldEdit>> fields = await ReadEditFieldsAsync(scope, cancellationToken)
            .ConfigureAwait(false);

        await using SqlCommand command = Command(
            scope,
            """
            SELECT edit_ordinal, type_id, definition_id, content_key, operation, retire_policy, replacement_id,
                   fork_key, fork_flag_field, family_id, imported_retired
            FROM dbo.catalog_draft_edit
            ORDER BY edit_ordinal;
            """);

        var edits = new List<ContentEdit>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long ordinal = reader.GetInt64(0);
            var type = new ContentTypeId((ushort)reader.GetInt32(1));
            int definitionId = reader.GetInt32(2);
            var key = new ContentKey(reader.GetString(3));
            var operation = (ContentEditOperation)reader.GetInt32(4);
            IReadOnlyList<ContentFieldEdit> payload =
                fields.TryGetValue(ordinal, out List<ContentFieldEdit>? held) ? held : [];

            edits.Add(operation switch
            {
                // An Add and an import are one factory: an ordinary add is exactly an import that carries no
                // id and is not already retired.
                ContentEditOperation.Add => ContentEdit.Import(
                    type,
                    definitionId,
                    key,
                    payload,
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.GetInt32(10) != 0),
                ContentEditOperation.Update => ContentEdit.Update(type, definitionId, key, payload),
                ContentEditOperation.Retire => ContentEdit.Retire(
                    type,
                    definitionId,
                    key,
                    (ContentRetirePolicy)reader.GetInt32(5),
                    reader.GetInt32(6)),
                ContentEditOperation.Fork => ContentEdit.Fork(
                    type,
                    definitionId,
                    key,
                    new ContentKey(reader.GetString(7)),
                    reader.GetString(8),
                    payload),
                _ => throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Draft edit {ordinal} carries operation {(int)operation}, which is not one of the four of spec 3.7."),
                    type,
                    definitionId,
                    ContentAuthoringException.UnknownEditOperationReason),
            });
        }

        return edits;
    }

    static async Task<Dictionary<long, List<ContentFieldEdit>>> ReadEditFieldsAsync(
        SqlServerCatalogScope scope,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT edit_ordinal, field_name, field_kind, int_value, blob_value
            FROM dbo.catalog_draft_edit_field
            ORDER BY edit_ordinal, field_name;
            """);

        var byEdit = new Dictionary<long, List<ContentFieldEdit>>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long ordinal = reader.GetInt64(0);
            if (!byEdit.TryGetValue(ordinal, out List<ContentFieldEdit>? held))
            {
                held = [];
                byEdit.Add(ordinal, held);
            }

            held.Add(new ContentFieldEdit(
                reader.GetString(1), ReadValue(reader, (ContentFieldKind)reader.GetInt32(2), 3, 4)));
        }

        return byEdit;
    }

    /// <summary>
    /// Opens the draft against the active version when none is open, and otherwise carries the standing one
    /// forward, replacing its note when this call brought a different one. The caller owns the transaction.
    /// <para>
    /// The draft row's <c>updated_at_utc</c> moves only with the note: an edit is a row of its own, with its own
    /// times, and adding one changes nothing in this row. The note comparison adds the byte length, because
    /// SQL Server pads the shorter string before it compares, and a note that differs only in trailing blanks is
    /// still a different value.
    /// </para>
    /// </summary>
    async Task OpenDraftAsync(
        SqlServerCatalogScope scope,
        string actor,
        string note,
        CancellationToken cancellationToken)
    {
        int active = await ReadActiveVersionAsync(scope, cancellationToken).ConfigureAwait(false);

        await using SqlCommand command = Command(
            scope,
            """
            MERGE dbo.catalog_draft WITH (HOLDLOCK) AS target
            USING (SELECT 1 AS draft_key) AS source ON target.draft_key = source.draft_key
            WHEN MATCHED AND @note <> N''
                AND (target.note <> @note OR DATALENGTH(target.note) <> DATALENGTH(@note)) THEN
                UPDATE SET note = @note, updated_at_utc = @at
            WHEN NOT MATCHED THEN INSERT (draft_key, base_version, opened_by, opened_at_utc, note, updated_at_utc)
                VALUES (1, @base, @actor, @at, @note, @at);
            """);
        BindInt(command, "@base", active);
        BindText(command, "@actor", actor);
        BindTime(command, "@at", _clock());
        BindText(command, "@note", note);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One edit written into the draft, replacing the standing one for its target or refusing it.</summary>
    async Task ApplyOneAsync(
        SqlServerCatalogScope scope,
        ContentEdit edit,
        string actor,
        CancellationToken cancellationToken)
    {
        (long Ordinal, ContentEditOperation Operation)? standing =
            await ReadStandingAsync(scope, edit, cancellationToken).ConfigureAwait(false);

        if (standing is { } held && held.Operation != edit.Operation)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The open draft already holds a {held.Operation} edit for type {edit.Type.Value} row {edit.DefinitionId} ('{edit.Key}'), so a {edit.Operation} of the same row is refused. A draft holds one pending intent per row."),
                edit.Type,
                edit.DefinitionId,
                ContentAuthoringException.EditTargetCollisionReason);
        }

        // One reading per edit, shared by the edit row and its field rows, which are written together.
        DateTimeOffset at = _clock();
        long ordinal;
        if (standing is { } replace)
        {
            // Same target, same operation: the newer edit wins the slot the older one already holds, so the
            // draft never carries two intents for one row and the edit keeps its place in the order. It keeps
            // its creation time too, and edited_at_utc is its update time.
            ordinal = replace.Ordinal;
            await using (SqlCommand update = Command(
                scope,
                """
                UPDATE dbo.catalog_draft_edit
                SET retire_policy = @policy, replacement_id = @replacement, fork_key = @forkKey,
                    fork_flag_field = @forkFlag, family_id = @family, imported_retired = @importedRetired,
                    edited_by = @actor, edited_at_utc = @at
                WHERE edit_ordinal = @ordinal;
                """))
            {
                BindEdit(update, edit, actor, at);
                BindBigInt(update, "@ordinal", ordinal);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using SqlCommand clear = Command(
                scope, "DELETE FROM dbo.catalog_draft_edit_field WHERE edit_ordinal = @ordinal;");
            BindBigInt(clear, "@ordinal", ordinal);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using SqlCommand insert = Command(
                scope,
                """
                INSERT INTO dbo.catalog_draft_edit(
                    type_id, definition_id, content_key, operation, retire_policy, replacement_id, fork_key,
                    fork_flag_field, family_id, imported_retired, edited_by, edited_at_utc, created_at_utc)
                VALUES (@type, @id, @key, @operation, @policy, @replacement, @forkKey, @forkFlag, @family,
                        @importedRetired, @actor, @at, @at);
                SELECT CAST(SCOPE_IDENTITY() AS bigint);
                """);
            BindEdit(insert, edit, actor, at);
            BindInt(insert, "@type", (int)edit.Type.Value);
            BindInt(insert, "@id", edit.DefinitionId);
            BindText(insert, "@key", edit.Key.ToString());
            BindInt(insert, "@operation", (int)edit.Operation);
            object? raw = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            ordinal = raw is long identity
                ? identity
                : throw new ContentAuthoringException(
                    "The draft edit insert returned no identity, so its field rows have nothing to hang off.",
                    edit.Type,
                    edit.DefinitionId,
                    ContentAuthoringException.SchemaMismatchReason);
        }

        for (int i = 0; i < edit.Fields.Count; i++)
        {
            ContentFieldEdit field = edit.Fields[i];
            await using SqlCommand command = Command(
                scope,
                """
                INSERT INTO dbo.catalog_draft_edit_field(
                    edit_ordinal, field_name, field_kind, int_value, text_value, blob_value, created_at_utc)
                VALUES (@ordinal, @name, @kind, @int, NULL, @blob, @at);
                """);
            BindBigInt(command, "@ordinal", ordinal);
            BindTime(command, "@at", at);
            BindText(command, "@name", field.Name);
            BindInt(command, "@kind", (int)field.Value.Kind);
            BindBigInt(
                command,
                "@int",
                ContentFieldValue.StoresNumber(field.Value.Kind) && !field.Value.IsAbsent
                    ? field.Value.Number
                    : null);
            BindBlob(
                command,
                "@blob",
                ContentFieldValue.StoresBytes(field.Value.Kind) && !field.Value.IsAbsent
                    ? field.Value.Bytes.ToArray()
                    : null);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    static async Task<(long Ordinal, ContentEditOperation Operation)?> ReadStandingAsync(
        SqlServerCatalogScope scope,
        ContentEdit edit,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT edit_ordinal, operation FROM dbo.catalog_draft_edit
            WHERE type_id = @type AND definition_id = @id AND content_key = @key;
            """);
        BindInt(command, "@type", (int)edit.Type.Value);
        BindInt(command, "@id", edit.DefinitionId);
        BindText(command, "@key", edit.Key.ToString());

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), (ContentEditOperation)reader.GetInt32(1))
            : null;
    }

    static void BindEdit(SqlCommand command, ContentEdit edit, string actor, DateTimeOffset at)
    {
        BindInt(command, "@policy", (int)edit.RetirePolicy);
        BindInt(command, "@replacement", edit.ReplacementId);
        BindText(command, "@forkKey", edit.ForkKey.IsEmpty ? null : edit.ForkKey.ToString());
        BindText(command, "@forkFlag", edit.ForkFlagField);
        BindBigInt(command, "@family", edit.FamilyId);
        BindInt(command, "@importedRetired", edit.ImportedAsRetired ? 1 : 0);
        BindText(command, "@actor", actor);
        BindTime(command, "@at", at);
    }

    /// <summary>
    /// The draft, its edits, its text intents and its introductions, gone. The edit fields go with the edits
    /// through the cascade the schema declares. The caller owns the transaction.
    /// </summary>
    static async Task DeleteDraftAsync(SqlServerCatalogScope scope, CancellationToken cancellationToken)
    {
        foreach (string statement in (string[])[
            "DELETE FROM dbo.catalog_draft_edit;",
            "DELETE FROM dbo.catalog_draft_text_edit;",
            "DELETE FROM dbo.catalog_draft_text_language;"])
        {
            await using SqlCommand edits = Command(scope, statement);
            await edits.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqlCommand draft = Command(scope, "DELETE FROM dbo.catalog_draft;");
        await draft.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An edit may only name fields its type declares, and may never author a value for a DERIVED marker,
    /// whose key is a function of the row it sits on. Both are refused at the boundary rather than at publish.
    /// </summary>
    void CheckAgainstSchema(ContentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        ContentTypeRegistration registration = RequireType(edit.Type);
        for (int i = 0; i < edit.Fields.Count; i++)
        {
            ContentFieldEdit field = edit.Fields[i];
            if (!registration.Schema.TryGet(field.Name, out ContentFieldEntry? entry))
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Content type {registration.Type.Value} '{registration.TypeKey}' declares no field named '{field.Name}', so the edit is refused at the boundary rather than at publish."),
                    edit.Type,
                    edit.DefinitionId,
                    ContentAuthoringException.UnknownFieldReason);
            }

            if (entry.IsDerivedMarker)
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Field '{field.Name}' of content type {registration.Type.Value} '{registration.TypeKey}' is a derived marker, whose key is a function of the row it sits on, so an edit cannot author a value for it."),
                    edit.Type,
                    edit.DefinitionId,
                    ContentAuthoringException.UnknownFieldReason);
            }
        }
    }
}
