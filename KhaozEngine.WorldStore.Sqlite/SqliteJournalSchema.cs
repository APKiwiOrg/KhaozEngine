using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

public enum SqliteJournalSchemaMode
{
    AutoCreate,
    ValidateOnly,
}

internal static class SqliteJournalSchema
{
    internal const int CurrentVersion = 2;
    internal const string RequiredMigration = "sqlite-journal-v2-operation-retention";

    private const string Tables = """
        CREATE TABLE IF NOT EXISTS journal_metadata (
            metadata_key INTEGER NOT NULL PRIMARY KEY CHECK (metadata_key = 1),
            schema_version INTEGER NOT NULL CHECK (schema_version >= 1),
            store_epoch TEXT COLLATE BINARY NOT NULL CHECK (length(store_epoch) IN (32, 36)),
            updated_at_utc INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS journal_stream (
            stream_key TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
            current_version INTEGER NOT NULL CHECK (current_version >= 0),
            retained_floor INTEGER NOT NULL DEFAULT 0 CHECK (retained_floor >= 0 AND retained_floor <= current_version),
            updated_at_utc INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS journal_event (
            stream_key TEXT COLLATE BINARY NOT NULL,
            stream_version INTEGER NOT NULL CHECK (stream_version > 0),
            operation_id TEXT COLLATE BINARY NOT NULL CHECK (length(operation_id) = 36),
            operation_ordinal INTEGER NOT NULL CHECK (operation_ordinal >= 0),
            event_type TEXT COLLATE BINARY NOT NULL CHECK (length(event_type) BETWEEN 1 AND 128),
            event_schema_version INTEGER NOT NULL CHECK (event_schema_version > 0),
            payload BLOB NOT NULL CHECK (length(payload) <= 262144),
            payload_sha256 BLOB NOT NULL CHECK (length(payload_sha256) = 32),
            committed_at_utc INTEGER NOT NULL,
            PRIMARY KEY (stream_key, stream_version),
            FOREIGN KEY (stream_key) REFERENCES journal_stream(stream_key));
        CREATE INDEX IF NOT EXISTS ix_journal_event_operation ON journal_event(operation_id);

        CREATE TABLE IF NOT EXISTS journal_operation (
            operation_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY CHECK (length(operation_id) = 36),
            operation_kind TEXT COLLATE BINARY NOT NULL CHECK (length(operation_kind) BETWEEN 1 AND 128),
            intent_fingerprint_format INTEGER NOT NULL CHECK (intent_fingerprint_format > 0),
            intent_fingerprint BLOB NOT NULL CHECK (length(intent_fingerprint) = 32),
            execution_fingerprint_format INTEGER NOT NULL CHECK (execution_fingerprint_format > 0),
            execution_fingerprint BLOB NOT NULL CHECK (length(execution_fingerprint) = 32),
            result_schema TEXT COLLATE BINARY NOT NULL CHECK (length(result_schema) BETWEEN 1 AND 128),
            result_schema_version INTEGER NOT NULL CHECK (result_schema_version > 0),
            result_data BLOB NOT NULL CHECK (length(result_data) <= 65536),
            result_sha256 BLOB NOT NULL CHECK (length(result_sha256) = 32),
            committed_at_utc INTEGER NOT NULL,
            retention_started_at_utc INTEGER NOT NULL DEFAULT 9223372036854775807);
        CREATE INDEX IF NOT EXISTS ix_journal_operation_commit ON journal_operation(committed_at_utc, operation_id);
        CREATE INDEX IF NOT EXISTS ix_journal_operation_retention ON journal_operation(retention_started_at_utc, operation_id);
        CREATE TRIGGER IF NOT EXISTS trg_journal_operation_retention
        AFTER INSERT ON journal_operation
        WHEN NEW.retention_started_at_utc = 9223372036854775807
        BEGIN
            UPDATE journal_operation
            SET retention_started_at_utc = CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
            WHERE operation_id = NEW.operation_id;
        END;

        CREATE TABLE IF NOT EXISTS journal_operation_stream (
            operation_id TEXT COLLATE BINARY NOT NULL,
            stream_key TEXT COLLATE BINARY NOT NULL,
            before_version INTEGER NOT NULL CHECK (before_version >= 0),
            after_version INTEGER NOT NULL CHECK (after_version >= before_version),
            event_count INTEGER NOT NULL CHECK (event_count >= 0 AND after_version - before_version = event_count),
            PRIMARY KEY (operation_id, stream_key),
            FOREIGN KEY (operation_id) REFERENCES journal_operation(operation_id),
            FOREIGN KEY (stream_key) REFERENCES journal_stream(stream_key));

        CREATE TABLE IF NOT EXISTS journal_snapshot (
            stream_key TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
            through_version INTEGER NOT NULL CHECK (through_version >= 0),
            snapshot_schema TEXT COLLATE BINARY NOT NULL CHECK (length(snapshot_schema) BETWEEN 1 AND 128),
            snapshot_schema_version INTEGER NOT NULL CHECK (snapshot_schema_version > 0),
            data BLOB NOT NULL CHECK (length(data) <= 8388608),
            data_sha256 BLOB NOT NULL CHECK (length(data_sha256) = 32),
            created_at_utc INTEGER NOT NULL,
            FOREIGN KEY (stream_key) REFERENCES journal_stream(stream_key));

        CREATE TABLE IF NOT EXISTS journal_projection (
            stream_key TEXT COLLATE BINARY NOT NULL,
            section_name TEXT COLLATE BINARY NOT NULL CHECK (length(section_name) BETWEEN 1 AND 128),
            source_version INTEGER NOT NULL CHECK (source_version >= 0),
            projection_schema TEXT COLLATE BINARY NOT NULL CHECK (length(projection_schema) BETWEEN 1 AND 128),
            projection_schema_version INTEGER NOT NULL CHECK (projection_schema_version > 0),
            data BLOB NOT NULL CHECK (length(data) <= 2097152),
            data_sha256 BLOB NOT NULL CHECK (length(data_sha256) = 32),
            updated_at_utc INTEGER NOT NULL,
            PRIMARY KEY (stream_key, section_name),
            FOREIGN KEY (stream_key) REFERENCES journal_stream(stream_key));
        CREATE INDEX IF NOT EXISTS ix_journal_projection_version ON journal_projection(stream_key, source_version);

        INSERT OR IGNORE INTO journal_metadata(metadata_key, schema_version, store_epoch, updated_at_utc)
        VALUES (1, 2, lower(hex(randomblob(16))), CAST(strftime('%s', 'now') AS INTEGER) * 1000);
        """;

    internal static string VersionOneSchemaSqlForTest => VersionOneTables;

    private static string VersionOneTables => Tables
        .Replace(",\n    retention_started_at_utc INTEGER NOT NULL DEFAULT 9223372036854775807", string.Empty, StringComparison.Ordinal)
        .Replace("CREATE INDEX IF NOT EXISTS ix_journal_operation_retention ON journal_operation(retention_started_at_utc, operation_id);\n", string.Empty, StringComparison.Ordinal)
        .Replace("""
CREATE TRIGGER IF NOT EXISTS trg_journal_operation_retention
AFTER INSERT ON journal_operation
WHEN NEW.retention_started_at_utc = 9223372036854775807
BEGIN
    UPDATE journal_operation
    SET retention_started_at_utc = CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
    WHERE operation_id = NEW.operation_id;
END;
""", string.Empty, StringComparison.Ordinal)
        .Replace("VALUES (1, 2, lower(hex(randomblob(16)))", "VALUES (1, 1, lower(hex(randomblob(16)))", StringComparison.Ordinal);

    internal static string BootstrapSql(SqliteMutationJournalStoreOptions options)
    {
        int milliseconds = checked((int)Math.Ceiling(options.BusyTimeout.TotalMilliseconds));
        return $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {milliseconds};";
    }

    internal static void Initialize(SqliteConnection connection, SqliteJournalSchemaMode mode)
    {
        try
        {
            IReadOnlyDictionary<string, string> actual = ReadSchemaObjects(connection);
            if (actual.Count == 0)
            {
                if (mode == SqliteJournalSchemaMode.ValidateOnly) throw Mismatch("missing");
                EnableWal(connection);
                using SqliteCommand create = connection.CreateCommand();
                create.CommandText = Tables;
                create.ExecuteNonQuery();
                actual = ReadSchemaObjects(connection);
            }

            long version = ReadSchemaVersion(connection);
            if (version == 1)
            {
                ValidateSchemaObjects(actual, VersionOneTables, 1);
                if (mode == SqliteJournalSchemaMode.ValidateOnly) throw Mismatch("unsupported version '1'");
                MigrateVersionOne(connection);
                actual = ReadSchemaObjects(connection);
                version = ReadSchemaVersion(connection);
            }
            if (version != CurrentVersion) throw Mismatch($"unsupported version '{version}'");
            ValidateSchemaObjects(actual, Tables, CurrentVersion);

            using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "PRAGMA foreign_keys;";
            if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                throw Mismatch("foreign key enforcement is disabled");

            if (mode == SqliteJournalSchemaMode.AutoCreate) EnableWal(connection);
            command.CommandText = "PRAGMA journal_mode;";
            string journalMode = Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
            if (!StringComparer.OrdinalIgnoreCase.Equals(journalMode, "wal") && !StringComparer.OrdinalIgnoreCase.Equals(journalMode, "memory"))
                throw Mismatch($"journal mode is '{journalMode}' instead of WAL");
        }
        catch (JournalStoreException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw Mismatch("schema metadata is unavailable", exception);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadSchemaObjects(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT type || ':' || name, sql
            FROM sqlite_master
            WHERE (type = 'table' AND lower(name) LIKE 'journal_%')
               OR (type = 'index' AND lower(name) LIKE 'ix_journal_%')
               OR (type = 'trigger' AND lower(name) LIKE 'trg_journal_%')
            ORDER BY type, name COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var objects = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) objects.Add(reader.GetString(0), NormalizeSql(reader.GetString(1)));
        return objects;
    }

    private static long ReadSchemaVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version FROM journal_metadata WHERE metadata_key = 1;";
        object? raw = command.ExecuteScalar();
        return raw is long version
            ? version
            : throw Mismatch(raw is null or DBNull ? "missing" : Convert.ToString(raw) ?? "unknown");
    }

    private static void MigrateVersionOne(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE journal_operation
            ADD COLUMN retention_started_at_utc INTEGER NOT NULL DEFAULT 9223372036854775807;
            CREATE INDEX ix_journal_operation_retention
            ON journal_operation(retention_started_at_utc, operation_id);
            UPDATE journal_operation
            SET retention_started_at_utc = CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER);
            CREATE TRIGGER trg_journal_operation_retention
            AFTER INSERT ON journal_operation
            WHEN NEW.retention_started_at_utc = 9223372036854775807
            BEGIN
                UPDATE journal_operation
                SET retention_started_at_utc = CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
                WHERE operation_id = NEW.operation_id;
            END;
            UPDATE journal_metadata
            SET schema_version = 2,
                updated_at_utc = CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
            WHERE metadata_key = 1 AND schema_version = 1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ValidateSchemaObjects(
        IReadOnlyDictionary<string, string> actual,
        string expectedTables,
        int expectedVersion)
    {
        using var reference = new SqliteConnection("Data Source=:memory:");
        reference.Open();
        using (SqliteCommand create = reference.CreateCommand())
        {
            create.CommandText = expectedTables;
            create.ExecuteNonQuery();
        }
        IReadOnlyDictionary<string, string> expected = ReadSchemaObjects(reference);
        if (actual.Count != expected.Count)
            throw Mismatch("partial or contains unexpected journal objects");
        foreach ((string name, string expectedSql) in expected)
        {
            if (!actual.TryGetValue(name, out string? actualSql) || !StringComparer.Ordinal.Equals(actualSql, expectedSql))
                throw Mismatch($"version {expectedVersion} object '{name}' does not match the supported shape");
        }
    }

    private static string NormalizeSql(string sql)
        => string.Concat(sql.Where(value => !char.IsWhiteSpace(value))).ToUpperInvariant();

    private static void EnableWal(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteScalar();
    }

    private static JournalStoreException Mismatch(string actual, Exception? innerException = null)
        => new(
            JournalStoreFailureKind.SchemaMismatch,
            JournalStoreFailureCertainty.DefinitelyNotCommitted,
            JournalStoreFailureScope.WholeStore,
            null,
            $"SQLite mutation journal schema is {actual}. Apply migration '{RequiredMigration}'.",
            innerException);
}
