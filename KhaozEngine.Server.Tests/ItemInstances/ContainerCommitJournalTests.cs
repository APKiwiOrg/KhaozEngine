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

/// <summary>
/// Spec 6.5's identity split and 6.6's crash and replay cases, against a REAL SQLite journal store, which is
/// how the store conformance suite runs too. An in-memory double would agree with whatever this code believes
/// about replay, and replay is exactly the belief these facts are here to check.
/// <para>
/// Nothing here writes process-global state, so no test needs a collection attribute.
/// </para>
/// </summary>
public sealed class ContainerCommitJournalTests : IDisposable
{
    const int BankPages = 2;
    readonly SqliteJournalTestDatabase database = new();

    [Fact]
    public async Task A_client_headed_resubmit_omitting_the_server_work_still_resolves_Replayed()
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        Guid clientId = Guid.NewGuid();
        ContainerOperation click = ContainerOperation.Take(Bank, 3, 2).FromClient(clientId);

        PagedItemContainer first = LoadedBank();
        ContainerCommitBuilder batch = OpenBank(first);
        Assert.True(batch.Apply(click));
        Assert.True(batch.Apply(ContainerOperation.Grant(Bank, 5, Sword, 1, Instance + 1)));
        JournalCommitResult applied = await store.CommitAsync(batch.Close(Mint(ServerId), new byte[] { 3 }));

        // The reconnect: the client resubmits the click it remembers, and it never saw the quest advance,
        // the sweep or the achievement its click caused, so its batch carries the click ALONE.
        PagedItemContainer second = LoadedBank();
        ContainerCommitBuilder resubmit = OpenBank(second);
        Assert.True(resubmit.Apply(click));
        JournalCommitResult replay = await store.CommitAsync(resubmit.Close(Mint(ServerId), new byte[] { 3 }));

        Assert.Equal(JournalCommitStatus.Applied, applied.Status);
        Assert.Equal(JournalCommitStatus.Replayed, replay.Status);
        Assert.True(replay.Receipt!.IsReplay);
        Assert.Equal(applied.Receipt!.CommittedAtUtc, replay.Receipt.CommittedAtUtc);
        Assert.Equal(applied.Receipt.ResultData.ToArray(), replay.Receipt.ResultData.ToArray());
        Assert.Equal(
            applied.Receipt.Streams.Select(value => value.AfterVersion),
            replay.Receipt.Streams.Select(value => value.AfterVersion));
    }

    [Fact]
    public async Task A_client_resubmit_with_different_parameters_is_OperationConflict()
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        Guid clientId = Guid.NewGuid();

        ContainerCommitBuilder batch = OpenBank(LoadedBank());
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 2).FromClient(clientId)));
        Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(batch.Close(Mint(ServerId)))).Status);

        ContainerCommitBuilder changed = OpenBank(LoadedBank());
        Assert.True(changed.Apply(ContainerOperation.Take(Bank, 3, 3).FromClient(clientId)));
        JournalCommit conflicting = changed.Close(Mint(ServerId));

        Assert.Equal(JournalCommitStatus.OperationConflict, (await store.CommitAsync(conflicting)).Status);
        Assert.Equal(
            JournalOperationResolutionStatus.OperationConflict,
            (await store.ResolveOperationAsync(conflicting.Identity)).Status);
    }

    [Fact]
    public async Task Crash_BEFORE_admission_leaves_nothing_and_a_client_resubmit_resolves_NotFound()
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        byte[] before = await SectionBytesAsync(store, 0);

        PagedItemContainer bank = LoadedBank();
        ContainerCommitBuilder batch = OpenBank(bank);
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 2).FromClient(Guid.NewGuid())));
        JournalCommit never = batch.Close(Mint(ServerId));

        // The process dies here. Nothing was submitted, so the store never heard of it.
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(never.Identity)).Status);
        Assert.Equal(before, await SectionBytesAsync(store, 0));
        Assert.Equal(0, (await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).HeadVersion);
    }

    [Fact]
    public async Task Crash_AFTER_admission_before_commit_reverts_the_pages_and_SKIPS_the_allocated_ids()
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        byte[] before = await SectionBytesAsync(store, 0);
        var idStore = new RecordingInstanceIdStore();
        var allocator = new InstanceIdAllocator(idStore, liveStoreEpoch: 1);

        PagedItemContainer bank = LoadedBank();
        ContainerCommitBuilder batch = OpenBank(bank);
        var issued = new List<long>();
        for (int grant = 0; grant < 3; grant++)
        {
            long id = allocator.Next();
            issued.Add(id);
            Assert.True(batch.Apply(ContainerOperation.Grant(Bank, 20 + grant, Sword, 1, id, Payload(grant + 1))));
        }

        JournalCommit admitted = batch.Close(Mint(ServerId));
        Assert.Equal(3, admitted.StreamMutations[0].Events.Count);

        // The process dies between admission and the store call. The working copy dies with it.
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(admitted.Identity)).Status);
        Assert.Equal(before, await SectionBytesAsync(store, 0));

        // The allocator persisted its high-water mark BEFORE issuing, so the reboot resumes past every id the
        // dead batch put on an item: a crafted item that never committed leaves a GAP and nothing else.
        var rebooted = new InstanceIdAllocator(idStore, liveStoreEpoch: 1);
        long next = rebooted.Next();
        Assert.All(issued, id => Assert.True(next > id, $"{next} must be past the abandoned {id}."));
        Assert.DoesNotContain(next, issued);
    }

    [Fact]
    public async Task Crash_AFTER_commit_replays_to_the_ORIGINAL_receipt_and_result()
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        ContainerCommitBuilder batch = OpenBank(LoadedBank());
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 2).FromClient(Guid.NewGuid())));
        JournalCommit commit = batch.Close(Mint(ServerId), new byte[] { 7, 7 });

        JournalCommitResult first = await store.CommitAsync(commit);

        // The response never reached the client, so it resubmits the same operation id and intent.
        JournalCommitResult second = await store.CommitAsync(commit);

        Assert.Equal(JournalCommitStatus.Applied, first.Status);
        Assert.Equal(JournalCommitStatus.Replayed, second.Status);
        Assert.Equal(new byte[] { 7, 7 }, second.Receipt!.ResultData.ToArray());
        Assert.Equal(first.Receipt!.CommittedAtUtc, second.Receipt.CommittedAtUtc);
        Assert.Equal(1L, (await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).HeadVersion);
        Assert.Equal(
            commit.ProjectionWrites.Single(write => write.SectionName == "bank/p00").Data.ToArray(),
            await SectionBytesAsync(store, 0));
    }

    [Fact]
    public async Task A_terminal_store_failure_attaches_a_correction_whose_section_keys_ARE_the_pages_to_resync()
    {
        bool armed = false;
        var hook = new SqliteJournalTestHook(phase =>
        {
            if (armed && phase == JournalTestHookPhase.BeforeCommit)
            {
                throw MutationJournalExecutorTestSupport.StoreFailure(
                    JournalStoreFailureKind.CorruptData, JournalStoreFailureCertainty.CommittedDataUnreadable, StreamKey);
            }
        });
        SqliteMutationJournalStore store = await NewStreamAsync(hook);
        MutationJournalExecutor executor = MutationJournalExecutorTestSupport.CreateExecutor(store);
        JournalProjectionRead loaded = await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey));
        executor.SeedCommitted(StreamKey, loaded.HeadVersion, loaded.Sections);

        PagedItemContainer bank = LoadedBank();
        ContainerCommitBuilder batch = OpenBank(bank);
        Assert.True(batch.Apply(ContainerOperation.Move(Bank, 4, Bank, 150, 1, Instance)));
        JournalCommit commit = batch.Close(Mint(ServerId));
        armed = true;

        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(commit).Status);
        JournalCompletion completion = await MutationJournalExecutorTestSupport.TakeCompletionAsync(executor);
        armed = false;

        Assert.Equal(JournalCompletionKind.Fatal, completion.Kind);
        Assert.NotNull(completion.Correction);
        Assert.Equal(new[] { StreamKey }, completion.Correction!.StreamKeys);

        // Because a batch writes WHOLE pages, the sections the correction names are exactly the pages the
        // consumer must resync. The journal's existing shape is the page resync list, with no addition.
        Assert.Equal(
            commit.ProjectionWrites.Select(write => write.SectionName),
            completion.Correction.SectionsToResync.Select(section => section.SectionName));
        Assert.Equal(new[] { "bank/p00", "bank/p01" }, completion.Correction.SectionsToResync.Select(section => section.SectionName));

        // Close did not clear the dirty flags, so the pages the consumer resyncs are also the pages the next
        // ordinary commit would carry again. Nothing here calls MarkCommitted, because nothing committed.
        Assert.Equal(2, bank.DirtyPageCount);

        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        executor.ReleaseQuarantine(new[] { StreamKey });
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task A_transient_retry_corrects_nothing_and_the_batch_stays_admitted()
    {
        int failures = 0;
        bool armed = false;
        var hook = new SqliteJournalTestHook(phase =>
        {
            if (armed && phase == JournalTestHookPhase.BeforeCommit && failures == 0)
            {
                failures++;
                throw MutationJournalExecutorTestSupport.StoreFailure(
                    JournalStoreFailureKind.Timeout, JournalStoreFailureCertainty.DefinitelyNotCommitted, StreamKey);
            }
        });
        SqliteMutationJournalStore store = await NewStreamAsync(hook);
        MutationJournalExecutor executor = MutationJournalExecutorTestSupport.CreateExecutor(store);
        JournalProjectionRead loaded = await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey));
        executor.SeedCommitted(StreamKey, loaded.HeadVersion, loaded.Sections);

        ContainerCommitBuilder batch = OpenBank(LoadedBank());
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 2)));
        JournalCommit commit = batch.Close(Mint(ServerId));
        armed = true;

        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(commit).Status);
        JournalCompletion completion = await MutationJournalExecutorTestSupport.TakeCompletionAsync(executor);
        armed = false;

        Assert.Equal(1, failures);
        Assert.Equal(JournalCompletionKind.Committed, completion.Kind);
        Assert.Equal(JournalCommitStatus.Applied, completion.Result!.Status);
        Assert.Null(completion.Correction);
        Assert.Equal(0L, executor.Metrics.Corrections);
        Assert.True(executor.TryGetAdmittedProjection(StreamKey, "bank/p00", out JournalAdmittedSection? section));
        Assert.Equal(
            commit.ProjectionWrites.Single(write => write.SectionName == "bank/p00").Data.ToArray(),
            section!.Data.ToArray());

        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task Two_moves_of_one_stack_across_containers_leave_exactly_one_winner()
    {
        SqliteMutationJournalStore store = await NewStreamAsync();

        // Two handlers, two working copies, one stored page. Both build on the same admitted head.
        JournalCommit left = CrossContainerMove(destinationSlot: 0, Guid.NewGuid());
        JournalCommit right = CrossContainerMove(destinationSlot: 1, Guid.NewGuid());

        JournalCommitResult first = await store.CommitAsync(left);
        JournalCommitResult second = await store.CommitAsync(right);

        Assert.Equal(JournalCommitStatus.Applied, first.Status);
        Assert.Equal(JournalCommitStatus.VersionConflict, second.Status);
        Assert.Equal(
            left.ProjectionWrites.Single(write => write.SectionName == "bank/p00").Data.ToArray(),
            await SectionBytesAsync(store, 0));
        JournalProjectionRead read = await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey));
        Assert.Equal(1L, read.HeadVersion);

        // The stack landed where the WINNER put it and nowhere else: one stored page, one destination.
        Assert.Equal(
            left.ProjectionWrites.Single(write => write.SectionName == "bag/p00").Data.ToArray(),
            read.Sections.Single(section => section.SectionName == "bag/p00").Data.ToArray());
    }

    [Fact]
    public async Task Stored_instance_operations_replay_to_the_final_bag_and_ten_bank_pages()
    {
        SqliteMutationJournalStore store = await NewReplayStreamAsync();
        PagedItemContainer liveBank = ReplayBank(), liveBag = ReplayBag();
        byte[] before = RarityPayload(42), after = RarityPayload(43);

        ContainerCommitBuilder grant = ReplayBatch(liveBank, liveBag, expectedVersion: 0);
        Assert.True(grant.Apply(ContainerOperation.Grant(Bag, 0, Sword, 1, Instance, before)));
        Assert.True(grant.TryBuildParts(out IReadOnlyList<JournalEvent> grantEvents,
            out IReadOnlyList<JournalProjectionWrite> grantWrites));
        const string lootStream = "loot:9";
        var composed = new JournalCommit(
            new JournalOperationIdentity(Guid.NewGuid(), Scope, grant.ActionKind, grant.BuildIntent()),
            [
                new JournalStreamMutation(StreamKey, 0, grantEvents),
                new JournalStreamMutation(lootStream, 0, [new JournalEvent("loot-claimed", 1, new byte[] { 1 })]),
            ],
            grantWrites,
            grant.Options.ResultSchema,
            grant.Options.ResultSchemaVersion,
            Array.Empty<byte>());
        composed.Validate(JournalLimits.Maximum);
        Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(composed)).Status);
        grant.MarkCommitted();

        await CommitReplayOperation(store, liveBank, liveBag,
            ContainerOperation.Move(Bag, 0, Bank, 999, 1, Instance), expectedVersion: 1);
        byte[] audit = new ItemCraftedEvent(1, Instance, 1, before, after).ToArray();
        await CommitReplayOperation(store, liveBank, liveBag,
            ContainerOperation.Craft(Bank, 999, Instance, after, audit,
                currencyContainer: Bag, currencySlot: 1,
                currencyDefinitionId: Currency, currencyCount: 1), expectedVersion: 2);
        await CommitReplayOperation(store, liveBank, liveBag,
            ContainerOperation.Take(Bank, 1, 1), expectedVersion: 3);
        await CommitReplayOperation(store, liveBank, liveBag,
            ContainerOperation.Slide(Bank, 1, 0, 999), expectedVersion: 4);

        JournalEventPage page = await store.ReadEventsAsync(
            new JournalEventRead(StreamKey, 0, null, 64, 64 * 1024));
        Assert.Equal(5, page.Events.Count);
        Assert.Equal(new[] { 2, 1, 2, 1, 1 },
            page.Events.Select(value => value.EventSchemaVersion));
        Assert.Single((await store.ReadEventsAsync(
            new JournalEventRead(lootStream, 0, null, 64, 64 * 1024))).Events);

        PagedItemContainer replayBank = ReplayBank(), replayBag = ReplayBag();
        var copies = new Dictionary<string, IPagedContainerWorkingCopy>(StringComparer.Ordinal)
        {
            [Bank] = replayBank,
            [Bag] = replayBag,
        };
        foreach (JournalStoredEvent stored in page.Events)
        {
            Assert.True(ContainerOperationEventCodec.TryRead(stored.EventType,
                stored.EventSchemaVersion, stored.Payload, out ContainerOperation operation,
                out string? readReason), readReason);
            Assert.True(ContainerOperationApplier.TryReplay(copies, operation,
                out string? applyReason), applyReason);
        }

        Assert.Equal(1, replayBank.SlotAt(0).Stack.Count);
        Assert.Equal(Instance, replayBank.SlotAt(998).Stack.InstanceId);
        Assert.Equal(after, replayBank.SlotAt(998).Payload.ToArray());
        Assert.True(replayBank.SlotAt(999).IsEmpty);
        Assert.True(replayBag.SlotAt(0).IsEmpty);
        Assert.Equal(2, replayBag.SlotAt(1).Stack.Count);

        JournalProjectionRead final = await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey));
        Assert.Equal(5L, final.HeadVersion);
        for (int index = 0; index < 10; index++)
            Assert.Equal(final.Sections.Single(section =>
                    section.SectionName == ContainerSectionNames.Format(Bank, index)).Data.ToArray(),
                EncodePage(replayBank, index));
        Assert.Equal(final.Sections.Single(section => section.SectionName == "bag/p00").Data.ToArray(),
            EncodePage(replayBag, 0));
    }

    public void Dispose() => database.Dispose();

    static PagedItemContainer ReplayBank()
    {
        PagedItemContainer bank = Container(pageCount: 10, capacity: 1000);
        for (int slot = 1; slot < 999; slot++) SeatStack(bank, slot, Potion, slot + 1);
        return bank;
    }

    static PagedItemContainer ReplayBag()
    {
        PagedItemContainer bag = Container(pageCount: 1, capacity: 30);
        SeatStack(bag, 1, Currency, 3);
        return bag;
    }

    static byte[] RarityPayload(int itemLevel)
        => new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, (ulong)itemLevel)
            .AddByte(InstancePropertyKind.Rarity, 1)
            .ToArray();

    static ContainerCommitBuilder ReplayBatch(PagedItemContainer bank, PagedItemContainer bag,
        long expectedVersion)
        => ContainerCommitBuilder.Open(StreamKey, ItemInstanceEvents.CraftActionKind, Scope,
            Containers((Bank, bank), (Bag, bag)), tick: 4,
            new ContainerCommitOptions { ExpectedVersion = expectedVersion });

    static async Task CommitReplayOperation(SqliteMutationJournalStore store,
        PagedItemContainer bank, PagedItemContainer bag, ContainerOperation operation, long expectedVersion)
    {
        ContainerCommitBuilder batch = ReplayBatch(bank, bag, expectedVersion);
        Assert.True(batch.Apply(operation));
        Assert.Equal(JournalCommitStatus.Applied,
            (await store.CommitAsync(batch.Close(Mint(Guid.NewGuid())))).Status);
        batch.MarkCommitted();
    }

    async Task<SqliteMutationJournalStore> NewReplayStreamAsync()
    {
        SqliteMutationJournalStore store = database.Open(database.NewPath());
        PagedItemContainer bank = ReplayBank(), bag = ReplayBag();
        var projections = new List<JournalProjectionWrite>();
        for (int index = 0; index < bank.PageCount; index++)
            projections.Add(new JournalProjectionWrite(StreamKey,
                ContainerSectionNames.Format(Bank, index),
                ContainerCommitOptions.DefaultProjectionSchema,
                ItemContainerPageCodec.Version, EncodePage(bank, index)));
        projections.Add(new JournalProjectionWrite(StreamKey, "bag/p00",
            ContainerCommitOptions.DefaultProjectionSchema,
            ItemContainerPageCodec.Version, EncodePage(bag, 0)));
        Assert.Equal(JournalInitializeStatus.Initialized,
            (await store.InitializeAsync(new JournalInitialization(
                new JournalOperationIdentity(Guid.NewGuid(), Scope, "items.initialize", new byte[] { 1 }),
                StreamKey, "player.snapshot.v1", 1, new byte[] { 1 }, projections,
                "initialize.result.v1", 1, Array.Empty<byte>()))).Status);
        Assert.Equal(JournalInitializeStatus.Initialized,
            (await store.InitializeAsync(new JournalInitialization(
                new JournalOperationIdentity(Guid.NewGuid(), Scope, "loot.initialize", new byte[] { 1 }),
                "loot:9", "loot.snapshot.v1", 1, new byte[] { 1 },
                Array.Empty<JournalProjectionWrite>(), "initialize.result.v1", 1,
                Array.Empty<byte>()))).Status);
        return store;
    }

    JournalCommit CrossContainerMove(int destinationSlot, Guid operationId)
    {
        PagedItemContainer bank = LoadedBank();
        PagedItemContainer bag = Container(pageCount: 1, capacity: 30);
        ContainerCommitBuilder batch = ContainerCommitBuilder.Open(
            StreamKey, ItemInstanceEvents.CraftActionKind, Scope, Containers((Bank, bank), (Bag, bag)));
        Assert.True(batch.Apply(ContainerOperation.Move(Bank, 4, Bag, destinationSlot, 1, Instance)));
        return batch.Close(Mint(operationId));
    }

    /// <summary>The container as the stream's stored pages hold it, which every handler loads its own copy
    /// of.</summary>
    static PagedItemContainer LoadedBank()
    {
        PagedItemContainer bank = Container(BankPages);
        SeatStack(bank, 3, Potion, 20);
        SeatItem(bank, 4, Sword, Instance);
        return bank;
    }

    static byte[] EncodePage(PagedItemContainer container, int pageIndex)
    {
        ItemContainerPage page = container.Pages[pageIndex];
        var entries = new PageSlotInput[ItemContainerPageCodec.ContainerPageSlots];
        int count = page.CopyEntriesTo(entries);
        return ItemContainerPageCodec.Encode(
            page.PageIndex, page.FirstSlot, page.SlotCount, page.ContentVersion, entries.AsSpan(0, count));
    }

    static async Task<byte[]> SectionBytesAsync(IMutationJournalStore store, int pageIndex)
    {
        JournalProjectionRead read = await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey));
        return read.Sections.Single(section => section.SectionName == ContainerSectionNames.Format(Bank, pageIndex))
            .Data.ToArray();
    }

    async Task<SqliteMutationJournalStore> NewStreamAsync(SqliteJournalTestHook? hook = null)
    {
        SqliteMutationJournalStore store = database.Open(database.NewPath(), hook: hook);
        PagedItemContainer bank = LoadedBank();
        var projections = new List<JournalProjectionWrite>();
        for (int page = 0; page < BankPages; page++)
        {
            projections.Add(new JournalProjectionWrite(
                StreamKey,
                ContainerSectionNames.Format(Bank, page),
                ContainerCommitOptions.DefaultProjectionSchema,
                ItemContainerPageCodec.Version,
                EncodePage(bank, page)));
        }

        JournalInitializeResult result = await store.InitializeAsync(new JournalInitialization(
            new JournalOperationIdentity(Guid.NewGuid(), Scope, "items.initialize", new byte[] { 1 }),
            StreamKey,
            "player.snapshot.v1",
            1,
            new byte[] { 1 },
            projections,
            "initialize.result.v1",
            1,
            Array.Empty<byte>()));
        Assert.Equal(JournalInitializeStatus.Initialized, result.Status);
        return store;
    }

    /// <summary>The allocator's durable half, which is a CONSTRUCTOR seam rather than an ambient static, so a
    /// crash is expressible as a second allocator over the same persisted state.</summary>
    sealed class RecordingInstanceIdStore : IInstanceIdStore
    {
        InstanceIdState _state;

        public InstanceIdState Read() => _state;

        public void Persist(in InstanceIdState state) => _state = state;
    }
}
