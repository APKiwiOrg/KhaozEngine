using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

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
public sealed partial class SqlServerContentAuthoringStore
{
    /// <inheritdoc />
    /// <remarks>Version 0 reads the ACTIVE version's live set, the same answer the in-memory reference gives:
    /// the draft-applied overlay is the publish pipeline's candidate rather than something approximated
    /// here.</remarks>
    public Task<ContentRowPage> ListRowsAsync(
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

        return ReadAsync(
            async (scope, token) =>
            {
                int at = versionNumber == 0
                    ? await ReadActiveVersionAsync(scope, token).ConfigureAwait(false)
                    : versionNumber;

                IReadOnlyList<ContentRowRevision> live = await ReadRevisionsAsync(
                    scope, type, null, at, token).ConfigureAwait(false);

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
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentRowRevision>> GetRowHistoryAsync(
        ContentTypeId type,
        int definitionId,
        CancellationToken cancellationToken = default)
        => ReadAsync(
            (scope, token) => ReadRevisionsAsync(scope, type, definitionId, null, token),
            cancellationToken);

    /// <summary>
    /// Row revisions, ordered by type then id then valid-from, filtered three ways: to one type (id 0 means
    /// every type), to one definition, and to those LIVE at one version. The caller owns the scope.
    /// </summary>
    async Task<IReadOnlyList<ContentRowRevision>> ReadRevisionsAsync(
        SqlServerCatalogScope scope,
        ContentTypeId type,
        int? definitionId,
        int? liveAtVersion,
        CancellationToken cancellationToken)
    {
        Dictionary<RowKey, Dictionary<string, ContentFieldValue>> fields = await ReadRowFieldsAsync(
            scope, type, definitionId, liveAtVersion, cancellationToken).ConfigureAwait(false);

        await using SqlCommand command = Command(
            scope,
            """
            SELECT type_id, definition_id, valid_from_version, replaced_in_version, content_key, parent_id,
                   family_id, retired
            FROM dbo.catalog_row
            WHERE (@type = 0 OR type_id = @type)
              AND (@id IS NULL OR definition_id = @id)
              AND (@at IS NULL OR (valid_from_version <= @at
                   AND (replaced_in_version IS NULL OR replaced_in_version > @at)))
            ORDER BY type_id, definition_id, valid_from_version;
            """);
        Bind(command, "@type", (int)type.Value);
        Bind(command, "@id", definitionId);
        Bind(command, "@at", liveAtVersion);

        var revisions = new List<ContentRowRevision>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rowType = new ContentTypeId((ushort)reader.GetInt32(0));
            int rowId = reader.GetInt32(1);
            int validFrom = reader.GetInt32(2);
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
                reader.GetInt32(5),
                reader.GetInt32(7) != 0,
                values);

            revisions.Add(new ContentRowRevision(
                row,
                validFrom,
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        }

        return revisions;
    }

    static async Task<Dictionary<RowKey, Dictionary<string, ContentFieldValue>>> ReadRowFieldsAsync(
        SqlServerCatalogScope scope,
        ContentTypeId type,
        int? definitionId,
        int? liveAtVersion,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT f.type_id, f.definition_id, f.valid_from_version, f.field_name, f.field_kind,
                   f.int_value, f.blob_value
            FROM dbo.catalog_row_field f
            JOIN dbo.catalog_row r ON r.type_id = f.type_id
                AND r.definition_id = f.definition_id
                AND r.valid_from_version = f.valid_from_version
            WHERE (@type = 0 OR r.type_id = @type)
              AND (@id IS NULL OR r.definition_id = @id)
              AND (@at IS NULL OR (r.valid_from_version <= @at
                   AND (r.replaced_in_version IS NULL OR r.replaced_in_version > @at)));
            """);
        Bind(command, "@type", (int)type.Value);
        Bind(command, "@id", definitionId);
        Bind(command, "@at", liveAtVersion);

        var byRow = new Dictionary<RowKey, Dictionary<string, ContentFieldValue>>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new RowKey((ushort)reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
            if (!byRow.TryGetValue(key, out Dictionary<string, ContentFieldValue>? held))
            {
                held = new Dictionary<string, ContentFieldValue>(StringComparer.Ordinal);
                byRow.Add(key, held);
            }

            held[reader.GetString(3)] = ReadValue(reader, (ContentFieldKind)reader.GetInt32(4), 5, 6);
        }

        return byRow;
    }

    /// <summary>One row revision and its field rows. The caller owns the transaction.</summary>
    async Task InsertRowAsync(
        SqlServerCatalogScope scope,
        ContentRowInsert insert,
        CancellationToken cancellationToken)
    {
        ContentRow row = insert.Row;
        await using (SqlCommand command = Command(
            scope,
            """
            INSERT INTO dbo.catalog_row(
                type_id, definition_id, valid_from_version, replaced_in_version, content_key, parent_id,
                family_id, retired)
            VALUES (@type, @id, @from, NULL, @key, @parent, @family, @retired);
            """))
        {
            Bind(command, "@type", (int)row.Type.Value);
            Bind(command, "@id", row.Id);
            Bind(command, "@from", insert.ValidFromVersion);
            Bind(command, "@key", row.Key.ToString());
            Bind(command, "@parent", row.ParentId);
            Bind(command, "@family", insert.FamilyId);
            Bind(command, "@retired", row.IsRetired ? 1 : 0);
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

            await using SqlCommand command = Command(
                scope,
                """
                INSERT INTO dbo.catalog_row_field(
                    type_id, definition_id, valid_from_version, field_name, field_kind,
                    int_value, text_value, blob_value)
                VALUES (@type, @id, @from, @name, @kind, @int, NULL, @blob);
                """);
            Bind(command, "@type", (int)row.Type.Value);
            Bind(command, "@id", row.Id);
            Bind(command, "@from", insert.ValidFromVersion);
            Bind(command, "@name", entry.Name);
            Bind(command, "@kind", (int)value.Kind);
            Bind(command, "@int", ContentFieldValue.StoresNumber(value.Kind) ? value.Number : null);
            Bind(command, "@blob", ContentFieldValue.StoresBytes(value.Kind) ? value.Bytes.ToArray() : null);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One row revision closed at the new version. The caller owns the transaction.</summary>
    static async Task CloseRowAsync(
        SqlServerCatalogScope scope,
        ContentRowClose close,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            UPDATE dbo.catalog_row SET replaced_in_version = @replaced
            WHERE type_id = @type AND definition_id = @id AND valid_from_version = @from
              AND replaced_in_version IS NULL;
            """);
        Bind(command, "@replaced", close.ReplacedInVersion);
        Bind(command, "@type", (int)close.Type.Value);
        Bind(command, "@id", close.DefinitionId);
        Bind(command, "@from", close.ValidFromVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One stored value back through its kind. The three value columns are exclusive by kind, and a NULL in
    /// the column the kind names is an ABSENT field rather than a zero.
    /// </summary>
    static ContentFieldValue ReadValue(
        SqlDataReader reader,
        ContentFieldKind kind,
        int intOrdinal,
        int blobOrdinal)
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
