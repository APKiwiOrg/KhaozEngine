using System;
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
    internal const int CurrentVersion = 1;
    internal const string RequiredMigration = "sqlite-journal-v1-create";

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
            committed_at_utc INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_journal_operation_commit ON journal_operation(committed_at_utc, operation_id);

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
        VALUES (1, 1, lower(hex(randomblob(16))), CAST(strftime('%s', 'now') AS INTEGER) * 1000);
        """;

    internal static string BootstrapSql(SqliteMutationJournalStoreOptions options)
    {
        int milliseconds = checked((int)Math.Ceiling(options.BusyTimeout.TotalMilliseconds));
        string pragmas = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {milliseconds};";
        return options.SchemaMode == SqliteJournalSchemaMode.AutoCreate
            ? $"PRAGMA journal_mode = WAL; {pragmas}"
            : pragmas;
    }

    internal static void Initialize(SqliteConnection connection, SqliteJournalSchemaMode mode)
    {
        try
        {
            if (!MetadataTableExists(connection))
            {
                if (mode == SqliteJournalSchemaMode.ValidateOnly) throw Mismatch("missing");
                using SqliteCommand create = connection.CreateCommand();
                create.CommandText = Tables;
                create.ExecuteNonQuery();
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT schema_version FROM journal_metadata WHERE metadata_key = 1;";
            object? raw = command.ExecuteScalar();
            if (raw is null || raw is DBNull || Convert.ToInt32(raw) != CurrentVersion)
                throw Mismatch(raw is null or DBNull ? "missing" : Convert.ToString(raw) ?? "unknown");

            command.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'table' AND name IN (
                    'journal_metadata', 'journal_stream', 'journal_event', 'journal_operation',
                    'journal_operation_stream', 'journal_snapshot', 'journal_projection');
                """;
            if (Convert.ToInt32(command.ExecuteScalar()) != 7)
                throw Mismatch("version 1 with missing tables");

            command.CommandText = "PRAGMA foreign_keys;";
            if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                throw Mismatch("foreign key enforcement is disabled");

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

    private static bool MetadataTableExists(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'journal_metadata';";
        return command.ExecuteScalar() is not null;
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
