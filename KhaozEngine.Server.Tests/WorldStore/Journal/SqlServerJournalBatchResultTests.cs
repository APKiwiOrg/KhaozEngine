using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class SqlServerJournalBatchResultTests
{
    [Theory]
    [InlineData(false, JournalStoreRefusal.BatchOutcomeMissing)]
    [InlineData(true, JournalStoreRefusal.BatchOutcomeUndefined)]
    public async Task Invalid_outcome_keeps_the_state_exception_catch_contract(bool undefined, JournalStoreRefusal reason)
    {
        // W7-B18b: host triggers can emit these result sets before the batch outcome.
        using DataTable outcome = Outcome(undefined ? 99 : null);
        using var reader = new DataTableReader(outcome);
        var refused = await Assert.ThrowsAsync<JournalStoreStateException>(() =>
            SqlServerJournalBatchResultReader.ReadAsync(reader, Guid.NewGuid(), CancellationToken.None));
        Assert.IsAssignableFrom<InvalidOperationException>(refused);
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(undefined ? 99 : null, refused.Outcome);
        using var control = new DataTableReader(Outcome(1));
        Assert.Equal(SqlServerJournalWriteOutcome.Applied,
            (await SqlServerJournalBatchResultReader.ReadAsync(control, Guid.NewGuid(), CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(false, JournalStoreRefusal.ReplayedOperationMissing)]
    [InlineData(true, JournalStoreRefusal.ReplayedStreamsMissing)]
    public async Task Missing_replay_result_preserves_corruption_dimensions(bool hasOperation, JournalStoreRefusal reason)
    {
        Guid operationId = Guid.NewGuid();
        using DataTable outcome = Outcome(3);
        using DataTable operation = Operation();
        using var reader = new DataTableReader(hasOperation ? [outcome, operation] : [outcome, operation.Clone()]);
        var refused = await Assert.ThrowsAsync<JournalStoreException>(() =>
            SqlServerJournalBatchResultReader.ReadAsync(reader, operationId, CancellationToken.None));
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(operationId, refused.OperationId);
        Assert.Equal(JournalStoreFailureKind.CorruptData, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.CommittedDataUnreadable, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
        Assert.Empty(refused.StreamKeys);
        using DataTable streams = Streams();
        using var control = new DataTableReader([outcome, operation, streams]);
        JournalCommitReceipt receipt = Assert.IsType<JournalCommitReceipt>(
            (await SqlServerJournalBatchResultReader.ReadAsync(control, operationId, CancellationToken.None)).Receipt);
        Assert.Equal(operationId, receipt.OperationId);
        Assert.Equal("stream", Assert.Single(receipt.Streams).StreamKey);
        Assert.Equal(new byte[] { 1 }, receipt.ResultData.ToArray());
        Assert.True(receipt.IsReplay);
    }

    [Fact]
    public void Incomplete_projection_limit_keeps_the_state_exception_catch_contract()
    {
        var batch = new SqlServerJournalWriteResult(SqlServerJournalWriteOutcome.ProjectionLimit, 0, null, null, null, null, null);
        var refused = Assert.Throws<JournalStoreStateException>(() => SqlServerJournalBatchResultReader.ProjectionLimitFailure(batch));
        Assert.IsAssignableFrom<InvalidOperationException>(refused);
        Assert.Equal(JournalStoreRefusal.BatchProjectionLimitIncomplete, refused.Reason);
        Assert.Null(refused.Outcome);
        var control = batch with { LimitStream = "stream", LimitReason = JournalStoreRefusal.ProjectionSectionCountExceeded, LimitActual = 3, LimitMaximum = 2 };
        Assert.Equal(JournalStoreRefusal.ProjectionSectionCountExceeded, SqlServerJournalBatchResultReader.ProjectionLimitFailure(control).Reason);
    }

    private static DataTable Outcome(int? outcome)
    {
        var table = new DataTable();
        foreach (Type type in new[] { typeof(int), typeof(int), typeof(string), typeof(int), typeof(long), typeof(long) })
            table.Columns.Add(null, type);
        if (outcome.HasValue) table.Rows.Add(outcome.Value, 0, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
        return table;
    }

    private static DataTable Operation()
    {
        var table = new DataTable();
        foreach (Type type in new[] { typeof(string), typeof(int), typeof(byte[]), typeof(byte[]), typeof(DateTimeOffset) })
            table.Columns.Add(null, type);
        table.Rows.Add("result", 1, new byte[] { 1 }, JournalCanonicalizer.ComputeSha256(new byte[] { 1 }), DateTimeOffset.UnixEpoch);
        return table;
    }

    private static DataTable Streams()
    {
        var table = new DataTable();
        foreach (Type type in new[] { typeof(string), typeof(long), typeof(long), typeof(int) }) table.Columns.Add(null, type);
        table.Rows.Add("stream", 0L, 1L, 1);
        return table;
    }
}
