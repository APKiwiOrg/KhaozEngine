using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using KhaozEngine.WorldStore.SqlServer;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalTask6TestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

[Collection("SQL Server mutation journal")]
public sealed class MutationJournalFaultMatrixTests
{
    private static readonly JournalTestHookPhase[] CommitPhases =
    {
        JournalTestHookPhase.BeforeTransaction,
        JournalTestHookPhase.AfterOperationResolution,
        JournalTestHookPhase.AfterHeadValidation,
        JournalTestHookPhase.AfterEventWrites,
        JournalTestHookPhase.AfterProjectionWrites,
        JournalTestHookPhase.BeforeCommit,
        JournalTestHookPhase.AfterCommitBeforeResponse,
    };

    private static readonly JournalTestHookPhase[] CompactionPhases =
    {
        JournalTestHookPhase.SnapshotWrittenBeforeVerification,
        JournalTestHookPhase.SnapshotVerifiedBeforePrune,
    };

    [Fact]
    public async Task In_memory_all_nine_fault_phases_leave_one_durable_outcome()
    {
        foreach (JournalTestHookPhase phase in CommitPhases)
            await AssertCommitFaultAsync(CreateInMemoryCase(phase), phase);
        foreach (JournalTestHookPhase phase in CompactionPhases)
            await AssertCompactionFaultAsync(CreateInMemoryCase(phase), phase);
    }

    [Fact]
    public async Task Sqlite_reopen_after_all_nine_fault_phases_leaves_one_durable_outcome()
    {
        foreach (JournalTestHookPhase phase in CommitPhases)
            await AssertCommitFaultAsync(CreateSqliteCase(phase), phase);
        foreach (JournalTestHookPhase phase in CompactionPhases)
            await AssertCompactionFaultAsync(CreateSqliteCase(phase), phase);
    }

    [SqlServerFact]
    public async Task Sql_server_reopen_after_all_nine_fault_phases_leaves_one_durable_outcome()
    {
        foreach (JournalTestHookPhase phase in CommitPhases)
            await AssertCommitFaultAsync(CreateSqlServerCase(phase), phase);
        foreach (JournalTestHookPhase phase in CompactionPhases)
            await AssertCompactionFaultAsync(CreateSqlServerCase(phase), phase);
    }

    private static async Task AssertCommitFaultAsync(FaultCase faultCase, JournalTestHookPhase phase)
    {
        using (faultCase)
        {
            await faultCase.Writer.InitializeAsync(Initialization(1));
            JournalCommit commit = Commit(2, 0, new byte[] { 7 }, 41, Projection("bag", 9));
            faultCase.Arm();

            await Assert.ThrowsAnyAsync<Exception>(() => faultCase.Writer.CommitAsync(commit));

            faultCase.Disarm();
            faultCase.CloseWriter();
            IMutationJournalStore recovered = faultCase.Reopen();
            JournalOperationResolution resolution = await recovered.ResolveOperationAsync(commit.Identity);
            bool committedBeforeLoss = phase == JournalTestHookPhase.AfterCommitBeforeResponse;
            Assert.Equal(
                committedBeforeLoss ? JournalOperationResolutionStatus.Replayed : JournalOperationResolutionStatus.NotFound,
                resolution.Status);

            JournalEventPage recoveredPage = await ReadAllAsync(recovered);
            JournalProjectionRead recoveredProjection = await recovered.ReadProjectionsAsync(
                new JournalProjectionQuery(StreamKey));
            if (committedBeforeLoss)
            {
                Assert.Single(recoveredPage.Events);
                Assert.Single(recoveredProjection.Sections);
            }
            else
            {
                Assert.Empty(recoveredPage.Events);
                Assert.Empty(recoveredProjection.Sections);
                Assert.Equal(0, recoveredProjection.HeadVersion);
            }

            JournalCommitResult retried = await recovered.CommitAsync(commit);
            Assert.Equal(
                committedBeforeLoss ? JournalCommitStatus.Replayed : JournalCommitStatus.Applied,
                retried.Status);
            JournalCommitReceipt receipt = retried.Receipt!;
            JournalStreamVersionRange range = Assert.Single(receipt.Streams);
            Assert.Equal((0L, 1L, 1), (range.BeforeVersion, range.AfterVersion, range.EventCount));
            Assert.Equal(new byte[] { 41 }, receipt.ResultData.ToArray());
            Assert.True(receipt.HasValidResultChecksum);

            JournalEventPage page = await ReadAllAsync(recovered);
            JournalStoredEvent stored = Assert.Single(page.Events);
            Assert.Equal((1L, 7), (stored.StreamVersion, stored.Payload.Span[0]));
            JournalProjectionSection projection = Assert.Single(
                (await recovered.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).Sections);
            Assert.Equal(("bag", 1L, 9),
                (projection.SectionName, projection.SourceVersion, projection.Data.Span[0]));
        }
    }

    private static async Task AssertCompactionFaultAsync(FaultCase faultCase, JournalTestHookPhase phase)
    {
        using (faultCase)
        {
            await faultCase.Writer.InitializeAsync(Initialization(1, snapshotValue: 10));
            await faultCase.Writer.CommitAsync(Commit(2, 0, new byte[] { 1, 2 }, 2));
            faultCase.Arm();

            await Assert.ThrowsAnyAsync<Exception>(() => faultCase.Writer.CompactAsync(
                new JournalCompaction(StreamKey, 1, "player.v2", 2, new byte[] { 20 }, 1)));

            faultCase.Disarm();
            faultCase.CloseWriter();
            IMutationJournalStore recovered = faultCase.Reopen();
            JournalSnapshot prior = (await recovered.LoadSnapshotAsync(StreamKey))!;
            Assert.Equal((0L, 10), (prior.ThroughVersion, prior.Data.Span[0]));
            Assert.Equal(new long[] { 1, 2 }, (await ReadAllAsync(recovered)).Events.Select(value => value.StreamVersion));

            JournalCompactionResult retried = await recovered.CompactAsync(
                new JournalCompaction(StreamKey, 1, "player.v2", 2, new byte[] { 20 }, 1));
            Assert.Equal(JournalCompactionStatus.Compacted, retried.Status);
            Assert.Equal(1, retried.PrunedEventCount);
            JournalSnapshot current = (await recovered.LoadSnapshotAsync(StreamKey))!;
            Assert.Equal((1L, 20), (current.ThroughVersion, current.Data.Span[0]));
            Assert.Equal(2, Assert.Single((await ReadAllAsync(recovered, afterVersion: 1)).Events).StreamVersion);
            Assert.Equal(JournalOperationResolutionStatus.Replayed,
                (await recovered.ResolveOperationAsync(MutationJournalTask6TestSupport.Identity(2))).Status);
        }
    }

    private static FaultCase CreateInMemoryCase(JournalTestHookPhase target)
    {
        bool armed = false;
        var hook = new InMemoryJournalTestHook(phase =>
        {
            if (armed && phase == target) throw new Task6InjectedFailure();
        });
        var store = new InMemoryMutationJournalStore(
            JournalLimits.Maximum,
            TimeSpan.FromHours(24),
            TimeProvider.System,
            hook);
        return new FaultCase(store, () => store, () => armed = true, () => armed = false);
    }

    private static FaultCase CreateSqliteCase(JournalTestHookPhase target)
    {
        bool armed = false;
        var scope = new Task6SqliteScope();
        var hook = new SqliteJournalTestHook(phase =>
        {
            if (armed && phase == target) throw new Task6InjectedFailure();
        });
        SqliteMutationJournalStore writer = scope.Open(hook);
        return new FaultCase(
            writer,
            () => scope.Open(),
            () => armed = true,
            () => armed = false,
            writer.Dispose,
            scope);
    }

    private static FaultCase CreateSqlServerCase(JournalTestHookPhase target)
    {
        bool armed = false;
        var scope = new Task6SqlServerScope();
        var hook = new SqlServerJournalTestHook(phase =>
        {
            if (armed && phase == target) throw new Task6InjectedFailure();
        });
        SqlServerJournalPrefixStore writer = scope.Open(hook);
        return new FaultCase(
            writer,
            () => scope.Open(),
            () => armed = true,
            () => armed = false,
            scope: scope);
    }

    private sealed class FaultCase : IDisposable
    {
        private readonly Action arm;
        private readonly Action disarm;
        private readonly Action closeWriter;
        private readonly IDisposable? scope;
        private readonly Func<IMutationJournalStore> reopen;

        internal FaultCase(
            IMutationJournalStore writer,
            Func<IMutationJournalStore> reopen,
            Action arm,
            Action disarm,
            Action? closeWriter = null,
            IDisposable? scope = null)
        {
            Writer = writer;
            this.reopen = reopen;
            this.arm = arm;
            this.disarm = disarm;
            this.closeWriter = closeWriter ?? (() => { });
            this.scope = scope;
        }

        internal IMutationJournalStore Writer { get; }
        internal void Arm() => arm();
        internal void Disarm() => disarm();
        internal void CloseWriter() => closeWriter();
        internal IMutationJournalStore Reopen() => reopen();
        public void Dispose() => scope?.Dispose();
    }
}
