using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// A container held as pages, spec 5: the shape a journal commit can rewrite one page of instead of
/// rewriting the whole thing. It splits the two concepts <see cref="ItemContainer"/> conflates.
/// <para>
/// <b>SLOT SPACE</b> is the page geometry, fixed at construction and
/// <c>PageCount * ContainerPageSlots</c>. It is an ADDRESS space and it never shrinks.
/// <b>CAPACITY</b> is a separate mutable integer, the number of OCCUPIED slots a grant may leave behind. It
/// is a gate consulted by <c>Add</c> and by nothing else (spec 5.7, and the capacity half of this type).
/// </para>
/// <para>
/// <b>Every page declares the full geometry, and that is the
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/916">#916</see> decision.</b> Spec 5.2 names
/// short last pages (<c>bag/p00</c> for a 30 slot bag) and spec 5.7 makes slot space
/// <c>PageCount * ContainerPageSlots</c>, which read as two different geometries. This type takes 5.7:
/// slot space is always the full multiple, a 30 slot bag is ONE full page whose CAPACITY is 30, and a
/// stored page declaring fewer slots than the geometry is a codec level anomaly for the load path to
/// refuse rather than a shape this type can hold. Two things fall out of it. Slot 743 is page 7 slot 43 for
/// every container in the fleet rather than only for containers nobody shortened, and a bag that grows from
/// 30 to 40 slots is a capacity edit rather than a re-paging of stored bytes. The codec's own one-sided
/// bound (it refuses a page declaring MORE than the caller's geometry and accepts fewer) is task 4's
/// loader concern and is left alone here, which is why #916 stays open.
/// </para>
/// <para>
/// <b>Entries are SPARSE and nothing compacts them.</b> A hole is the absence of an entry and costs zero
/// bytes, so the dense renumber a consumer's repair pass explicitly refuses to do is not something this
/// container can do by accident.
/// </para>
/// <para>
/// Nothing here is thread-safe, exactly like the kernel it pages: one owner, one container.
/// </para>
/// </summary>
public sealed partial class PagedItemContainer
{
    readonly ItemContainerPage[] _pages;
    readonly Func<int, bool> _stackable;

    /// <summary>Builds an empty paged container.</summary>
    /// <param name="pageCount">How many pages of <see cref="ItemContainerPageCodec.ContainerPageSlots"/>
    /// slots, fixed for the container's life. At least one.</param>
    /// <param name="capacity">The occupied-slot gate, which a grant may not push past. It may exceed the
    /// slot space, in which case the slot space is the effective limit, and it may be lowered below
    /// occupancy later, which is legal and trims nothing.</param>
    /// <param name="stackable">The game's rule for whether a definition merges into one slot. Held rather
    /// than its ANSWER: every operation asks again, so a game whose rule reads its catalog sees catalog
    /// edits live.</param>
    /// <param name="payloadCanonical">Whether an instance payload is canonical, spec 4.7's door check. It
    /// is the same predicate <see cref="ItemContainer"/> takes and reaches the same kernel, so the two
    /// doors cannot come to disagree. Left null, every non-empty, non-quarantined payload is refused.</param>
    /// <param name="quarantineWellFormed">Whether a quarantined slot's bytes are a well formed quarantine
    /// wrapper, which is <see cref="QuarantineWrapper.Verify(ReadOnlyMemory{byte})"/>. Left null, every
    /// non-empty quarantined payload is refused.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageCount"/> is not positive or names
    /// a page the codec's header cannot, or <paramref name="capacity"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stackable"/> is null.</exception>
    public PagedItemContainer(
        int pageCount,
        int capacity,
        Func<int, bool> stackable,
        Func<ReadOnlyMemory<byte>, bool>? payloadCanonical = null,
        Func<ReadOnlyMemory<byte>, bool>? quarantineWellFormed = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageCount, ItemContainerPage.MaxPageIndex + 1);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentNullException.ThrowIfNull(stackable);

        _stackable = stackable;
        _pages = new ItemContainerPage[pageCount];
        for (int index = 0; index < pageCount; index++)
            _pages[index] = new ItemContainerPage(index, stackable, payloadCanonical, quarantineWellFormed);

        Pages = new ReadOnlyCollection<ItemContainerPage>(_pages);
        _capacity = capacity;
    }

    /// <summary>How many pages this container has, fixed for its life.</summary>
    public int PageCount => _pages.Length;

    /// <summary>The address space: <c>PageCount</c> times
    /// <see cref="ItemContainerPageCodec.ContainerPageSlots"/>. It is not what fits, which is
    /// <see cref="Capacity"/>, and it never shrinks.</summary>
    public int SlotSpace => _pages.Length * ItemContainerPageCodec.ContainerPageSlots;

    /// <summary>The pages, in index order. Each one carries its own stamp and dirty flag.</summary>
    public IReadOnlyList<ItemContainerPage> Pages { get; }

    /// <summary>How many slots hold an entry, which is the number <see cref="Capacity"/> gates.</summary>
    public int Occupancy
    {
        get
        {
            int occupied = 0;
            foreach (ItemContainerPage page in _pages) occupied += page.EntryCount;
            return occupied;
        }
    }

    /// <summary>How many slots of the ADDRESS SPACE hold nothing. A container at capacity usually has free
    /// slots and refuses to open one anyway, which is the whole of spec 5.7.</summary>
    public int FreeSlots => SlotSpace - Occupancy;

    /// <summary>How many pages owe the next commit a rewrite.</summary>
    public int DirtyPageCount
    {
        get
        {
            int dirty = 0;
            foreach (ItemContainerPage page in _pages) if (page.IsDirty) dirty++;
            return dirty;
        }
    }

    /// <summary>The page a container slot falls in.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is outside the slot space.</exception>
    public ItemContainerPage PageForSlot(int containerSlot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(containerSlot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(containerSlot, SlotSpace);
        return _pages[ItemContainerPage.PageOf(containerSlot)];
    }

    /// <summary>One slot's whole state. Reading never dirties a page.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is outside the slot space.</exception>
    public ItemSlot SlotAt(int containerSlot) => PageForSlot(containerSlot).SlotAt(containerSlot);

    /// <summary>Total units of one definition across every page.</summary>
    /// <param name="definitionId">The definition counted. Zero counts nothing.</param>
    public int CountOf(int definitionId)
    {
        if (definitionId == 0) return 0;

        long total = 0;
        foreach (ItemContainerPage page in _pages)
        {
            for (int slot = page.FirstSlot; slot < page.FirstSlot + page.SlotCount; slot++)
            {
                ItemStack stack = page.SlotAt(slot).Stack;
                if (stack.ItemId == definitionId) total += stack.Count;
            }
        }

        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>Seats a decoded slot: the LOAD path's door, which never dirties a page.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <param name="value">The decoded slot.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is outside the slot space.</exception>
    /// <exception cref="ArgumentException">The slot breaks one of spec 4.7's four invariants.</exception>
    public void Seat(int containerSlot, ItemSlot value) => PageForSlot(containerSlot).Seat(containerSlot, value);

    /// <summary>Writes one slot as an OPERATION and answers whether the page changed.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <param name="value">The whole slot to seat.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is outside the slot space.</exception>
    /// <exception cref="ArgumentException">The slot breaks one of spec 4.7's four invariants.</exception>
    public bool SetSlotAt(int containerSlot, ItemSlot value) =>
        PageForSlot(containerSlot).Write(containerSlot, value);

    /// <summary>Empties one slot and answers what it held.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is outside the slot space.</exception>
    public ItemSlot TakeSlotAt(int containerSlot) => PageForSlot(containerSlot).Take(containerSlot);

    /// <summary>Swaps two slots outright, which is the click-to-move a player performs. A swap ACROSS two
    /// pages dirties both, which is spec 5.6's one commit with two projection writes, and a swap of a slot
    /// with itself changes nothing and therefore dirties nothing.</summary>
    /// <param name="left">One absolute container slot.</param>
    /// <param name="right">The other.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either slot is outside the slot space.</exception>
    public void Swap(int left, int right)
    {
        ItemSlot first = SlotAt(left);
        ItemSlot second = SlotAt(right);
        PageForSlot(left).Write(left, second);
        PageForSlot(right).Write(right, first);
    }

    /// <summary>Removes units of a definition, walking slots first to last, which is the visible,
    /// predictable order a player expects.</summary>
    /// <param name="definitionId">The definition removed. Zero removes nothing.</param>
    /// <param name="count">Units to remove. Non-positive removes nothing.</param>
    /// <returns>How many units actually left. Less than <paramref name="count"/> means the container held
    /// fewer.</returns>
    public int Remove(int definitionId, int count)
    {
        if (definitionId == 0 || count <= 0) return 0;

        int remaining = count;
        foreach (ItemContainerPage page in _pages)
        {
            for (int slot = page.FirstSlot; slot < page.FirstSlot + page.SlotCount && remaining > 0; slot++)
            {
                ItemSlot held = page.SlotAt(slot);
                if (held.IsEmpty || held.Stack.ItemId != definitionId) continue;

                int taken = Math.Min(held.Stack.Count, remaining);
                int left = held.Stack.Count - taken;
                if (left == 0) page.Take(slot);
                else page.Write(slot, held with { Stack = held.Stack with { Count = left } });
                remaining -= taken;
            }

            if (remaining == 0) break;
        }

        return count - remaining;
    }

    /// <summary>
    /// Copies the dirty pages out, which is what the commit builder asks for (spec 5.6). It folds them into
    /// whatever commit comes next alongside the pages the operation itself changed, and that is what makes
    /// the lazy remap rewrite cost nothing: it never CAUSES a commit, it only joins one.
    /// </summary>
    /// <param name="destination">At least <see cref="DirtyPageCount"/> long.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public int CopyDirtyPagesTo(Span<ItemContainerPage> destination)
    {
        int dirty = DirtyPageCount;
        if (destination.Length < dirty)
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"{dirty} pages are dirty and the span holds {destination.Length}."),
                nameof(destination));

        int count = 0;
        foreach (ItemContainerPage page in _pages) if (page.IsDirty) destination[count++] = page;
        return count;
    }

    /// <summary>Clears every page's dirty flag, which the commit builder owes them once the commit carrying
    /// them has landed.</summary>
    public void MarkClean()
    {
        foreach (ItemContainerPage page in _pages) page.MarkClean();
    }
}
