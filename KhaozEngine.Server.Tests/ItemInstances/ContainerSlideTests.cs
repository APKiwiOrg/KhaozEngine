using System;
using System.Collections.Generic;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.Items;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerCommitFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>One journal event compacts a bank run without losing an instance or exceeding its page budget.</summary>
public sealed class ContainerSlideTests
{
    [Fact]
    public void A_full_bank_left_slide_is_one_event_and_replays_all_ten_pages()
    {
        PagedItemContainer live = FullBank(), replayed = FullBank();
        ContainerCommitBuilder batch = OpenBank(live);

        Assert.True(batch.Apply(ContainerOperation.Slide(Bank, 1, 0, 999)));
        Assert.Equal(1, batch.EventCount);
        Assert.Equal(10, batch.ProjectionWriteCount);
        JournalEvent stored = Assert.Single(Assert.Single(batch.Close(Mint(ServerId)).StreamMutations).Events);
        Assert.Equal(ItemInstanceEvents.Slid, stored.EventType);
        Assert.Equal(1, stored.EventSchemaVersion);
        Assert.True(ContainerOperationEventCodec.TryRead(stored.EventType, stored.EventSchemaVersion,
            stored.Payload, out ContainerOperation decoded, out string? readReason), readReason);
        Assert.True(ContainerOperationApplier.TryApply(Copies(replayed), decoded,
            out string? applyReason), applyReason);

        Assert.Equal(2, replayed.SlotAt(0).Stack.Count);
        Assert.Equal(1000, replayed.SlotAt(998).Stack.Count);
        Assert.True(replayed.SlotAt(999).IsEmpty);
        for (int page = 0; page < 10; page++)
            Assert.Equal(EncodePage(live, page), EncodePage(replayed, page));
    }

    [Fact]
    public void An_overlapping_right_slide_preserves_distinct_instance_ids_and_payloads()
    {
        PagedItemContainer bank = Container(pageCount: 1);
        for (int slot = 0; slot < 3; slot++)
            SeatItem(bank, slot, Sword, Instance + slot, Payload(42 + slot));

        Assert.True(ContainerOperationApplier.TryApply(Copies(bank),
            ContainerOperation.Slide(Bank, 0, 1, 3), out string? reason), reason);

        Assert.True(bank.SlotAt(0).IsEmpty);
        for (int slot = 1; slot <= 3; slot++)
        {
            Assert.Equal(Instance + slot - 1, bank.SlotAt(slot).Stack.InstanceId);
            Assert.Equal(Payload(41 + slot), bank.SlotAt(slot).Payload.ToArray());
        }
    }

    [Fact]
    public void An_occupied_destination_fringe_or_a_source_hole_refuses_without_a_partial_slide()
    {
        PagedItemContainer blocked = Container(pageCount: 1);
        for (int slot = 0; slot <= 3; slot++) SeatStack(blocked, slot, Potion, slot + 1);
        byte[] before = EncodePage(blocked, 0);

        Assert.False(ContainerOperationApplier.TryApply(Copies(blocked),
            ContainerOperation.Slide(Bank, 1, 0, 3), out _));
        Assert.Equal(before, EncodePage(blocked, 0));

        PagedItemContainer withHole = Container(pageCount: 1);
        SeatStack(withHole, 1, Potion, 2);
        SeatStack(withHole, 3, Potion, 4);
        before = EncodePage(withHole, 0);
        Assert.False(ContainerOperationApplier.TryApply(Copies(withHole),
            ContainerOperation.Slide(Bank, 1, 0, 3), out _));
        Assert.Equal(before, EncodePage(withHole, 0));
    }

    [Fact]
    public void A_destination_outside_the_address_space_refuses_without_mutation()
    {
        PagedItemContainer bank = Container(pageCount: 1);
        SeatStack(bank, 99, Potion, 1);
        byte[] before = EncodePage(bank, 0);

        Assert.False(ContainerOperationApplier.TryApply(Copies(bank),
            ContainerOperation.Slide(Bank, 99, 100, 1), out _));
        Assert.Equal(before, EncodePage(bank, 0));
    }

    [Fact]
    public void A_slide_touching_sixty_five_pages_is_refused_by_the_write_cap_before_mutation()
    {
        PagedItemContainer bank = Container(pageCount: 65, capacity: 6500);
        SeatStack(bank, 1, Potion, 2);
        ContainerCommitBuilder batch = OpenBank(bank);

        Assert.False(batch.Apply(ContainerOperation.Slide(Bank, 1, 0, 6400)));
        Assert.Equal(ContainerBatchCloseReason.LimitReached, batch.Window.CloseReason);
        Assert.Equal(2, bank.SlotAt(1).Stack.Count);
        Assert.True(bank.SlotAt(0).IsEmpty);
    }

    [Fact]
    public void A_slide_is_refused_before_its_multi_page_commit_would_exceed_the_byte_cap()
    {
        PagedItemContainer roomyBank = TwoPageBank();
        ContainerCommitBuilder roomy = OpenBank(roomyBank);
        ContainerOperation slide = ContainerOperation.Slide(Bank, 1, 0, 199);
        Assert.True(roomy.Apply(slide));
        int actualBytes = roomy.Close(Mint(ServerId)).OwnedByteCount;

        PagedItemContainer tightBank = TwoPageBank();
        byte[] beforeFirst = EncodePage(tightBank, 0);
        byte[] beforeSecond = EncodePage(tightBank, 1);
        ContainerCommitBuilder tight = OpenBank(tightBank, options: new ContainerCommitOptions
        {
            Limits = new JournalLimits(aggregateCommitBytes: actualBytes - 1),
        });

        Assert.False(tight.Apply(slide));
        Assert.Equal(ContainerBatchCloseReason.LimitReached, tight.Window.CloseReason);
        Assert.Equal(beforeFirst, EncodePage(tightBank, 0));
        Assert.Equal(beforeSecond, EncodePage(tightBank, 1));
    }

    static PagedItemContainer FullBank()
    {
        PagedItemContainer bank = Container(pageCount: 10, capacity: 1000);
        for (int slot = 1; slot < 1000; slot++) SeatStack(bank, slot, Potion, slot + 1);
        return bank;
    }

    static PagedItemContainer TwoPageBank()
    {
        PagedItemContainer bank = Container(pageCount: 2, capacity: 200);
        for (int slot = 1; slot < 200; slot++) SeatStack(bank, slot, Potion, slot + 1);
        return bank;
    }

    static Dictionary<string, IPagedContainerWorkingCopy> Copies(PagedItemContainer bank)
        => new(StringComparer.Ordinal) { [Bank] = bank };

    static byte[] EncodePage(PagedItemContainer container, int index)
    {
        ItemContainerPage page = container.Pages[index];
        var entries = new PageSlotInput[ItemContainerPageCodec.ContainerPageSlots];
        int count = page.CopyEntriesTo(entries);
        return ItemContainerPageCodec.Encode(page.PageIndex, page.FirstSlot,
            page.SlotCount, page.ContentVersion, entries.AsSpan(0, count));
    }
}
