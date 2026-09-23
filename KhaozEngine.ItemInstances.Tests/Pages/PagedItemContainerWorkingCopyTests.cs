using System;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Pages;

/// <summary>
/// <see cref="PagedItemContainer"/> as the <see cref="IPagedContainerWorkingCopy"/> a commit builder writes
/// through (https://github.com/APKiwiOrg/KhaozEngine/issues/1045). The builder's own facts, which prove it reaches
/// nothing wider, are <c>ContainerCommitWorkingCopyTests</c> in <c>KhaozEngine.Server.Tests</c>, the project
/// that references the journal package.
/// </summary>
public class PagedItemContainerWorkingCopyTests
{
    const int PageSlots = ItemContainerPageCodec.ContainerPageSlots;
    const int Potion = 995;

    static PagedItemContainer NewContainer() =>
        new(pageCount: 3, capacity: 1_000, static id => id != 0, ItemInstancePayload.IsCanonical, QuarantineWrapper.Verify);

    static ItemSlot Stack(int count) => new(new ItemStack(Potion, count), Array.Empty<byte>(), Quarantined: false);

    [Fact]
    public void The_page_reads_by_index_answer_what_the_pages_themselves_answer()
    {
        PagedItemContainer container = NewContainer();
        container.Seat(5, Stack(3));
        container.Seat(PageSlots + 7, Stack(4));
        container.Pages[1].SeatStamp(12);
        Assert.True(container.SetSlotAt((2 * PageSlots) + 1, Stack(9)));

        IPagedContainerWorkingCopy copy = container;
        var fromCopy = new PageSlotInput[PageSlots];
        var fromPage = new PageSlotInput[PageSlots];
        for (int page = 0; page < container.PageCount; page++)
        {
            Assert.Equal(container.Pages[page].IsDirty, copy.IsPageDirty(page));
            Assert.Equal(container.Pages[page].ContentVersion, copy.PageContentVersion(page));

            int copied = copy.CopyPageEntriesTo(page, fromCopy);
            int direct = container.Pages[page].CopyEntriesTo(fromPage);
            Assert.Equal(direct, copied);
            for (int entry = 0; entry < copied; entry++)
            {
                Assert.Equal(fromPage[entry].Slot, fromCopy[entry].Slot);
                Assert.Equal(fromPage[entry].Count, fromCopy[entry].Count);
            }
        }

        Assert.False(copy.IsPageDirty(0));
        Assert.Equal(12, copy.PageContentVersion(1));
        Assert.True(copy.IsPageDirty(2));

        copy.MarkClean();
        Assert.Equal(0, container.DirtyPageCount);
    }

    [Fact]
    public void Every_other_member_is_the_containers_own_door()
    {
        // The builder's writes land through the same doors a caller's do, so a write through the interface
        // dirties exactly what the container's own member would.
        PagedItemContainer container = NewContainer();
        IPagedContainerWorkingCopy copy = container;

        Assert.Equal(container.PageCount, copy.PageCount);
        Assert.Same(container.Stackable, copy.Stackable);
        Assert.Equal(container.IsAtCapacity, copy.IsAtCapacity);

        Assert.True(copy.SetSlotAt(PageSlots + 2, Stack(6)));
        Assert.Equal(6, container.SlotAt(PageSlots + 2).Stack.Count);
        Assert.Equal(6, copy.SlotAt(PageSlots + 2).Stack.Count);
        Assert.True(container.Pages[1].IsDirty);

        Assert.Equal(6, copy.TakeSlotAt(PageSlots + 2).Stack.Count);
        Assert.True(container.SlotAt(PageSlots + 2).IsEmpty);
    }

    [Fact]
    public void A_page_index_outside_the_container_is_refused()
    {
        IPagedContainerWorkingCopy copy = NewContainer();
        var entries = new PageSlotInput[PageSlots];

        Assert.Throws<ArgumentOutOfRangeException>(() => copy.IsPageDirty(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => copy.IsPageDirty(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => copy.PageContentVersion(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => copy.CopyPageEntriesTo(3, entries));
    }
}
