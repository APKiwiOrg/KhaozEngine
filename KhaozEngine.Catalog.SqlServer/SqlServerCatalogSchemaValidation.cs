using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The two schema modes as behaviour: <see cref="ContentAuthoringSchemaMode.AutoCreate"/> creates the schema
/// when the database carries no catalog table and then validates it, and
/// <see cref="ContentAuthoringSchemaMode.ValidateOnly"/> refuses an empty or mismatched database rather than
/// creating anything.
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
/// foreign key and every default constraint against what version 1 declares, both directions, because a
/// missing object and an extra one are each a schema this build cannot write to safely. A mismatch names the object and the required migration, since an operator can
/// act on those two facts and cannot act on "schema is wrong".
/// </para>
/// </summary>
internal static class SqlServerCatalogSchemaValidation
{
    /// <summary>
    /// The resource EVERY statement that reshapes this schema takes an exclusive application lock on. The
    /// create and the reset both take it, on the same name, or the lock would not serialize them against
    /// each other.
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
    /// <exception cref="ContentAuthoringException">The schema is absent under ValidateOnly, carries a version this build does not support, or holds an object that does not match.</exception>
    internal static async Task InitializeAsync(
        SqlConnection connection,
        ContentAuthoringSchemaMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            if (await CountTablesAsync(connection, cancellationToken).ConfigureAwait(false) == 0)
            {
                if (mode == ContentAuthoringSchemaMode.ValidateOnly)
                {
                    throw Mismatch("missing");
                }

                await CreateAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            int version = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version != SqlServerCatalogSchema.CurrentVersion)
            {
                throw Mismatch(FormattableString.Invariant($"at unsupported version '{version}'"));
            }

            await ValidateObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (ContentAuthoringException)
        {
            throw;
        }
        catch (SqlException exception)
        {
            throw Mismatch("unreadable, so its metadata could not be checked", exception);
        }
    }

    /// <summary>The schema version the database carries, which a migration compares against.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ContentAuthoringException">The metadata row is absent or does not hold a number.</exception>
    internal static async Task<int> ReadSchemaVersionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using SqlCommand command = connection.CreateCommand();
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
    /// <b>Every statement that reshapes this schema takes it, on this one resource name.</b> A lock the
    /// create holds and the reset does not is not a lock: the two would interleave, and a reset that drops
    /// fourteen tables while a starting host is creating them is a database neither of them can describe.
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
                $"locked by another process creating or resetting it, which returned application lock code {held}, so this call changed nothing"));
        }
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
    /// The five name sets read back and compared against what version 1 declares. Tables outside the
    /// <c>catalog_</c> namespace are invisible here on purpose: a host may keep its own tables in the same
    /// database, and this schema has no opinion about them.
    /// </summary>
    static async Task ValidateObjectsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> tables = await ReadNamesAsync(
            connection,
            """
            SELECT name FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'dbo') AND name LIKE N'catalog[_]%';
            """,
            cancellationToken).ConfigureAwait(false);
        Compare("table", tables, SqlServerCatalogSchemaExpectations.Tables);

        IReadOnlySet<string> indexes = await ReadNamesAsync(
            connection,
            """
            SELECT t.name + N'.' + i.name
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%' AND i.name IS NOT NULL;
            """,
            cancellationToken).ConfigureAwait(false);
        Compare("index", indexes, SqlServerCatalogSchemaExpectations.Indexes);

        IReadOnlySet<string> checks = await ReadNamesAsync(
            connection,
            """
            SELECT t.name + N'.' + c.name
            FROM sys.check_constraints c
            JOIN sys.tables t ON t.object_id = c.parent_object_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%';
            """,
            cancellationToken).ConfigureAwait(false);
        Compare("check constraint", checks, SqlServerCatalogSchemaExpectations.Checks);

        // The foreign keys and the defaults, which the journal's own validator has always compared and this
        // one did not. A missing foreign key accepts a chunk row pointing at a version that is not there, and
        // a missing default turns an insert that omits a column into a NULL in a NOT NULL column. Both are
        // writes this build believes the schema makes impossible, so neither can be left unchecked.
        IReadOnlySet<string> foreignKeys = await ReadNamesAsync(
            connection,
            """
            SELECT t.name + N'.' + f.name
            FROM sys.foreign_keys f
            JOIN sys.tables t ON t.object_id = f.parent_object_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%';
            """,
            cancellationToken).ConfigureAwait(false);
        Compare("foreign key", foreignKeys, SqlServerCatalogSchemaExpectations.ForeignKeys);

        IReadOnlySet<string> defaults = await ReadNamesAsync(
            connection,
            """
            SELECT t.name + N'.' + d.name
            FROM sys.default_constraints d
            JOIN sys.tables t ON t.object_id = d.parent_object_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%';
            """,
            cancellationToken).ConfigureAwait(false);
        Compare("default constraint", defaults, SqlServerCatalogSchemaExpectations.Defaults);
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
    static void Compare(string kind, IReadOnlySet<string> actual, IReadOnlySet<string> expected)
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
                    $"carrying unexpected {kind} '{name}', which version {SqlServerCatalogSchema.CurrentVersion} does not declare"));
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
