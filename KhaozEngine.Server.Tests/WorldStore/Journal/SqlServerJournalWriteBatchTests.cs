using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// Offline pins on the single T-SQL batch one journal operation sends. These need no SQL Server: they read the
/// generated statement text and its bound parameters, which is what decides the round-trip count, the lock order,
/// and the parameter budget.
/// </summary>
public sealed class SqlServerJournalWriteBatchTests
{
    [Fact]
    public void Commit_sends_one_batch_holding_every_statement_of_the_transaction()
    {
        using SqlCommand command = SqlServerMutationJournalStore.BuildCommitBatchForTest(
            Commit(
                new[] { Mutation("player/a", 4, 1, 2) },
                new[] { Projection("player/a", "bag", 7), Projection("player/a", "skills", 8) }),
            JournalLimits.Maximum);

        string[] ordered =
        {
            "sys.sp_getapplock",
            "FROM dbo.journal_operation WITH (UPDLOCK, HOLDLOCK)",
            "SELECT @head = current_version FROM dbo.journal_stream WITH (UPDLOCK, HOLDLOCK)",
            "FROM dbo.journal_projection WITH (UPDLOCK, HOLDLOCK)",
            "INSERT INTO dbo.journal_event(",
            "UPDATE dbo.journal_stream",
            "UPDATE dbo.journal_projection",
            "INSERT INTO dbo.journal_operation(",
            "INSERT INTO dbo.journal_operation_stream(",
            "SELECT @outcome, @lockResult, @limitStream;",
        };
        int previous = -1;
        foreach (string statement in ordered)
        {
            int position = command.CommandText.IndexOf(statement, StringComparison.Ordinal);
            Assert.True(position > previous, $"'{statement}' is missing or out of order.");
            previous = position;
        }
        Assert.Equal(1, Occurrences(command.CommandText, "sys.sp_getapplock"));
        Assert.Equal(1, Occurrences(command.CommandText, "INSERT INTO dbo.journal_event("));
        Assert.Equal(2, Occurrences(command.CommandText, "UPDATE dbo.journal_projection"));
    }

    [Fact]
    public void Commit_locks_stream_heads_in_ordinal_key_order_whatever_order_the_caller_used()
    {
        using SqlCommand command = SqlServerMutationJournalStore.BuildCommitBatchForTest(
            Commit(
                new[] { Mutation("player:2", 0, 1), Mutation("player/B", 0, 2), Mutation("player/a", 0, 3) },
                Array.Empty<JournalProjectionWrite>()),
            JournalLimits.Maximum);

        Assert.Equal(
            new[] { "player/B", "player/a", "player:2" },
            new[] { "@stream0", "@stream1", "@stream2" }.Select(name => command.Parameters[name].Value));
        Assert.Equal(
            new[] { "@stream0", "@stream1", "@stream2" },
            new[] { "@stream0", "@stream1", "@stream2" }
                .OrderBy(name => command.CommandText.IndexOf(
                    $"WITH (UPDLOCK, HOLDLOCK) WHERE stream_key = {name}",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public void Commit_batch_reports_every_outcome_the_caller_distinguishes()
    {
        using SqlCommand command = SqlServerMutationJournalStore.BuildCommitBatchForTest(
            Commit(
                new[] { Mutation("player/a", 0, 1) },
                new[] { Projection("player/a", "bag", 7) }),
            JournalLimits.Maximum);

        foreach (SqlServerJournalWriteOutcome outcome in new[]
        {
            SqlServerJournalWriteOutcome.VersionConflict,
            SqlServerJournalWriteOutcome.ProjectionLimit,
            SqlServerJournalWriteOutcome.ApplicationLock,
            SqlServerJournalWriteOutcome.StreamVanished,
        })
            Assert.Contains($"SET @outcome = {(int)outcome};", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            $"THEN {(int)SqlServerJournalWriteOutcome.Replayed} ELSE {(int)SqlServerJournalWriteOutcome.OperationConflict} END",
            command.CommandText,
            StringComparison.Ordinal);
        Assert.DoesNotContain($"SET @outcome = {(int)SqlServerJournalWriteOutcome.ExistingStream};", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Suppressed_operation_lookup_leaves_the_replay_read_out_of_the_batch()
    {
        using SqlCommand command = SqlServerMutationJournalStore.BuildCommitBatchForTest(
            Commit(new[] { Mutation("player/a", 0, 1) }, Array.Empty<JournalProjectionWrite>()),
            JournalLimits.Maximum,
            skipOperationLookup: true);

        Assert.DoesNotContain("SELECT @storedIntent = intent_fingerprint", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("SELECT @outcome, @lockResult, @limitStream;", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialization_sends_one_batch_that_creates_the_stream_and_its_snapshot()
    {
        using SqlCommand command = SqlServerMutationJournalStore.BuildInitializeBatchForTest(new JournalInitialization(
            Identity(9),
            "player/a",
            "player.v1",
            1,
            new byte[] { 1 },
            new[] { Projection("player/a", "bag", 2) },
            "result.v1",
            1,
            new byte[] { 3 }));

        Assert.Contains($"IF @head IS NOT NULL BEGIN SET @outcome = {(int)SqlServerJournalWriteOutcome.ExistingStream};", command.CommandText, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(command.CommandText, "INSERT INTO dbo.journal_stream("));
        Assert.Equal(1, Occurrences(command.CommandText, "INSERT INTO dbo.journal_snapshot("));
        Assert.DoesNotContain("INSERT INTO dbo.journal_event(", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain($"SET @outcome = {(int)SqlServerJournalWriteOutcome.VersionConflict};", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_largest_commit_the_limits_allow_stays_under_the_provider_parameter_ceiling()
    {
        JournalStreamMutation[] mutations = Enumerable.Range(0, JournalLimits.EngineMaximumStreamsPerOperation)
            .Select(stream => new JournalStreamMutation(
                $"player/{stream:D2}",
                0,
                Enumerable.Range(0, JournalLimits.EngineMaximumEventsPerOperation / JournalLimits.EngineMaximumStreamsPerOperation)
                    .Select(value => new JournalEvent("state.changed", 1, new[] { (byte)value }))
                    .ToArray()))
            .ToArray();
        JournalProjectionWrite[] projections = Enumerable.Range(0, JournalLimits.EngineMaximumProjectionWritesPerOperation)
            .Select(index => Projection($"player/{index % JournalLimits.EngineMaximumStreamsPerOperation:D2}", $"section{index:D2}", (byte)index))
            .ToArray();

        using SqlCommand command = SqlServerMutationJournalStore.BuildCommitBatchForTest(
            Commit(mutations, projections),
            JournalLimits.Maximum);

        Assert.Equal(1, Occurrences(command.CommandText, "INSERT INTO dbo.journal_event("));
        Assert.Equal(JournalLimits.EngineMaximumEventsPerOperation, Occurrences(command.CommandText, "@eventPayload"));
        Assert.InRange(command.Parameters.Count, 1, 2_100);
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static JournalOperationIdentity Identity(int suffix)
        => new(
            new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, checked((byte)suffix)),
            "world/account",
            "bank.deposit",
            new byte[] { checked((byte)suffix) });

    private static JournalStreamMutation Mutation(string streamKey, long expectedVersion, params byte[] events)
        => new(
            streamKey,
            expectedVersion,
            events.Select(value => new JournalEvent("state.changed", 1, new[] { value })).ToArray());

    private static JournalProjectionWrite Projection(string streamKey, string sectionName, byte value)
        => new(streamKey, sectionName, "section.v1", 1, new[] { value });

    private static JournalCommit Commit(
        IReadOnlyList<JournalStreamMutation> mutations,
        IReadOnlyList<JournalProjectionWrite> projections)
        => new(Identity(1), mutations, projections, "result.v1", 1, new byte[] { 5 });
}
