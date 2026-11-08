using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

[Collection("SQL Server mutation journal")]
public sealed class SqlServerJournalSchemaRefusalTests
{
    [SqlServerFact]
    public Task Missing_version_column_names_metadata_shape_and_rolls_back()
        => RefuseAsync("EXEC sys.sp_rename N'dbo.journal_metadata.schema_version', N'bad_version', N'COLUMN';",
            JournalStoreRefusal.SchemaMetadataShapeInvalid);

    [SqlServerFact]
    public Task Missing_metadata_row_is_refused_and_rolls_back()
        => RefuseAsync("DELETE FROM dbo.journal_metadata;", JournalStoreRefusal.SchemaMetadataMissing);

    [SqlServerFact]
    public Task Null_version_is_missing_metadata_and_rolls_back()
        => RefuseAsync("ALTER TABLE dbo.journal_metadata ALTER COLUMN schema_version int NULL; UPDATE dbo.journal_metadata SET schema_version = NULL;",
            JournalStoreRefusal.SchemaMetadataMissing);

    [SqlServerFact]
    public Task Noninteger_version_names_its_type_and_rolls_back()
        => RefuseAsync("ALTER TABLE dbo.journal_metadata DROP CONSTRAINT ck_journal_metadata_version; ALTER TABLE dbo.journal_metadata ALTER COLUMN schema_version nvarchar(10) NOT NULL;",
            JournalStoreRefusal.SchemaVersionMalformed);

    [SqlServerFact]
    public Task Newer_version_names_supported_versions_and_rolls_back()
        => RefuseAsync("UPDATE dbo.journal_metadata SET schema_version = 4;", JournalStoreRefusal.SchemaVersionUnsupported);

    [SqlServerFact]
    public Task Empty_epoch_is_refused_and_rolls_back()
        => RefuseAsync("UPDATE dbo.journal_metadata SET store_epoch = '00000000-0000-0000-0000-000000000000';",
            JournalStoreRefusal.SchemaEpochEmpty);

    [SqlServerFact]
    public Task Extra_column_names_the_complete_column_difference_and_rolls_back()
        => RefuseAsync("ALTER TABLE dbo.journal_stream ADD unexpected_column int NULL;", JournalStoreRefusal.SchemaColumnsMismatch,
            unexpected: "journal_stream.unexpected_column|int|4|True|");

    [SqlServerFact]
    public Task Disabled_foreign_key_names_the_complete_key_difference_and_rolls_back()
        => RefuseAsync("ALTER TABLE dbo.journal_event NOCHECK CONSTRAINT fk_journal_event_stream;", JournalStoreRefusal.SchemaForeignKeysMismatch,
            missing: "fk_journal_event_stream|journal_event.stream_key|journal_stream.stream_key|NO_ACTION|NO_ACTION|False|False|1",
            unexpected: "fk_journal_event_stream|journal_event.stream_key|journal_stream.stream_key|NO_ACTION|NO_ACTION|True|True|1");

    [SqlServerFact]
    public Task Disabled_check_names_the_complete_check_difference_and_rolls_back()
        => RefuseAsync("ALTER TABLE dbo.journal_stream NOCHECK CONSTRAINT ck_journal_stream_version;", JournalStoreRefusal.SchemaChecksMismatch,
            missing: "ck_journal_stream_version|([CURRENT_VERSION]>=(0))|False|False",
            unexpected: "ck_journal_stream_version|([CURRENT_VERSION]>=(0))|True|True");

    [SqlServerFact]
    public Task Missing_default_names_the_complete_default_difference_and_rolls_back()
        => RefuseAsync("ALTER TABLE dbo.journal_stream DROP CONSTRAINT df_journal_stream_floor;", JournalStoreRefusal.SchemaDefaultsMismatch,
            missing: "df_journal_stream_floor|journal_stream.retained_floor|((0))");

    private static async Task RefuseAsync(string sql, JournalStoreRefusal reason, string? missing = null, string? unexpected = null)
    {
        string connectionString = SqlServerJournalResetHarness.ConnectionString;
        await SqlServerJournalResetHarness.SeedAsync();
        await ExecuteAsync("DROP TABLE IF EXISTS dbo.host_journal_schema_probe; CREATE TABLE dbo.host_journal_schema_probe (value int NOT NULL);");
        try
        {
            string before = await SqlServerJournalSnapshot.CaptureAsync(connectionString);
            var hook = new SqlServerJournalSchemaTestHook(async (connection, transaction, cancellation) =>
            {
                await using (SqlCommand write = connection.CreateCommand())
                {
                    write.Transaction = transaction;
                    write.CommandText = "INSERT INTO dbo.host_journal_schema_probe VALUES (1);";
                    await write.ExecuteNonQueryAsync(cancellation);
                }
                // Compile each statement after earlier DDL has completed.
                foreach (string statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    await using SqlCommand command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync(cancellation);
                }
            });
            var refused = Assert.Throws<JournalStoreException>(() => new SqlServerMutationJournalStore(
                new SqlServerMutationJournalStoreOptions(connectionString) { SchemaMode = SqlServerJournalSchemaMode.ValidateOnly },
                testHook: null, schemaTestHook: hook));
            Assert.Equal(reason, refused.Reason);
            Assert.Equal("sqlserver-journal-v3-row-timestamps", refused.RequiredMigration);
            Assert.Equal(JournalStoreFailureKind.SchemaMismatch, refused.Kind);
            Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
            Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
            if (reason == JournalStoreRefusal.SchemaVersionMalformed) Assert.Equal(typeof(string), refused.MetadataType);
            if (reason == JournalStoreRefusal.SchemaVersionUnsupported)
            {
                Assert.Equal(4L, refused.ActualVersion);
                Assert.Equal(new long[] { 3 }, refused.SupportedVersions);
            }
            if (missing is not null || unexpected is not null)
            {
                Assert.Equal(3L, refused.ExpectedVersion);
                Assert.Equal(missing is null ? Array.Empty<string>() : new[] { missing }, refused.MissingObjects);
                Assert.Equal(unexpected is null ? Array.Empty<string>() : new[] { unexpected }, refused.UnexpectedObjects);
            }
            Assert.Equal(before, await SqlServerJournalSnapshot.CaptureAsync(connectionString));
            _ = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(connectionString) { SchemaMode = SqlServerJournalSchemaMode.ValidateOnly });
        }
        finally { await ExecuteAsync("DROP TABLE IF EXISTS dbo.host_journal_schema_probe;"); }
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(SqlServerJournalResetHarness.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(1205, JournalOperationStage.SchemaInitialization, JournalStoreFailureKind.Deadlock)]
    [InlineData(1222, JournalOperationStage.SchemaTransactionBegin, JournalStoreFailureKind.Timeout)]
    [InlineData(-2, JournalOperationStage.SchemaReadOnlyValidation, JournalStoreFailureKind.Timeout)]
    [InlineData(50000, JournalOperationStage.SchemaInitialization, JournalStoreFailureKind.Unavailable)]
    [InlineData(1205, JournalOperationStage.SchemaOpen, JournalStoreFailureKind.Unavailable)]
    [InlineData(1205, JournalOperationStage.SchemaReadOnlyOpen, JournalStoreFailureKind.Unavailable)]
    public void Provider_failure_keeps_the_typed_number_stage_and_existing_classification(int code, JournalOperationStage stage, JournalStoreFailureKind kind)
    {
        var refused = Assert.Throws<JournalStoreException>(() => SqlServerJournalSchema.RefuseSchemaProviderFailureForTests(code, stage));
        Assert.Equal(JournalStoreRefusal.SchemaProviderFailure, refused.Reason);
        Assert.Equal(code, refused.ProviderCode);
        Assert.Equal(stage, refused.Stage);
        Assert.Equal(kind, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
        var cancelled = Assert.Throws<JournalStoreException>(() => SqlServerJournalSchema.RefuseSchemaProviderFailureForTests(code, stage, cancelled: true));
        Assert.Equal(JournalStoreRefusal.SchemaCancelled, cancelled.Reason);
        Assert.Equal(stage, cancelled.Stage);
        Assert.Null(cancelled.ProviderCode);
    }

    [Fact]
    public void Unconfirmed_schema_rollback_preserves_unknown_certainty()
    {
        var refused = Assert.Throws<JournalStoreException>(() => SqlServerJournalSchema.RefuseSchemaProviderFailureForTests(50000,
            JournalOperationStage.SchemaInitialization, rollbackConfirmed: false));
        Assert.Equal(JournalStoreRefusal.SchemaRollbackUnconfirmed, refused.Reason);
        Assert.Equal(JournalStoreFailureKind.UnknownOutcome, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.Unknown, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
        var rolledBack = Assert.Throws<JournalStoreException>(() => SqlServerJournalSchema.RefuseSchemaProviderFailureForTests(50000,
            JournalOperationStage.SchemaInitialization));
        Assert.Equal(JournalStoreRefusal.SchemaProviderFailure, rolledBack.Reason);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, rolledBack.Certainty);
    }

    [Fact]
    public async Task Precancelled_schema_open_names_the_refused_stage_without_connecting()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        const string connection = "Server=localhost;Database=cancelled-journal-test-open;Integrated Security=true;";
        var writer = await Assert.ThrowsAsync<JournalStoreException>(() => SqlServerJournalSchema.InitializeAsync(connection,
            SqlServerJournalSchemaMode.ValidateOnly, 30, cancellation.Token));
        Assert.Equal(JournalStoreRefusal.SchemaCancelled, writer.Reason);
        Assert.Equal(JournalOperationStage.SchemaOpen, writer.Stage);
        var reader = await Assert.ThrowsAsync<JournalStoreException>(() => SqlServerJournalSchema.ValidateReadOnlyAsync(connection, 30, cancellation.Token));
        Assert.Equal(JournalStoreRefusal.SchemaCancelled, reader.Reason);
        Assert.Equal(JournalOperationStage.SchemaReadOnlyOpen, reader.Stage);
    }
}
