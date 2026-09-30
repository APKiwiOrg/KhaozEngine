using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

public sealed partial class SqliteContentAuthoringStore
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
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        SqliteCatalogSchemaValidation.Initialize(_connection.Connection, mode);
        if (mode == ContentAuthoringSchemaMode.ValidateOnlyWithoutTypeSync)
        {
            foreach (ContentTypeRegistration registration in _registry.ByTypeId)
            {
                await RequireTypeAgreesAsync(registration, null, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await SyncTypesAsync(cancellationToken).ConfigureAwait(false);
        }

        _useStoredTypeMetadata = mode == ContentAuthoringSchemaMode.ValidateOnlyWithoutTypeSync;
    }

    /// <summary>
    /// The registry's types written into <c>catalog_type</c>, which pins each id to its key. The caller
    /// already holds the lease.
    /// <para>
    /// A rename (the id is there under another key) and a reassignment (the key is there under another id)
    /// are both refused, because either one silently repoints every stored row of that type. The other three
    /// columns are a RECORD of the declaration this process carries and are refreshed, since the registry is
    /// the authority the publish actually reads them from.
    /// </para>
    /// <para>
    /// A refresh is an UPDATE only when one of the three differs, so <c>updated_at_utc</c> is when the
    /// declaration last changed rather than when a host last booted.
    /// </para>
    /// </summary>
    async Task SyncTypesAsync(CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        long active = await ReadLongAsync(
            "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", transaction, cancellationToken)
            .ConfigureAwait(false);
        long now = Millis(_clock());

        IReadOnlyList<ContentTypeRegistration> registrations = _registry.ByTypeId;
        for (int i = 0; i < registrations.Count; i++)
        {
            ContentTypeRegistration registration = registrations[i];
            await RequireTypeAgreesAsync(registration, transaction, cancellationToken).ConfigureAwait(false);

            using SqliteCommand upsert = Command(
                """
                INSERT INTO catalog_type(
                    type_id, type_key, chunk_slots, default_visibility, max_definition_id, first_seen_version,
                    created_at_utc, updated_at_utc)
                VALUES ($type, $key, $slots, $visibility, $ceiling, $firstSeen, $now, $now)
                ON CONFLICT(type_id) DO UPDATE SET
                    chunk_slots = excluded.chunk_slots,
                    default_visibility = excluded.default_visibility,
                    max_definition_id = excluded.max_definition_id,
                    updated_at_utc = excluded.updated_at_utc
                WHERE catalog_type.chunk_slots IS NOT excluded.chunk_slots
                   OR catalog_type.default_visibility IS NOT excluded.default_visibility
                   OR catalog_type.max_definition_id IS NOT excluded.max_definition_id;
                """,
                transaction);
            Bind(upsert, "$type", (long)registration.Type.Value);
            Bind(upsert, "$key", registration.TypeKey);
            Bind(upsert, "$slots", (long)registration.ChunkSlots);
            Bind(upsert, "$visibility", (long)registration.DefaultVisibility);
            Bind(upsert, "$ceiling", registration.MaxDefinitionId);
            Bind(upsert, "$firstSeen", active);
            Bind(upsert, "$now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    async Task RequireTypeAgreesAsync(
        ContentTypeRegistration registration,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT type_id, type_key FROM catalog_type
            WHERE type_id = $type OR type_key = $key;
            """,
            transaction);
        Bind(command, "$type", (long)registration.Type.Value);
        Bind(command, "$key", registration.TypeKey);

        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int storedId = (int)reader.GetInt64(0);
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
