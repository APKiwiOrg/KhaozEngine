using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// Schema initialization as behaviour: <see cref="ContentAuthoringSchemaMode.AutoCreate"/> creates the schema
/// when the database carries no catalog table and then validates it, and
/// <see cref="ContentAuthoringSchemaMode.ValidateOnly"/> refuses an empty or mismatched database rather than
/// creating anything. ValidateOnlyWithoutTypeSync uses the same schema validation.
/// <para>
/// <b>ValidateOnly is what a production host sets, and its job is the typo.</b> A connection string pointing
/// at a database nothing has created would otherwise create a second, empty catalog and serve it, which is a
/// catalog that reports healthy while carrying none of the operator's content.
/// </para>
/// <para>
/// <b>The create runs under an application lock, because this backend exists for the shared case.</b> Two
/// consoles starting at once against one database is the ordinary deployment here, not an edge, and two
/// concurrent bare creates are one succeeded create and one "there is already an object named" failure. The
/// lock is the journal's, held for the transaction and released with it.
/// </para>
/// <para>
/// Validation compares the NAMES of every catalog table, every named index, every check constraint, every
/// foreign key and every default constraint against what the declared version says, both directions, because a
/// missing object and an extra one are each a schema this build cannot write to safely. It compares every row
/// time column with its type and nullability the same way. A mismatch names the object and the required
/// migration, since an operator can act on those two facts and cannot act on "schema is wrong".
/// </para>
/// <para>
/// <b>A version 1, 2 or 3 database is MIGRATED under AutoCreate and refused under either validation mode</b>.
/// This follows the journal's split per provider and per mode. Version 1 chains through versions 2 and 3 to version
/// 4 in one open. Each migration runs behind the same application lock the create takes and re-reads the version
/// inside it, so two hosts starting at once are one migration and one host that finds the work already done.
/// </para>
/// </summary>
internal static class SqlServerCatalogSchemaValidation
{
    /// <summary>
    /// The resource EVERY statement that reshapes this schema takes an exclusive application lock on. The
    /// create, the migration and the reset all take it, on the same name, or the lock would not serialize
    /// them against each other.
    /// </summary>
    internal const string LockResource = "KhaozEngine.Catalog.SqlServer.CatalogSchema";

    /// <summary>The seconds the application lock and the create are given.</summary>
    const int LockTimeoutSeconds = 60;

    /// <summary>
    /// Brings the open connection to the current schema under one mode, or throws naming what is wrong.
    /// </summary>
    /// <param name="connection">An open connection to the catalog database.</param>
    /// <param name="mode">Whether a database carrying no catalog table may be created into.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <exception cref="ContentAuthoringException">The schema is absent under ValidateOnly, carries a version this build does not support, holds an object that does not match, or does not read as a catalog.</exception>
    /// <exception cref="SqlException">SQL Server failed for a reason that is not the schema, for example a lock held past the timeout.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    internal static async Task InitializeAsync(
        SqlConnection connection,
        ContentAuthoringSchemaMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (await ReadAsync(() => CountTablesAsync(connection, cancellationToken)).ConfigureAwait(false) == 0)
        {
            if (mode != ContentAuthoringSchemaMode.AutoCreate)
            {
                throw Mismatch("missing");
            }

            await CreateAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        int version = await ReadAsync(() => ReadSchemaVersionAsync(connection, cancellationToken))
            .ConfigureAwait(false);
        if (version == 1)
        {
            // The version 1 shape is checked BEFORE the migration runs, so a database that is version 1
            // and something else besides is refused rather than half migrated. Under ValidateOnly the
            // refusal names the migration, which is the one thing an operator can act on.
            await ValidateObjectsAsync(connection, 1, cancellationToken).ConfigureAwait(false);
            if (mode != ContentAuthoringSchemaMode.AutoCreate)
            {
                throw Mismatch("at unsupported version '1'");
            }

            await MigrateAsync(connection, 1, SqlServerCatalogSchema.VersionOneMigrationSql, cancellationToken)
                .ConfigureAwait(false);
            version = await ReadAsync(() => ReadSchemaVersionAsync(connection, cancellationToken))
                .ConfigureAwait(false);
        }

        if (version == 2)
        {
            // Read after the version, like every validation here, so a version 1 database this open just
            // migrated, or a version 2 one another host is migrating, is judged on what it holds now. The
            // version 2 check accepts a version 3 column in its exact shape, which is what a half-finished
            // migration or a host that migrated in between leaves. A host that went further, to version 3 or
            // on to 4, leaves objects version 2 does not declare, which is answered by the version it moved to.
            version = await ValidateVersionTwoAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version == 2)
            {
                if (mode != ContentAuthoringSchemaMode.AutoCreate)
                {
                    throw Mismatch("at unsupported version '2'");
                }

                await MigrateAsync(connection, 2, SqlServerCatalogSchema.VersionTwoMigrationSql, cancellationToken)
                    .ConfigureAwait(false);
                version = await ReadAsync(() => ReadSchemaVersionAsync(connection, cancellationToken))
                    .ConfigureAwait(false);
            }
        }

        if (version == 3)
        {
            // Validated against version 3's own name sets BEFORE the migration, read here for the same reason
            // as version 2. A host that migrated in between leaves version 4 objects, which is answered by the
            // version it moved to rather than refused.
            version = await ValidateVersionThreeAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version == 3)
            {
                if (mode != ContentAuthoringSchemaMode.AutoCreate)
                {
                    throw Mismatch("at unsupported version '3'");
                }

                await MigrateAsync(connection, 3, SqlServerCatalogSchema.VersionThreeMigrationSql, cancellationToken)
                    .ConfigureAwait(false);
                version = await ReadAsync(() => ReadSchemaVersionAsync(connection, cancellationToken))
                    .ConfigureAwait(false);
            }
        }

        if (version != SqlServerCatalogSchema.CurrentVersion)
        {
            throw Mismatch(FormattableString.Invariant($"at unsupported version '{version}'"));
        }

        await ValidateObjectsAsync(connection, SqlServerCatalogSchema.CurrentVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a version 2 catalog against version 2's name sets and answers the version it stands at. When
    /// the objects do not match and the version has moved past 2 since it was read, another host migrated in
    /// between, which is answered with the version it moved to. Otherwise the mismatch is refused.
    /// </summary>
    /// <remarks>Internal rather than private so a test can drive the migrated-in-between branch directly.</remarks>
    internal static async Task<int> ValidateVersionTwoAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await ValidateObjectsAsync(connection, 2, cancellationToken).ConfigureAwait(false);
            return 2;
        }
        catch (ContentAuthoringException)
        {
            int now = await ReadAsync(() => ReadSchemaVersionAsync(connection, cancellationToken)).ConfigureAwait(false);
            if (now > 2)
            {
                return now;
            }

            throw;
        }
    }

    /// <summary>
    /// Validates a version 3 catalog against version 3's exact name sets and answers the version it stands at.
    /// When the objects do not match and the version has moved to 4 since it was read, another host migrated in
    /// between, which is answered with 4. Otherwise the mismatch is refused.
    /// </summary>
    static async Task<int> ValidateVersionThreeAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await ValidateObjectsAsync(connection, 3, cancellationToken).ConfigureAwait(false);
            return 3;
        }
        catch (ContentAuthoringException)
        {
            int now = await ReadAsync(() => ReadSchemaVersionAsync(connection, cancellationToken)).ConfigureAwait(false);
            if (now == SqlServerCatalogSchema.CurrentVersion)
            {
                return now;
            }

            throw;
        }
    }

    /// <summary>
    /// One schema or metadata read, translating only a missing column or object into the schema refusal.
    /// A lock timeout, deadlock, permission failure or lost connection says nothing about the schema and
    /// propagates as the provider's own exception. Creates and migrations do not pass through this helper.
    /// </summary>
    static async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (SqlException exception) when (IsMissingName(exception.Number))
        {
            throw Mismatch("unreadable, so its metadata could not be checked", exception);
        }
    }

    /// <summary>Whether SQL Server says a named column or object is missing.</summary>
    /// <param name="number">The provider's error number.</param>
    internal static bool IsMissingName(int number) => number is 207 or 208;

    /// <summary>The schema version the database carries, which a migration compares against.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ContentAuthoringException">The metadata row is absent or does not hold a number.</exception>
    internal static Task<int> ReadSchemaVersionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
        => ReadSchemaVersionAsync(connection, null, cancellationToken);

    /// <summary>The same read, enlisted in an open transaction.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The open transaction, or null.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ContentAuthoringException">The metadata row is absent or does not hold a number.</exception>
    static async Task<int> ReadSchemaVersionAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT schema_version FROM dbo.catalog_metadata WHERE metadata_key = 1;";
        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is int version
            ? version
            : throw Mismatch(raw is null or DBNull
                ? "missing its metadata row"
                : "carrying an unreadable schema version");
    }

    /// <summary>
    /// The whole DDL as one batch inside one transaction, behind the exclusive application lock. Either every
    /// object exists afterwards or none of them does, so a create that fails half way leaves nothing for the
    /// next start to trip over.
    /// </summary>
    static async Task CreateAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using SqlTransaction transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        await TakeSchemaLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        // The other holder of the lock may have created the schema while this one waited, which is the whole
        // point of taking it. Re-read inside the lock rather than trusting the count taken outside it.
        if (await CountTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false) == 0)
        {
            await using SqlCommand create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = SqlServerCatalogSchema.SchemaSql;
            create.CommandTimeout = LockTimeoutSeconds;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the exclusive application lock for the given transaction, waiting up to a minute, and throws
    /// naming the lock code if it cannot be had.
    /// <para>
    /// <b>Every statement that reshapes this schema takes it, on this one resource name:</b> the create, the
    /// migrations and the reset. A lock one of them holds and another does not is not a lock: the two
    /// would interleave, and a reset that drops every catalog table while a starting host is creating or
    /// migrating them is a database neither of them can describe.
    /// The lock is held for the transaction and released with it, however it ends.
    /// </para>
    /// </summary>
    /// <param name="connection">The connection the transaction belongs to.</param>
    /// <param name="transaction">The transaction that will own the lock.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="ContentAuthoringException">The lock could not be taken inside the timeout.</exception>
    internal static async Task TakeSchemaLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        int held;
        await using (SqlCommand applicationLock = connection.CreateCommand())
        {
            applicationLock.Transaction = transaction;
            applicationLock.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = @timeout;
                SELECT @result;
                """;
            applicationLock.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
            applicationLock.Parameters.Add("@timeout", SqlDbType.Int).Value = LockTimeoutSeconds * 1000;
            applicationLock.CommandTimeout = LockTimeoutSeconds * 2;
            object? raw = await applicationLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            held = raw is int code ? code : -999;
        }

        if (held < 0)
        {
            throw Mismatch(FormattableString.Invariant(
                $"locked by another process creating, migrating or resetting it, which returned application lock code {held}, so this call changed nothing"));
        }
    }

    /// <summary>
    /// One migration, behind the SAME exclusive application lock the create takes and inside one transaction,
    /// each statement its own batch: version 1 to 2 adds the ledger table, version 2 to 3 adds the row time
    /// columns and backfills them, and version 3 to 4 adds the text tables and the two nullable columns. Two hosts starting at once against one database is the ordinary deployment
    /// here, so the second one waits and then finds the version already moved, which is why the version is
    /// re-read INSIDE the lock. A second run would also overwrite times a version 3 writer has stamped since.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="from">The version the statements move a database from.</param>
    /// <param name="statements">The migration, each statement run as its own batch and the version moved last.</param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    static async Task MigrateAsync(
        SqlConnection connection,
        int from,
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken)
    {
        await using SqlTransaction transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        await TakeSchemaLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        if (await ReadSchemaVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false) == from)
        {
            foreach (string sql in statements)
            {
                await using SqlCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.CommandTimeout = LockTimeoutSeconds;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    static Task<int> CountTablesAsync(SqlConnection connection, CancellationToken cancellationToken)
        => CountTablesAsync(connection, null, cancellationToken);

    static async Task<int> CountTablesAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'dbo') AND name LIKE N'catalog[_]%';
            """;
        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is int count ? count : 0;
    }

    /// <summary>
    /// The five name sets and the row time columns read back and compared against what the given version
    /// declares. Tables outside the <c>catalog_</c> namespace are invisible here on purpose: a host may keep its
    /// own tables in the same database, and this schema has no opinion about them.
    /// </summary>
    static async Task ValidateObjectsAsync(
        SqlConnection connection,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<string> tables = await ReadAsync(() => ReadNamesAsync(
                connection,
                """
                SELECT name FROM sys.tables
                WHERE schema_id = SCHEMA_ID(N'dbo') AND name LIKE N'catalog[_]%';
                """,
                cancellationToken))
            .ConfigureAwait(false);
        Compare(
            "table",
            tables,
            SqlServerCatalogSchemaExpectations.TablesFor(expectedVersion),
            expectedVersion);

        IReadOnlySet<string> indexes = await ReadAsync(() => ReadNamesAsync(
                connection,
                """
                SELECT t.name + N'.' + i.name
                FROM sys.indexes i
                JOIN sys.tables t ON t.object_id = i.object_id
                WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%' AND i.name IS NOT NULL;
                """,
                cancellationToken))
            .ConfigureAwait(false);
        Compare(
            "index",
            indexes,
            SqlServerCatalogSchemaExpectations.IndexesFor(expectedVersion),
            expectedVersion);

        IReadOnlySet<string> checks = await ReadAsync(() => ReadNamesAsync(
                connection,
                """
                SELECT t.name + N'.' + c.name
                FROM sys.check_constraints c
                JOIN sys.tables t ON t.object_id = c.parent_object_id
                WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%';
                """,
                cancellationToken))
            .ConfigureAwait(false);
        Compare(
            "check constraint",
            checks,
            SqlServerCatalogSchemaExpectations.ChecksFor(expectedVersion),
            expectedVersion);

        // The foreign keys and the defaults, which the journal's own validator has always compared and this
        // one did not. A missing foreign key accepts a chunk row pointing at a version that is not there, and
        // a missing default turns an insert that omits a column into a NULL in a NOT NULL column. Both are
        // writes this build believes the schema makes impossible, so neither can be left unchecked.
        IReadOnlySet<string> foreignKeys = await ReadAsync(() => ReadNamesAsync(
                connection,
                """
                SELECT t.name + N'.' + f.name
                FROM sys.foreign_keys f
                JOIN sys.tables t ON t.object_id = f.parent_object_id
                WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%';
                """,
                cancellationToken))
            .ConfigureAwait(false);
        Compare(
            "foreign key",
            foreignKeys,
            SqlServerCatalogSchemaExpectations.ForeignKeysFor(expectedVersion),
            expectedVersion);

        IReadOnlySet<string> defaults = await ReadAsync(() => ReadNamesAsync(
                connection,
                """
                SELECT t.name + N'.' + d.name
                FROM sys.default_constraints d
                JOIN sys.tables t ON t.object_id = d.parent_object_id
                WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%';
                """,
                cancellationToken))
            .ConfigureAwait(false);
        Compare(
            "default constraint",
            defaults,
            SqlServerCatalogSchemaExpectations.DefaultsFor(expectedVersion),
            expectedVersion);

        // Version 3 is columns and nothing else, so a database that says version 3 without them, or version 2
        // with one of them in a shape the migration would not have left, is caught only here.
        IReadOnlySet<string> timeColumns = await ReadAsync(() => ReadNamesAsync(
                connection,
                """
                SELECT t.name + N'.' + c.name + N'|' + ty.name + N'|' + CONVERT(nvarchar(3), c.scale) + N'|'
                    + CASE c.is_nullable WHEN 1 THEN N'NULL' ELSE N'NOT NULL' END
                FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%'
                  AND c.name IN (N'created_at_utc', N'updated_at_utc');
                """,
                cancellationToken))
            .ConfigureAwait(false);
        Compare(
            "row time column",
            SqlServerCatalogSchemaExpectations.JudgedTimeColumns(timeColumns, expectedVersion),
            SqlServerCatalogSchemaExpectations.TimeColumnsFor(expectedVersion),
            expectedVersion);
    }

    static async Task<IReadOnlySet<string>> ReadNamesAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// Both directions, first difference wins. A missing object is a schema this build cannot write to, and an
    /// extra one is a half-applied migration, which is worse: writing to it writes rows the next build cannot
    /// read.
    /// </summary>
    static void Compare(
        string kind,
        IReadOnlySet<string> actual,
        IReadOnlySet<string> expected,
        int expectedVersion)
    {
        foreach (string name in expected)
        {
            if (!actual.Contains(name))
            {
                throw Mismatch(FormattableString.Invariant($"missing {kind} '{name}'"));
            }
        }

        foreach (string name in actual)
        {
            if (!expected.Contains(name))
            {
                throw Mismatch(FormattableString.Invariant(
                    $"carrying unexpected {kind} '{name}', which version {expectedVersion} does not declare"));
            }
        }
    }

    /// <summary>
    /// The one refusal this file throws, naming the object and the migration. The cause is folded into the
    /// message rather than carried as an inner exception, because the reason token is what an operator's log
    /// line keys on and the refusal has to carry it whichever way it was reached.
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
