using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The field-level audit half: the append every mutating member makes INSIDE its own transaction, and the
/// paged newest-first read.
/// <para>
/// <b>The append is never best effort.</b> A content edit with no audit row is indistinguishable from no
/// edit, so the insert shares the edit's transaction and an audit failure fails the edit (spec 4.6).
/// </para>
/// <para>
/// The two renderers below are the THIRD copy of one rule, after the in-memory store's and the SQLite
/// provider's. The type that owns it is internal to <c>KhaozEngine.Catalog.Authoring</c> and therefore
/// unreachable from here. A shared renderer in the authoring package is the remedy and it is filed rather
/// than done inside this task
/// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/919">issue 919</see>, item 2).
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ContentAuditEntry>> ListAuditAsync(
        ContentTypeId type,
        int definitionId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);

        return ReadAsync(
            async (scope, token) =>
            {
                await using SqlCommand command = Command(
                    scope,
                    """
                    SELECT audit_id, occurred_at_utc, actor, [operator], action, type_id, definition_id,
                           content_key, field_name, before_value, after_value, version_number, note
                    FROM dbo.catalog_audit
                    WHERE (@type = 0 OR type_id = @type)
                      AND (@id = 0 OR definition_id = @id)
                    ORDER BY audit_id DESC
                    OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
                    """);
                Bind(command, "@type", (int)type.Value);
                Bind(command, "@id", definitionId);
                Bind(command, "@skip", skip);
                Bind(command, "@take", Math.Min(take, MaxPageSize));

                var entries = new List<ContentAuditEntry>();
                await using SqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    entries.Add(new ContentAuditEntry(
                        reader.GetInt64(0),
                        reader.GetDateTimeOffset(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        new ContentTypeId((ushort)reader.GetInt32(5)),
                        reader.GetInt32(6),
                        new ContentKey(reader.GetString(7)),
                        reader.GetString(8),
                        reader.IsDBNull(9) ? null : reader.GetString(9),
                        reader.IsDBNull(10) ? null : reader.GetString(10),
                        reader.GetInt32(11),
                        reader.GetString(12)));
                }

                return (IReadOnlyList<ContentAuditEntry>)entries;
            },
            cancellationToken);
    }

    /// <summary>One audit row, stamped from this store's own clock. The caller owns the transaction.</summary>
    async Task AppendAuditAsync(
        SqlServerCatalogScope scope,
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
        await using SqlCommand command = Command(
            scope,
            """
            INSERT INTO dbo.catalog_audit(
                occurred_at_utc, actor, [operator], action, type_id, definition_id, content_key, field_name,
                before_value, after_value, version_number, note)
            VALUES (@at, @actor, @operator, @action, @type, @id, @key, @field, @before, @after, @version, @note);
            """);
        Bind(command, "@at", _clock());
        Bind(command, "@actor", actor);
        Bind(command, "@operator", operatorId);
        Bind(command, "@action", action);
        Bind(command, "@type", (int)type.Value);
        Bind(command, "@id", definitionId);
        Bind(command, "@key", key.ToString());
        Bind(command, "@field", fieldName);
        Bind(command, "@before", before);
        Bind(command, "@after", after);
        Bind(command, "@version", versionNumber);
        Bind(command, "@note", note);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One edit's audit rows: one per CHANGED FIELD, or one row-level entry naming the operation when the
    /// edit changes none, so a retire still leaves a trace. The caller owns the transaction.
    /// </summary>
    async Task AppendEditAuditAsync(
        SqlServerCatalogScope scope,
        ContentEdit edit,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken)
    {
        if (edit.Fields.Count == 0)
        {
            await AppendAuditAsync(
                scope,
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
                scope,
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
