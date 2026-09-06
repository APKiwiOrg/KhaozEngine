using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

internal static class SqlServerJournalSchema
{
    internal const int CurrentVersion = 1;
    internal const string RequiredMigration = "sqlserver-journal-v1-create";
    private const string ApplicationLockResource = "KhaozEngine.WorldStore.SqlServer.JournalSchema";

    internal static string SchemaSql { get; } = LoadSchemaSql();

    internal static async Task InitializeAsync(
        string connectionString,
        SqlServerJournalSchemaMode mode,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw Failure(JournalStoreFailureKind.Cancelled, "Schema initialization was cancelled before a transaction began.", exception);
        }
        catch (SqlException exception)
        {
            throw Failure(JournalStoreFailureKind.Unavailable, "SQL Server journal schema could not open a connection.", exception);
        }

        await using SqlTransaction transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
        bool completed = false;
        try
        {
            int lockResult;
            await using (SqlCommand applicationLock = Command(connection, transaction, commandTimeoutSeconds, """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = @timeout;
                SELECT @result;
                """))
            {
                applicationLock.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = ApplicationLockResource;
                applicationLock.Parameters.Add("@timeout", SqlDbType.Int).Value =
                    (int)Math.Min((long)commandTimeoutSeconds * 1000L, int.MaxValue);
                lockResult = Convert.ToInt32(await applicationLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            }
            if (lockResult < 0)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                completed = true;
                throw ApplicationLockFailure(lockResult);
            }

            int journalObjectCount;
            await using (SqlCommand count = Command(connection, transaction, commandTimeoutSeconds, """
                SELECT COUNT(*)
                FROM sys.objects
                WHERE schema_id = SCHEMA_ID(N'dbo')
                  AND name LIKE N'journal[_]%'
                  AND type IN (N'U', N'PK', N'F', N'C', N'D');
                """))
                journalObjectCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

            if (journalObjectCount == 0)
            {
                if (mode == SqlServerJournalSchemaMode.ValidateOnly) throw Mismatch("missing");
                await using SqlCommand create = Command(connection, transaction, commandTimeoutSeconds, SchemaSql);
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ValidateShapeAsync(connection, transaction, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            completed = true;
        }
        catch (JournalStoreException)
        {
            if (!completed) await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            bool rolledBack = await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw rolledBack
                ? Failure(JournalStoreFailureKind.Cancelled, "Schema initialization was cancelled.", exception)
                : Unknown("Schema initialization was cancelled and rollback could not be confirmed.", exception);
        }
        catch (SqlException exception)
        {
            bool rolledBack = await TryRollbackAsync(transaction).ConfigureAwait(false);
            if (!rolledBack) throw Unknown("Schema initialization failed and rollback could not be confirmed.", exception);
            throw exception.Number switch
            {
                1205 => Failure(JournalStoreFailureKind.Deadlock, "Schema initialization was a deadlock victim.", exception),
                1222 or -2 => Failure(JournalStoreFailureKind.Timeout, "Schema initialization timed out.", exception),
                _ => Failure(JournalStoreFailureKind.Unavailable, "SQL Server journal schema initialization failed.", exception),
            };
        }
    }

    internal static JournalStoreException ApplicationLockFailure(int returnCode)
        => Failure(
            returnCode switch
            {
                -1 => JournalStoreFailureKind.Timeout,
                -2 => JournalStoreFailureKind.Cancelled,
                -3 => JournalStoreFailureKind.Deadlock,
                _ => JournalStoreFailureKind.Unavailable,
            },
            $"SQL Server journal schema application lock failed with return code {returnCode}.");

    private static async Task ValidateShapeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var actualColumns = new HashSet<string>(StringComparer.Ordinal);
        await using (SqlCommand columns = Command(connection, transaction, commandTimeoutSeconds, """
            SELECT t.name, c.name, ty.name, c.max_length, c.is_nullable,
                   COALESCE(c.collation_name, N'')
            FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'journal[_]%';
            """))
        await using (SqlDataReader reader = await columns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                actualColumns.Add(Column(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt16(3), reader.GetBoolean(4), reader.GetString(5)));
        }
        if (!actualColumns.SetEquals(ExpectedColumns))
            throw Mismatch("partial, malformed, or contains unsupported columns");

        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using (SqlCommand command = Command(connection, transaction, commandTimeoutSeconds, """
            SELECT t.name, i.name, i.is_unique, i.is_primary_key, i.type_desc,
                   ic.key_ordinal, c.name, ic.is_descending_key, ic.is_included_column
            FROM sys.tables t
            JOIN sys.indexes i ON i.object_id = t.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo')
              AND t.name LIKE N'journal[_]%'
              AND i.name IS NOT NULL;
            """))
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                indexes.Add(Index(
                    reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3),
                    reader.GetString(4), reader.GetByte(5), reader.GetString(6), reader.GetBoolean(7), reader.GetBoolean(8)));
        }
        if (!indexes.SetEquals(ExpectedIndexes)) throw Mismatch("version 1 indexes do not match the supported shape");

        var foreignKeys = new HashSet<string>(StringComparer.Ordinal);
        await using (SqlCommand command = Command(connection, transaction, commandTimeoutSeconds, """
            SELECT fk.name, pt.name, pc.name, rt.name, rc.name,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc,
                   fk.is_disabled, fk.is_not_trusted, fkc.constraint_column_id
            FROM sys.foreign_keys fk
            JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE pt.schema_id = SCHEMA_ID(N'dbo') AND pt.name LIKE N'journal[_]%';
            """))
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                foreignKeys.Add(ForeignKey(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetInt32(9)));
        }
        if (!foreignKeys.SetEquals(ExpectedForeignKeys)) throw Mismatch("version 1 foreign keys do not match the supported shape");

        var constraints = new HashSet<string>(StringComparer.Ordinal);
        await using (SqlCommand command = Command(connection, transaction, commandTimeoutSeconds, """
            SELECT name, definition, is_disabled, is_not_trusted FROM sys.check_constraints
            WHERE schema_id = SCHEMA_ID(N'dbo') AND parent_object_id IN (
                SELECT object_id FROM sys.tables WHERE name LIKE N'journal[_]%');
            """))
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                constraints.Add(Check(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        }
        if (!constraints.SetEquals(ExpectedChecks)) throw Mismatch("version 1 check constraints do not match the supported shape");

        var defaults = new HashSet<string>(StringComparer.Ordinal);
        await using (SqlCommand command = Command(connection, transaction, commandTimeoutSeconds, """
            SELECT dc.name, t.name, c.name, dc.definition
            FROM sys.default_constraints dc
            JOIN sys.tables t ON t.object_id = dc.parent_object_id
            JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'journal[_]%';
            """))
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                defaults.Add(Default(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        if (!defaults.SetEquals(ExpectedDefaults)) throw Mismatch("version 1 defaults do not match the supported shape");

        await using SqlCommand metadata = Command(connection, transaction, commandTimeoutSeconds, "SELECT schema_version, store_epoch FROM dbo.journal_metadata WHERE metadata_key = 1;");
        await using SqlDataReader metadataReader = await metadata.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await metadataReader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw Mismatch("missing metadata");
        int version = metadataReader.GetInt32(0);
        Guid epoch = metadataReader.GetGuid(1);
        if (version != CurrentVersion) throw Mismatch($"unsupported version '{version}'");
        if (epoch == Guid.Empty) throw Mismatch("metadata has an empty store epoch");
    }

    private static readonly HashSet<string> ExpectedColumns = new(StringComparer.Ordinal)
    {
        Column("journal_metadata", "metadata_key", "tinyint", 1, false, ""),
        Column("journal_metadata", "schema_version", "int", 4, false, ""),
        Column("journal_metadata", "store_epoch", "uniqueidentifier", 16, false, ""),
        Column("journal_metadata", "updated_at_utc", "datetimeoffset", 10, false, ""),
        Column("journal_stream", "stream_key", "nvarchar", 512, false, "Latin1_General_100_BIN2"),
        Column("journal_stream", "current_version", "bigint", 8, false, ""),
        Column("journal_stream", "retained_floor", "bigint", 8, false, ""),
        Column("journal_stream", "updated_at_utc", "datetimeoffset", 10, false, ""),
        Column("journal_event", "stream_key", "nvarchar", 512, false, "Latin1_General_100_BIN2"),
        Column("journal_event", "stream_version", "bigint", 8, false, ""),
        Column("journal_event", "operation_id", "uniqueidentifier", 16, false, ""),
        Column("journal_event", "operation_ordinal", "int", 4, false, ""),
        Column("journal_event", "event_type", "nvarchar", 256, false, "Latin1_General_100_BIN2"),
        Column("journal_event", "event_schema_version", "int", 4, false, ""),
        Column("journal_event", "payload", "varbinary", -1, false, ""),
        Column("journal_event", "payload_sha256", "binary", 32, false, ""),
        Column("journal_event", "committed_at_utc", "datetimeoffset", 10, false, ""),
        Column("journal_operation", "operation_id", "uniqueidentifier", 16, false, ""),
        Column("journal_operation", "operation_kind", "nvarchar", 256, false, "Latin1_General_100_BIN2"),
        Column("journal_operation", "intent_fingerprint_format", "int", 4, false, ""),
        Column("journal_operation", "intent_fingerprint", "binary", 32, false, ""),
        Column("journal_operation", "execution_fingerprint_format", "int", 4, false, ""),
        Column("journal_operation", "execution_fingerprint", "binary", 32, false, ""),
        Column("journal_operation", "result_schema", "nvarchar", 256, false, "Latin1_General_100_BIN2"),
        Column("journal_operation", "result_schema_version", "int", 4, false, ""),
        Column("journal_operation", "result_data", "varbinary", -1, false, ""),
        Column("journal_operation", "result_sha256", "binary", 32, false, ""),
        Column("journal_operation", "committed_at_utc", "datetimeoffset", 10, false, ""),
        Column("journal_operation_stream", "operation_id", "uniqueidentifier", 16, false, ""),
        Column("journal_operation_stream", "stream_key", "nvarchar", 512, false, "Latin1_General_100_BIN2"),
        Column("journal_operation_stream", "before_version", "bigint", 8, false, ""),
        Column("journal_operation_stream", "after_version", "bigint", 8, false, ""),
        Column("journal_operation_stream", "event_count", "int", 4, false, ""),
        Column("journal_snapshot", "stream_key", "nvarchar", 512, false, "Latin1_General_100_BIN2"),
        Column("journal_snapshot", "through_version", "bigint", 8, false, ""),
        Column("journal_snapshot", "snapshot_schema", "nvarchar", 256, false, "Latin1_General_100_BIN2"),
        Column("journal_snapshot", "snapshot_schema_version", "int", 4, false, ""),
        Column("journal_snapshot", "data", "varbinary", -1, false, ""),
        Column("journal_snapshot", "data_sha256", "binary", 32, false, ""),
        Column("journal_snapshot", "created_at_utc", "datetimeoffset", 10, false, ""),
        Column("journal_projection", "stream_key", "nvarchar", 512, false, "Latin1_General_100_BIN2"),
        Column("journal_projection", "section_name", "nvarchar", 256, false, "Latin1_General_100_BIN2"),
        Column("journal_projection", "source_version", "bigint", 8, false, ""),
        Column("journal_projection", "projection_schema", "nvarchar", 256, false, "Latin1_General_100_BIN2"),
        Column("journal_projection", "projection_schema_version", "int", 4, false, ""),
        Column("journal_projection", "data", "varbinary", -1, false, ""),
        Column("journal_projection", "data_sha256", "binary", 32, false, ""),
        Column("journal_projection", "updated_at_utc", "datetimeoffset", 10, false, ""),
    };

    private static readonly HashSet<string> ExpectedIndexes = new(StringComparer.Ordinal)
    {
        Index("journal_metadata", "pk_journal_metadata", true, true, "CLUSTERED", 1, "metadata_key", false, false),
        Index("journal_stream", "pk_journal_stream", true, true, "CLUSTERED", 1, "stream_key", false, false),
        Index("journal_event", "pk_journal_event", true, true, "CLUSTERED", 1, "stream_key", false, false),
        Index("journal_event", "pk_journal_event", true, true, "CLUSTERED", 2, "stream_version", false, false),
        Index("journal_event", "ix_journal_event_operation", false, false, "NONCLUSTERED", 1, "operation_id", false, false),
        Index("journal_operation", "pk_journal_operation", true, true, "CLUSTERED", 1, "operation_id", false, false),
        Index("journal_operation", "ix_journal_operation_commit", false, false, "NONCLUSTERED", 1, "committed_at_utc", false, false),
        Index("journal_operation", "ix_journal_operation_commit", false, false, "NONCLUSTERED", 2, "operation_id", false, false),
        Index("journal_operation_stream", "pk_journal_operation_stream", true, true, "CLUSTERED", 1, "operation_id", false, false),
        Index("journal_operation_stream", "pk_journal_operation_stream", true, true, "CLUSTERED", 2, "stream_key", false, false),
        Index("journal_snapshot", "pk_journal_snapshot", true, true, "CLUSTERED", 1, "stream_key", false, false),
        Index("journal_projection", "pk_journal_projection", true, true, "CLUSTERED", 1, "stream_key", false, false),
        Index("journal_projection", "pk_journal_projection", true, true, "CLUSTERED", 2, "section_name", false, false),
        Index("journal_projection", "ix_journal_projection_version", false, false, "NONCLUSTERED", 1, "stream_key", false, false),
        Index("journal_projection", "ix_journal_projection_version", false, false, "NONCLUSTERED", 2, "source_version", false, false),
    };

    private static readonly HashSet<string> ExpectedForeignKeys = new(StringComparer.Ordinal)
    {
        ForeignKey("fk_journal_event_stream", "journal_event", "stream_key", "journal_stream", "stream_key", "NO_ACTION", "NO_ACTION", false, false, 1),
        ForeignKey("fk_journal_operation_stream_operation", "journal_operation_stream", "operation_id", "journal_operation", "operation_id", "NO_ACTION", "NO_ACTION", false, false, 1),
        ForeignKey("fk_journal_operation_stream_stream", "journal_operation_stream", "stream_key", "journal_stream", "stream_key", "NO_ACTION", "NO_ACTION", false, false, 1),
        ForeignKey("fk_journal_snapshot_stream", "journal_snapshot", "stream_key", "journal_stream", "stream_key", "NO_ACTION", "NO_ACTION", false, false, 1),
        ForeignKey("fk_journal_projection_stream", "journal_projection", "stream_key", "journal_stream", "stream_key", "NO_ACTION", "NO_ACTION", false, false, 1),
    };

    private static readonly HashSet<string> ExpectedChecks = new(StringComparer.Ordinal)
    {
        Check("ck_journal_metadata_key", "([metadata_key]=(1))", false, false),
        Check("ck_journal_metadata_version", "([schema_version]>=(1))", false, false),
        Check("ck_journal_stream_version", "([current_version]>=(0))", false, false),
        Check("ck_journal_stream_floor", "([retained_floor]>=(0) AND [retained_floor]<=[current_version])", false, false),
        Check("ck_journal_event_version", "([stream_version]>(0))", false, false),
        Check("ck_journal_event_ordinal", "([operation_ordinal]>=(0))", false, false),
        Check("ck_journal_event_schema_version", "([event_schema_version]>(0))", false, false),
        Check("ck_journal_event_payload", "(datalength([payload])<=(262144))", false, false),
        Check("ck_journal_operation_intent_format", "([intent_fingerprint_format]>(0))", false, false),
        Check("ck_journal_operation_execution_format", "([execution_fingerprint_format]>(0))", false, false),
        Check("ck_journal_operation_result_schema_version", "([result_schema_version]>(0))", false, false),
        Check("ck_journal_operation_result", "(datalength([result_data])<=(65536))", false, false),
        Check("ck_journal_operation_stream_versions", "([before_version]>=(0) AND [after_version]>=[before_version])", false, false),
        Check("ck_journal_operation_stream_count", "([event_count]>=(0) AND ([after_version]-[before_version])=[event_count])", false, false),
        Check("ck_journal_snapshot_version", "([through_version]>=(0))", false, false),
        Check("ck_journal_snapshot_schema_version", "([snapshot_schema_version]>(0))", false, false),
        Check("ck_journal_snapshot_data", "(datalength([data])<=(8388608))", false, false),
        Check("ck_journal_projection_version", "([source_version]>=(0))", false, false),
        Check("ck_journal_projection_schema_version", "([projection_schema_version]>(0))", false, false),
        Check("ck_journal_projection_data", "(datalength([data])<=(2097152))", false, false),
    };

    private static readonly HashSet<string> ExpectedDefaults = new(StringComparer.Ordinal)
    {
        Default("df_journal_stream_floor", "journal_stream", "retained_floor", "((0))"),
    };

    private static string Column(string table, string column, string type, int length, bool nullable, string collation)
        => $"{table}.{column}|{type}|{length}|{nullable}|{collation}";

    private static string Index(
        string table,
        string name,
        bool unique,
        bool primaryKey,
        string type,
        int ordinal,
        string column,
        bool descending,
        bool included)
        => $"{table}.{name}|{unique}|{primaryKey}|{type}|{ordinal}|{column}|{descending}|{included}";

    private static string ForeignKey(
        string name,
        string parentTable,
        string parentColumn,
        string referencedTable,
        string referencedColumn,
        string deleteAction,
        string updateAction,
        bool disabled,
        bool untrusted,
        int ordinal)
        => $"{name}|{parentTable}.{parentColumn}|{referencedTable}.{referencedColumn}|{deleteAction}|{updateAction}|{disabled}|{untrusted}|{ordinal}";

    private static string Check(string name, string definition, bool disabled, bool untrusted)
        => $"{name}|{NormalizeSql(definition)}|{disabled}|{untrusted}";

    private static string Default(string name, string table, string column, string definition)
        => $"{name}|{table}.{column}|{NormalizeSql(definition)}";

    private static string NormalizeSql(string sql)
        => string.Concat(sql.Where(value => !char.IsWhiteSpace(value))).ToUpperInvariant();

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, int timeout, string sql)
    {
        SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = timeout;
        command.CommandText = sql;
        return command;
    }

    private static async Task<SqlTransaction> BeginTransactionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            return (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw Failure(JournalStoreFailureKind.Cancelled, "Schema initialization was cancelled before the transaction began.", exception);
        }
        catch (SqlException exception)
        {
            throw exception.Number switch
            {
                1205 => Failure(JournalStoreFailureKind.Deadlock, "Schema initialization was a deadlock victim before transaction admission.", exception),
                1222 or -2 => Failure(JournalStoreFailureKind.Timeout, "Schema initialization timed out before transaction admission.", exception),
                _ => Failure(JournalStoreFailureKind.Unavailable, "SQL Server journal schema could not begin its transaction.", exception),
            };
        }
    }

    private static async Task<bool> TryRollbackAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private static string LoadSchemaSql()
    {
        Assembly assembly = typeof(SqlServerJournalSchema).Assembly;
        string resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("JournalSchemaV1.sql", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded SQL Server journal schema is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static JournalStoreException Mismatch(string actual, Exception? exception = null)
        => new(
            JournalStoreFailureKind.SchemaMismatch,
            JournalStoreFailureCertainty.DefinitelyNotCommitted,
            JournalStoreFailureScope.WholeStore,
            null,
            $"SQL Server mutation journal schema is {actual}. Apply migration '{RequiredMigration}'.",
            exception);

    private static JournalStoreException Failure(JournalStoreFailureKind kind, string message, Exception? exception = null)
        => new(
            kind,
            JournalStoreFailureCertainty.DefinitelyNotCommitted,
            JournalStoreFailureScope.WholeStore,
            null,
            message,
            exception);

    private static JournalStoreException Unknown(string message, Exception exception)
        => new(
            JournalStoreFailureKind.UnknownOutcome,
            JournalStoreFailureCertainty.Unknown,
            JournalStoreFailureScope.WholeStore,
            null,
            message,
            exception);
}
