using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.Items;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerCommitFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>
/// Spec 6.4's batch window and the commit it emits. The facts that need a REAL journal store, which is every
/// replay and crash case of 6.6, live beside these in <see cref="ContainerCommitJournalTests"/>.
/// </summary>
public sealed class ContainerCommitBuilderTests
{
    [Fact]
    public void The_window_defaults_are_the_journals_own_caps()
    {
        var options = new ContainerCommitOptions();

        Assert.Equal(JournalLimits.EngineMaximumEventsPerOperation, options.Limits.EventsPerOperation);
        Assert.Equal(JournalLimits.EngineMaximumProjectionWritesPerOperation, options.Limits.ProjectionWritesPerOperation);
        Assert.Equal(JournalLimits.EngineMaximumAggregateCommitBytes, options.Limits.AggregateCommitBytes);
        Assert.Equal(128, options.Limits.EventsPerOperation);
        Assert.Equal(64, options.Limits.ProjectionWritesPerOperation);
        Assert.Equal(8 * 1024 * 1024, options.Limits.AggregateCommitBytes);
    }

    [Fact]
    public void The_tick_boundary_closes_the_batch()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 20);
        ContainerCommitBuilder batch = OpenBank(bank, tick: 4);

        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 1), tick: 4));
        Assert.False(batch.Apply(ContainerOperation.Take(Bank, 3, 1), tick: 5));

        Assert.Equal(ContainerBatchCloseReason.TickBoundary, batch.Window.CloseReason);
        Assert.Equal(1, batch.EventCount);
        Assert.Equal(19, bank.SlotAt(3).Stack.Count);
    }

    [Fact]
    public void An_operation_touching_a_SECOND_stream_closes_the_batch()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 20);
        ContainerCommitBuilder batch = OpenBank(bank);

        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 1)));
        Assert.False(batch.Apply(ContainerOperation.Move(Bank, 3, Vault, 0, 19)));

        Assert.Equal(ContainerBatchCloseReason.SecondStream, batch.Window.CloseReason);
        Assert.Equal(1, batch.EventCount);
        Assert.Equal(19, bank.SlotAt(3).Stack.Count);
    }

    [Fact]
    public void An_operation_that_sets_PresentAtCommit_never_shares_a_batch()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 20);
        SeatItem(bank, 4, Sword, Instance);
        ContainerOperation trade = ContainerOperation.Take(Bank, 4, 1, Instance) with { PresentAtCommit = true };

        ContainerCommitBuilder behind = OpenBank(bank);
        Assert.True(behind.Apply(ContainerOperation.Take(Bank, 3, 1)));
        Assert.False(behind.Apply(trade));
        Assert.Equal(ContainerBatchCloseReason.PresentAtCommit, behind.Window.CloseReason);

        ContainerCommitBuilder alone = OpenBank(bank);
        Assert.True(alone.Apply(trade));
        Assert.False(alone.Apply(ContainerOperation.Take(Bank, 3, 1)));
        Assert.Equal(ContainerBatchCloseReason.PresentAtCommit, alone.Window.CloseReason);
        Assert.True(alone.Close(Mint(ServerId)).PresentAtCommit);
    }

    [Fact]
    public void A_CLIENT_originated_operation_closes_a_batch_already_under_way()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 20);
        ContainerCommitBuilder batch = OpenBank(bank);

        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 1)));
        Assert.False(batch.Apply(ContainerOperation.Take(Bank, 3, 1).FromClient(Guid.NewGuid())));

        Assert.Equal(ContainerBatchCloseReason.ClientOperation, batch.Window.CloseReason);
        Assert.Equal(1, batch.EventCount);
    }

    [Fact]
    public void The_event_cap_closes_the_batch_at_the_journals_own_128()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 200);
        ContainerCommitBuilder batch = OpenBank(bank);

        for (int taken = 0; taken < JournalLimits.EngineMaximumEventsPerOperation; taken++)
            Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 1)));
        Assert.False(batch.Apply(ContainerOperation.Take(Bank, 3, 1)));

        Assert.Equal(ContainerBatchCloseReason.LimitReached, batch.Window.CloseReason);
        Assert.Equal(128, batch.EventCount);
        Assert.Equal(1, batch.ProjectionWriteCount);
        Assert.Equal(128, batch.Close(Mint(ServerId)).StreamMutations[0].Events.Count);
    }

    [Fact]
    public void The_projection_write_cap_closes_the_batch_at_the_journals_own_64()
    {
        PagedItemContainer bank = Container(pageCount: 70);
        ContainerCommitBuilder batch = OpenBank(bank);

        for (int page = 0; page < JournalLimits.EngineMaximumProjectionWritesPerOperation; page++)
            Assert.True(batch.Apply(ContainerOperation.Grant(Bank, page * ItemContainerPageCodec.ContainerPageSlots, Potion, 1)));
        Assert.False(batch.Apply(ContainerOperation.Grant(Bank, 64 * ItemContainerPageCodec.ContainerPageSlots, Potion, 1)));

        Assert.Equal(ContainerBatchCloseReason.LimitReached, batch.Window.CloseReason);
        Assert.Equal(64, batch.ProjectionWriteCount);
        Assert.Equal(64, batch.Close(Mint(ServerId)).ProjectionWrites.Count);
    }

    [Fact]
    public void The_aggregate_byte_cap_closes_the_batch()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 200);
        SeatItem(bank, 4, Sword, Instance);
        ContainerCommitBuilder batch = OpenBank(
            bank, options: new ContainerCommitOptions { Limits = new JournalLimits(aggregateCommitBytes: 1_024) });

        // The bound crosses a kilobyte before the 128-event cap even with compact audit bodies.
        int applied = 0;
        while (batch.Apply(ContainerOperation.Craft(Bank, 4, Instance, Payload(applied + 1), CraftEventBody(applied))))
            applied++;

        Assert.Equal(ContainerBatchCloseReason.LimitReached, batch.Window.CloseReason);
        Assert.InRange(applied, 1, 127);
        Assert.True(batch.Close(Mint(ServerId)).OwnedByteCount <= 1_024);
    }

    [Fact]
    public void A_large_version_two_grant_is_refused_before_its_event_would_exceed_the_commit_cap()
    {
        byte[] payload = new ItemInstancePayloadBuilder().Add(1024, new byte[400]).ToArray();
        ContainerOperation grant = ContainerOperation.Grant(Bank, 4, Sword, 1, Instance, payload);
        PagedItemContainer roomyBank = Container();
        ContainerCommitBuilder roomy = OpenBank(roomyBank);
        Assert.True(roomy.Apply(grant));
        int actualBytes = roomy.Close(Mint(ServerId)).OwnedByteCount;

        PagedItemContainer tightBank = Container();
        ContainerCommitBuilder tight = OpenBank(tightBank, options: new ContainerCommitOptions
        {
            Limits = new JournalLimits(aggregateCommitBytes: actualBytes - 1),
        });

        Assert.False(tight.Apply(grant));
        Assert.True(tightBank.SlotAt(4).IsEmpty);
        Assert.Equal(ContainerBatchCloseReason.LimitReached, tight.Window.CloseReason);
    }

    [Fact]
    public void The_FIRST_closer_wins()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 20);
        ContainerCommitBuilder batch = OpenBank(bank, tick: 4);
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 1), tick: 4));

        // Both a tick boundary AND a client operation, on the tick after the batch opened.
        Assert.False(batch.Apply(ContainerOperation.Take(Bank, 3, 1).FromClient(Guid.NewGuid()), tick: 5));
        Assert.Equal(ContainerBatchCloseReason.TickBoundary, batch.Window.CloseReason);

        // A later closer never overwrites the recorded one.
        Assert.False(batch.Apply(ContainerOperation.Move(Bank, 3, Vault, 0, 19), tick: 4));
        Assert.Equal(ContainerBatchCloseReason.TickBoundary, batch.Window.CloseReason);
    }

    [Fact]
    public void A_batch_never_merges_two_CLIENT_originated_operations()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 20);
        ContainerCommitBuilder batch = OpenBank(bank);
        Guid first = Guid.NewGuid();

        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 1).FromClient(first)));
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 2)));
        Assert.False(batch.Apply(ContainerOperation.Take(Bank, 3, 4).FromClient(Guid.NewGuid())));

        Assert.Equal(ContainerBatchCloseReason.ClientOperation, batch.Window.CloseReason);
        Assert.Equal(first, batch.Close(Mint(ServerId)).Identity.OperationId);
    }

    [Fact]
    public void A_SERVER_minted_batchs_intent_is_the_canonical_ORDERED_operation_list()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 20);
        ContainerCommitBuilder batch = OpenBank(bank);

        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 2)));
        Assert.True(batch.Apply(ContainerOperation.Grant(Bank, 5, Sword, 1, Instance)));
        JournalCommit commit = batch.Close(Mint(ServerId));

        // Hand written rather than round tripped: [Count][ [Kind][Parameters] ]*, minimal unsigned varints,
        // a container name as [Length][UTF8], and instance 7,001 as the two byte LEB128 0xD9 0x36.
        byte[] expected =
        {
            2,
            5, 4, (byte)'b', (byte)'a', (byte)'n', (byte)'k', 3, 2, 0,
            4, 4, (byte)'b', (byte)'a', (byte)'n', (byte)'k', 5, 100, 1, 0xD9, 0x36,
        };
        Assert.Equal(expected, commit.Identity.NormalizedIntent.ToArray());
        Assert.Equal(ServerId, commit.Identity.OperationId);
    }

    [Fact]
    public void A_CLIENT_headed_batchs_intent_is_the_clients_own_operation_ALONE()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 20);
        ContainerCommitBuilder batch = OpenBank(bank);
        Guid clientId = Guid.NewGuid();
        ContainerOperation click = ContainerOperation.Take(Bank, 3, 2).FromClient(clientId);

        Assert.True(batch.Apply(click));
        Assert.True(batch.Apply(ContainerOperation.Grant(Bank, 5, Sword, 1, Instance)));
        JournalCommit commit = batch.Close(Mint(ServerId));

        byte[] expected = { 5, 4, (byte)'b', (byte)'a', (byte)'n', (byte)'k', 3, 2, 0 };
        Assert.Equal(expected, commit.Identity.NormalizedIntent.ToArray());
        Assert.Equal(clientId, commit.Identity.OperationId);

        // The server work rode the batch: two events and one page, contributing no intent bytes at all.
        Assert.Equal(2, commit.StreamMutations[0].Events.Count);
        Assert.Single(commit.ProjectionWrites);
    }

    [Fact]
    public void Close_emits_one_event_per_operation_in_order_and_one_write_per_dirty_page()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 20);
        SeatItem(bank, 4, Sword, Instance);
        ContainerCommitBuilder batch = OpenBank(bank);

        Assert.True(batch.Apply(ContainerOperation.Split(Bank, 3, 7, 5)));
        Assert.True(batch.Apply(ContainerOperation.Merge(Bank, 7, 3)));
        Assert.True(batch.Apply(ContainerOperation.Move(Bank, 4, Bank, 150, 1, Instance)));
        JournalCommit commit = batch.Close(Mint(ServerId), new byte[] { 9 });

        Assert.Equal(
            new[] { ItemInstanceEvents.StackSplit, ItemInstanceEvents.StackMerged, ItemInstanceEvents.Moved },
            commit.StreamMutations[0].Events.Select(value => value.EventType));
        Assert.Equal(new[] { "bank/p00", "bank/p01" }, commit.ProjectionWrites.Select(value => value.SectionName));
        Assert.Equal(new byte[] { 9 }, commit.ResultData.ToArray());
        Assert.Equal(ItemInstanceEvents.CraftActionKind, commit.Identity.ActionKind);
        Assert.Equal(Scope, commit.Identity.AuthenticatedScope);
    }

    [Fact]
    public void A_move_across_two_pages_is_ONE_commit_with_TWO_writes_on_ONE_stream()
    {
        PagedItemContainer bank = Container();
        SeatItem(bank, 4, Sword, Instance);
        ContainerCommitBuilder batch = OpenBank(bank);

        Assert.True(batch.Apply(ContainerOperation.Move(Bank, 4, Bank, 150, 1, Instance)));
        JournalCommit commit = batch.Close(Mint(ServerId));

        Assert.Single(commit.StreamMutations);
        Assert.Equal(StreamKey, commit.StreamMutations[0].StreamKey);
        Assert.Single(commit.StreamMutations[0].Events);
        Assert.Equal(2, commit.ProjectionWrites.Count);
        Assert.All(commit.ProjectionWrites, write => Assert.Equal(StreamKey, write.StreamKey));
        Assert.True(bank.SlotAt(4).IsEmpty);
        Assert.Equal(Instance, bank.SlotAt(150).Stack.InstanceId);
    }

    [Fact]
    public void A_move_between_two_containers_of_one_stream_writes_both()
    {
        PagedItemContainer bank = Container();
        PagedItemContainer bag = Container(pageCount: 1, capacity: 30);
        SeatItem(bank, 4, Sword, Instance);
        ContainerCommitBuilder batch = ContainerCommitBuilder.Open(
            StreamKey, ItemInstanceEvents.CraftActionKind, Scope, Containers((Bank, bank), (Bag, bag)));

        Assert.True(batch.Apply(ContainerOperation.Move(Bank, 4, Bag, 2, 1, Instance)));
        JournalCommit commit = batch.Close(Mint(ServerId));

        Assert.Equal(new[] { "bag/p00", "bank/p00" }, commit.ProjectionWrites.Select(value => value.SectionName));
        Assert.Equal(Instance, bag.SlotAt(2).Stack.InstanceId);
    }

    [Fact]
    public void A_page_a_remap_dirtied_rides_the_next_commit_and_causes_none_of_its_own()
    {
        PagedItemContainer bank = Container();
        SeatItem(bank, 4, Sword, Instance);
        SeatStack(bank, 150, Potion, 3);

        // What a load time remap leaves behind: a dirtied page nobody operated on.
        Assert.True(bank.Pages[1].ApplyRemap(150, new ItemSlot(new ItemStack(Potion + 5, 3), default, false), 9));
        Assert.Equal(1, bank.DirtyPageCount);

        ContainerCommitBuilder rides = OpenBank(bank);
        Assert.Throws<InvalidOperationException>(() => rides.Close(Mint(ServerId)));

        Assert.True(rides.Apply(ContainerOperation.Take(Bank, 4, 1, Instance)));
        JournalCommit commit = rides.Close(Mint(ServerId));

        Assert.Single(commit.StreamMutations[0].Events);
        Assert.Equal(new[] { "bank/p00", "bank/p01" }, commit.ProjectionWrites.Select(value => value.SectionName));
    }

    [Fact]
    public void Close_leaves_the_pages_dirty_and_MarkCommitted_clears_them()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 20);
        ContainerCommitBuilder batch = OpenBank(bank);
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 1)));

        batch.Close(Mint(ServerId));
        Assert.Equal(1, bank.DirtyPageCount);

        batch.MarkCommitted();
        Assert.Equal(0, bank.DirtyPageCount);
    }

    [Fact]
    public void A_caller_closing_the_batch_is_its_own_reason_rather_than_a_tick_boundary()
    {
        // Close recorded TickBoundary, so a batch the caller closed on its own tick read afterwards as one
        // the clock took away from it. The five closers of spec 6.4 are the reasons an operation was
        // REFUSED, and a caller acts on them, so a sixth reason is what a caller-initiated close says.
        PagedItemContainer bank = Container();
        SeatStack(bank, 3, Potion, 20);
        ContainerCommitBuilder batch = OpenBank(bank, tick: 4);
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 3, 1), tick: 4));

        _ = batch.Close(Mint(ServerId));

        Assert.Equal(ContainerBatchCloseReason.Closed, batch.Window.CloseReason);
        Assert.False(batch.Window.IsOpen);

        // The tick that would have closed it is still the tick that closes an unclosed one, so the five
        // spec closers still mean what they say.
        ContainerCommitBuilder next = OpenBank(bank, tick: 4);
        Assert.True(next.Apply(ContainerOperation.Take(Bank, 3, 1), tick: 4));
        Assert.False(next.Apply(ContainerOperation.Take(Bank, 3, 1), tick: 5));
        Assert.Equal(ContainerBatchCloseReason.TickBoundary, next.Window.CloseReason);
    }

    [Fact]
    public void A_Close_that_throws_leaves_the_batch_where_it_was_rather_than_half_closed()
    {
        // Close flagged the batch closed and closed the WINDOW before it built anything, so a throw out of
        // the projection writes left a batch that could never be closed again, with its pages still dirty
        // and no fault flag: the caller held something that had committed nothing and could do nothing.
        // Nothing is recorded now until the commit is built and validated.
        //
        // The throw is reachable because Open vets a container name at page 0, where the suffix is three
        // characters, and page 100's is five. A name four short of the identity cap opens and then cannot
        // name its own hundredth page.
        string wide = new('b', JournalLimits.EngineMaximumIdentityCharacters - 4);
        PagedItemContainer bank = Container(pageCount: 101);
        ContainerCommitBuilder batch = ContainerCommitBuilder.Open(
            StreamKey, ItemInstanceEvents.CraftActionKind, Scope, Containers((wide, bank)), tick: 4);

        Assert.True(batch.Apply(ContainerOperation.Grant(wide, slot: 10_000, Sword, 1, Instance, Payload())));
        Assert.Equal(1, batch.ProjectionWriteCount);

        Assert.Throws<ArgumentException>(() => batch.Close(Mint(ServerId)));

        Assert.True(batch.Window.IsOpen);
        Assert.Equal(ContainerBatchCloseReason.Open, batch.Window.CloseReason);
        Assert.Equal(1, batch.ProjectionWriteCount);
    }

    [Fact]
    public void Twenty_crafts_in_one_held_action_are_ONE_commit()
    {
        PagedItemContainer bank = Container();
        SeatItem(bank, 4, Sword, Instance);
        SeatStack(bank, 5, Currency, 20);
        ContainerCommitBuilder batch = OpenBank(bank);

        for (int craft = 0; craft < 20; craft++)
        {
            Assert.True(batch.Apply(ContainerOperation.Craft(
                Bank,
                4,
                Instance,
                Payload(craft + 1),
                CraftEventBody(craft),
                currencySlot: 5,
                currencyDefinitionId: Currency,
                currencyCount: 1)));
        }

        JournalCommit commit = batch.Close(Mint(ServerId));

        Assert.Equal(ContainerBatchCloseReason.Closed, batch.Window.CloseReason);
        Assert.Equal(20, commit.StreamMutations[0].Events.Count);
        Assert.All(commit.StreamMutations[0].Events, value => Assert.Equal(ItemInstanceEvents.Crafted, value.EventType));
        Assert.Single(commit.ProjectionWrites);
        Assert.True(bank.SlotAt(5).IsEmpty);
        Assert.Equal(Payload(20), bank.SlotAt(4).Payload.ToArray());
    }

    [Fact]
    public void Every_kind_applies_to_the_working_copy_and_writes_its_own_durable_event_type()
    {
        PagedItemContainer bank = Container();
        SeatStack(bank, 0, Potion, 10);
        SeatItem(bank, 1, Sword, Instance);
        SeatStack(bank, 2, Currency, 4);
        ContainerCommitBuilder batch = OpenBank(bank);

        Assert.True(batch.Apply(ContainerOperation.Split(Bank, 0, 10, 4)));
        Assert.True(batch.Apply(ContainerOperation.Merge(Bank, 10, 0)));
        Assert.True(batch.Apply(ContainerOperation.Move(Bank, 1, Bank, 11, 1, Instance)));
        Assert.True(batch.Apply(ContainerOperation.Grant(Bank, 12, Potion, 3)));
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 0, 2)));
        Assert.True(batch.Apply(ContainerOperation.Craft(
            Bank, 11, Instance, Payload(99), CraftEventBody(0, afterLevel: 99), currencySlot: 2, currencyDefinitionId: Currency, currencyCount: 1)));

        IReadOnlyList<JournalEvent> events = batch.Close(Mint(ServerId)).StreamMutations[0].Events;
        Assert.Equal(
            new[]
            {
                ItemInstanceEvents.StackSplit,
                ItemInstanceEvents.StackMerged,
                ItemInstanceEvents.Moved,
                ItemInstanceEvents.Granted,
                ItemInstanceEvents.Taken,
                ItemInstanceEvents.Crafted,
            },
            events.Select(value => value.EventType));
        Assert.Equal(new[] { 1, 1, 1, 2, 1, 2 },
            events.Select(value => value.EventSchemaVersion));

        Assert.Equal(8, bank.SlotAt(0).Stack.Count);
        Assert.True(bank.SlotAt(10).IsEmpty);
        Assert.Equal(Payload(99), bank.SlotAt(11).Payload.ToArray());
        Assert.Equal(3, bank.SlotAt(12).Stack.Count);
        Assert.Equal(3, bank.SlotAt(2).Stack.Count);
    }

    [Fact]
    public void A_grant_opening_a_new_slot_is_refused_at_capacity_and_a_merge_is_not()
    {
        PagedItemContainer bank = Container(pageCount: 1, capacity: 1);
        SeatStack(bank, 0, Potion, 5);
        ContainerCommitBuilder batch = OpenBank(bank);

        Assert.Throws<ArgumentException>(() => batch.Apply(ContainerOperation.Grant(Bank, 1, Potion, 1)));
        Assert.Throws<InvalidOperationException>(() => batch.Apply(ContainerOperation.Grant(Bank, 0, Potion, 1)));

        ContainerCommitBuilder merging = OpenBank(bank);
        Assert.True(merging.Apply(ContainerOperation.Grant(Bank, 0, Potion, 1)));
        Assert.Equal(6, bank.SlotAt(0).Stack.Count);
    }

    [Fact]
    public void An_operation_the_working_copy_cannot_perform_throws_and_abandons_the_batch()
    {
        PagedItemContainer bank = Container();
        SeatItem(bank, 4, Sword, Instance);
        ContainerCommitBuilder batch = OpenBank(bank);

        // Spec 15.1: the declared instance id is held against the slot, so a stale operation cannot land on
        // an item that replaced the one it named.
        Assert.Throws<ArgumentException>(() => batch.Apply(ContainerOperation.Take(Bank, 4, 1, Instance + 1)));
        Assert.Throws<InvalidOperationException>(() => batch.Apply(ContainerOperation.Take(Bank, 4, 1, Instance)));
        Assert.Throws<InvalidOperationException>(() => batch.Close(Mint(ServerId)));
    }

    [Fact]
    public void A_batch_refuses_a_malformed_operation_before_it_reaches_the_working_copy()
    {
        PagedItemContainer bank = Container();
        ContainerCommitBuilder batch = OpenBank(bank);

        Assert.Throws<ArgumentException>(() => batch.Apply(default));
        Assert.Throws<ArgumentException>(() => batch.Apply(
            ContainerOperation.Take(Bank, 0, 1) with { Origin = ContainerOperationOrigin.Client }));
        Assert.Throws<ArgumentException>(() => batch.Apply(
            ContainerOperation.Craft(Bank, 0, Instance, default, default)));
        Assert.Throws<ArgumentException>(() => batch.Apply(
            ContainerOperation.Take(Bank, 0, 1) with { EventPayload = new byte[] { 1 } }));
        Assert.True(batch.Window.IsOpen);
    }

    [Fact]
    public void A_craft_that_consumes_no_currency_carries_no_currency_fields()
    {
        // The canonical intent is what an operation HASHES under, so two operations that do the same thing
        // have to encode the same way. A craft with no currency writes its currency container, slot,
        // definition id and count anyway, and ApplyCraft reads none of them once the definition id is 0, so
        // the same craft under two callers' defaults hashed two ways and resolved as a conflict.
        PagedItemContainer bank = Container();
        SeatItem(bank, 4, Sword, Instance);
        ContainerCommitBuilder batch = OpenBank(bank);

        ContainerOperation free = ContainerOperation.Craft(Bank, 4, Instance, Payload(2), CraftEventBody(0, afterLevel: 2));
        Assert.True(batch.Apply(free));

        Assert.Throws<ArgumentException>(() => (free with { DestinationContainer = Bag }).Validate());
        Assert.Throws<ArgumentException>(() => (free with { DestinationSlot = 5 }).Validate());
        Assert.Throws<ArgumentException>(() => (free with { Count = 1 }).Validate());

        // The currency fields are the CURRENCY's, so they are legal the moment one is consumed.
        ContainerOperation paid = ContainerOperation.Craft(
            Bank, 4, Instance, Payload(3), CraftEventBody(1, afterLevel: 3, beforeLevel: 2),
            currencySlot: 5, currencyDefinitionId: Currency, currencyCount: 1);
        paid.Validate();
    }

    [Fact]
    public void A_batch_is_opened_over_at_least_one_container_and_only_over_nameable_ones()
    {
        Assert.Throws<ArgumentException>(() => ContainerCommitBuilder.Open(
            StreamKey, ItemInstanceEvents.CraftActionKind, Scope, new Dictionary<string, PagedItemContainer>()));
        Assert.Throws<ArgumentException>(() => ContainerCommitBuilder.Open(
            StreamKey, ItemInstanceEvents.CraftActionKind, Scope, Containers(("bank/p00", Container()))));
        Assert.Throws<ArgumentNullException>(() => ContainerCommitBuilder.Open(
            StreamKey, ItemInstanceEvents.CraftActionKind, Scope, (IReadOnlyDictionary<string, PagedItemContainer>)null!));
        Assert.Throws<ArgumentNullException>(() => ContainerCommitBuilder.Open(
            StreamKey, ItemInstanceEvents.CraftActionKind, Scope, (IReadOnlyDictionary<string, IPagedContainerWorkingCopy>)null!));
        Assert.Throws<ArgumentException>(() => ContainerCommitBuilder.Open(
            StreamKey, ItemInstanceEvents.CraftActionKind, Scope, new Dictionary<string, PagedItemContainer> { [Bank] = null! }));
    }
}
