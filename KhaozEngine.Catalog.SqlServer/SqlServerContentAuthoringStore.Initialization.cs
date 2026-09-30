using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

public sealed partial class SqlServerContentAuthoringStore
{
    bool _useStoredTypeMetadata;

    /// <inheritdoc />
    /// <remarks>
    /// AutoCreate and ValidateOnly synchronize registrations. ValidateOnlyWithoutTypeSync checks only
    /// the stored id/key pairings and leaves registrations unchanged.
    /// </remarks>
    public async Task InitializeAsync(
        ContentAuthoringSchemaMode mode,
        CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqlServerCatalogSchemaValidation.InitializeAsync(connection, mode, cancellationToken)
            .ConfigureAwait(false);
        if (mode == ContentAuthoringSchemaMode.ValidateOnlyWithoutTypeSync)
        {
            var scope = new SqlServerCatalogScope(connection, null);
            foreach (ContentTypeRegistration registration in _registry.ByTypeId)
            {
                await RequireTypeAgreesAsync(scope, registration, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await SyncTypesAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        _useStoredTypeMetadata = mode == ContentAuthoringSchemaMode.ValidateOnlyWithoutTypeSync;
    }

    /// <summary>
    /// The registry's types written into <c>catalog_type</c>, which pins each id to its key.
    /// <para>
    /// A rename (the id is there under another key) and a reassignment (the key is there under another id)
    /// are both refused, because either one silently repoints every stored row of that type. The other three
    /// columns are a RECORD of the declaration this process carries and are refreshed, since the registry is
    /// the authority the publish actually reads them from.
    /// </para>
    /// <para>
    /// A refresh is an UPDATE only when one of the three differs, so <c>updated_at_utc</c> is when the
    /// declaration last changed rather than when a host last booted. The ceiling may be NULL on either side, so
    /// its comparison spells the NULL cases out.
    /// </para>
    /// </summary>
    async Task SyncTypesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using SqlTransaction transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var scope = new SqlServerCatalogScope(connection, transaction);
        int active = await ReadActiveVersionAsync(scope, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _clock();

        IReadOnlyList<ContentTypeRegistration> registrations = _registry.ByTypeId;
        for (int i = 0; i < registrations.Count; i++)
        {
            ContentTypeRegistration registration = registrations[i];
            await RequireTypeAgreesAsync(scope, registration, cancellationToken).ConfigureAwait(false);

            await using SqlCommand upsert = Command(
                scope,
                """
                MERGE dbo.catalog_type WITH (HOLDLOCK) AS target
                USING (SELECT @type AS type_id) AS source ON target.type_id = source.type_id
                WHEN MATCHED AND (
                    target.chunk_slots <> @slots
                    OR target.default_visibility <> @visibility
                    OR target.max_definition_id <> @ceiling
                    OR (target.max_definition_id IS NULL AND @ceiling IS NOT NULL)
                    OR (target.max_definition_id IS NOT NULL AND @ceiling IS NULL)) THEN UPDATE SET
                    chunk_slots = @slots,
                    default_visibility = @visibility,
                    max_definition_id = @ceiling,
                    updated_at_utc = @now
                WHEN NOT MATCHED THEN INSERT (
                    type_id, type_key, chunk_slots, default_visibility, max_definition_id, first_seen_version,
                    created_at_utc, updated_at_utc)
                    VALUES (@type, @key, @slots, @visibility, @ceiling, @firstSeen, @now, @now);
                """);
            BindInt(upsert, "@type", (int)registration.Type.Value);
            BindText(upsert, "@key", registration.TypeKey);
            BindInt(upsert, "@slots", registration.ChunkSlots);
            BindInt(upsert, "@visibility", (int)registration.DefaultVisibility);
            BindInt(upsert, "@ceiling", registration.MaxDefinitionId);
            BindInt(upsert, "@firstSeen", active);
            BindTime(upsert, "@now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task RequireTypeAgreesAsync(
        SqlServerCatalogScope scope,
        ContentTypeRegistration registration,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT type_id, type_key FROM dbo.catalog_type
            WHERE type_id = @type OR type_key = @key;
            """);
        BindInt(command, "@type", (int)registration.Type.Value);
        BindText(command, "@key", registration.TypeKey);

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int storedId = reader.GetInt32(0);
            string storedKey = reader.GetString(1);
            if (storedId == registration.Type.Value
                && string.Equals(storedKey, registration.TypeKey, StringComparison.Ordinal))
            {
                continue;
            }

            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"This database already pins content type {storedId} to key '{storedKey}', and the registry declares type {registration.Type.Value} as '{registration.TypeKey}'. Neither a rename nor a reassignment is possible: both repoint every row already stored under the old pairing."),
                registration.Type,
                0,
                ContentAuthoringException.UnknownTypeReason);
        }
    }

}
