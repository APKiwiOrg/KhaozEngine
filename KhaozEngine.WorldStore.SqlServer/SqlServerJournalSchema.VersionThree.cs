using System;
using System.Collections.Generic;

namespace KhaozEngine.WorldStore.SqlServer;

/// <summary>
/// Schema version 3 and the version 2 to 3 migration. Version 3 adds a nullable <c>created_at_utc</c>
/// <c>datetimeoffset(7)</c> to <c>journal_metadata</c>, <c>journal_stream</c>, <c>journal_operation_stream</c> and
/// <c>journal_projection</c>. A legacy row takes a time only where the database proves it and stays NULL otherwise.
/// Version 3 adds no index, key, check, default or trigger, so those sets serve version 2 unchanged.
/// <para>
/// Every version's column set is declared here, version 3 first and each earlier one derived from the next, because
/// static field initializers in different parts of a partial class run in no defined order.
/// </para>
/// </summary>
internal static partial class SqlServerJournalSchema
{
    /// <summary>
    /// Each column add is its own statement guarded by <c>COL_LENGTH</c>, so a database that already carries some of
    /// the columns gains only the rest. A stream's creation time is its initialization snapshot's, and only while that
    /// snapshot has never been replaced (<c>through_version = 0</c>). Every other stream is set to NULL. An operation
    /// is never a source, because a later commit with no events has the same shape as an initialization. A receipt
    /// stream range was written with its operation, so it takes the operation's commit time. The version moves last.
    /// </summary>
    internal static IReadOnlyList<string> VersionTwoMigrationSql { get; } = Array.AsReadOnly(new[]
    {
        AddCreationColumn("journal_metadata"),
        AddCreationColumn("journal_stream"),
        AddCreationColumn("journal_operation_stream"),
        AddCreationColumn("journal_projection"),
        """
        UPDATE st
        SET created_at_utc = sn.created_at_utc
        FROM dbo.journal_stream st
        LEFT JOIN dbo.journal_snapshot sn
            ON sn.stream_key = st.stream_key AND sn.through_version = 0;
        """,
        """
        UPDATE os
        SET created_at_utc = op.committed_at_utc
        FROM dbo.journal_operation_stream os
        JOIN dbo.journal_operation op ON op.operation_id = os.operation_id;
        """,
        """
        UPDATE dbo.journal_metadata
        SET schema_version = 3,
            updated_at_utc = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
        WHERE metadata_key = 1 AND schema_version = 2;
        """,
    });

    private static readonly HashSet<string> ExpectedColumns = new(StringComparer.Ordinal)
    {
        Column("journal_metadata", "metadata_key", "tinyint", 1, false, ""),
        Column("journal_metadata", "schema_version", "int", 4, false, ""),
        Column("journal_metadata", "store_epoch", "uniqueidentifier", 16, false, ""),
        Column("journal_metadata", "updated_at_utc", "datetimeoffset", 10, false, ""),
        Column("journal_metadata", "created_at_utc", "datetimeoffset", 10, true, ""),
        Column("journal_stream", "stream_key", "nvarchar", 512, false, "Latin1_General_100_BIN2"),
        Column("journal_stream", "current_version", "bigint", 8, false, ""),
        Column("journal_stream", "retained_floor", "bigint", 8, false, ""),
        Column("journal_stream", "updated_at_utc", "datetimeoffset", 10, false, ""),
        Column("journal_stream", "created_at_utc", "datetimeoffset", 10, true, ""),
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
        Column("journal_operation", "retention_started_at_utc", "datetimeoffset", 10, false, ""),
        Column("journal_operation_stream", "operation_id", "uniqueidentifier", 16, false, ""),
        Column("journal_operation_stream", "stream_key", "nvarchar", 512, false, "Latin1_General_100_BIN2"),
        Column("journal_operation_stream", "before_version", "bigint", 8, false, ""),
        Column("journal_operation_stream", "after_version", "bigint", 8, false, ""),
        Column("journal_operation_stream", "event_count", "int", 4, false, ""),
        Column("journal_operation_stream", "created_at_utc", "datetimeoffset", 10, true, ""),
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
        Column("journal_projection", "created_at_utc", "datetimeoffset", 10, true, ""),
    };

    /// <summary>The columns version 3 adds, each in the only shape version 3 accepts.</summary>
    private static readonly string[] VersionThreeColumns =
    {
        Column("journal_metadata", "created_at_utc", "datetimeoffset", 10, true, ""),
        Column("journal_stream", "created_at_utc", "datetimeoffset", 10, true, ""),
        Column("journal_operation_stream", "created_at_utc", "datetimeoffset", 10, true, ""),
        Column("journal_projection", "created_at_utc", "datetimeoffset", 10, true, ""),
    };

    private static readonly HashSet<string> ExpectedColumnsV2 = Without(ExpectedColumns, VersionThreeColumns);

    private static readonly HashSet<string> ExpectedColumnsV1 = Without(
        ExpectedColumnsV2,
        Column("journal_operation", "retention_started_at_utc", "datetimeoffset", 10, false, ""));

    internal static IReadOnlySet<string> ExpectedColumnsFor(int version)
        => version switch
        {
            1 => ExpectedColumnsV1,
            2 => ExpectedColumnsV2,
            3 => ExpectedColumns,
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, "No journal schema has this version."),
        };

    /// <summary>
    /// Whether <paramref name="actual"/> is the column set of <paramref name="version"/>. A version 2 table may already
    /// carry its version 3 column, in exactly its version 3 shape, when the column adds ran without the version move.
    /// The migration then adds only what is missing.
    /// </summary>
    internal static bool ColumnsMatch(IReadOnlySet<string> actual, int version)
    {
        if (version != 2) return actual.SetEquals(ExpectedColumnsFor(version));
        var withoutAdded = new HashSet<string>(actual, StringComparer.Ordinal);
        withoutAdded.ExceptWith(VersionThreeColumns);
        return withoutAdded.SetEquals(ExpectedColumnsV2);
    }

    private static string AddCreationColumn(string table)
        => $"""
            IF COL_LENGTH(N'dbo.{table}', N'created_at_utc') IS NULL
                ALTER TABLE dbo.{table} ADD created_at_utc datetimeoffset(7) NULL;
            """;
}
