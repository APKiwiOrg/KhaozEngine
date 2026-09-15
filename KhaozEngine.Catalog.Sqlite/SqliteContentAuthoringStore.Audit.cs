using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The field-level audit half: the append every mutating member makes INSIDE its own transaction, and the
/// paged newest-first read.
/// <para>
/// <b>The append is never best effort.</b> A content edit with no audit row is indistinguishable from no
/// edit, so the insert shares the edit's transaction and an audit failure fails the edit (spec 4.6).
/// </para>
/// <para>
/// The two renderers below duplicate the in-memory store's, which is internal to another assembly and
/// therefore unreachable from here. Two copies of one rule is a drift candidate and the answer is a shared
/// renderer in the authoring package, which is filed rather than done inside this task.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentAuditEntry>> ListAuditAsync(
        ContentTypeId type,
        int definitionId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = Command(
            """
            SELECT audit_id, occurred_at_utc, actor, operator, action, type_id, definition_id, content_key,
                   field_name, before_value, after_value, version_number, note
            FROM catalog_audit
            WHERE ($type = 0 OR type_id = $type)
              AND ($id = 0 OR definition_id = $id)
            ORDER BY audit_id DESC
            LIMIT $take OFFSET $skip;
            """);
        Bind(command, "$type", (long)type.Value);
        Bind(command, "$id", (long)definitionId);
        Bind(command, "$take", (long)Math.Min(take, MaxPageSize));
        Bind(command, "$skip", (long)skip);

        var entries = new List<ContentAuditEntry>();
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new ContentAuditEntry(
                reader.GetInt64(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                new ContentTypeId((ushort)reader.GetInt64(5)),
                (int)reader.GetInt64(6),
                new ContentKey(reader.GetString(7)),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                (int)reader.GetInt64(11),
                reader.GetString(12)));
        }

        return entries;
    }

    /// <summary>One audit row, stamped from this store's own clock. The caller owns the transaction.</summary>
    async Task AppendAuditAsync(
        SqliteTransaction transaction,
        string action,
        string actor,
        string operatorId,
        ContentTypeId type,
        int definitionId,
        ContentKey key,
        string fieldName,
        string? before,
        string? after,
        int versionNumber,
        string note,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            INSERT INTO catalog_audit(
                occurred_at_utc, actor, operator, action, type_id, definition_id, content_key, field_name,
                before_value, after_value, version_number, note)
            VALUES ($at, $actor, $operator, $action, $type, $id, $key, $field, $before, $after, $version, $note);
            """,
            transaction);
        Bind(command, "$at", Millis(_clock()));
        Bind(command, "$actor", actor);
        Bind(command, "$operator", operatorId);
        Bind(command, "$action", action);
        Bind(command, "$type", (long)type.Value);
        Bind(command, "$id", (long)definitionId);
        Bind(command, "$key", key.ToString());
        Bind(command, "$field", fieldName);
        Bind(command, "$before", before);
        Bind(command, "$after", after);
        Bind(command, "$version", (long)versionNumber);
        Bind(command, "$note", note);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One edit's audit rows: one per CHANGED FIELD, or one row-level entry naming the operation when the
    /// edit changes none, so a retire still leaves a trace. The caller owns the transaction.
    /// </summary>
    async Task AppendEditAuditAsync(
        SqliteTransaction transaction,
        ContentEdit edit,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken)
    {
        if (edit.Fields.Count == 0)
        {
            await AppendAuditAsync(
                transaction,
                ContentAuditActions.DraftEdit,
                actor,
                operatorId,
                edit.Type,
                edit.DefinitionId,
                edit.Key,
                string.Empty,
                null,
                edit.Operation.ToString(),
                0,
                note,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        for (int i = 0; i < edit.Fields.Count; i++)
        {
            ContentFieldEdit field = edit.Fields[i];
            await AppendAuditAsync(
                transaction,
                ContentAuditActions.DraftEdit,
                actor,
                operatorId,
                edit.Type,
                edit.DefinitionId,
                edit.Key,
                field.Name,
                null,
                Render(field.Value),
                0,
                note,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A number rendered the way an audit column holds it, invariant culture, null for absent.</summary>
    static string? Render(int? value)
        => value is int number ? number.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// One field value rendered through its kind. A rendering that would not fit the audit column is
    /// abbreviated VISIBLY, with a trailing marker, so a reader can never take an abbreviated value for a
    /// complete one.
    /// </summary>
    static string? Render(ContentFieldValue value)
    {
        if (value.IsAbsent)
        {
            return null;
        }

        string rendered = ContentFieldValue.StoresNumber(value.Kind)
            ? value.Number.ToString(CultureInfo.InvariantCulture)
            : Convert.ToHexString(value.Bytes.Span).ToLowerInvariant();

        return rendered.Length <= ContentAuditEntry.MaxValueLength
            ? rendered
            : string.Concat(rendered.AsSpan(0, ContentAuditEntry.MaxValueLength - 5), "[cut]");
    }
}
