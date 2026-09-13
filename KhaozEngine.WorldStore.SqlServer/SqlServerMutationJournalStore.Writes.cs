using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

public sealed partial class SqlServerMutationJournalStore
{
    public async Task<JournalInitializeResult> InitializeAsync(
        JournalInitialization initialization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        cancellationToken.ThrowIfCancellationRequested();
        initialization.Identity.Validate(limits);
        initialization.Validate(limits);
        JournalFingerprint intent = JournalCanonicalizer.CreateIntentFingerprint(initialization.Identity);
        JournalFingerprint execution = JournalCanonicalizer.CreateInitializationFingerprint(initialization);
        string[] streamKeys = { initialization.AbsentStreamKey };
        var tally = new SqlServerJournalCommandTally();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqlTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = UtcNow();
            SqlServerJournalWriteResult batch;
            using (SqlCommand command = CreateCommand(transaction, string.Empty))
            {
                SqlServerJournalWriteBatch.BuildInitialize(
                    command,
                    initialization,
                    intent,
                    execution,
                    now,
                    MaintenanceLockTimeoutMilliseconds,
                    SuppressOperationLookup());
                batch = await ExecuteWriteBatchAsync(
                    command,
                    initialization.Identity.OperationId,
                    tally,
                    cancellationToken).ConfigureAwait(false);
            }
            Invoke(JournalTestHookPhase.AfterOperationResolution);
            switch (batch.Outcome)
            {
                case SqlServerJournalWriteOutcome.Applied:
                    break;
                case SqlServerJournalWriteOutcome.Replayed:
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new JournalInitializeResult(JournalInitializeStatus.Replayed, batch.Receipt);
                case SqlServerJournalWriteOutcome.OperationConflict:
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new JournalInitializeResult(JournalInitializeStatus.OperationConflict);
                case SqlServerJournalWriteOutcome.ExistingStream:
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new JournalInitializeResult(JournalInitializeStatus.ExistingStream);
                default:
                    throw WriteBatchFailure(batch, streamKeys);
            }
            Invoke(JournalTestHookPhase.AfterHeadValidation);
            Invoke(JournalTestHookPhase.AfterEventWrites);
            Invoke(JournalTestHookPhase.AfterProjectionWrites);

            var range = new JournalStreamVersionRange(initialization.AbsentStreamKey, 0, 0, 0);
            var receipt = new JournalCommitReceipt(
                initialization.Identity.OperationId,
                now,
                new[] { range },
                initialization.ResultSchema,
                initialization.ResultSchemaVersion,
                initialization.ResultData.ToArray(),
                initialization.ResultChecksum.ToArray());
            Invoke(JournalTestHookPhase.BeforeCommit);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return new JournalInitializeResult(JournalInitializeStatus.Initialized, receipt);
        }
        catch (SqlException exception) when (!commitStarted && exception.Number is 2601 or 2627)
        {
            OperationLookup lookup = await ResolveOperationInsertCollisionAsync(
                exception,
                initialization.Identity,
                intent,
                transaction!,
                streamKeys,
                cancellationToken).ConfigureAwait(false);
            return lookup.Status == OperationLookupStatus.Conflict
                ? new JournalInitializeResult(JournalInitializeStatus.OperationConflict)
                : new JournalInitializeResult(JournalInitializeStatus.Replayed, lookup.Receipt);
        }
        catch (Exception exception)
        {
            await ThrowWriteFailureAsync(exception, transaction, streamKeys, commitStarted, committed).ConfigureAwait(false);
            throw;
        }
        finally
        {
            counters.Record(tally.Count);
            transaction?.Dispose();
        }
    }

    public async Task<JournalCommitResult> CommitAsync(JournalCommit commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        commit.Identity.Validate(limits);
        commit.Validate(limits);
        JournalFingerprint intent = JournalCanonicalizer.CreateIntentFingerprint(commit.Identity);
        JournalFingerprint execution = JournalCanonicalizer.CreateCommitFingerprint(commit);
        string[] streamKeys = commit.StreamMutations.Select(value => value.StreamKey).ToArray();
        var tally = new SqlServerJournalCommandTally();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqlTransaction? transaction = null;
        bool commitStarted = false;
        bool committed = false;
        try
        {
            transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = UtcNow();
            SqlServerJournalWriteResult batch;
            using (SqlCommand command = CreateCommand(transaction, string.Empty))
            {
                SqlServerJournalWriteBatch.BuildCommit(
                    command,
                    commit,
                    intent,
                    execution,
                    now,
                    limits,
                    MaintenanceLockTimeoutMilliseconds,
                    SuppressOperationLookup());
                batch = await ExecuteWriteBatchAsync(
                    command,
                    commit.Identity.OperationId,
                    tally,
                    cancellationToken).ConfigureAwait(false);
            }
            Invoke(JournalTestHookPhase.AfterOperationResolution);
            switch (batch.Outcome)
            {
                case SqlServerJournalWriteOutcome.Applied:
                    break;
                case SqlServerJournalWriteOutcome.Replayed:
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new JournalCommitResult(JournalCommitStatus.Replayed, batch.Receipt);
                case SqlServerJournalWriteOutcome.OperationConflict:
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new JournalCommitResult(JournalCommitStatus.OperationConflict);
                case SqlServerJournalWriteOutcome.VersionConflict:
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new JournalCommitResult(JournalCommitStatus.VersionConflict);
                default:
                    throw WriteBatchFailure(batch, streamKeys);
            }
            Invoke(JournalTestHookPhase.AfterHeadValidation);
            Invoke(JournalTestHookPhase.AfterEventWrites);
            Invoke(JournalTestHookPhase.AfterProjectionWrites);

            var receipt = new JournalCommitReceipt(
                commit.Identity.OperationId,
                now,
                CommittedRanges(commit),
                commit.ResultSchema,
                commit.ResultSchemaVersion,
                commit.ResultData.ToArray(),
                commit.ResultChecksum.ToArray());
            Invoke(JournalTestHookPhase.BeforeCommit);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return new JournalCommitResult(JournalCommitStatus.Applied, receipt);
        }
        catch (SqlException exception) when (!commitStarted && exception.Number is 2601 or 2627)
        {
            OperationLookup lookup = await ResolveOperationInsertCollisionAsync(
                exception,
                commit.Identity,
                intent,
                transaction!,
                streamKeys,
                cancellationToken).ConfigureAwait(false);
            return lookup.Status == OperationLookupStatus.Conflict
                ? new JournalCommitResult(JournalCommitStatus.OperationConflict)
                : new JournalCommitResult(JournalCommitStatus.Replayed, lookup.Receipt);
        }
        catch (Exception exception)
        {
            await ThrowWriteFailureAsync(exception, transaction, streamKeys, commitStarted, committed).ConfigureAwait(false);
            throw;
        }
        finally
        {
            counters.Record(tally.Count);
            transaction?.Dispose();
        }
    }

    private static IReadOnlyList<JournalStreamVersionRange> CommittedRanges(JournalCommit commit)
    {
        var ranges = new List<JournalStreamVersionRange>(commit.StreamMutations.Count);
        foreach (JournalStreamMutation mutation in commit.StreamMutations)
            ranges.Add(new JournalStreamVersionRange(
                mutation.StreamKey,
                mutation.ExpectedVersion,
                checked(mutation.ExpectedVersion + mutation.Events.Count),
                mutation.Events.Count));
        return ranges;
    }
}
