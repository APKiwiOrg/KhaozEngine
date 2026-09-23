using System;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The narrow door a commit builder reads and writes a paged container through: exactly the members its
/// operations, its window arithmetic and its projection writes call, and nothing else
/// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1045">#1045</see>).
/// <para>
/// <b>It exists so a host keeps ownership.</b> A builder holds what it is opened over from <c>Open</c> until
/// <c>MarkCommitted</c> and writes through it on every <c>Apply</c>. Handed a <see cref="PagedItemContainer"/>,
/// it holds a real write door, which defeats a host that shares its containers copy on write: the host has to
/// give the door away and deep copy from then on. Handed the host's own implementation instead, every write
/// arrives through <see cref="SetSlotAt"/>, <see cref="TakeSlotAt"/> or <see cref="MarkClean"/>, where the host
/// runs its ownership check first, and every read leaves shared state shared.
/// </para>
/// <para>
/// <b>No page object crosses it.</b> A page is read by INDEX, because an <see cref="ItemContainerPage"/> carries
/// write members of its own and handing one out would be a second door around the first. A page's first slot
/// and width are the geometry's (<see cref="ItemContainerPage.FirstSlotOf"/> and
/// <see cref="ItemContainerPageCodec.ContainerPageSlots"/>), so only its stamp, its dirty flag and its entries
/// are asked.
/// </para>
/// <para>
/// An implementation keeps <see cref="PagedItemContainer"/>'s meaning for every member: slots are ABSOLUTE
/// container slots over the address space <c>PageCount * ContainerPageSlots</c>, a write dirties a page only
/// when it changed what the page stores, and <see cref="MarkClean"/> clears every page.
/// </para>
/// <para>
/// <b><see cref="MarkClean"/> is called only on a container holding a dirty page, and only after the commit
/// carrying that page has landed.</b> A builder opened over a bag and a bank where a click touched only the bag
/// never calls it on the bank, so a host that takes ownership in it copies only what a commit wrote.
/// </para>
/// </summary>
public interface IPagedContainerWorkingCopy
{
    /// <summary>How many pages, fixed for the container's life.</summary>
    int PageCount { get; }

    /// <summary>The game's rule for whether a definition merges into one slot, which a merge and a grant ask.</summary>
    Func<int, bool> Stackable { get; }

    /// <summary>Whether a grant that opens a new slot would be refused (spec 5.7).</summary>
    bool IsAtCapacity { get; }

    /// <summary>One slot's whole state. Reading never dirties a page.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    ItemSlot SlotAt(int containerSlot);

    /// <summary>Writes one slot as an OPERATION and answers whether the page changed.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <param name="value">The whole slot to seat.</param>
    bool SetSlotAt(int containerSlot, ItemSlot value);

    /// <summary>Empties one slot as an OPERATION and answers what it held.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    ItemSlot TakeSlotAt(int containerSlot);

    /// <summary>Whether one page owes the next commit a rewrite.</summary>
    /// <param name="pageIndex">The page, below <see cref="PageCount"/>.</param>
    bool IsPageDirty(int pageIndex);

    /// <summary>One page's stamp, the content version number it was last brought up to date with.</summary>
    /// <param name="pageIndex">The page, below <see cref="PageCount"/>.</param>
    int PageContentVersion(int pageIndex);

    /// <summary>Copies one page's occupied entries out in ascending slot order, ready for the page codec, and
    /// answers how many there were, exactly as <see cref="ItemContainerPage.CopyEntriesTo"/> does.</summary>
    /// <param name="pageIndex">The page, below <see cref="PageCount"/>.</param>
    /// <param name="destination">At least <see cref="ItemContainerPageCodec.ContainerPageSlots"/> long.</param>
    int CopyPageEntriesTo(int pageIndex, Span<PageSlotInput> destination);

    /// <summary>Clears every page's dirty flag, which a builder owes once the commit carrying them has landed. A
    /// builder calls it only when at least one page is dirty.</summary>
    void MarkClean();
}
