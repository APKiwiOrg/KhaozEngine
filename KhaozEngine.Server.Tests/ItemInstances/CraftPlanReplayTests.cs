using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.Tests.WorldStore.Journal;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerCommitFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

public sealed partial class CraftPlanReplayTests : IDisposable
{
    const int TargetSlot = 4;
    const int CurrencySlot = 5;
    readonly SqliteJournalTestDatabase database = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reusing_an_operation_id_for_another_authored_plan_conflicts(bool paid)
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        Guid operationId = Guid.NewGuid();
        CraftPlan originalPlan = Plan(7, paid);
        CraftPlan changedPlan = Plan(8, paid);
        JournalCommit first = Commit(originalPlan, operationId);
        Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(first)).Status);
        byte[] page = await StoredPageAsync(store);

        JournalCommit conflicting = Commit(changedPlan, operationId);
        Assert.Equal(JournalCommitStatus.OperationConflict, (await store.CommitAsync(conflicting)).Status);
        Assert.Equal(JournalOperationResolutionStatus.OperationConflict,
            (await store.ResolveOperationAsync(conflicting.Identity)).Status);
        Assert.Equal(page, await StoredPageAsync(store));
        JournalStoredEvent stored = Assert.Single(await StoredEventsAsync(store));
        Assert.Equal(operationId, stored.OperationId);
        Assert.Equal(1L, (await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).HeadVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_same_authored_plan_replays_without_using_resolved_outcome_bytes(bool paid)
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        Guid operationId = Guid.NewGuid();
        CraftPlan plan = Plan(7, paid);
        JournalCommit first = Commit(plan, operationId);
        JournalCommitResult applied = await store.CommitAsync(first);
        Assert.Equal(JournalCommitStatus.Applied, applied.Status);
        byte[] page = await StoredPageAsync(store);

        JournalCommit retry = Commit(plan, operationId, afterLevel: 99);
        JournalCommitResult replay = await store.CommitAsync(retry);

        Assert.Equal(JournalCommitStatus.Replayed, replay.Status);
        Assert.Equal(applied.Receipt!.CommittedAtUtc, replay.Receipt!.CommittedAtUtc);
        Assert.Equal(applied.Receipt.ResultData.ToArray(), replay.Receipt.ResultData.ToArray());
        Assert.Equal(first.Identity.NormalizedIntent.ToArray(), retry.Identity.NormalizedIntent.ToArray());
        Assert.Equal(page, await StoredPageAsync(store));
        Assert.Single(await StoredEventsAsync(store));
    }

    public void Dispose() => database.Dispose();

    static CraftPlan Plan(int planId, bool paid)
        => new(planId, paid ? Currency : 0, paid ? 1 : 0, [], []);

    static PagedItemContainer LoadedBank()
    {
        PagedItemContainer bank = Container(1);
        SeatItem(bank, TargetSlot, Sword, Instance, Payload());
        SeatStack(bank, CurrencySlot, Currency, 20);
        return bank;
    }

    static JournalCommit Commit(CraftPlan plan, Guid operationId, int afterLevel = 43)
    {
        ContainerCommitBuilder batch = OpenBank(LoadedBank());
        byte[] after = Payload(afterLevel);
        byte[] audit = ItemCraftedEvent.From(plan, Instance, 7, Payload(), after).ToArray();
        ContainerOperation craft = ContainerOperation.Craft(Bank, TargetSlot, Instance, after, audit,
            currencySlot: plan.IsFree ? 0 : CurrencySlot,
            currencyDefinitionId: plan.ConsumesDefinitionId,
            currencyCount: plan.ConsumesCount);
        Assert.True(batch.Apply(craft.FromClient(operationId)));
        return batch.Close(Mint(ServerId), new byte[] { 9 });
    }

    static byte[] EncodePage(PagedItemContainer bank)
    {
        ItemContainerPage page = bank.Pages[0];
        var entries = new PageSlotInput[ItemContainerPageCodec.ContainerPageSlots];
        int count = page.CopyEntriesTo(entries);
        return ItemContainerPageCodec.Encode(0, 0, page.SlotCount, page.ContentVersion,
            entries.AsSpan(0, count));
    }

    static async Task<byte[]> StoredPageAsync(IMutationJournalStore store)
        => (await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).Sections
            .Single(section => section.SectionName == "bank/p00").Data.ToArray();

    static async Task<IReadOnlyList<JournalStoredEvent>> StoredEventsAsync(IMutationJournalStore store)
        => (await store.ReadEventsAsync(new JournalEventRead(StreamKey, 0, null, 64, 64 * 1024))).Events;

    async Task<SqliteMutationJournalStore> NewStreamAsync(JournalLimits? limits = null)
    {
        string path = database.NewPath();
        SqliteMutationJournalStore store = database.Open(path,
            new SqliteMutationJournalStoreOptions(database.ConnectionString(path))
            {
                Limits = limits ?? JournalLimits.Maximum,
            });
        var page = new JournalProjectionWrite(StreamKey, "bank/p00",
            ContainerCommitOptions.DefaultProjectionSchema, ItemContainerPageCodec.Version,
            EncodePage(LoadedBank()));
        JournalInitializeResult initialized = await store.InitializeAsync(new JournalInitialization(
            new JournalOperationIdentity(Guid.NewGuid(), Scope, "items.initialize", new byte[] { 1 }),
            StreamKey, "player.snapshot.v1", 1, new byte[] { 1 }, [page],
            "initialize.result.v1", 1, Array.Empty<byte>()));
        Assert.Equal(JournalInitializeStatus.Initialized, initialized.Status);
        return store;
    }
}
