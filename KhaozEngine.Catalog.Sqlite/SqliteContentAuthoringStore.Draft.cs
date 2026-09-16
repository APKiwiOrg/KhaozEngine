using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

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
/// written, and the writes share one transaction, so a batch save from a grid is atomic and one refusal
/// leaves the open draft exactly as it was.
/// </para>
/// <para>
/// <b>Both writers refuse while a publish holds the draft.</b> The marker is
/// <c>catalog_draft.frozen_for_base_version</c> and the half that sets and clears it is
/// <c>SqliteContentAuthoringStore.Freeze.cs</c>. The read is inside each writer's own transaction, so the
/// marker cannot arrive between the check and the write it guards.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore
{
    /// <inheritdoc />
    public async Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDraftAsync(null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContentDraft> ApplyEditsAsync(
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

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();

        // Spec 6.2's refusal, inside this call's OWN transaction, so the marker cannot be written between
        // reading it and writing the edits it guards.
        await RequireNotFrozenAsync(nameof(ApplyEditsAsync), transaction, cancellationToken)
            .ConfigureAwait(false);

        await OpenDraftAsync(actor, note, transaction, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < edits.Count; i++)
        {
            ContentEdit edit = edits[i];
            await ApplyOneAsync(edit, actor, transaction, cancellationToken).ConfigureAwait(false);
            await AppendEditAuditAsync(transaction, edit, actor, operatorId, note, cancellationToken)
                .ConfigureAwait(false);
        }

        ContentDraft draft = await ReadDraftAsync(transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new ContentAuthoringException(
                "The draft row went missing inside the transaction that opened it.",
                default,
                0,
                ContentAuthoringException.NoOpenDraftReason);

        transaction.Commit();
        return draft;
    }

    /// <inheritdoc />
    public async Task DiscardDraftAsync(
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        await RequireNotFrozenAsync(nameof(DiscardDraftAsync), transaction, cancellationToken)
            .ConfigureAwait(false);
        int discarded = await CountEditsAsync(transaction, cancellationToken).ConfigureAwait(false);
        await DeleteDraftAsync(transaction, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(
            transaction,
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
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    /// <summary>The open draft with its edits expanded, or null. The caller already holds the lease.</summary>
    async Task<ContentDraft?> ReadDraftAsync(SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        int baseVersion;
        string openedBy;
        DateTimeOffset openedAt;
        string note;
        int? frozen;
        using (SqliteCommand command = Command(
            """
            SELECT base_version, opened_by, opened_at_utc, note, frozen_for_base_version
            FROM catalog_draft WHERE draft_key = 1;
            """,
            transaction))
        {
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            baseVersion = (int)reader.GetInt64(0);
            openedBy = reader.GetString(1);
            openedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
            note = reader.GetString(3);
            frozen = reader.IsDBNull(4) ? null : (int)reader.GetInt64(4);
        }

        IReadOnlyList<ContentEdit> edits = await ReadEditsAsync(transaction, cancellationToken)
            .ConfigureAwait(false);
        return new ContentDraft(baseVersion, openedBy, openedAt, note, new ContentChangeSet(edits), frozen);
    }

    /// <summary>Every pending edit in EDIT ORDINAL order, which is the order ids are allocated in.</summary>
    async Task<IReadOnlyList<ContentEdit>> ReadEditsAsync(
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        Dictionary<long, List<ContentFieldEdit>> fields = await ReadEditFieldsAsync(transaction, cancellationToken)
            .ConfigureAwait(false);

        using SqliteCommand command = Command(
            """
            SELECT edit_ordinal, type_id, definition_id, content_key, operation, retire_policy, replacement_id,
                   fork_key, fork_flag_field, family_id, imported_retired
            FROM catalog_draft_edit
            ORDER BY edit_ordinal;
            """,
            transaction);

        var edits = new List<ContentEdit>();
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long ordinal = reader.GetInt64(0);
            var type = new ContentTypeId((ushort)reader.GetInt64(1));
            int definitionId = (int)reader.GetInt64(2);
            var key = new ContentKey(reader.GetString(3));
            var operation = (ContentEditOperation)reader.GetInt64(4);
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
                    reader.GetInt64(10) != 0),
                ContentEditOperation.Update => ContentEdit.Update(type, definitionId, key, payload),
                ContentEditOperation.Retire => ContentEdit.Retire(
                    type,
                    definitionId,
                    key,
                    (ContentRetirePolicy)reader.GetInt64(5),
                    (int)reader.GetInt64(6)),
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

    async Task<Dictionary<long, List<ContentFieldEdit>>> ReadEditFieldsAsync(
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT edit_ordinal, field_name, field_kind, int_value, blob_value
            FROM catalog_draft_edit_field
            ORDER BY edit_ordinal, rowid;
            """,
            transaction);

        var byEdit = new Dictionary<long, List<ContentFieldEdit>>();
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long ordinal = reader.GetInt64(0);
            if (!byEdit.TryGetValue(ordinal, out List<ContentFieldEdit>? held))
            {
                held = [];
                byEdit.Add(ordinal, held);
            }

            held.Add(new ContentFieldEdit(
                reader.GetString(1), ReadValue(reader, (ContentFieldKind)reader.GetInt64(2), 3, 4)));
        }

        return byEdit;
    }

    /// <summary>
    /// Opens the draft against the active version when none is open, and otherwise carries the standing one
    /// forward, replacing its note when this call brought one. The caller owns the transaction.
    /// </summary>
    async Task OpenDraftAsync(
        string actor,
        string note,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        long active = await ReadLongAsync(
            "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", transaction, cancellationToken)
            .ConfigureAwait(false);

        using SqliteCommand command = Command(
            """
            INSERT INTO catalog_draft(draft_key, base_version, opened_by, opened_at_utc, note)
            VALUES (1, $base, $actor, $at, $note)
            ON CONFLICT(draft_key) DO UPDATE SET note = CASE WHEN $note = '' THEN note ELSE $note END;
            """,
            transaction);
        Bind(command, "$base", active);
        Bind(command, "$actor", actor);
        Bind(command, "$at", Millis(_clock()));
        Bind(command, "$note", note);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One edit written into the draft, replacing the standing one for its target or refusing it.</summary>
    async Task ApplyOneAsync(
        ContentEdit edit,
        string actor,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        (long Ordinal, ContentEditOperation Operation)? standing =
            await ReadStandingAsync(edit, transaction, cancellationToken).ConfigureAwait(false);

        if (standing is { } held && held.Operation != edit.Operation)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The open draft already holds a {held.Operation} edit for type {edit.Type.Value} row {edit.DefinitionId} ('{edit.Key}'), so a {edit.Operation} of the same row is refused. A draft holds one pending intent per row."),
                edit.Type,
                edit.DefinitionId,
                ContentAuthoringException.EditTargetCollisionReason);
        }

        long ordinal;
        if (standing is { } replace)
        {
            // Same target, same operation: the newer edit wins the slot the older one already holds, so the
            // draft never carries two intents for one row and the edit keeps its place in the order.
            ordinal = replace.Ordinal;
            using SqliteCommand update = Command(
                """
                UPDATE catalog_draft_edit
                SET retire_policy = $policy, replacement_id = $replacement, fork_key = $forkKey,
                    fork_flag_field = $forkFlag, family_id = $family, imported_retired = $importedRetired,
                    edited_by = $actor, edited_at_utc = $at
                WHERE edit_ordinal = $ordinal;
                """,
                transaction);
            BindEdit(update, edit, actor);
            Bind(update, "$ordinal", ordinal);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            using SqliteCommand clear = Command(
                "DELETE FROM catalog_draft_edit_field WHERE edit_ordinal = $ordinal;", transaction);
            Bind(clear, "$ordinal", ordinal);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using SqliteCommand insert = Command(
                """
                INSERT INTO catalog_draft_edit(
                    type_id, definition_id, content_key, operation, retire_policy, replacement_id, fork_key,
                    fork_flag_field, family_id, imported_retired, edited_by, edited_at_utc)
                VALUES ($type, $id, $key, $operation, $policy, $replacement, $forkKey, $forkFlag, $family,
                        $importedRetired, $actor, $at);
                """,
                transaction);
            BindEdit(insert, edit, actor);
            Bind(insert, "$type", (long)edit.Type.Value);
            Bind(insert, "$id", (long)edit.DefinitionId);
            Bind(insert, "$key", edit.Key.ToString());
            Bind(insert, "$operation", (long)edit.Operation);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            ordinal = await ReadLongAsync("SELECT last_insert_rowid();", transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        for (int i = 0; i < edit.Fields.Count; i++)
        {
            ContentFieldEdit field = edit.Fields[i];
            using SqliteCommand command = Command(
                """
                INSERT INTO catalog_draft_edit_field(
                    edit_ordinal, field_name, field_kind, int_value, text_value, blob_value)
                VALUES ($ordinal, $name, $kind, $int, NULL, $blob);
                """,
                transaction);
            Bind(command, "$ordinal", ordinal);
            Bind(command, "$name", field.Name);
            Bind(command, "$kind", (long)field.Value.Kind);
            Bind(
                command,
                "$int",
                ContentFieldValue.StoresNumber(field.Value.Kind) && !field.Value.IsAbsent
                    ? field.Value.Number
                    : null);
            Bind(
                command,
                "$blob",
                ContentFieldValue.StoresBytes(field.Value.Kind) && !field.Value.IsAbsent
                    ? field.Value.Bytes.ToArray()
                    : null);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    async Task<(long Ordinal, ContentEditOperation Operation)?> ReadStandingAsync(
        ContentEdit edit,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT edit_ordinal, operation FROM catalog_draft_edit
            WHERE type_id = $type AND definition_id = $id AND content_key = $key;
            """,
            transaction);
        Bind(command, "$type", (long)edit.Type.Value);
        Bind(command, "$id", (long)edit.DefinitionId);
        Bind(command, "$key", edit.Key.ToString());

        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), (ContentEditOperation)reader.GetInt64(1))
            : null;
    }

    void BindEdit(SqliteCommand command, ContentEdit edit, string actor)
    {
        Bind(command, "$policy", (long)edit.RetirePolicy);
        Bind(command, "$replacement", (long)edit.ReplacementId);
        Bind(command, "$forkKey", edit.ForkKey.IsEmpty ? null : edit.ForkKey.ToString());
        Bind(command, "$forkFlag", edit.ForkFlagField);
        Bind(command, "$family", edit.FamilyId);
        Bind(command, "$importedRetired", edit.ImportedAsRetired ? 1L : 0L);
        Bind(command, "$actor", actor);
        Bind(command, "$at", Millis(_clock()));
    }

    async Task<int> CountEditsAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
        => (int)await ReadLongAsync("SELECT COUNT(*) FROM catalog_draft_edit;", transaction, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The draft and its edits, gone. The edit fields go with the edits through the cascade the schema
    /// declares, which is why the bootstrap turns foreign keys on.
    /// </summary>
    async Task DeleteDraftAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using (SqliteCommand edits = Command("DELETE FROM catalog_draft_edit;", transaction))
        {
            await edits.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using SqliteCommand draft = Command("DELETE FROM catalog_draft;", transaction);
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
