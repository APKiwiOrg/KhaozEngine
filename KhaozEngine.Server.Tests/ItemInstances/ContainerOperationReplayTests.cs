using System;
using System.Collections.Generic;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.Items;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerCommitFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>A stored operation rebuilds the same complete slots from the same prior pages.</summary>
public sealed class ContainerOperationReplayTests
{
    [Fact]
    public void Grant_replays_an_owned_items_payload_and_page_bytes()
    {
        PagedItemContainer live = Container();
        PagedItemContainer replayed = Container();
        byte[] payload = Payload(43);
        ContainerCommitBuilder batch = OpenBank(live);
        Assert.True(batch.Apply(ContainerOperation.Grant(Bank, 4, Sword, 1, Instance, payload)));

        ContainerOperation operation = ReadSingle(batch);
        Assert.True(ContainerOperationApplier.TryReplay(Copies((Bank, replayed)), operation,
            out string? reason), reason);

        Assert.Equal(Sword, replayed.SlotAt(4).Stack.ItemId);
        Assert.Equal(Instance, replayed.SlotAt(4).Stack.InstanceId);
        Assert.Equal(payload, replayed.SlotAt(4).Payload.ToArray());
        Assert.Equal(EncodePage(live, 0), EncodePage(replayed, 0));
    }

    [Fact]
    public void A_whole_instance_move_replays_across_two_containers()
    {
        PagedItemContainer liveBank = Container(), liveBag = Container();
        PagedItemContainer replayBank = Container(), replayBag = Container();
        SeatItem(liveBank, 2, Sword, Instance, Payload(43));
        SeatItem(replayBank, 2, Sword, Instance, Payload(43));
        ContainerCommitBuilder batch = ContainerCommitBuilder.Open(StreamKey,
            ItemInstanceEvents.CraftActionKind, Scope,
            Containers((Bank, liveBank), (Bag, liveBag)), tick: 4);
        Assert.True(batch.Apply(ContainerOperation.Move(Bank, 2, Bag, 4, 1, Instance)));

        ContainerOperation operation = ReadSingle(batch);
        Assert.True(ContainerOperationApplier.TryReplay(Copies((Bank, replayBank), (Bag, replayBag)),
            operation, out string? reason), reason);

        Assert.True(replayBank.SlotAt(2).IsEmpty);
        Assert.Equal(Instance, replayBag.SlotAt(4).Stack.InstanceId);
        Assert.Equal(Payload(43), replayBag.SlotAt(4).Payload.ToArray());
        Assert.Equal(EncodePage(liveBank, 0), EncodePage(replayBank, 0));
        Assert.Equal(EncodePage(liveBag, 0), EncodePage(replayBag, 0));
    }

    [Fact]
    public void A_plain_take_split_and_merge_replay_their_counts()
    {
        PagedItemContainer live = Container(), replayed = Container();
        foreach (PagedItemContainer bank in new[] { live, replayed })
        {
            SeatStack(bank, 0, Potion, 4);
            SeatStack(bank, 1, Potion, 6);
        }
        ContainerCommitBuilder batch = OpenBank(live);
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 1, 2)));
        Assert.True(batch.Apply(ContainerOperation.Split(Bank, 1, 2, 2)));
        Assert.True(batch.Apply(ContainerOperation.Merge(Bank, 1, 0)));

        JournalCommit commit = batch.Close(Mint(ServerId));
        foreach (JournalEvent entry in Assert.Single(commit.StreamMutations).Events)
        {
            Assert.True(ContainerOperationEventCodec.TryRead(entry.EventType, entry.EventSchemaVersion,
                entry.Payload, out ContainerOperation operation, out string? readReason), readReason);
            Assert.True(ContainerOperationApplier.TryReplay(Copies((Bank, replayed)), operation,
                out string? applyReason), applyReason);
        }

        Assert.Equal(6, replayed.SlotAt(0).Stack.Count);
        Assert.True(replayed.SlotAt(1).IsEmpty);
        Assert.Equal(2, replayed.SlotAt(2).Stack.Count);
        Assert.Equal(EncodePage(live, 0), EncodePage(replayed, 0));
    }

    [Fact]
    public void Historical_merge_and_occupied_grant_replay_after_stackability_is_retuned()
    {
        PagedItemContainer live = Container();
        PagedItemContainer replayed = new(2, 10_000, static _ => false,
            static _ => true, QuarantineWrapper.Verify);
        foreach (PagedItemContainer bank in new[] { live, replayed })
        {
            SeatStack(bank, 0, Potion, 4);
            SeatStack(bank, 1, Potion, 6);
        }

        ContainerCommitBuilder batch = OpenBank(live);
        Assert.True(batch.Apply(ContainerOperation.Merge(Bank, 1, 0)));
        Assert.True(batch.Apply(ContainerOperation.Grant(Bank, 0, Potion, 2)));
        JournalCommit commit = batch.Close(Mint(ServerId));
        foreach (JournalEvent entry in Assert.Single(commit.StreamMutations).Events)
        {
            Assert.True(ContainerOperationEventCodec.TryRead(entry.EventType, entry.EventSchemaVersion,
                entry.Payload, out ContainerOperation operation, out string? readReason), readReason);
            Assert.True(ContainerOperationApplier.TryReplay(Copies((Bank, replayed)), operation,
                out string? applyReason), applyReason);
        }

        Assert.Equal(12, replayed.SlotAt(0).Stack.Count);
        Assert.True(replayed.SlotAt(1).IsEmpty);
        Assert.Equal(EncodePage(live, 0), EncodePage(replayed, 0));

        PagedItemContainer retunedLive = new(2, 10_000, static _ => false,
            static _ => true, QuarantineWrapper.Verify);
        SeatStack(retunedLive, 0, Potion, 4);
        SeatStack(retunedLive, 1, Potion, 6);
        ContainerCommitBuilder retunedBatch = OpenBank(retunedLive);
        Assert.Throws<ArgumentException>(() => retunedBatch.Apply(ContainerOperation.Merge(Bank, 1, 0)));
        ContainerCommitBuilder retunedGrant = OpenBank(retunedLive);
        Assert.Throws<ArgumentException>(() => retunedGrant.Apply(ContainerOperation.Grant(Bank, 0, Potion, 2)));
    }

    [Fact]
    public void Historical_grant_replays_after_container_capacity_is_reduced()
    {
        PagedItemContainer live = Container(capacity: 2);
        PagedItemContainer replayed = Container(capacity: 1);
        SeatStack(live, 0, Potion, 4);
        SeatStack(replayed, 0, Potion, 4);

        ContainerCommitBuilder batch = OpenBank(live);
        Assert.True(batch.Apply(ContainerOperation.Grant(Bank, 1, Sword, 1, Instance, Payload())));
        ContainerOperation operation = ReadSingle(batch);
        Assert.True(ContainerOperationApplier.TryReplay(Copies((Bank, replayed)), operation,
            out string? reason), reason);
        Assert.Equal(EncodePage(live, 0), EncodePage(replayed, 0));

        PagedItemContainer current = Container(capacity: 1);
        SeatStack(current, 0, Potion, 4);
        ContainerCommitBuilder currentBatch = OpenBank(current);
        Assert.Throws<ArgumentException>(() => currentBatch.Apply(
            ContainerOperation.Grant(Bank, 1, Sword, 1, Instance, Payload())));
    }

    [Fact]
    public void A_paid_craft_replays_after_payload_and_currency_count()
    {
        PagedItemContainer live = Container(), replayed = Container();
        foreach (PagedItemContainer bank in new[] { live, replayed })
        {
            SeatItem(bank, 4, Sword, Instance, Payload(42));
            SeatStack(bank, 5, Currency, 3);
        }
        byte[] after = Payload(43);
        byte[] audit = new ItemCraftedEvent(1, Instance, 5, Payload(42), after).ToArray();
        ContainerCommitBuilder batch = OpenBank(live);
        Assert.True(batch.Apply(ContainerOperation.Craft(Bank, 4, Instance, after, audit,
            currencySlot: 5, currencyDefinitionId: Currency, currencyCount: 1)));

        ContainerOperation operation = ReadSingle(batch);
        Assert.True(ContainerOperationApplier.TryReplay(Copies((Bank, replayed)), operation,
            out string? reason), reason);

        Assert.Equal(after, replayed.SlotAt(4).Payload.ToArray());
        Assert.Equal(2, replayed.SlotAt(5).Stack.Count);
        Assert.Equal(EncodePage(live, 0), EncodePage(replayed, 0));
    }

    [Fact]
    public void A_craft_with_a_wrong_before_payload_refuses_without_changing_either_slot()
    {
        PagedItemContainer bank = Container();
        SeatItem(bank, 4, Sword, Instance, Payload(42));
        SeatStack(bank, 5, Currency, 3);
        byte[] beforePage = EncodePage(bank, 0);
        byte[] after = Payload(43);
        byte[] audit = new ItemCraftedEvent(1, Instance, 5, Payload(41), after).ToArray();
        ContainerOperation operation = ContainerOperation.Craft(Bank, 4, Instance, after, audit,
            currencySlot: 5, currencyDefinitionId: Currency, currencyCount: 1);

        Assert.False(ContainerOperationApplier.TryReplay(Copies((Bank, bank)), operation,
            out string? reason));
        Assert.Equal("before-mismatch", reason);
        Assert.Equal(beforePage, EncodePage(bank, 0));
    }

    [Fact]
    public void The_live_builder_uses_the_same_before_check_before_mutation()
    {
        PagedItemContainer bank = Container();
        SeatItem(bank, 4, Sword, Instance, Payload(42));
        SeatStack(bank, 5, Currency, 3);
        byte[] beforePage = EncodePage(bank, 0);
        byte[] after = Payload(43);
        byte[] audit = new ItemCraftedEvent(1, Instance, 5, Payload(41), after).ToArray();
        ContainerCommitBuilder batch = OpenBank(bank);

        Assert.Throws<ArgumentException>(() => batch.Apply(ContainerOperation.Craft(
            Bank, 4, Instance, after, audit,
            currencySlot: 5, currencyDefinitionId: Currency, currencyCount: 1)));
        Assert.Equal(beforePage, EncodePage(bank, 0));
    }

    [Fact]
    public void Missing_destination_and_full_capacity_refuse_before_any_slot_changes()
    {
        PagedItemContainer bank = Container();
        SeatItem(bank, 2, Sword, Instance, Payload());
        byte[] before = EncodePage(bank, 0);

        Assert.False(ContainerOperationApplier.TryApply(Copies((Bank, bank)),
            ContainerOperation.Move(Bank, 2, Vault, 4, 1, Instance), out _));
        Assert.Equal(before, EncodePage(bank, 0));

        PagedItemContainer full = Container(pageCount: 1, capacity: 1);
        SeatStack(full, 0, Potion, 5);
        before = EncodePage(full, 0);
        Assert.False(ContainerOperationApplier.TryApply(Copies((Bank, full)),
            ContainerOperation.Grant(Bank, 1, Sword, 1, Instance, Payload()), out _));
        Assert.Equal(before, EncodePage(full, 0));
    }

    [Fact]
    public void A_payload_grant_without_an_instance_id_is_refused_before_seating()
    {
        PagedItemContainer bank = Container();
        byte[] before = EncodePage(bank, 0);

        Assert.False(ContainerOperationApplier.TryReplay(Copies((Bank, bank)),
            ContainerOperation.Grant(Bank, 4, Sword, 1, payload: Payload()), out _));
        Assert.Equal(before, EncodePage(bank, 0));
    }

    [Fact]
    public void A_craft_cannot_consume_its_target_as_the_currency_slot()
    {
        PagedItemContainer bank = Container();
        SeatItem(bank, 4, Sword, Instance, Payload(42));
        byte[] before = EncodePage(bank, 0);
        byte[] after = Payload(43);
        byte[] audit = new ItemCraftedEvent(1, Instance, 5, Payload(42), after).ToArray();
        ContainerOperation craft = ContainerOperation.Craft(Bank, 4, Instance, after, audit,
            currencySlot: 4, currencyDefinitionId: Sword, currencyCount: 1);

        Assert.False(ContainerOperationApplier.TryReplay(Copies((Bank, bank)), craft, out _));
        Assert.Equal(before, EncodePage(bank, 0));
    }

    static ContainerOperation ReadSingle(ContainerCommitBuilder batch)
    {
        JournalEvent entry = Assert.Single(Assert.Single(batch.Close(Mint(ServerId)).StreamMutations).Events);
        Assert.True(ContainerOperationEventCodec.TryRead(entry.EventType, entry.EventSchemaVersion,
            entry.Payload, out ContainerOperation operation, out string? reason), reason);
        return operation;
    }

    static Dictionary<string, IPagedContainerWorkingCopy> Copies(
        params (string Name, PagedItemContainer Container)[] entries)
    {
        var copies = new Dictionary<string, IPagedContainerWorkingCopy>(StringComparer.Ordinal);
        foreach ((string name, PagedItemContainer container) in entries) copies.Add(name, container);
        return copies;
    }

    static byte[] EncodePage(PagedItemContainer container, int index)
    {
        ItemContainerPage page = container.Pages[index];
        var entries = new PageSlotInput[ItemContainerPageCodec.ContainerPageSlots];
        int count = page.CopyEntriesTo(entries);
        return ItemContainerPageCodec.Encode(page.PageIndex, page.FirstSlot,
            page.SlotCount, page.ContentVersion, entries.AsSpan(0, count));
    }
}
