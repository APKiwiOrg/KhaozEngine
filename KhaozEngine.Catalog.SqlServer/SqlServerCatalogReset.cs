using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The catalog RESET: every catalog object dropped and the schema recreated from the same embedded script
/// <see cref="SqlServerContentAuthoringStore.InitializeAsync"/> creates from, in one transaction, leaving a
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
/// <b>A plain <c>DELETE</c> would not do.</b> <c>catalog_family.family_id</c>,
/// <c>catalog_draft_edit.edit_ordinal</c> and <c>catalog_audit.audit_id</c> are <c>IDENTITY</c> columns, and a
/// delete leaves the identity mark where it stood, so the next family created after a reimport lands ABOVE
/// the bundle's ids. Dropping the table takes its identity state with it, which is what makes the reimport
/// reproduce the bundle's ids exactly.
/// </para>
/// <para>
/// <b>What it does NOT touch is the pack store on disk or in blob storage.</b> The reset knows nothing about
/// a pack root, and a caller that replaces content at the same version number must clear or rebuild its own,
/// because <c>ContentBoot.ReadManifestAsync</c> trusts the pointer it finds without comparing its hash with
/// <c>catalog_version.server_manifest_hash</c>. The hashes that STOOD come back on
/// <see cref="ContentCatalogResetResult"/> for exactly that comparison.
/// </para>
/// </summary>
public static class SqlServerCatalogReset
{
    /// <summary>The seconds the drop and the recreate are given, matching the schema create's own budget.</summary>
    const int TimeoutSeconds = 60;

    /// <summary>The cap on <c>catalog_audit.actor</c>, which also requires at least one character.</summary>
    const int ActorMaxLength = 128;

    /// <summary>The cap on <c>catalog_audit.[operator]</c>, which may be empty.</summary>
    const int OperatorMaxLength = 128;

    /// <summary>The cap on <c>catalog_audit.note</c>, which may be empty.</summary>
    const int NoteMaxLength = 1024;

    /// <summary>
    /// How many of the schema's OWN tables stand. Zero means a database with no catalog in it, the full
    /// count means a catalog to replace, and anything between means a partial one.
    /// </summary>
    static readonly string CountTablesSql = FormattableString.Invariant($"""
        SELECT COUNT(*) FROM sys.tables
        WHERE schema_id = SCHEMA_ID(N'dbo') AND name IN ({SqlServerCatalogSchemaExpectations.TableNameList});
        """);

    /// <summary>
    /// Every catalog object gone, driven off the schema's own INVENTORY intersected with what
    /// <c>sys.tables</c> holds, so a table added to the schema is dropped by this without anyone remembering
    /// to add it here and a host table that merely looks like one is never reached. The foreign keys go
    /// first, so the order the tables come off in does not matter and does not have to be maintained beside
    /// the schema. An index, a check, a default and the identity state all go with the table that owns them.
    /// </summary>
    static readonly string DropSql = FormattableString.Invariant($"""
        DECLARE @sql nvarchar(max) = N'';

        SELECT @sql = @sql + N'ALTER TABLE dbo.' + QUOTENAME(t.name)
            + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
        FROM sys.foreign_keys fk
        JOIN sys.tables t ON t.object_id = fk.parent_object_id
        WHERE t.schema_id = SCHEMA_ID(N'dbo')
          AND t.name IN ({SqlServerCatalogSchemaExpectations.TableNameList});

        SELECT @sql = @sql + N'DROP TABLE dbo.' + QUOTENAME(name) + N';'
        FROM sys.tables
        WHERE schema_id = SCHEMA_ID(N'dbo')
          AND name IN ({SqlServerCatalogSchemaExpectations.TableNameList});

        EXEC sys.sp_executesql @sql;
        """);

    /// <summary>
    /// Drops every catalog object and recreates the schema, all or nothing, and files one audit row in the
    /// new store recording what stood.
    /// <para>
    /// The store this resets is left needing <see cref="SqlServerContentAuthoringStore.InitializeAsync"/>
    /// again, the same as a database that has just been created: <c>catalog_type</c> is empty until a store
    /// syncs its registry into it.
    /// </para>
    /// <para>
    /// <b>It takes no application lock, unlike the schema create.</b> The create takes one because two hosts
    /// starting at once is the ordinary deployment, and two bare creates would be one success and one "there
    /// is already an object named" failure. A reset is a maintenance action taken with writers stopped, and
    /// the schema modification locks its own drops hold already serialize it against a concurrent create or
    /// reset in the database, so the second one waits and then finds a schema to work from rather than
    /// interleaving with the first.
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
    /// <exception cref="ArgumentException"><paramref name="actor"/> is empty, or an argument is longer than the audit column that holds it.</exception>
    /// <exception cref="ContentAuthoringException">The database carries a catalog schema version this build does not support, or it carries an open draft and <paramref name="force"/> is false.</exception>
    public static async Task<ContentCatalogResetResult> ResetAsync(
        string connectionString,
        string actor,
        string operatorId,
        string note,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ValidateAuditArguments(actor, operatorId, note);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ResetAsync(connection, actor, operatorId, note, force, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The whole reset inside ONE serializable transaction. SQL Server runs DDL transactionally, so a failure
    /// anywhere below (a drop, a create, or the audit insert refusing an over-long actor) rolls the drop back
    /// with it and leaves the catalog exactly as it was. That is not a nicety: a half-dropped catalog fails
    /// the next open outright, because the initializer creates only when it counts ZERO catalog tables and
    /// validates every object by name otherwise.
    /// </summary>
    static async Task<ContentCatalogResetResult> ResetAsync(
        SqlConnection connection,
        string actor,
        string operatorId,
        string note,
        bool force,
        CancellationToken cancellationToken)
    {
        await using SqlTransaction transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        if (await ReadIntAsync(connection, transaction, CountTablesSql, cancellationToken)
            .ConfigureAwait(false) == 0)
        {
            throw Mismatch("missing, so there is nothing to reset");
        }

        ContentCatalogResetResult stood = await ReadBeforeAsync(
            connection, transaction, force, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, DropSql, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, SqlServerCatalogSchema.SchemaSql, cancellationToken)
            .ConfigureAwait(false);

        ContentCatalogResetResult result = stood with
        {
            StoreEpoch = await ReadTextAsync(
                connection,
                transaction,
                "SELECT store_epoch FROM dbo.catalog_metadata WHERE metadata_key = 1;",
                cancellationToken).ConfigureAwait(false),
        };

        await AppendResetAuditAsync(connection, transaction, actor, operatorId, note, result, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// What the store stood at, read while it still exists, with the open-draft refusal on the way past.
    /// The epoch on the returned record is the OLD one and the caller replaces it after the recreate.
    /// </summary>
    static async Task<ContentCatalogResetResult> ReadBeforeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        bool force,
        CancellationToken cancellationToken)
    {
        int schema;
        int active;
        int versions;
        int rows;
        int drafts;
        try
        {
            schema = await ReadIntAsync(
                connection,
                transaction,
                "SELECT schema_version FROM dbo.catalog_metadata WHERE metadata_key = 1;",
                cancellationToken).ConfigureAwait(false);
            active = await ReadIntAsync(
                connection,
                transaction,
                "SELECT active_version FROM dbo.catalog_metadata WHERE metadata_key = 1;",
                cancellationToken).ConfigureAwait(false);
            versions = await ReadIntAsync(
                connection, transaction, "SELECT COUNT(*) FROM dbo.catalog_version;", cancellationToken)
                .ConfigureAwait(false);
            rows = await ReadIntAsync(
                connection, transaction, "SELECT COUNT(*) FROM dbo.catalog_row;", cancellationToken)
                .ConfigureAwait(false);
            drafts = await ReadIntAsync(
                connection, transaction, "SELECT COUNT(*) FROM dbo.catalog_draft;", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqlException exception)
        {
            // Only the reads BEFORE the drop are folded into the schema refusal. A database carrying some
            // catalog tables and not the ones this reads is a half-applied migration, and an operator can act
            // on being told that. A provider error after the drop is a different thing entirely and is left
            // to speak for itself, having rolled the reset back on the way out.
            throw Mismatch("unreadable, so what it holds could not be checked", exception);
        }

        // The recreate runs THIS build's script, so resetting a database at another schema version would
        // silently move it to version 1 and call that a reset.
        if (schema != SqlServerCatalogSchema.CurrentVersion)
        {
            throw Mismatch(FormattableString.Invariant(
                $"at unsupported version '{schema}', and a reset recreates version {SqlServerCatalogSchema.CurrentVersion}"));
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
            await using SqlCommand command = Command(
                connection,
                transaction,
                """
                SELECT server_manifest_hash, client_manifest_hash
                FROM dbo.catalog_version WHERE version_number = @version;
                """);
            SqlServerContentAuthoringStore.BindInt(command, "@version", active);
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
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
    /// The audit row's own CHECK constraints, mirrored from <c>dbo.catalog_audit</c> in
    /// <c>CatalogSchemaV1.sql</c> and applied BEFORE anything is opened or dropped.
    /// <para>
    /// The insert is the last statement of the reset, so without this an empty actor dropped the whole
    /// catalog, recreated it, and then failed on its own argument list with a raw provider exception. The
    /// catalog came back, because the transaction is all or nothing, but nothing about that is an answer a
    /// caller should have to receive for a mistake it could be told about on the way in.
    /// </para>
    /// <para>
    /// <b>The lengths are measured the way <c>LEN</c> measures them.</b> T-SQL's <c>LEN</c> ignores TRAILING
    /// SPACES, so an actor of one letter and a hundred blanks satisfies the CHECK and an actor of a hundred
    /// blanks alone does not. Measuring with the raw string length instead would refuse the first and accept
    /// the second, and the second is the one that reaches a constraint violation after the drop.
    /// </para>
    /// </summary>
    static void ValidateAuditArguments(string actor, string operatorId, string note)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        // CHECK (LEN(actor) BETWEEN 1 AND 128)
        if (StoredLength(actor) == 0)
        {
            throw new ArgumentException(
                "The reset's actor is written to catalog_audit.actor, which requires at least one character that is not a trailing blank.",
                nameof(actor));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(StoredLength(actor), ActorMaxLength, nameof(actor));

        // CHECK (LEN([operator]) <= 128) and CHECK (LEN(note) <= 1024). Both may be empty.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            StoredLength(operatorId), OperatorMaxLength, nameof(operatorId));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(StoredLength(note), NoteMaxLength, nameof(note));
    }

    /// <summary>What <c>LEN</c> will say about the value, which is the length with trailing spaces removed.</summary>
    static int StoredLength(string value) => value.TrimEnd(' ').Length;

    /// <summary>
    /// The ONE audit row the new store opens with. <c>catalog_audit</c> carries no foreign key to
    /// <c>catalog_version</c> and defaults every target column, so a row naming no version and no row is a
    /// shape the schema already accepts and nothing about it had to bend to record this.
    /// </summary>
    static async Task AppendResetAuditAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string actor,
        string operatorId,
        string note,
        ContentCatalogResetResult result,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            connection,
            transaction,
            """
            INSERT INTO dbo.catalog_audit(
                occurred_at_utc, actor, [operator], action, before_value, version_number, note)
            VALUES (@at, @actor, @operator, @action, @before, 0, @note);
            """);
        SqlServerContentAuthoringStore.BindTime(command, "@at", DateTimeOffset.UtcNow);
        SqlServerContentAuthoringStore.BindText(command, "@actor", actor);
        SqlServerContentAuthoringStore.BindText(command, "@operator", operatorId);
        SqlServerContentAuthoringStore.BindText(command, "@action", ContentAuditActions.Reset);
        SqlServerContentAuthoringStore.BindLargeText(command, "@before", result.Summary);
        SqlServerContentAuthoringStore.BindText(command, "@note", note);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task<int> ReadIntAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(connection, transaction, sql);
        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is int value ? value : throw Mismatch("missing its metadata row");
    }

    static async Task<string> ReadTextAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(connection, transaction, sql);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw Mismatch("missing its metadata row after the recreate");
    }

    static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.CommandTimeout = TimeoutSeconds;
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
                $"The SQL Server content catalog schema is {actual}{Because(innerException)}. Apply migration '{SqlServerCatalogSchema.RequiredMigration}'."),
            default,
            0,
            ContentAuthoringException.SchemaMismatchReason);

    static string Because(Exception? innerException)
        => innerException is null ? string.Empty : ": " + innerException.Message;
}
