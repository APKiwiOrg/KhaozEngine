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
    [InlineData(false, 6, JournalStoreRefusal.BatchOutcomeMissing)]
    [InlineData(true, 6, JournalStoreRefusal.BatchOutcomeUndefined)]
    [InlineData(true, 3, JournalStoreRefusal.BatchOutcomeUndefined)]
    public async Task Invalid_outcome_keeps_the_state_exception_catch_contract(bool undefined, int columns, JournalStoreRefusal reason)
    {
        // W7-B18b: host triggers can emit these result sets before the batch outcome.
        using DataTable outcome = Outcome(undefined ? 99 : null);
        while (outcome.Columns.Count > columns) outcome.Columns.RemoveAt(outcome.Columns.Count - 1);
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

    [Theory]
    [InlineData(3, -1, typeof(int), false)]
    [InlineData(7, -1, typeof(int), false)]
    [InlineData(6, 0, typeof(int), true)]
    [InlineData(6, 0, typeof(string), false)]
    [InlineData(6, 1, typeof(int), true)]
    [InlineData(6, 1, typeof(string), false)]
    [InlineData(6, 2, typeof(int), false)]
    [InlineData(6, 3, typeof(string), false)]
    [InlineData(6, 4, typeof(int), false)]
    [InlineData(6, 5, typeof(int), false)]
    public async Task Malformed_outcome_is_a_state_refusal(int columns, int changedColumn, Type type, bool isNull)
    {
        // W7-B18c: validate the outcome before typed getters can throw or accept a spoof.
        using DataTable valid = Outcome(1);
        using DataTable malformed = valid.Clone();
        while (malformed.Columns.Count > columns) malformed.Columns.RemoveAt(malformed.Columns.Count - 1);
        while (malformed.Columns.Count < columns) malformed.Columns.Add(null, typeof(int));
        object[] values = new object[columns];
        Array.Fill(values, DBNull.Value);
        values[0] = 1;
        values[1] = 0;
        if (changedColumn >= 0)
        {
            malformed.Columns[changedColumn].DataType = type;
            values[changedColumn] = isNull ? DBNull.Value : type == typeof(string) ? "bad" : 1;
        }
        malformed.Rows.Add(values);
        using var reader = new DataTableReader(malformed);
        var refused = await Assert.ThrowsAsync<JournalStoreStateException>(() =>
            SqlServerJournalBatchResultReader.ReadAsync(reader, Guid.NewGuid(), CancellationToken.None));
        Assert.IsAssignableFrom<InvalidOperationException>(refused);
        Assert.Equal(JournalStoreRefusal.BatchResultMalformed, refused.Reason);
        Assert.Null(refused.Outcome);
        using var control = new DataTableReader(valid);
        Assert.Equal(SqlServerJournalWriteOutcome.Applied,
            (await SqlServerJournalBatchResultReader.ReadAsync(control, Guid.NewGuid(), CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public async Task Malformed_replay_set_is_a_state_refusal(bool operationSet, bool wrongShape, bool isNull)
    {
        // W7-B18c: a trigger can put the real outcome where a replay row was expected.
        using DataTable outcome = Outcome(3);
        using DataTable operation = Operation();
        using DataTable streams = Streams();
        using DataTable malformed = (operationSet ? operation : streams).Clone();
        object?[] values = (operationSet ? operation : streams).Rows[0].ItemArray;
        if (wrongShape)
        {
            malformed.Columns.Add(null, typeof(int));
            Array.Resize(ref values, values.Length + 1);
            values[^1] = 0;
        }
        else if (isNull) values[0] = DBNull.Value;
        else
        {
            malformed.Columns[0].DataType = typeof(int);
            values[0] = 1;
        }
        malformed.Rows.Add(values);
        using var reader = new DataTableReader(operationSet ? [outcome, malformed, streams] : [outcome, operation, malformed]);
        var refused = await Assert.ThrowsAsync<JournalStoreStateException>(() =>
            SqlServerJournalBatchResultReader.ReadAsync(reader, Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(JournalStoreRefusal.BatchResultMalformed, refused.Reason);
        Assert.Null(refused.Outcome);
        using var control = new DataTableReader([outcome, operation, streams]);
        Assert.Equal(SqlServerJournalWriteOutcome.Replayed,
            (await SqlServerJournalBatchResultReader.ReadAsync(control, Guid.NewGuid(), CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Success_requires_the_complete_protocol_without_trailing_sets(bool replay, bool emptyStreams)
    {
        // W7-B18c: even a complete spoofed success is followed by the real batch outcome.
        Guid operationId = Guid.NewGuid();
        using DataTable outcome = Outcome(replay ? 3 : 1);
        using DataTable operation = Operation();
        using DataTable streams = Streams();
        if (emptyStreams) streams.Clear();
        using DataTable trailing = Outcome(8);
        // A SQL literal NULL has int metadata even in a nullable string or bigint slot.
        outcome.Clear();
        using DataTable literalNullOutcome = outcome.Clone();
        for (int column = 2; column < 6; column++) literalNullOutcome.Columns[column].DataType = typeof(int);
        literalNullOutcome.Rows.Add(replay ? 3 : 1, 0, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
        using var reader = new DataTableReader(replay ? [literalNullOutcome, operation, streams, trailing] : [literalNullOutcome, trailing]);
        var refused = await Assert.ThrowsAsync<JournalStoreStateException>(() =>
            SqlServerJournalBatchResultReader.ReadAsync(reader, operationId, CancellationToken.None));
        Assert.Equal(JournalStoreRefusal.BatchResultMalformed, refused.Reason);
        Assert.Null(refused.Outcome);
        using var control = new DataTableReader(replay ? [literalNullOutcome, operation, streams] : [literalNullOutcome]);
        var result = await SqlServerJournalBatchResultReader.ReadAsync(control, operationId, CancellationToken.None);
        Assert.Equal(replay ? SqlServerJournalWriteOutcome.Replayed : SqlServerJournalWriteOutcome.Applied, result.Outcome);
        if (replay)
        {
            JournalCommitReceipt receipt = Assert.IsType<JournalCommitReceipt>(result.Receipt);
            Assert.Equal(operationId, receipt.OperationId);
            Assert.Equal(new byte[] { 1 }, receipt.ResultData.ToArray());
            Assert.True(receipt.IsReplay);
            if (emptyStreams) Assert.Empty(receipt.Streams);
            else Assert.Equal("stream", Assert.Single(receipt.Streams).StreamKey);
        }
        else Assert.Null(result.Receipt);
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
