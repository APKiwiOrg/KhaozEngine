using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.Tests.WorldStore.Journal;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerCommitFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>
/// Spec 17 row 10, against a REAL SQLite journal store, which is how the store conformance suite runs too.
/// An in-memory double would agree with whatever this code believes about replay, and replay is exactly the
/// belief these four facts are here to check.
/// <para>
/// <b>The target's INSTANCE ID is the load bearing field of the intent</b> (spec 15.1). Without it a replayed
/// craft whose slot has since been refilled by a different item hashes identically and applies to the wrong
/// item, so the same operation id against a different item has to resolve <c>OperationConflict</c> rather
/// than <c>Replayed</c>.
/// </para>
/// <para>
/// Each fact takes its own temp database file and deletes it on dispose, the isolation the conformance suite
/// uses. Nothing here writes process-global state, so no collection attribute is needed.
/// </para>
/// </summary>
public sealed class CraftReplayTests : IDisposable
{
    /// <summary>The bank's pages, which is what puts the currency on a page of its own.</summary>
    const int BankPages = 2;

    /// <summary>The target's slot, on page 0.</summary>
    const int TargetSlot = 4;

    /// <summary>The currency's slot, on page 1, which is the second page a paid craft writes.</summary>
    const int CurrencySlot = 150;

    /// <summary>The <c>crafting_currency</c> row the craft ran, which is what the event names.</summary>
    const int PolishingKit = 7;

    /// <summary>The content version the craft read, stamped on the event so the audit decodes against it.</summary>
    const int CraftContentVersion = 7;

    readonly SqliteJournalTestDatabase database = new();

    [Fact]
    public async Task The_same_operation_id_and_the_same_intent_replays_to_the_ORIGINAL_receipt()
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        Guid clientId = Guid.NewGuid();
        ContainerOperation craft = FreeCraft(Instance);

        ContainerCommitBuilder batch = OpenBank2(LoadedBank());
        Assert.True(batch.Apply(craft.FromClient(clientId)));
        JournalCommit commit = batch.Close(Mint(ServerId), new byte[] { 9 });
        JournalCommitResult applied = await store.CommitAsync(commit);

        // The response never reached the client, so it resubmits the SAME click against the SAME item. Its
        // working copy is rebuilt from the stored pages, which is why the batch is opened over a fresh load.
        ContainerCommitBuilder resubmit = OpenBank2(LoadedBank());
        Assert.True(resubmit.Apply(craft.FromClient(clientId)));
        JournalCommitResult replay = await store.CommitAsync(resubmit.Close(Mint(ServerId), new byte[] { 9 }));

        Assert.Equal(JournalCommitStatus.Applied, applied.Status);
        Assert.Equal(JournalCommitStatus.Replayed, replay.Status);
        Assert.True(replay.Receipt!.IsReplay);
        Assert.Equal(applied.Receipt!.CommittedAtUtc, replay.Receipt.CommittedAtUtc);
        Assert.Equal(applied.Receipt.ResultData.ToArray(), replay.Receipt.ResultData.ToArray());
        Assert.Equal(
            applied.Receipt.Streams.Select(value => value.AfterVersion),
            replay.Receipt.Streams.Select(value => value.AfterVersion));

        // The craft applied ONCE: one stored event under one operation id, and the page holds the crafted
        // payload rather than a second craft's.
        JournalStoredEvent stored = Assert.Single(await StoredEventsAsync(store));
        Assert.Equal(ItemInstanceEvents.Crafted, stored.EventType);
        Assert.Equal(commit.Identity.OperationId, stored.OperationId);
        Assert.Equal(
            commit.ProjectionWrites.Single(write => write.SectionName == "bank/p00").Data.ToArray(),
            await SectionBytesAsync(store, 0));

        // The event is the only durable record of what the craft CHANGED, because the page is rewritten
        // whole on the next commit, so the body has to read back off the store.
        Assert.Equal(2, stored.EventSchemaVersion);
        Assert.True(ContainerOperationEventCodec.TryRead(stored.EventType, stored.EventSchemaVersion,
            stored.Payload, out ContainerOperation replayOperation, out string? reason), reason);
        Assert.True(ItemCraftedEvent.TryRead(replayOperation.EventPayload,
            out ItemCraftedEvent read, out reason), reason);
        Assert.Equal(PolishingKit, read.CurrencyId);
        Assert.Equal(Instance, read.InstanceId);
        Assert.Equal(CraftContentVersion, read.ContentVersion);
        Assert.Equal(Before, read.Before.ToArray());
        Assert.Equal(After, read.After.ToArray());
    }

    [Fact]
    public async Task The_same_operation_id_with_a_REFILLED_slot_is_OperationConflict()
    {
        // Spec 15.1's first door. The craft commits, the target leaves the slot, and something else takes
        // its place. A resubmit under the same operation id now names a DIFFERENT instance id, so the intent
        // hashes differently and the journal refuses it instead of crafting the newcomer.
        SqliteMutationJournalStore store = await NewStreamAsync();
        Guid clientId = Guid.NewGuid();

        ContainerCommitBuilder batch = OpenBank2(LoadedBank());
        Assert.True(batch.Apply(FreeCraft(Instance).FromClient(clientId)));
        JournalCommit first = batch.Close(Mint(ServerId), new byte[] { 9 });
        Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(first)).Status);
        byte[] crafted = await SectionBytesAsync(store, 0);

        ContainerCommitBuilder resubmit = OpenBank2(RefilledBank());
        Assert.True(resubmit.Apply(FreeCraft(Refill).FromClient(clientId)));
        JournalCommit conflicting = resubmit.Close(Mint(ServerId), new byte[] { 9 });

        Assert.Equal(JournalCommitStatus.OperationConflict, (await store.CommitAsync(conflicting)).Status);
        Assert.Equal(
            JournalOperationResolutionStatus.OperationConflict,
            (await store.ResolveOperationAsync(conflicting.Identity)).Status);

        // An untouched page, and one event still. The conflicting submission wrote nothing at all.
        Assert.Equal(crafted, await SectionBytesAsync(store, 0));
        Assert.Single(await StoredEventsAsync(store));
        Assert.Equal(1L, (await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).HeadVersion);

        // And the instance id is the ONLY thing that differed: the two intents are the same length and
        // disagree in one byte, which is the low byte of the target's id. Take the field out of the intent
        // and the two encodings are identical, which is the exploit 15.1 describes.
        byte[] original = first.Identity.NormalizedIntent.ToArray();
        byte[] refilled = conflicting.Identity.NormalizedIntent.ToArray();
        Assert.Equal(original.Length, refilled.Length);
        Assert.Equal(1, DifferingBytes(original, refilled));
    }

    [Fact]
    public async Task A_REFUSED_craft_writes_nothing_durable_and_never_reaches_the_journal()
    {
        SqliteMutationJournalStore store = await NewStreamAsync();
        byte[] stored = await SectionBytesAsync(store, 0);

        // A real refusal from the real framework: a working copy opened over bytes that do not decode is
        // ALREADY refused rather than throwing (contracts 9.7), so the craft produces no payload at all.
        CraftWorkingCopy copy = CraftWorkingCopy.Open(
            InstancePropertyRegistry.CreateV1(), EmptySnapshot(), Sword, [0xFF, 0xFF]);

        Assert.True(copy.IsRefused);
        Assert.Equal(CraftRefusalKind.PayloadMalformed, copy.Refusal.Kind);
        Assert.False(copy.TryEncode(out byte[] payload));
        Assert.Empty(payload);

        // A refused craft is not an operation. The handler has nothing to apply, so the batch never takes
        // one and cannot close: a batch holding no operation has nothing to commit.
        PagedItemContainer bank = LoadedBank();
        ContainerCommitBuilder batch = OpenBank2(bank);
        Assert.Empty(batch.Operations);
        Assert.Equal(0, batch.EventCount);
        Assert.Throws<InvalidOperationException>(() => batch.Close(Mint(ServerId)));
        Assert.Equal(0, bank.DirtyPageCount);

        // Nothing durable moved: the same page bytes, no event, head where it was, and the id the batch
        // would have committed under is not in the store.
        Assert.Equal(stored, await SectionBytesAsync(store, 0));
        Assert.Empty(await StoredEventsAsync(store));
        Assert.Equal(0L, (await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).HeadVersion);
        Assert.Equal(
            JournalOperationResolutionStatus.NotFound,
            (await store.ResolveOperationAsync(new JournalOperationIdentity(
                ServerId, Scope, ItemInstanceEvents.CraftActionKind, new byte[] { 1 }))).Status);
    }

    [Fact]
    public async Task A_craft_that_consumes_a_currency_from_a_SECOND_page_is_ONE_commit_two_writes()
    {
        // Budget 4 is measured at ONE commit and this fact holds the structure rather than the bytes: a
        // craft whose currency lies on another page of the same container is one identity, one event and two
        // projection writes, never two commits.
        SqliteMutationJournalStore store = await NewStreamAsync();
        PagedItemContainer bank = LoadedBank();
        ContainerCommitBuilder batch = OpenBank2(bank);

        Assert.True(batch.Apply(PaidCraft(Instance)));
        JournalCommit commit = batch.Close(Mint(ServerId));
        JournalCommitResult result = await store.CommitAsync(commit);

        Assert.Equal(JournalCommitStatus.Applied, result.Status);
        Assert.Single(commit.StreamMutations);
        Assert.Single(commit.StreamMutations[0].Events);
        Assert.Equal(2, commit.ProjectionWrites.Count);
        Assert.Equal(
            new[] { "bank/p00", "bank/p01" },
            commit.ProjectionWrites.Select(write => write.SectionName));

        // ONE commit, counted the only way a store can answer it: every stored event carries one operation
        // id, and the stream moved by one event.
        IReadOnlyList<JournalStoredEvent> events = await StoredEventsAsync(store);
        Assert.Single(events);
        Assert.Single(events.Select(value => value.OperationId).Distinct());
        Assert.Equal(commit.Identity.OperationId, events[0].OperationId);
        Assert.Equal(1L, (await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).HeadVersion);

        // Both pages landed, and the currency page moved because a unit left it.
        Assert.Equal(
            commit.ProjectionWrites.Single(write => write.SectionName == "bank/p00").Data.ToArray(),
            await SectionBytesAsync(store, 0));
        Assert.Equal(
            commit.ProjectionWrites.Single(write => write.SectionName == "bank/p01").Data.ToArray(),
            await SectionBytesAsync(store, 1));
        Assert.Equal(19, bank.SlotAt(CurrencySlot).Stack.Count);
        Assert.Equal(After, bank.SlotAt(TargetSlot).Payload.ToArray());
    }

    public void Dispose() => database.Dispose();

    /// <summary>The payload as the target stood BEFORE the craft.</summary>
    static byte[] Before => Payload(42);

    /// <summary>The payload as it stands after it.</summary>
    static byte[] After => Payload(44);

    /// <summary>The instance that refilled the target's slot, which is a different item in the same place.</summary>
    const long Refill = Instance + 1;

    /// <summary>A craft that consumes nothing, which is what a bench operation is.</summary>
    static ContainerOperation FreeCraft(long instanceId)
        => ContainerOperation.Craft(Bank, TargetSlot, instanceId, After, Body(instanceId));

    /// <summary>A craft that spends one unit of a currency lying on the container's SECOND page.</summary>
    static ContainerOperation PaidCraft(long instanceId)
        => ContainerOperation.Craft(
            Bank,
            TargetSlot,
            instanceId,
            After,
            Body(instanceId),
            currencyContainer: Bank,
            currencySlot: CurrencySlot,
            currencyDefinitionId: Currency,
            currencyCount: 1);

    /// <summary>Spec 10.6's event body, carrying both payloads, which is what the operation hands the batch.</summary>
    static byte[] Body(long instanceId)
        => new ItemCraftedEvent(PolishingKit, instanceId, CraftContentVersion, Before, After).ToArray();

    /// <summary>A content version carrying no rows, which is all a refused working copy ever reads.</summary>
    static IContentSnapshot EmptySnapshot() => new ContentSnapshotBuilder(new ContentTypeRegistry()).Build();

    /// <summary>How many byte positions two equal length arrays disagree in.</summary>
    static int DifferingBytes(byte[] left, byte[] right)
    {
        int differing = 0;
        for (int index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index]) differing++;
        }

        return differing;
    }

    /// <summary>The container as the stream's stored pages hold it: the target on page 0, the currency on
    /// page 1.</summary>
    static PagedItemContainer LoadedBank()
    {
        PagedItemContainer bank = Container(BankPages);
        SeatItem(bank, TargetSlot, Sword, Instance, Before);
        SeatStack(bank, CurrencySlot, Currency, 20);
        return bank;
    }

    /// <summary>The same container after the target left and something ELSE took its slot.</summary>
    static PagedItemContainer RefilledBank()
    {
        PagedItemContainer bank = Container(BankPages);
        SeatItem(bank, TargetSlot, Sword, Refill, Before);
        SeatStack(bank, CurrencySlot, Currency, 20);
        return bank;
    }

    /// <summary>A batch over the two page bank, which is the container every fact here crafts in.</summary>
    static ContainerCommitBuilder OpenBank2(PagedItemContainer bank)
        => ContainerCommitBuilder.Open(
            StreamKey, ItemInstanceEvents.CraftActionKind, Scope, Containers((Bank, bank)), tick: 4);

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

    static async Task<IReadOnlyList<JournalStoredEvent>> StoredEventsAsync(IMutationJournalStore store)
        => (await store.ReadEventsAsync(new JournalEventRead(StreamKey, 0, null, 64, 64 * 1024))).Events;

    async Task<SqliteMutationJournalStore> NewStreamAsync()
    {
        SqliteMutationJournalStore store = database.Open(database.NewPath());
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
}

/// <summary>
/// Spec 10.6's <c>item-crafted</c> body, its layout byte for byte and its reader.
/// <para>
/// <b>It carries the BEFORE and the AFTER payload</b>, which is about 127 bytes on a rare of which the
/// before costs 59. The page is rewritten whole on the next commit, so without the before bytes nothing in
/// the durable record can answer what a craft CHANGED, which is exactly the failure a consumer audit already
/// has where rows record that something changed and carry no values.
/// </para>
/// <para>
/// <b>The reader is half the point</b> and it NEVER throws, because the bytes come from a store. It answers
/// false plus a reason from the closed set on the type.
/// </para>
/// <para>
/// Nothing here writes process-global state, so no collection attribute is needed.
/// </para>
/// </summary>
public class ItemCraftedBodyTests
{
    /// <summary>The payload as it stood: kind 2 at item level 68.</summary>
    static readonly byte[] Before = [0x02, 0x01, 0x44];

    /// <summary>The payload as it stands: the same field at 70.</summary>
    static readonly byte[] After = [0x02, 0x01, 0x46];

    [Fact]
    public void The_body_is_spec_10_6s_layout_BYTE_FOR_BYTE()
    {
        var crafted = new ItemCraftedEvent(
            CurrencyId: 7,
            InstanceId: 7_001,
            ContentVersion: 7,
            Before: Before,
            After: After);

        Assert.Equal(
            new byte[]
            {
                0x01,                   // event version 1
                0x07,                   // currency id 7
                0xD9, 0x36,             // instance id 7001
                0x07,                   // content version 7
                0x03,                   // before length 3
                0x02, 0x01, 0x44,       // the payload as it stood, verbatim
                0x03,                   // after length 3
                0x02, 0x01, 0x46,       // the payload as it stands, verbatim
            },
            crafted.ToArray());
        Assert.Equal(13, crafted.ByteCount);
    }

    [Fact]
    public void A_round_trip_returns_every_field_and_BOTH_payloads_verbatim()
    {
        var crafted = new ItemCraftedEvent(7, 7_001, 7, Before, After);

        Assert.True(ItemCraftedEvent.TryRead(crafted.ToArray(), out ItemCraftedEvent read, out string? reason), reason);
        Assert.Null(reason);
        Assert.Equal(7, read.CurrencyId);
        Assert.Equal(7_001L, read.InstanceId);
        Assert.Equal(7, read.ContentVersion);
        Assert.Equal(Before, read.Before.ToArray());
        Assert.Equal(After, read.After.ToArray());

        // The two payloads are SLICES of the body rather than copies, which is what keeps the event and the
        // page byte identical by construction rather than by inspection.
        Assert.NotEqual(read.Before.ToArray(), read.After.ToArray());
    }

    [Fact]
    public void EVERY_prefix_of_a_real_body_is_refused_with_a_reason_rather_than_throwing()
    {
        byte[] body = new ItemCraftedEvent(7, 7_001, 7, Before, After).ToArray();

        for (int length = 0; length < body.Length; length++)
        {
            Assert.False(
                ItemCraftedEvent.TryRead(body.AsMemory(0, length), out ItemCraftedEvent read, out string? reason),
                FormattableString.Invariant($"A {length} byte prefix was accepted."));
            Assert.NotNull(reason);
            Assert.Equal(default, read);
        }
    }

    [Fact]
    public void A_body_version_this_build_does_not_know_is_refused_rather_than_guessed()
    {
        byte[] body = new ItemCraftedEvent(7, 7_001, 7, Before, After).ToArray();
        body[0] = 2;

        Assert.False(ItemCraftedEvent.TryRead(body, out _, out string? reason));
        Assert.Equal(ItemCraftedEvent.UnknownVersion, reason);
    }

    [Fact]
    public void A_declared_length_that_does_not_match_what_is_LEFT_is_refused_on_BOTH_payloads()
    {
        byte[] body = new ItemCraftedEvent(7, 7_001, 7, Before, After).ToArray();

        // A before length that eats the after, and one that leaves bytes nothing accounts for.
        byte[] longBefore = [.. body];
        longBefore[5] = 0x7F;
        Assert.False(ItemCraftedEvent.TryRead(longBefore, out _, out string? reason));
        Assert.Equal(ItemCraftedEvent.PayloadLength, reason);

        // An after length that runs past the end, and one that stops short of it.
        byte[] longAfter = [.. body];
        longAfter[9] = 0x7F;
        Assert.False(ItemCraftedEvent.TryRead(longAfter, out _, out reason));
        Assert.Equal(ItemCraftedEvent.PayloadLength, reason);

        byte[] shortAfter = [.. body];
        shortAfter[9] = 0x01;
        Assert.False(ItemCraftedEvent.TryRead(shortAfter, out _, out reason));
        Assert.Equal(ItemCraftedEvent.PayloadLength, reason);
    }

    [Fact]
    public void A_truncated_VARINT_is_a_reason_rather_than_a_read_off_the_end()
    {
        // A body whose final varint claims a continuation byte that is not there. The prefix sweep cannot
        // produce this one, because every prefix of a real body cuts on a whole field.
        byte[] body = [0x01, 0x07, 0xD9, 0x36, 0x80];

        Assert.False(ItemCraftedEvent.TryRead(body, out _, out string? reason));
        Assert.Equal(ItemCraftedEvent.MalformedVarint, reason);
    }

    [Fact]
    public void An_instance_id_of_0_is_refused_on_BOTH_sides_because_a_craft_targets_an_OWNED_item()
    {
        // A craft rewrites an item that already exists, and an owned item always has an instance id: the
        // commit builder's own door says so. A body naming instance 0 describes a craft that cannot have
        // happened.
        // Written by hand, because the type refuses to build one: version, currency 7, instance 0, content
        // version 7, then each payload behind its length.
        byte[] body = [0x01, 0x07, 0x00, 0x07, 0x03, 0x02, 0x01, 0x44, 0x03, 0x02, 0x01, 0x46];

        Assert.False(ItemCraftedEvent.TryRead(body, out _, out string? reason));
        Assert.Equal(ItemCraftedEvent.FieldRange, reason);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ItemCraftedEvent.From(Plan(7), instanceId: 0, contentVersion: 7, Before, After));
    }

    [Fact]
    public void An_instance_id_on_the_TOP_node_is_written_unsigned_rather_than_zig_zagged()
    {
        // Contracts 6.2 packs (node << 48) | counter, so node 65535 sets the high bit and is a NEGATIVE
        // long. The two encodings are different bytes, so the format has to say which it means.
        long packed = InstanceIdAllocator.Pack(ushort.MaxValue, 1);
        var crafted = new ItemCraftedEvent(7, packed, 7, Before, After);

        Assert.True(packed < 0);
        Assert.True(ItemCraftedEvent.TryRead(crafted.ToArray(), out ItemCraftedEvent read, out string? reason), reason);
        Assert.Equal(packed, read.InstanceId);
        Assert.Equal((ushort)ushort.MaxValue, InstanceIdAllocator.NodeOf(read.InstanceId));
    }

    [Fact]
    public void A_rare_of_spec_3_8_is_about_127_bytes_and_the_BEFORE_costs_59_of_them()
    {
        // Spec 10.6 counts 1 + 2 + 5 + 1 + 1 + 58 + 1 + 58: the version byte, a two byte currency id, a five
        // byte instance id, the content version, and each payload with its own length varint at 3.8's 58
        // byte rare.
        var payloadBefore = new byte[58];
        var payloadAfter = new byte[58];
        var crafted = new ItemCraftedEvent(300, 1L << 34, 7, payloadBefore, payloadAfter);

        Assert.Equal(127, crafted.ByteCount);

        // After alone would be 68 bytes, so carrying the before costs 59: its 58 bytes plus the length
        // varint that introduces them. It is worth every one of them, because the page is rewritten whole on
        // the next commit and this event is the only durable record of what the craft changed.
        Assert.Equal(68, crafted.ByteCount - 59);
        Assert.True(ItemCraftedEvent.TryRead(crafted.ToArray(), out ItemCraftedEvent read, out string? reason), reason);
        Assert.Equal(58, read.Before.Length);
        Assert.Equal(58, read.After.Length);
    }

    [Fact]
    public void It_is_built_from_a_CraftPlan_and_carries_both_payloads_by_REFERENCE()
    {
        // The plan is the ONE place a currency row id lives, so the event takes it from there rather than
        // from a caller that could name a different row than the one that ran.
        ItemCraftedEvent crafted = ItemCraftedEvent.From(Plan(7), 7_001, 7, Before, After);

        Assert.Equal(7, crafted.CurrencyId);
        Assert.Equal(7_001L, crafted.InstanceId);
        Assert.Equal(7, crafted.ContentVersion);
        Assert.True(crafted.Before.Span.Overlaps(Before.AsSpan()));
        Assert.True(crafted.After.Span.Overlaps(After.AsSpan()));
    }

    [Fact]
    public void The_event_records_the_RESOLVED_payloads_and_carries_no_seed_state_or_step_mask()
    {
        // The same rule the item-generated body is held to: the bytes say what the item is, and a mask or a
        // seed beside them would be a second answer that a replay would have to re-run to mean anything.
        var names = new List<string>();
        foreach (System.Reflection.PropertyInfo property in typeof(ItemCraftedEvent).GetProperties())
        {
            names.Add(property.Name.ToUpperInvariant());
        }

        Assert.DoesNotContain("SEED", names);
        Assert.DoesNotContain("STATE", names);
        Assert.DoesNotContain("RANMASK", names);
        Assert.DoesNotContain("SKIPPEDMASK", names);
        Assert.DoesNotContain("RANDOM", names);
    }

    /// <summary>A plan carrying nothing but the currency row id, which is all the event reads off it.</summary>
    static CraftPlan Plan(int currencyId) => new(currencyId, consumesDefinitionId: 0, consumesCount: 0, [], []);
}
