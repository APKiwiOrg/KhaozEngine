using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// The working copy half: what each operation of the vocabulary DOES to a paged container, and the arithmetic
/// the window needs before it decides.
/// <para>
/// <b>The page rule is spec 5.6's and nothing wider:</b> the pages holding the slots the operation changed,
/// and no others. Every kind names its slots, so those pages are known BEFORE the operation is applied, which
/// is what lets the window close on the projection write cap without a mutation to undo. A move across two
/// pages of one container is one commit with two projection writes on the same stream, and atomicity is the
/// database transaction's.
/// </para>
/// <para>
/// <b>Everything here refuses by throwing.</b> An action a game refuses never reaches the journal (spec
/// 10.6), so an operation the working copy cannot perform is a caller bug rather than a player outcome.
/// </para>
/// </summary>
public sealed partial class ContainerCommitBuilder
{
    /// <summary>The slack a page write is allowed to grow by beyond the entry it seats: the header fields, a
    /// wider entry count varint and a slot varint. It exists to keep the window's byte check an UPPER bound,
    /// which is the only direction a cap can be approximated in.</summary>
    const int PageGrowthSlack = 64;

    void ApplyToWorkingCopy(in ContainerOperation operation)
    {
        if (!ContainerOperationApplier.TryApply(_containers, operation, out string? reason))
            throw new ArgumentException(reason, nameof(operation));
        if (operation.PresentAtCommit) _presentAtCommit = true;
    }

    /// <summary>The pages this operation would dirty that are not dirty already, which is what the projection
    /// write cap is counted in.</summary>
    int CountUndirtiedPages(in ContainerOperation operation)
    {
        if (operation.Kind == ContainerOperationKind.Slide)
        {
            int pagesAdded = 0;
            foreach (PageRef page in SlidePages(operation)) if (!page.IsDirty) pagesAdded++;
            return pagesAdded;
        }

        PageRef first = PageFor(operation.Container, operation.Slot);
        PageRef? second = SecondPage(operation);
        int added = first.IsDirty ? 0 : 1;
        if (second is { } other && !other.Is(first) && !other.IsDirty) added++;
        return added;
    }

    /// <summary>The normalized intent's size once this operation joins, which is spec 6.5's two shapes.</summary>
    int ProjectedIntentBytes(in ContainerOperation operation)
    {
        if (Window.HoldsClientOperation) return _operations[0].CanonicalByteCount;
        if (operation.Origin == ContainerOperationOrigin.Client && _operations.Count == 0)
            return operation.CanonicalByteCount;

        int size = ContentVarint.Size((uint)(_operations.Count + 1)) + operation.CanonicalByteCount;
        foreach (ContainerOperation joined in _operations) size += joined.CanonicalByteCount;
        return size;
    }

    /// <summary>
    /// An UPPER bound on the commit's owned bytes once this operation joins: the intent, every event, every
    /// page already dirty, every page this operation would newly dirty at its CURRENT size, and the most the
    /// touched pages can grow by. It never underestimates, which is the only direction a cap may be
    /// approximated in. The result bytes are the caller's and <c>JournalCommit</c> counts them itself, so
    /// Close validates the real total against the same limits.
    /// </summary>
    int ProjectedCommitBytes(in ContainerOperation operation, int projectedIntentBytes)
    {
        if (operation.Kind == ContainerOperationKind.Slide)
            return ProjectedSlideBytes(operation, projectedIntentBytes);

        PageRef first = PageFor(operation.Container, operation.Slot);
        PageRef? second = SecondPage(operation);
        int joining = first.IsDirty ? 0 : MeasurePage(first.Container, first.Index);
        if (second is { } other && !other.Is(first) && !other.IsDirty) joining += MeasurePage(other.Container, other.Index);

        int eventBytes = ContainerOperationEventCodec.EncodedSize(operation);
        int growth = EntryBound(_containers[operation.Container].SlotAt(operation.Slot))
            + operation.Payload.Length
            + PageGrowthSlack;
        return projectedIntentBytes + _eventBytes + eventBytes + _pageBytes + joining + growth;
    }

    int ProjectedSlideBytes(in ContainerOperation operation, int projectedIntentBytes)
    {
        List<PageRef> pages = SlidePages(operation);
        int joining = 0;
        foreach (PageRef page in pages)
            if (!page.IsDirty) joining = checked(joining + MeasurePage(page.Container, page.Index));

        IPagedContainerWorkingCopy source = _containers[operation.Container];
        int growth = checked(pages.Count * PageGrowthSlack);
        for (int offset = 0; offset < operation.Count; offset++)
            growth = checked(growth + EntryBound(source.SlotAt(operation.Slot + offset)));

        return checked(projectedIntentBytes + _eventBytes
            + ContainerOperationEventCodec.EncodedSize(operation) + _pageBytes + joining + growth);
    }

    /// <summary>Every page intersecting either run, once, so a slide spanning a gap does not charge its
    /// untouched pages against the journal's write limit.</summary>
    List<PageRef> SlidePages(in ContainerOperation operation)
    {
        PageRef sourceFirst = PageFor(operation.Container, operation.Slot);
        PageRef sourceLast = PageFor(operation.Container,
            checked(operation.Slot + operation.Count - 1));
        PageRef destinationFirst = PageFor(operation.Container, operation.DestinationSlot);
        PageRef destinationLast = PageFor(operation.Container,
            checked(operation.DestinationSlot + operation.Count - 1));

        var pages = new List<PageRef>(sourceLast.Index - sourceFirst.Index + 1
            + destinationLast.Index - destinationFirst.Index + 1);
        for (int page = sourceFirst.Index; page <= sourceLast.Index; page++)
            pages.Add(new PageRef(sourceFirst.Container, page));
        for (int page = destinationFirst.Index; page <= destinationLast.Index; page++)
            if (page < sourceFirst.Index || page > sourceLast.Index)
                pages.Add(new PageRef(destinationFirst.Container, page));
        return pages;
    }

    PageRef? SecondPage(in ContainerOperation operation) => operation.Kind switch
    {
        ContainerOperationKind.Move or ContainerOperationKind.Split or ContainerOperationKind.Merge =>
            PageFor(operation.DestinationContainerOrOwn, operation.DestinationSlot),
        ContainerOperationKind.Craft when operation.DefinitionId != 0 =>
            PageFor(operation.DestinationContainerOrOwn, operation.DestinationSlot),
        _ => null,
    };

    /// <summary>The page a slot falls in, refused when the slot is outside the container's address space, which
    /// is checked HERE rather than left to the working copy so no write is reached with a slot it would refuse.</summary>
    PageRef PageFor(string container, int containerSlot)
    {
        IPagedContainerWorkingCopy copy = _containers[container];
        ArgumentOutOfRangeException.ThrowIfNegative(containerSlot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            containerSlot, copy.PageCount * ItemContainerPageCodec.ContainerPageSlots);
        return new PageRef(copy, ItemContainerPage.PageOf(containerSlot));
    }

    /// <summary>One page, named by its working copy and its index, because the working copy hands out no page
    /// object. Two refs name the same page only over the same working copy INSTANCE.</summary>
    readonly record struct PageRef(IPagedContainerWorkingCopy Container, int Index)
    {
        public bool IsDirty => Container.IsPageDirty(Index);

        public bool Is(in PageRef other) => ReferenceEquals(Container, other.Container) && Index == other.Index;
    }

    static int EntryBound(in ItemSlot slot)
        => slot.IsEmpty
            ? 0
            : ItemContainerPageCodec.EntryBodySize(
                slot.Quarantined ? ItemContainerPageCodec.EntryFlagQuarantined : 0u,
                slot.Stack.ItemId,
                slot.Stack.Count,
                slot.Stack.InstanceId,
                slot.Payload.Length);


}
