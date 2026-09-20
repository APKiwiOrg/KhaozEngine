using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The catalog RESET: every catalog object dropped and the schema recreated from the same script
/// <see cref="SqliteContentAuthoringStore.InitializeAsync"/> creates from, in one transaction, leaving a
/// database indistinguishable from one that has just been initialized for the first time.
/// <para>
/// <b>It exists because a game that ships its client pack inside the client build is always at content
/// version 1.</b> The connect door compares the version NUMBER as well as the manifest hash, and the number
/// is inside the hashed manifest, so such a game cannot publish its server store forward to version 2: it has
/// to REPLACE the store from its committed bundle on every content release. <c>ImportBundleAsync</c> refuses
/// a store that has published anything, and that refusal is right, so the way back to an importable store is
/// this.
/// </para>
/// <para>
/// <b>It is a separate type taking a connection string rather than a member on
/// <see cref="IContentAuthoringStore"/>, and the reason is the CREDENTIAL.</b> A reset is DDL: it drops and
/// creates tables. The everyday authoring path is DML and nothing more, and a deployment that denies its
/// application role DDL (which is the shape a production catalog should have) is doing the right thing. A
/// member on the seam would put a drop-everything verb on the object every console and every admin endpoint
/// already holds, reachable through the interface they were handed for editing rows, and would imply the
/// authoring credential can do it. Keeping it here means a caller reaches the reset by naming this type and
/// passing the migration credential's own connection string, which is a second, deliberate step, and the
/// authoring seam keeps the surface it had.
/// </para>
/// <para>
/// <b>A plain <c>DELETE</c> would not do.</b> <c>catalog_family.family_id</c>, <c>edit_ordinal</c> and
/// <c>audit_id</c> are <c>AUTOINCREMENT</c>, so their <c>sqlite_sequence</c> marks survive a delete and the
/// next family created after a reimport lands ABOVE the bundle's ids. Dropping the table takes its
/// <c>sqlite_sequence</c> row with it, which is what makes the reimport reproduce the bundle's ids exactly.
/// </para>
/// <para>
/// <b>What it does NOT touch is the pack store on disk.</b> The reset knows nothing about a pack root, and a
/// caller that replaces content at the same version number must clear or rebuild its own, because
/// <c>ContentBoot.ReadManifestAsync</c> trusts the pointer it finds on disk without comparing its hash with
/// <c>catalog_version.server_manifest_hash</c>. The hashes that STOOD come back on
/// <see cref="ContentCatalogResetResult"/> for exactly that comparison.
/// </para>
/// </summary>
public static class SqliteCatalogReset
{
    /// <summary>
    /// Drops every catalog object and recreates the schema, all or nothing, and files one audit row in the
    /// new store recording what stood.
    /// <para>
    /// It opens its OWN connection, so the connection string has to name a durable database. A
    /// <c>Data Source=:memory:</c> store lives and dies with the holder's connection and cannot be reached
    /// from here at all, so a reset against one is refused as a database carrying no catalog table.
    /// </para>
    /// <para>
    /// The store this resets is left needing <see cref="SqliteContentAuthoringStore.InitializeAsync"/>
    /// again, the same as a database that has just been created: <c>catalog_type</c> is empty until a store
    /// syncs its registry into it.
    /// </para>
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string, under a credential holding DDL rights.</param>
    /// <param name="actor">What the caller authenticated, at most 128 characters, written to the audit row.</param>
    /// <param name="operatorId">The stable identity the console asserted, empty when it asserted none.</param>
    /// <param name="note">Why the catalog was replaced, at most 1024 characters.</param>
    /// <param name="force">Whether to reset even though a draft is open, which DESTROYS that draft.</param>
    /// <param name="cancellationToken">Cancels the work. Nothing is committed on the way out.</param>
    /// <returns>What stood before the reset, and the epoch the recreated schema minted.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ContentAuthoringException">The database carries no catalog schema this build supports, or it carries an open draft and <paramref name="force"/> is false.</exception>
    public static async Task<ContentCatalogResetResult> ResetAsync(
        string connectionString,
        string actor,
        string operatorId,
        string note,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using (SqliteCommand bootstrap = connection.CreateCommand())
            {
                bootstrap.CommandText = SqliteCatalogSchema.BootstrapSql;
                await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return await ResetAsync(connection, actor, operatorId, note, force, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // The pool is cleared BEFORE the close, the same order SqliteStoreConnection disposes in, so the
            // database file is genuinely released rather than parked in the provider's pool.
            SqliteConnection.ClearPool(connection);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The whole reset inside ONE transaction. SQLite runs DDL transactionally, so a failure anywhere below
    /// (a drop, a create, or the audit insert refusing an over-long actor) rolls the drop back with it and
    /// leaves the catalog exactly as it was. That is not a nicety: a half-dropped catalog fails the next
    /// open outright, because the initializer creates only when it counts ZERO catalog tables and validates
    /// every object by name otherwise.
    /// </summary>
    static async Task<ContentCatalogResetResult> ResetAsync(
        SqliteConnection connection,
        string actor,
        string operatorId,
        string note,
        bool force,
        CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Foreign keys are on and every catalog table references another, so the drop order would otherwise
        // matter and would have to be maintained by hand beside the schema. Deferring moves the check to the
        // commit, by which point there is no row left to violate anything. It is the one pragma that may be
        // set INSIDE a transaction, and it resets itself when the transaction ends.
        await ExecuteAsync(connection, transaction, "PRAGMA defer_foreign_keys = ON;", cancellationToken)
            .ConfigureAwait(false);

        // The schema's own inventory intersected with what stands, which is the rule the whole drop runs
        // under. A name pattern would take a host's own catalogs, cataloguer or catalog_overrides_by_host
        // with it, and those tables are none of this schema's business.
        IReadOnlyList<string> tables = SqliteCatalogSchemaInventory.ReadExisting(connection, transaction);
        if (tables.Count == 0)
        {
            throw Mismatch("missing, so there is nothing to reset");
        }

        ContentCatalogResetResult stood = await ReadBeforeAsync(
            connection, transaction, force, cancellationToken).ConfigureAwait(false);

        // An index goes with the table that owns it, and so does the sqlite_sequence row behind AUTOINCREMENT.
        for (int i = 0; i < tables.Count; i++)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "DROP TABLE \"" + tables[i].Replace("\"", "\"\"", StringComparison.Ordinal) + "\";",
                cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, SqliteCatalogSchema.Tables, cancellationToken)
            .ConfigureAwait(false);

        ContentCatalogResetResult result = stood with
        {
            StoreEpoch = await ReadTextAsync(
                connection,
                transaction,
                "SELECT store_epoch FROM catalog_metadata WHERE metadata_key = 1;",
                cancellationToken).ConfigureAwait(false),
        };

        await AppendResetAuditAsync(connection, transaction, actor, operatorId, note, result, cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
        return result;
    }

    /// <summary>
    /// What the store stood at, read while it still exists, with the open-draft refusal on the way past.
    /// The epoch on the returned record is the OLD one and the caller replaces it after the recreate.
    /// </summary>
    static async Task<ContentCatalogResetResult> ReadBeforeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool force,
        CancellationToken cancellationToken)
    {
        long schema;
        int active;
        int versions;
        int rows;
        long drafts;
        try
        {
            schema = await ReadLongAsync(
                connection,
                transaction,
                "SELECT schema_version FROM catalog_metadata WHERE metadata_key = 1;",
                cancellationToken).ConfigureAwait(false);
            active = (int)await ReadLongAsync(
                connection,
                transaction,
                "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;",
                cancellationToken).ConfigureAwait(false);
            versions = (int)await ReadLongAsync(
                connection, transaction, "SELECT COUNT(*) FROM catalog_version;", cancellationToken)
                .ConfigureAwait(false);
            rows = (int)await ReadLongAsync(
                connection, transaction, "SELECT COUNT(*) FROM catalog_row;", cancellationToken)
                .ConfigureAwait(false);
            drafts = await ReadLongAsync(
                connection, transaction, "SELECT COUNT(*) FROM catalog_draft;", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            // Only the reads BEFORE the drop are folded into the schema refusal. A database carrying some
            // catalog tables and not the ones this reads is a half-applied migration, and an operator can act
            // on being told that. A provider error after the drop is a different thing entirely and is left
            // to speak for itself, having rolled the reset back on the way out.
            throw Mismatch("unreadable, so what it holds could not be checked", exception);
        }

        // The recreate runs THIS build's script, so resetting a database at another schema version would
        // silently move it to version 1 and call that a reset.
        if (schema != SqliteCatalogSchema.CurrentVersion)
        {
            throw Mismatch(FormattableString.Invariant(
                $"at unsupported version '{schema}', and a reset recreates version {SqliteCatalogSchema.CurrentVersion}"));
        }

        if (drafts > 0 && !force)
        {
            throw new ContentAuthoringException(
                "This catalog carries an OPEN DRAFT, and a reset drops it with everything else. Publish or discard the draft, or pass the reset's force flag to destroy it deliberately.",
                default,
                0,
                ContentAuthoringException.DraftOpenReason);
        }

        string? server = null;
        string? client = null;
        if (active > 0)
        {
            using SqliteCommand command = Command(
                connection,
                transaction,
                """
                SELECT server_manifest_hash, client_manifest_hash
                FROM catalog_version WHERE version_number = $version;
                """);
            command.Parameters.AddWithValue("$version", (long)active);
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                server = reader.GetString(0);
                client = reader.GetString(1);
            }
        }

        return new ContentCatalogResetResult(active, server, client, versions, rows, string.Empty);
    }

    /// <summary>
    /// The ONE audit row the new store opens with. <c>catalog_audit</c> carries no foreign key to
    /// <c>catalog_version</c> and defaults every target column, so a row naming no version and no row is a
    /// shape the schema already accepts and nothing about it had to bend to record this.
    /// </summary>
    static async Task AppendResetAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actor,
        string operatorId,
        string note,
        ContentCatalogResetResult result,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            connection,
            transaction,
            """
            INSERT INTO catalog_audit(
                occurred_at_utc, actor, operator, action, before_value, version_number, note)
            VALUES ($at, $actor, $operator, $action, $before, 0, $note);
            """);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$operator", operatorId);
        command.Parameters.AddWithValue("$action", ContentAuditActions.Reset);
        command.Parameters.AddWithValue("$before", result.Summary);
        command.Parameters.AddWithValue("$note", note);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task<long> ReadLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(connection, transaction, sql);
        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is long value ? value : throw Mismatch("missing its metadata row");
    }

    static async Task<string> ReadTextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(connection, transaction, sql);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw Mismatch("missing its metadata row after the recreate");
    }

    static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    /// <summary>
    /// The schema refusal, in the words and under the reason token the provider's own validation uses, so a
    /// console that keys on <c>schema-mismatch</c> reads one answer whether the schema was wrong at open or
    /// wrong at reset.
    /// </summary>
    static ContentAuthoringException Mismatch(string actual, Exception? innerException = null)
        => new(
            FormattableString.Invariant(
                $"The SQLite content catalog schema is {actual}{Because(innerException)}. Apply migration '{SqliteCatalogSchema.RequiredMigration}'."),
            default,
            0,
            ContentAuthoringException.SchemaMismatchReason);

    static string Because(Exception? innerException)
        => innerException is null ? string.Empty : ": " + innerException.Message;
}
