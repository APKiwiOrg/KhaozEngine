using System;
using System.Threading.Tasks;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerCommitFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

public sealed partial class CraftPlanReplayTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task A_pre_upgrade_retry_requires_retained_evidence_of_the_same_authored_plan(
        int eventSchema, bool paid)
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        Guid operationId = Guid.NewGuid();
        JournalCommit legacy = LegacyCommit(7, paid, operationId, eventSchema);
        JournalCommitResult applied = await store.CommitAsync(legacy);
        Assert.Equal(JournalCommitStatus.Applied, applied.Status);
        byte[] page = await StoredPageAsync(store);
        JournalOperationIdentity request = Commit(Plan(7, paid), operationId, afterLevel: 99).Identity;

        Assert.Equal(JournalOperationResolutionStatus.OperationConflict,
            (await store.ResolveOperationAsync(request)).Status);
        ContainerCraftReplayResult replay = await ContainerCraftReplay.ResolveLegacyAsync(
            store, StreamKey, request);

        Assert.Equal(ContainerCraftReplayStatus.Replayed, replay.Status);
        Assert.Equal(applied.Receipt!.CommittedAtUtc, replay.Receipt!.CommittedAtUtc);
        Assert.Equal(applied.Receipt.ResultData.ToArray(), replay.Receipt.ResultData.ToArray());
        Assert.True(replay.Receipt.IsReplay);
        Assert.Equal(page, await StoredPageAsync(store));
        Assert.Single(await StoredEventsAsync(store));
        Assert.Equal(JournalOperationResolutionStatus.Replayed,
            (await store.ResolveOperationAsync(legacy.Identity)).Status);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task A_changed_plan_conflicts_even_when_its_legacy_fingerprint_matches(
        int eventSchema, bool paid)
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        Guid operationId = Guid.NewGuid();
        JournalCommit legacy = LegacyCommit(7, paid, operationId, eventSchema);
        Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(legacy)).Status);
        byte[] page = await StoredPageAsync(store);
        JournalOperationIdentity request = Commit(Plan(8, paid), operationId).Identity;

        ContainerCraftReplayResult conflict = await ContainerCraftReplay.ResolveLegacyAsync(
            store, StreamKey, request);

        Assert.Equal(ContainerCraftReplayStatus.OperationConflict, conflict.Status);
        Assert.Null(conflict.Receipt);
        Assert.Equal(page, await StoredPageAsync(store));
        Assert.Single(await StoredEventsAsync(store));
    }

    [Fact]
    public async Task A_retained_legacy_receipt_without_compacted_plan_evidence_fails_closed()
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        Guid operationId = Guid.NewGuid();
        JournalCommit legacy = LegacyCommit(7, false, operationId, 2);
        Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(legacy)).Status);
        byte[] page = await StoredPageAsync(store);
        JournalCompactionResult compacted = await store.CompactAsync(new JournalCompaction(
            StreamKey, 1, "player.snapshot.v1", 1, page, pruneThroughVersion: 1));
        Assert.Equal(JournalCompactionStatus.Compacted, compacted.Status);
        Assert.Equal(1, compacted.PrunedEventCount);
        Assert.Equal(JournalOperationResolutionStatus.Replayed,
            (await store.ResolveOperationAsync(legacy.Identity)).Status);

        ContainerCraftReplayResult unavailable = await ContainerCraftReplay.ResolveLegacyAsync(
            store, StreamKey, Commit(Plan(7, false), operationId).Identity);

        Assert.Equal(ContainerCraftReplayStatus.EvidenceUnavailable, unavailable.Status);
        Assert.Null(unavailable.Receipt);
        Assert.Equal(page, await StoredPageAsync(store));
        Assert.Equal(1L, (await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).HeadVersion);
    }

    [Fact]
    public async Task A_missing_legacy_operation_returns_not_found_without_writing()
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        ContainerCraftReplayResult missing = await ContainerCraftReplay.ResolveLegacyAsync(
            store, StreamKey, Commit(Plan(7, false), Guid.NewGuid()).Identity);

        Assert.Equal(ContainerCraftReplayStatus.NotFound, missing.Status);
        Assert.Null(missing.Receipt);
        Assert.Empty(await StoredEventsAsync(store));
    }

    static JournalCommit LegacyCommit(int planId, bool paid, Guid operationId, int eventSchema)
    {
        JournalCommit request = Commit(Plan(planId, paid), operationId);
        JournalEvent envelope = request.StreamMutations[0].Events[0];
        Assert.True(ContainerOperationEventCodec.TryRead(envelope.EventType, envelope.EventSchemaVersion,
            envelope.Payload, out ContainerOperation craft, out string? reason), reason);
        byte[] body = eventSchema == 1 ? craft.EventPayload.ToArray() : envelope.Payload.ToArray();
        var historical = new JournalStreamMutation(StreamKey, 0,
            [new JournalEvent(ItemInstanceEvents.Crafted, eventSchema, body)]);
        return new JournalCommit(new JournalOperationIdentity(operationId, Scope,
                ItemInstanceEvents.CraftActionKind, craft.ToCanonicalArray()),
            [historical], request.ProjectionWrites, request.ResultSchema, request.ResultSchemaVersion,
            request.ResultData.ToArray());
    }
}
