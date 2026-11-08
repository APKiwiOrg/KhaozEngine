using System;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalTask6TestSupport;
using static KhaozEngine.Tests.WorldStore.Journal.SqlServerJournalTestDatabase;

namespace KhaozEngine.Tests.WorldStore.Journal;

[Collection("SQL Server mutation journal")]
public sealed class SqlServerJournalBatchProtocolTests
{
    [SqlServerTheory]
    [InlineData("SELECT 0, 0, NULL, NULL, NULL, NULL WHERE 1 = 0;", JournalStoreRefusal.BatchOutcomeMissing)]
    [InlineData("SELECT 99, 0, NULL, NULL, NULL, NULL;", JournalStoreRefusal.BatchOutcomeUndefined)]
    [InlineData("SELECT 3, 0, NULL, NULL, NULL, NULL;", JournalStoreRefusal.BatchResultMalformed)]
    [InlineData("SELECT 7, 0, NULL, NULL, NULL, NULL;", JournalStoreRefusal.BatchOutcomeUnsupported)]
    [InlineData("SELECT 5, 0, NULL, NULL, NULL, NULL;", JournalStoreRefusal.BatchProjectionLimitIncomplete)]
    [InlineData("SELECT 3, 0, NULL, NULL, NULL, NULL; SELECT 0 WHERE 1 = 0;", JournalStoreRefusal.ReplayedOperationMissing)]
    public async Task Trigger_results_refuse_the_batch_and_roll_back_all_content(string results, JournalStoreRefusal reason)
    {
        // An AFTER trigger's result sets precede the batch's own protocol.
        using var scope = new Task6SqlServerScope();
        var store = scope.Open();
        await store.InitializeAsync(Initialization(1, 7, Projection("bag", 1)));
        await ExecuteAsync(scope.ConnectionString, "CREATE TABLE dbo.host_journal_batch_probe (value int NOT NULL);");
        try
        {
            // CREATE TRIGGER is first in its batch. NOCOUNT hides row counts, not SELECT result sets.
            await ExecuteAsync(scope.ConnectionString, $"""
                CREATE TRIGGER dbo.host_journal_batch_fault ON dbo.journal_event AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.host_journal_batch_probe VALUES (1);
                    {results}
                END;
                """);
            string before = await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString);
            var commit = Commit(2, 0, [2], 2, Projection("bag", 3));
            if (reason == JournalStoreRefusal.ReplayedOperationMissing)
            {
                var refused = await Assert.ThrowsAsync<JournalStoreException>(() => store.CommitAsync(commit));
                Assert.Equal(reason, refused.Reason);
                Assert.Equal(store.PhysicalOperationId(commit.Identity.OperationId), refused.OperationId);
                Assert.Equal(JournalStoreFailureKind.CorruptData, refused.Kind);
                Assert.Equal(JournalStoreFailureCertainty.CommittedDataUnreadable, refused.Certainty);
                Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
                Assert.Empty(refused.StreamKeys);
            }
            else if (reason == JournalStoreRefusal.BatchProjectionLimitIncomplete)
            {
                var refused = await Assert.ThrowsAsync<JournalStoreException>(() => store.CommitAsync(commit));
                Assert.Equal(reason, refused.Reason);
                Assert.Equal(JournalStoreFailureKind.ConstraintViolation, refused.Kind);
                Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
                Assert.Equal(JournalStoreFailureScope.OperationStreams, refused.Scope);
                Assert.Equal(new[] { store.Prefix + StreamKey }, refused.StreamKeys);
                Assert.Null(refused.StreamKey);
            }
            else
            {
                var refused = await Assert.ThrowsAsync<JournalStoreStateException>(() => store.CommitAsync(commit));
                Assert.IsAssignableFrom<InvalidOperationException>(refused);
                Assert.Equal(reason, refused.Reason);
                Assert.Equal(reason == JournalStoreRefusal.BatchOutcomeUndefined ? 99 : reason == JournalStoreRefusal.BatchOutcomeUnsupported ? 7 : null, refused.Outcome);
            }
            Assert.Equal(before, await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString));
            await ExecuteAsync(scope.ConnectionString, "DROP TRIGGER dbo.host_journal_batch_fault;");
            Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(commit)).Status);
        }
        finally
        {
            await ExecuteAsync(scope.ConnectionString, "DROP TRIGGER IF EXISTS dbo.host_journal_batch_fault; DROP TABLE dbo.host_journal_batch_probe;");
        }
    }
    [SqlServerTheory]
    [InlineData(false, JournalStoreRefusal.BatchStreamVanished)]
    [InlineData(true, JournalStoreRefusal.BatchResultMalformed)]
    public async Task Trigger_deleted_stream_refuses_the_batch_and_restores_all_content(bool spoofSuccess, JournalStoreRefusal reason)
    {
        // Serializable locks do not stop a trigger in this same transaction.
        using var scope = new Task6SqlServerScope();
        var store = scope.Open();
        await store.InitializeAsync(Initialization(1, 7, Projection("bag", 1)));
        await store.CommitAsync(Commit(2, 0, [2], 2, Projection("bag", 2)));
        await ExecuteAsync(scope.ConnectionString, "CREATE TABLE dbo.host_journal_batch_probe (value int NOT NULL);");
        try
        {
            await ExecuteAsync(scope.ConnectionString, $"""
                CREATE TRIGGER dbo.host_journal_batch_fault ON dbo.journal_event AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.host_journal_batch_probe VALUES (1);
                    DELETE p FROM dbo.journal_projection p JOIN inserted i ON i.stream_key = p.stream_key;
                    DELETE s FROM dbo.journal_snapshot s JOIN inserted i ON i.stream_key = s.stream_key;
                    DELETE o FROM dbo.journal_operation_stream o JOIN inserted i ON i.stream_key = o.stream_key;
                    DELETE e FROM dbo.journal_event e JOIN inserted i ON i.stream_key = e.stream_key;
                    DELETE s FROM dbo.journal_stream s JOIN inserted i ON i.stream_key = s.stream_key;
                    {(spoofSuccess ? "SELECT 1, 0, NULL, NULL, NULL, NULL;" : string.Empty)}
                END;
                """);
            string before = await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString);
            var commit = Commit(3, 1, [3], 3, Projection("bag", 3));
            var refused = await Assert.ThrowsAsync<JournalStoreStateException>(() => store.CommitAsync(commit));
            Assert.IsAssignableFrom<InvalidOperationException>(refused);
            Assert.Equal(reason, refused.Reason);
            Assert.Null(refused.Outcome);
            Assert.Equal(before, await SqlServerJournalSnapshot.CaptureAsync(scope.ConnectionString));
            await ExecuteAsync(scope.ConnectionString, "DROP TRIGGER dbo.host_journal_batch_fault;");
            Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(commit)).Status);
        }
        finally
        {
            await ExecuteAsync(scope.ConnectionString, "DROP TRIGGER IF EXISTS dbo.host_journal_batch_fault; DROP TABLE dbo.host_journal_batch_probe;");
        }
    }

}
