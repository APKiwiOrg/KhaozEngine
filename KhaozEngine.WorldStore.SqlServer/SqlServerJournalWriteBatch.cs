using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

internal enum SqlServerJournalWriteOutcome
{
    Applied = 1,
    VersionConflict = 2,
    Replayed = 3,
    OperationConflict = 4,
    ProjectionLimit = 5,
    ApplicationLock = 6,
    ExistingStream = 7,
    StreamVanished = 8,
}

/// <summary>
/// Builds the single parameterized T-SQL batch that performs one journal initialization or commit inside the
/// caller's serializable transaction. The batch acquires the shared maintenance lock, resolves the operation for
/// idempotent replay, locks every touched stream head in ordinal stream-key order, checks projection limits,
/// writes events, heads, projections and the operation receipt, and reports its outcome through a result set.
/// The caller still owns BEGIN and COMMIT, so no result is visible before the caller commits.
/// </summary>
internal static class SqlServerJournalWriteBatch
{
    private const string MaintenanceLockResource = "KhaozEngine.WorldStore.SqlServer.JournalMaintenance";

    internal static void BuildCommit(
        SqlCommand command,
        JournalCommit commit,
        JournalFingerprint intent,
        JournalFingerprint execution,
        DateTimeOffset now,
        JournalLimits limits,
        int lockTimeoutMilliseconds,
        bool skipOperationLookup)
    {
        JournalStreamMutation[] streams = Ordered(commit.StreamMutations);
        IReadOnlyList<JournalProjectionWrite> projections = commit.ProjectionWrites;
        var text = new StringBuilder();
        AppendPrologue(text, command, commit.Identity.OperationId, intent, now, lockTimeoutMilliseconds, skipOperationLookup);
        AppendSectionParameters(command, projections);
        AppendHeadLocks(text, command, streams);
        AppendProjectionLimits(text, streams, projections, limits);
        AppendEventInserts(text, command, streams);
        AppendHeadUpdates(text, streams);
        for (int index = 0; index < projections.Count; index++)
        {
            int stream = StreamIndex(streams, projections[index].StreamKey);
            AppendProjectionUpsert(text, command, StreamParameter(stream), index, projections[index], AfterVersion(streams, stream));
        }
        AppendOperationInsert(
            text,
            command,
            commit.Identity,
            intent,
            execution,
            commit.ResultSchema,
            commit.ResultSchemaVersion,
            commit.ResultData.ToArray(),
            commit.ResultChecksum.ToArray());
        AppendOperationStreamInserts(text, streams);
        AppendEpilogue(text);
        command.CommandText = text.ToString();
    }

    internal static void BuildInitialize(
        SqlCommand command,
        JournalInitialization initialization,
        JournalFingerprint intent,
        JournalFingerprint execution,
        DateTimeOffset now,
        int lockTimeoutMilliseconds,
        bool skipOperationLookup)
    {
        IReadOnlyList<JournalProjectionWrite> projections = initialization.ProjectionWrites;
        var text = new StringBuilder();
        AppendPrologue(text, command, initialization.Identity.OperationId, intent, now, lockTimeoutMilliseconds, skipOperationLookup);
        AppendSectionParameters(command, projections);
        SqlServerMutationJournalStore.Add(command, StreamParameter(0), initialization.AbsentStreamKey);
        text.Append(CultureInfo.InvariantCulture, $"""
                SET @head = NULL;
                SELECT @head = current_version FROM dbo.journal_stream WITH (UPDLOCK, HOLDLOCK) WHERE stream_key = @stream0;
                IF @head IS NOT NULL BEGIN SET @outcome = 7; BREAK; END

                INSERT INTO dbo.journal_stream(stream_key, current_version, retained_floor, updated_at_utc)
                VALUES (@stream0, 0, 0, @now);

                INSERT INTO dbo.journal_snapshot(
                    stream_key, through_version, snapshot_schema, snapshot_schema_version, data, data_sha256, created_at_utc)
                VALUES (@stream0, 0, @snapshotSchema, @snapshotSchemaVersion, @snapshotData, @snapshotChecksum, @now);


            """);
        SqlServerMutationJournalStore.Add(command, "@snapshotSchema", initialization.SnapshotSchema);
        SqlServerMutationJournalStore.Add(command, "@snapshotSchemaVersion", initialization.SnapshotSchemaVersion);
        SqlServerMutationJournalStore.Add(command, "@snapshotData", initialization.SnapshotData.ToArray());
        SqlServerMutationJournalStore.Add(command, "@snapshotChecksum", initialization.SnapshotChecksum.ToArray());
        for (int index = 0; index < projections.Count; index++)
            AppendProjectionUpsert(text, command, StreamParameter(0), index, projections[index], "0");
        AppendOperationInsert(
            text,
            command,
            initialization.Identity,
            intent,
            execution,
            initialization.ResultSchema,
            initialization.ResultSchemaVersion,
            initialization.ResultData.ToArray(),
            initialization.ResultChecksum.ToArray());
        text.Append("""
                INSERT INTO dbo.journal_operation_stream(operation_id, stream_key, before_version, after_version, event_count)
                VALUES (@operation, @stream0, 0, 0, 0);


            """);
        AppendEpilogue(text);
        command.CommandText = text.ToString();
    }

    private static void AppendPrologue(
        StringBuilder text,
        SqlCommand command,
        Guid operationId,
        JournalFingerprint intent,
        DateTimeOffset now,
        int lockTimeoutMilliseconds,
        bool skipOperationLookup)
    {
        SqlServerMutationJournalStore.Add(command, "@operation", operationId);
        SqlServerMutationJournalStore.Add(command, "@intent", intent.Digest.ToArray());
        SqlServerMutationJournalStore.Add(command, "@now", now);
        SqlServerMutationJournalStore.Add(command, "@lockTimeout", lockTimeoutMilliseconds);
        text.Append(CultureInfo.InvariantCulture, $"""
            SET NOCOUNT ON;
            DECLARE @outcome int = 1;
            DECLARE @lockResult int = 0;
            DECLARE @limitStream nvarchar(256) = NULL;
            DECLARE @storedIntent binary(32) = NULL;
            DECLARE @head bigint = NULL;
            DECLARE @sections int = 0;
            DECLARE @bytes bigint = 0;
            BEGIN TRY
            -- Single pass. BREAK is the structured early exit for every non-applied outcome.
            WHILE 1 = 1
            BEGIN
                EXEC @lockResult = sys.sp_getapplock
                    @Resource = N'{MaintenanceLockResource}',
                    @LockMode = 'Shared',
                    @LockOwner = 'Transaction',
                    @LockTimeout = @lockTimeout;
                IF @lockResult < 0 BEGIN SET @outcome = 6; BREAK; END


            """);
        if (skipOperationLookup) return;
        text.Append("""
                SELECT @storedIntent = intent_fingerprint
                FROM dbo.journal_operation WITH (UPDLOCK, HOLDLOCK)
                WHERE operation_id = @operation;
                IF @@ROWCOUNT <> 0
                BEGIN
                    SET @outcome = CASE WHEN @storedIntent = @intent THEN 3 ELSE 4 END;
                    BREAK;
                END


            """);
    }

    private static void AppendHeadLocks(StringBuilder text, SqlCommand command, JournalStreamMutation[] streams)
    {
        for (int index = 0; index < streams.Length; index++)
        {
            string stream = StreamParameter(index);
            string expected = ExpectedParameter(index);
            SqlServerMutationJournalStore.Add(command, stream, streams[index].StreamKey);
            SqlServerMutationJournalStore.Add(command, expected, streams[index].ExpectedVersion);
            text.Append(CultureInfo.InvariantCulture, $"""
                    SET @head = NULL;
                    SELECT @head = current_version FROM dbo.journal_stream WITH (UPDLOCK, HOLDLOCK) WHERE stream_key = {stream};
                    IF @head IS NULL OR @head <> {expected} BEGIN SET @outcome = 2; BREAK; END


                """);
        }
    }

    private static void AppendProjectionLimits(
        StringBuilder text,
        JournalStreamMutation[] streams,
        IReadOnlyList<JournalProjectionWrite> projections,
        JournalLimits limits)
    {
        int start = 0;
        while (start < projections.Count)
        {
            int end = start;
            while (end + 1 < projections.Count
                && StringComparer.Ordinal.Equals(projections[end + 1].StreamKey, projections[start].StreamKey))
                end++;
            string stream = StreamParameter(StreamIndex(streams, projections[start].StreamKey));
            var replaced = new StringBuilder();
            long replacedBytes = 0;
            for (int index = start; index <= end; index++)
            {
                if (index > start) replaced.Append(", ");
                replaced.Append(SectionParameter(index));
                replacedBytes = checked(replacedBytes + projections[index].Data.Length);
            }
            text.Append(CultureInfo.InvariantCulture, $"""
                    SET @sections = 0;
                    SET @bytes = 0;
                    SELECT @sections = COUNT(CASE WHEN section_name NOT IN ({replaced}) THEN 1 END),
                           @bytes = COALESCE(SUM(CASE WHEN section_name NOT IN ({replaced}) THEN DATALENGTH(data) ELSE 0 END), 0)
                    FROM dbo.journal_projection WITH (UPDLOCK, HOLDLOCK)
                    WHERE stream_key = {stream};
                    IF @sections + {Number(end - start + 1)} > {Number(limits.ProjectionSectionsPerStream)}
                        OR @bytes + {Number(replacedBytes)} > {Number(limits.AggregateProjectionBytesPerStream)}
                    BEGIN SET @outcome = 5; SET @limitStream = {stream}; BREAK; END


                """);
            start = end + 1;
        }
    }

    private static void AppendEventInserts(StringBuilder text, SqlCommand command, JournalStreamMutation[] streams)
    {
        var rows = new StringBuilder();
        int ordinal = 0;
        for (int index = 0; index < streams.Length; index++)
        {
            string stream = StreamParameter(index);
            string expected = ExpectedParameter(index);
            for (int position = 0; position < streams[index].Events.Count; position++)
            {
                JournalEvent journalEvent = streams[index].Events[position];
                if (ordinal > 0) rows.Append(",\n        ");
                rows.Append(CultureInfo.InvariantCulture, $"({stream}, {expected} + {Number(position + 1)}, @operation, {Number(ordinal)}, {EventParameter("Type", ordinal)}, {EventParameter("SchemaVersion", ordinal)}, {EventParameter("Payload", ordinal)}, {EventParameter("Checksum", ordinal)}, @now)");
                SqlServerMutationJournalStore.Add(command, EventParameter("Type", ordinal), journalEvent.EventType);
                SqlServerMutationJournalStore.Add(command, EventParameter("SchemaVersion", ordinal), journalEvent.EventSchemaVersion);
                SqlServerMutationJournalStore.Add(command, EventParameter("Payload", ordinal), journalEvent.Payload.ToArray());
                SqlServerMutationJournalStore.Add(command, EventParameter("Checksum", ordinal), journalEvent.PayloadChecksum.ToArray());
                ordinal++;
            }
        }
        if (ordinal == 0) return;
        text.Append(CultureInfo.InvariantCulture, $"""
                INSERT INTO dbo.journal_event(
                    stream_key, stream_version, operation_id, operation_ordinal, event_type,
                    event_schema_version, payload, payload_sha256, committed_at_utc)
                VALUES
                    {rows};


            """);
    }

    private static void AppendHeadUpdates(StringBuilder text, JournalStreamMutation[] streams)
    {
        for (int index = 0; index < streams.Length; index++)
        {
            if (streams[index].Events.Count == 0) continue;
            text.Append(CultureInfo.InvariantCulture, $"""
                    UPDATE dbo.journal_stream
                    SET current_version = {AfterVersion(streams, index)}, updated_at_utc = @now
                    WHERE stream_key = {StreamParameter(index)};
                    IF @@ROWCOUNT <> 1 BEGIN SET @outcome = 8; BREAK; END


                """);
        }
    }

    private static void AppendProjectionUpsert(
        StringBuilder text,
        SqlCommand command,
        string stream,
        int index,
        JournalProjectionWrite projection,
        string sourceVersion)
    {
        string schema = ProjectionParameter("Schema", index);
        string schemaVersion = ProjectionParameter("SchemaVersion", index);
        string data = ProjectionParameter("Data", index);
        string checksum = ProjectionParameter("Checksum", index);
        SqlServerMutationJournalStore.Add(command, schema, projection.ProjectionSchema);
        SqlServerMutationJournalStore.Add(command, schemaVersion, projection.ProjectionSchemaVersion);
        SqlServerMutationJournalStore.Add(command, data, projection.Data.ToArray());
        SqlServerMutationJournalStore.Add(command, checksum, projection.DataChecksum.ToArray());
        text.Append(CultureInfo.InvariantCulture, $"""
                UPDATE dbo.journal_projection
                SET source_version = {sourceVersion}, projection_schema = {schema},
                    projection_schema_version = {schemaVersion}, data = {data},
                    data_sha256 = {checksum}, updated_at_utc = @now
                WHERE stream_key = {stream} AND section_name = {SectionParameter(index)};
                IF @@ROWCOUNT = 0
                    INSERT INTO dbo.journal_projection(
                        stream_key, section_name, source_version, projection_schema,
                        projection_schema_version, data, data_sha256, updated_at_utc)
                    VALUES ({stream}, {SectionParameter(index)}, {sourceVersion}, {schema}, {schemaVersion}, {data}, {checksum}, @now);


            """);
    }

    private static void AppendOperationInsert(
        StringBuilder text,
        SqlCommand command,
        JournalOperationIdentity identity,
        JournalFingerprint intent,
        JournalFingerprint execution,
        string resultSchema,
        int resultSchemaVersion,
        byte[] resultData,
        byte[] resultChecksum)
    {
        SqlServerMutationJournalStore.Add(command, "@kind", identity.ActionKind);
        SqlServerMutationJournalStore.Add(command, "@intentFormat", intent.FormatVersion);
        SqlServerMutationJournalStore.Add(command, "@executionFormat", execution.FormatVersion);
        SqlServerMutationJournalStore.Add(command, "@execution", execution.Digest.ToArray());
        SqlServerMutationJournalStore.Add(command, "@resultSchema", resultSchema);
        SqlServerMutationJournalStore.Add(command, "@resultSchemaVersion", resultSchemaVersion);
        SqlServerMutationJournalStore.Add(command, "@result", resultData);
        SqlServerMutationJournalStore.Add(command, "@resultChecksum", resultChecksum);
        text.Append("""
                INSERT INTO dbo.journal_operation(
                    operation_id, operation_kind, intent_fingerprint_format, intent_fingerprint,
                    execution_fingerprint_format, execution_fingerprint, result_schema,
                    result_schema_version, result_data, result_sha256, committed_at_utc,
                    retention_started_at_utc)
                VALUES (@operation, @kind, @intentFormat, @intent, @executionFormat, @execution,
                        @resultSchema, @resultSchemaVersion, @result, @resultChecksum, @now,
                        TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));


            """);
    }

    private static void AppendOperationStreamInserts(StringBuilder text, JournalStreamMutation[] streams)
    {
        var rows = new StringBuilder();
        for (int index = 0; index < streams.Length; index++)
        {
            if (index > 0) rows.Append(",\n        ");
            rows.Append(CultureInfo.InvariantCulture, $"(@operation, {StreamParameter(index)}, {ExpectedParameter(index)}, {AfterVersion(streams, index)}, {Number(streams[index].Events.Count)})");
        }
        text.Append(CultureInfo.InvariantCulture, $"""
                INSERT INTO dbo.journal_operation_stream(operation_id, stream_key, before_version, after_version, event_count)
                VALUES
                    {rows};


            """);
    }

    private static void AppendEpilogue(StringBuilder text)
        => text.Append("""
                BREAK;
            END

            SELECT @outcome, @lockResult, @limitStream;
            IF @outcome = 3
            BEGIN
                SELECT result_schema, result_schema_version, result_data, result_sha256, committed_at_utc
                FROM dbo.journal_operation WHERE operation_id = @operation;
                SELECT stream_key, before_version, after_version, event_count
                FROM dbo.journal_operation_stream WHERE operation_id = @operation
                ORDER BY stream_key COLLATE Latin1_General_100_BIN2;
            END
            END TRY
            BEGIN CATCH
                THROW;
            END CATCH
            """);

    private static void AppendSectionParameters(SqlCommand command, IReadOnlyList<JournalProjectionWrite> projections)
    {
        for (int index = 0; index < projections.Count; index++)
            SqlServerMutationJournalStore.Add(command, SectionParameter(index), projections[index].SectionName);
    }

    private static JournalStreamMutation[] Ordered(IReadOnlyList<JournalStreamMutation> streams)
    {
        var ordered = new JournalStreamMutation[streams.Count];
        for (int index = 0; index < ordered.Length; index++) ordered[index] = streams[index];
        Array.Sort(ordered, static (left, right) => StringComparer.Ordinal.Compare(left.StreamKey, right.StreamKey));
        return ordered;
    }

    private static int StreamIndex(JournalStreamMutation[] streams, string streamKey)
    {
        for (int index = 0; index < streams.Length; index++)
            if (StringComparer.Ordinal.Equals(streams[index].StreamKey, streamKey))
                return index;
        throw new InvalidOperationException($"Projection stream '{streamKey}' is not touched by the operation.");
    }

    private static string AfterVersion(JournalStreamMutation[] streams, int index)
        => streams[index].Events.Count == 0
            ? ExpectedParameter(index)
            : $"{ExpectedParameter(index)} + {Number(streams[index].Events.Count)}";

    private static string StreamParameter(int index) => "@stream" + Number(index);
    private static string ExpectedParameter(int index) => "@expected" + Number(index);
    private static string SectionParameter(int index) => "@section" + Number(index);
    private static string EventParameter(string part, int index) => "@event" + part + Number(index);
    private static string ProjectionParameter(string part, int index) => "@projection" + part + Number(index);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
