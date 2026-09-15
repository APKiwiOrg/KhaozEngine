using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The temporal row half: reading revisions out of <c>catalog_row</c> and <c>catalog_row_field</c>, and
/// writing the ones a publish inserts.
/// <para>
/// <b>The encoded row blob is NOT stored</b> (spec 4.3). A row's values live one per field, which is what
/// makes the audit and the diff field level without decoding anything, and the chunk bytes are computed at
/// publish through the type's codec. Storing both would be two sources of truth for one row with no
/// mechanism to keep them equal.
/// </para>
/// <para>
/// <b>A field row exists only for a value the row actually carries.</b> A
/// <see cref="ContentFieldKind.LocalizedTextKey"/> is a derived marker whose key is a function of the row it
/// sits on, so it writes no row at all, and an ABSENT optional field writes none either: a read rebuilds the
/// full field list from the type's schema and fills what is missing with
/// <see cref="ContentFieldValue.Absent"/>, so the values stay parallel BY INDEX to the schema whatever the
/// table holds.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore
{
    /// <inheritdoc />
    /// <remarks>Version 0 reads the ACTIVE version's live set, the same answer the in-memory reference gives:
    /// the draft-applied overlay is the publish pipeline's candidate rather than something approximated
    /// here.</remarks>
    public async Task<ContentRowPage> ListRowsAsync(
        ContentTypeId type,
        int versionNumber,
        string? keyPrefix,
        bool includeRetired,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        int at = versionNumber == 0
            ? (int)await ReadLongAsync(
                "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", null, cancellationToken)
                .ConfigureAwait(false)
            : versionNumber;

        IReadOnlyList<ContentRowRevision> live = await ReadRevisionsAsync(
            type, null, at, null, cancellationToken).ConfigureAwait(false);

        var matched = new List<ContentRow>();
        for (int i = 0; i < live.Count; i++)
        {
            ContentRow row = live[i].Row;
            if (!includeRetired && row.IsRetired)
            {
                continue;
            }

            if (keyPrefix is not null
                && !row.Key.ToString().StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            matched.Add(row);
        }

        matched.Sort(static (left, right) => left.Id.CompareTo(right.Id));

        int from = Math.Min(skip, matched.Count);
        int count = Math.Min(Math.Min(take, MaxPageSize), matched.Count - from);
        return new ContentRowPage(at, matched.Count, matched.GetRange(from, count));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentRowRevision>> GetRowHistoryAsync(
        ContentTypeId type,
        int definitionId,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRevisionsAsync(type, definitionId, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Row revisions, ordered by type then id then valid-from, filtered three ways: to one type (id 0 means
    /// every type), to one definition, and to those LIVE at one version. The caller already holds the lease.
    /// </summary>
    async Task<IReadOnlyList<ContentRowRevision>> ReadRevisionsAsync(
        ContentTypeId type,
        int? definitionId,
        int? liveAtVersion,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var fields = await ReadRowFieldsAsync(type, definitionId, liveAtVersion, transaction, cancellationToken)
            .ConfigureAwait(false);

        using SqliteCommand command = Command(
            """
            SELECT type_id, definition_id, valid_from_version, replaced_in_version, content_key, parent_id,
                   family_id, retired
            FROM catalog_row
            WHERE ($type = 0 OR type_id = $type)
              AND ($id IS NULL OR definition_id = $id)
              AND ($at IS NULL OR (valid_from_version <= $at
                   AND (replaced_in_version IS NULL OR replaced_in_version > $at)))
            ORDER BY type_id, definition_id, valid_from_version;
            """,
            transaction);
        Bind(command, "$type", (long)type.Value);
        Bind(command, "$id", definitionId is int id ? (long)id : null);
        Bind(command, "$at", liveAtVersion is int at ? (long)at : null);

        var revisions = new List<ContentRowRevision>();
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rowType = new ContentTypeId((ushort)reader.GetInt64(0));
            int rowId = (int)reader.GetInt64(1);
            int validFrom = (int)reader.GetInt64(2);
            ContentFieldSchema schema = RequireType(rowType).Schema;
            var key = new RowKey(rowType.Value, rowId, validFrom);

            var values = new ContentFieldValue[schema.Fields.Count];
            for (int i = 0; i < values.Length; i++)
            {
                ContentFieldEntry entry = schema.Fields[i];
                values[i] = fields.TryGetValue(key, out Dictionary<string, ContentFieldValue>? held)
                    && held.TryGetValue(entry.Name, out ContentFieldValue value)
                        ? value
                        : ContentFieldValue.Absent(entry.Kind);
            }

            var row = new ContentRow(
                rowType,
                rowId,
                new ContentKey(reader.GetString(4)),
                (int)reader.GetInt64(5),
                reader.GetInt64(7) != 0,
                values);

            revisions.Add(new ContentRowRevision(
                row,
                validFrom,
                reader.IsDBNull(3) ? null : (int)reader.GetInt64(3),
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        }

        return revisions;
    }

    async Task<Dictionary<RowKey, Dictionary<string, ContentFieldValue>>> ReadRowFieldsAsync(
        ContentTypeId type,
        int? definitionId,
        int? liveAtVersion,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT f.type_id, f.definition_id, f.valid_from_version, f.field_name, f.field_kind,
                   f.int_value, f.blob_value
            FROM catalog_row_field f
            JOIN catalog_row r ON r.type_id = f.type_id
                AND r.definition_id = f.definition_id
                AND r.valid_from_version = f.valid_from_version
            WHERE ($type = 0 OR r.type_id = $type)
              AND ($id IS NULL OR r.definition_id = $id)
              AND ($at IS NULL OR (r.valid_from_version <= $at
                   AND (r.replaced_in_version IS NULL OR r.replaced_in_version > $at)));
            """,
            transaction);
        Bind(command, "$type", (long)type.Value);
        Bind(command, "$id", definitionId is int id ? (long)id : null);
        Bind(command, "$at", liveAtVersion is int at ? (long)at : null);

        var byRow = new Dictionary<RowKey, Dictionary<string, ContentFieldValue>>();
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new RowKey((ushort)reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2));
            if (!byRow.TryGetValue(key, out Dictionary<string, ContentFieldValue>? held))
            {
                held = new Dictionary<string, ContentFieldValue>(StringComparer.Ordinal);
                byRow.Add(key, held);
            }

            held[reader.GetString(3)] = ReadValue(reader, (ContentFieldKind)reader.GetInt64(4), 5, 6);
        }

        return byRow;
    }

    /// <summary>One row revision and its field rows. The caller owns the transaction.</summary>
    async Task InsertRowAsync(ContentRowInsert insert, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        ContentRow row = insert.Row;
        using (SqliteCommand command = Command(
            """
            INSERT INTO catalog_row(
                type_id, definition_id, valid_from_version, replaced_in_version, content_key, parent_id,
                family_id, retired)
            VALUES ($type, $id, $from, NULL, $key, $parent, $family, $retired);
            """,
            transaction))
        {
            Bind(command, "$type", (long)row.Type.Value);
            Bind(command, "$id", (long)row.Id);
            Bind(command, "$from", (long)insert.ValidFromVersion);
            Bind(command, "$key", row.Key.ToString());
            Bind(command, "$parent", (long)row.ParentId);
            Bind(command, "$family", insert.FamilyId);
            Bind(command, "$retired", row.IsRetired ? 1L : 0L);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        ContentFieldSchema schema = RequireType(row.Type).Schema;
        for (int i = 0; i < schema.Fields.Count && i < row.Fields.Count; i++)
        {
            ContentFieldEntry entry = schema.Fields[i];
            ContentFieldValue value = row.Fields[i];
            if (entry.IsDerivedMarker || value.IsAbsent)
            {
                continue;
            }

            using SqliteCommand command = Command(
                """
                INSERT INTO catalog_row_field(
                    type_id, definition_id, valid_from_version, field_name, field_kind,
                    int_value, text_value, blob_value)
                VALUES ($type, $id, $from, $name, $kind, $int, NULL, $blob);
                """,
                transaction);
            Bind(command, "$type", (long)row.Type.Value);
            Bind(command, "$id", (long)row.Id);
            Bind(command, "$from", (long)insert.ValidFromVersion);
            Bind(command, "$name", entry.Name);
            Bind(command, "$kind", (long)value.Kind);
            Bind(command, "$int", ContentFieldValue.StoresNumber(value.Kind) ? value.Number : null);
            Bind(command, "$blob", ContentFieldValue.StoresBytes(value.Kind) ? value.Bytes.ToArray() : null);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One row revision closed at the new version. The caller owns the transaction.</summary>
    async Task CloseRowAsync(ContentRowClose close, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            UPDATE catalog_row SET replaced_in_version = $replaced
            WHERE type_id = $type AND definition_id = $id AND valid_from_version = $from
              AND replaced_in_version IS NULL;
            """,
            transaction);
        Bind(command, "$replaced", (long)close.ReplacedInVersion);
        Bind(command, "$type", (long)close.Type.Value);
        Bind(command, "$id", (long)close.DefinitionId);
        Bind(command, "$from", (long)close.ValidFromVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One stored value back through its kind. The three value columns are exclusive by kind, and a NULL in
    /// the column the kind names is an ABSENT field rather than a zero.
    /// </summary>
    static ContentFieldValue ReadValue(SqliteDataReader reader, ContentFieldKind kind, int intOrdinal, int blobOrdinal)
    {
        if (ContentFieldValue.StoresNumber(kind))
        {
            return reader.IsDBNull(intOrdinal)
                ? ContentFieldValue.Absent(kind)
                : ContentFieldValue.OfNumber(kind, reader.GetInt64(intOrdinal));
        }

        if (ContentFieldValue.StoresBytes(kind))
        {
            return reader.IsDBNull(blobOrdinal)
                ? ContentFieldValue.Absent(kind)
                : ContentFieldValue.OfBytes(kind, reader.GetFieldValue<byte[]>(blobOrdinal));
        }

        return ContentFieldValue.Absent(kind);
    }

    /// <summary>One row version's identity, which is what a field row is grouped under while reading.</summary>
    readonly record struct RowKey(ushort Type, int DefinitionId, int ValidFromVersion);
}
