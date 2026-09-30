using System;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Pages;

/// <summary>A consumer can restore an admitted paged-container view without inventing a mutation to
/// preserve a pending page rewrite.</summary>
public sealed class PagedItemContainerRestorationTests
{
    const int PageSlots = ItemContainerPageCodec.ContainerPageSlots;
    const int Sword = 4200;
    const int Potion = 995;

    static bool NeverStacks(int definitionId)
    {
        _ = definitionId;
        return false;
    }

    static byte[] Level(ulong level) =>
        new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.ItemLevel, level).ToArray();

    static ItemSlot Plain(int definitionId, int count = 1) =>
        new(new ItemStack(definitionId, count), default, Quarantined: false);

    static ItemSlot Instance(int definitionId, long instanceId, ReadOnlyMemory<byte> payload) =>
        new(new ItemStack(definitionId, 1, instanceId), payload, Quarantined: false);

    [Fact]
    public void External_copy_preserves_entries_stamps_capacity_and_dirty_pages()
    {
        var source = new PagedItemContainer(
            pageCount: 3,
            capacity: 175,
            NeverStacks,
            ItemInstancePayload.IsCanonical,
            QuarantineWrapper.Verify,
            stackCap: static _ => 20);
        byte[] payload = Level(42);

        source.Pages[0].SeatStamp(11);
        source.Seat(4, Instance(Sword, 101, payload));

        source.Pages[1].SeatStamp(12);
        source.SetSlotAt(PageSlots + 7, Plain(Potion, 3));
        source.TakeSlotAt(PageSlots + 7);

        source.Pages[2].SeatStamp(13);
        source.SetSlotAt((2 * PageSlots) + 9, Plain(Sword));

        PagedItemContainer copy = ExternalCopy(source);

        Assert.Equal(175, copy.Capacity);
        Assert.Equal(2, copy.Occupancy);
        Assert.Equal(new[] { 11, 12, 13 },
            new[] { copy.Pages[0].ContentVersion, copy.Pages[1].ContentVersion, copy.Pages[2].ContentVersion });
        Assert.False(copy.Pages[0].IsDirty);
        Assert.True(copy.Pages[1].IsDirty);
        Assert.True(copy.Pages[2].IsDirty);
        Assert.Equal(0, copy.Pages[1].EntryCount);
        Assert.Equal(payload, copy.SlotAt(4).Payload.ToArray());

        Span<ItemContainerPage> dirty = new ItemContainerPage[copy.PageCount];
        Assert.Equal(2, copy.CopyDirtyPagesTo(dirty));
        Assert.Equal(1, dirty[0].PageIndex);
        Assert.Equal(2, dirty[1].PageIndex);
    }

    [Fact]
    public void SeatDirty_restores_both_states_without_changing_page_contents_or_stamp()
    {
        var page = new ItemContainerPage(
            pageIndex: 0,
            NeverStacks,
            ItemInstancePayload.IsCanonical,
            QuarantineWrapper.Verify);
        ItemSlot held = Instance(Sword, 202, Level(68));
        page.SeatStamp(17);
        page.Seat(3, held);

        page.SeatDirty(true);

        Assert.True(page.IsDirty);
        Assert.Equal(17, page.ContentVersion);
        Assert.Equal(1, page.EntryCount);
        Assert.Equal(held, page.SlotAt(3));

        page.SeatDirty(false);

        Assert.False(page.IsDirty);
        Assert.Equal(17, page.ContentVersion);
        Assert.Equal(1, page.EntryCount);
        Assert.Equal(held, page.SlotAt(3));
    }

    [Fact]
    public void External_copy_keeps_the_original_live_acceptance_predicates()
    {
        bool acceptPayload = true;
        bool acceptWrapper = true;
        byte[] payload = { 0x02, 0x01, 0x2A };
        byte[] wrapper = { 0x4B, 0x45, 0x43, 0x51 };
        Func<ReadOnlyMemory<byte>, bool> payloadCanonical = value =>
            acceptPayload && value.Span.SequenceEqual(payload);
        Func<ReadOnlyMemory<byte>, bool> quarantineWellFormed = value =>
            acceptWrapper && value.Span.SequenceEqual(wrapper);
        var source = new PagedItemContainer(
            pageCount: 1,
            capacity: 10,
            NeverStacks,
            payloadCanonical,
            quarantineWellFormed);

        PagedItemContainer copy = ExternalCopy(source);

        Assert.Same(payloadCanonical, copy.PayloadCanonical);
        Assert.Same(quarantineWellFormed, copy.QuarantineWellFormed);
        source.Seat(0, Instance(Sword, 301, payload));
        copy.Seat(0, Instance(Sword, 301, payload));
        source.Seat(1, new ItemSlot(new ItemStack(Sword, 1, 302), wrapper, Quarantined: true));
        copy.Seat(1, new ItemSlot(new ItemStack(Sword, 1, 302), wrapper, Quarantined: true));

        acceptPayload = false;
        acceptWrapper = false;

        Assert.Throws<ArgumentException>(() => source.Seat(2, Instance(Sword, 303, payload)));
        Assert.Throws<ArgumentException>(() => copy.Seat(2, Instance(Sword, 303, payload)));
        Assert.Throws<ArgumentException>(() => source.Seat(
            3, new ItemSlot(new ItemStack(Sword, 1, 304), wrapper, Quarantined: true)));
        Assert.Throws<ArgumentException>(() => copy.Seat(
            3, new ItemSlot(new ItemStack(Sword, 1, 304), wrapper, Quarantined: true)));
    }

    [Fact]
    public void Mutating_an_external_copy_does_not_change_the_source()
    {
        var source = new PagedItemContainer(
            pageCount: 1,
            capacity: 10,
            NeverStacks,
            ItemInstancePayload.IsCanonical,
            QuarantineWrapper.Verify);
        source.Seat(0, Instance(Sword, 401, Level(75)));

        PagedItemContainer copy = ExternalCopy(source);
        copy.TakeSlotAt(0);
        copy.SetSlotAt(1, Plain(Potion, 4));

        Assert.False(source.SlotAt(0).IsEmpty);
        Assert.True(source.SlotAt(1).IsEmpty);
        Assert.True(copy.SlotAt(0).IsEmpty);
        Assert.Equal(Plain(Potion, 4), copy.SlotAt(1));
    }

    static PagedItemContainer ExternalCopy(PagedItemContainer source)
    {
        var copy = new PagedItemContainer(
            source.PageCount,
            source.Capacity,
            source.Stackable,
            source.PayloadCanonical,
            source.QuarantineWellFormed,
            source.StackCap);
        var entries = new PageSlotInput[PageSlots];
        for (int pageIndex = 0; pageIndex < source.PageCount; pageIndex++)
        {
            ItemContainerPage sourcePage = source.Pages[pageIndex];
            ItemContainerPage copyPage = copy.Pages[pageIndex];
            int count = sourcePage.CopyEntriesTo(entries);
            copyPage.SeatStamp(sourcePage.ContentVersion);
            for (int entryIndex = 0; entryIndex < count; entryIndex++)
            {
                PageSlotInput entry = entries[entryIndex];
                copyPage.Seat(
                    entry.Slot,
                    new ItemSlot(
                        new ItemStack(entry.DefinitionId, entry.Count, entry.InstanceId),
                        entry.Payload,
                        entry.Quarantined));
            }
            copyPage.SeatDirty(sourcePage.IsDirty);
        }

        return copy;
    }
}
