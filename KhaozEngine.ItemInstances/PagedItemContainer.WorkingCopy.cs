using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The working copy half: this container as the <see cref="IPagedContainerWorkingCopy"/> a commit builder
/// writes through. Every member but the three page reads below is the container's own public one, so the
/// builder's writes land through the same doors a caller's do.
/// <para>
/// The page reads are explicit because <see cref="Pages"/> already answers them for a caller holding the
/// container. They exist for the builder, which is handed no page object at all.
/// </para>
/// </summary>
public sealed partial class PagedItemContainer : IPagedContainerWorkingCopy
{
    bool IPagedContainerWorkingCopy.IsPageDirty(int pageIndex) => PageAt(pageIndex).IsDirty;

    int IPagedContainerWorkingCopy.PageContentVersion(int pageIndex) => PageAt(pageIndex).ContentVersion;

    int IPagedContainerWorkingCopy.CopyPageEntriesTo(int pageIndex, Span<PageSlotInput> destination) =>
        PageAt(pageIndex).CopyEntriesTo(destination);

    ItemContainerPage PageAt(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _pages.Length);
        return _pages[pageIndex];
    }
}
