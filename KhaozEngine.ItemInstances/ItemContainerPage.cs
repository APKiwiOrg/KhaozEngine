using System;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// One page of a paged container, spec 5.3: the decoded slots, the content version stamp, the dirty flag
/// and the page index. A page is the unit a journal commit rewrites, which is the whole reason it exists
/// (spec 5.1).
/// <para>
/// <b>The geometry constant has ONE home and it is not here.</b>
/// <see cref="ItemContainerPageCodec.ContainerPageSlots"/> shipped with the codec in phase 1 and this type
/// references it rather than declaring a second copy, which is what
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/910">#910 item 5</see> asks for: the number
/// is durable (every section name and every page's <c>FirstSlot</c> check depends on it, spec 21), the
/// codec's goldens already pin it, and a second declaration is a second thing to move.
/// </para>
/// <para>
/// <b>Every page declares the FULL geometry, so a page is always
/// <see cref="ItemContainerPageCodec.ContainerPageSlots"/> slots wide.</b> That is the
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/916">#916</see> decision, taken for
/// <see cref="PagedItemContainer"/> and recorded there in full: spec 5.7's slot space is
/// <c>PageCount * ContainerPageSlots</c>, an ADDRESS space rather than a count of what fits, so spec 5.2's
/// 30 slot bag is one full page whose capacity gate is 30. A stored page declaring fewer slots is a codec
/// level anomaly for the load path to refuse rather than a shape this type can hold.
/// </para>
/// <para>
/// <b>Exactly two things dirty a page</b>, spec 5.3: an operation that CHANGED a slot
/// (<see cref="Write"/>, <see cref="Take"/>) and a remap that changed an id
/// (<see cref="ApplyRemap"/>). Nothing else does, and in particular reading one never does. Seating a
/// decoded page (<see cref="Seat"/>, <see cref="SeatStamp"/>) is not an operation either: it IS the page's
/// stored state, so a container that has only been loaded owes the journal nothing.
/// </para>
/// <para>
/// The slots live in an <see cref="ItemContainer"/> of exactly one page's width rather than in a second
/// array, so the payload doors and their four invariants (spec 4.7) are the SAME code a whole-container
/// consumer already runs rather than a copy that can come to disagree with it. This type never calls that
/// kernel's <c>Add</c>: stacking spans pages and belongs to <see cref="PagedItemContainer"/>.
/// </para>
/// </summary>
public sealed class ItemContainerPage
{
    readonly ItemContainer _slots;
    int _entryCount;

    /// <summary>Builds one empty page at stamp 0, which is older than every published content version and
    /// therefore takes the full remap rule set on its first load (contracts 8.3).</summary>
    /// <param name="pageIndex">Which page of the container this is, at most <see cref="MaxPageIndex"/>.</param>
    /// <param name="stackable">The game's rule for whether a definition merges into one slot, handed to the
    /// inner kernel so the two cannot differ. This type never consults it.</param>
    /// <param name="payloadCanonical">Whether an instance payload is canonical, the door check of spec 4.7.
    /// Left null, the page refuses every non-empty, non-quarantined payload, exactly as
    /// <see cref="ItemContainer"/> does.</param>
    /// <param name="quarantineWellFormed">Whether a quarantined slot's bytes are a well formed quarantine
    /// wrapper, which is <see cref="QuarantineWrapper.Verify(ReadOnlyMemory{byte})"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is negative or above
    /// <see cref="MaxPageIndex"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stackable"/> is null.</exception>
    public ItemContainerPage(
        int pageIndex,
        Func<int, bool> stackable,
        Func<ReadOnlyMemory<byte>, bool>? payloadCanonical = null,
        Func<ReadOnlyMemory<byte>, bool>? quarantineWellFormed = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageIndex, MaxPageIndex);
        ArgumentNullException.ThrowIfNull(stackable);

        PageIndex = pageIndex;
        FirstSlot = FirstSlotOf(pageIndex);
        _slots = new ItemContainer(
            ItemContainerPageCodec.ContainerPageSlots, stackable, payloadCanonical, quarantineWellFormed);
    }

    /// <summary>
    /// The largest page index a stored page can NAME. Container codec version 2 writes the page index and
    /// the first slot as <c>uint16</c> fields (spec 4.4), and the first slot is the page index times the
    /// geometry, so the first slot is what binds. Spec 5.4's 64 projection sections per stream is the real
    /// ceiling on a container and sits an order of magnitude below this one.
    /// </summary>
    public static int MaxPageIndex => ushort.MaxValue / ItemContainerPageCodec.ContainerPageSlots;

    /// <summary>Which page a container slot falls in: slot 743 is page 7.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="containerSlot"/> is negative.</exception>
    public static int PageOf(int containerSlot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(containerSlot);
        return containerSlot / ItemContainerPageCodec.ContainerPageSlots;
    }

    /// <summary>Where a container slot sits inside its page: slot 743 is slot 43 of page 7.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="containerSlot"/> is negative.</exception>
    public static int SlotWithin(int containerSlot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(containerSlot);
        return containerSlot % ItemContainerPageCodec.ContainerPageSlots;
    }

    /// <summary>The container slot a page's slot 0 is, which is the <c>FirstSlot</c> the page header
    /// declares and the decoder checks against the section it arrived in.</summary>
    /// <param name="pageIndex">The page index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is negative.</exception>
    public static int FirstSlotOf(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        return pageIndex * ItemContainerPageCodec.ContainerPageSlots;
    }

    /// <summary>Which page of the container this is.</summary>
    public int PageIndex { get; }

    /// <summary>The container slot this page's slot 0 is.</summary>
    public int FirstSlot { get; }

    /// <summary>Slots in this page, which is always the full geometry (the #916 decision above).</summary>
    public int SlotCount => ItemContainerPageCodec.ContainerPageSlots;

    /// <summary>The page stamp: the content version NUMBER this page was last brought up to date with
    /// (contracts 7.2). A version 1 blob takes 0. It is a number rather than a hash because a remap rule
    /// applies to a page whose stamp is OLDER than the rule's version, older is a comparison, and a digest
    /// has no order.</summary>
    public int ContentVersion { get; private set; }

    /// <summary>Whether this page owes the next commit a rewrite.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>How many slots this page holds an entry in. Entries are SPARSE, so this is never a bound on
    /// which slots are occupied.</summary>
    public int EntryCount => _entryCount;

    /// <summary>Whether a container slot falls in this page.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    public bool Holds(int containerSlot) => (uint)(containerSlot - FirstSlot) < (uint)SlotCount;

    /// <summary>One slot's whole state, addressed by its ABSOLUTE container slot. Reading never dirties the
    /// page. The payload is a window over bytes this page owns, so reading one allocates nothing and a
    /// caller holding one cannot change what the page holds.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is not in this page.</exception>
    public ItemSlot SlotAt(int containerSlot) => _slots.SlotAt(Relative(containerSlot));

    /// <summary>Seats a decoded slot: the LOAD path's door, which never dirties the page because the bytes
    /// it seats are the bytes the store already holds.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <param name="value">The decoded slot. Its payload bytes are copied.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is not in this page.</exception>
    /// <exception cref="ArgumentException">The slot breaks one of spec 4.7's four invariants.</exception>
    public void Seat(int containerSlot, ItemSlot value)
    {
        int relative = Relative(containerSlot);
        bool wasEmpty = _slots.SlotAt(relative).IsEmpty;
        _slots.SetSlotAt(relative, value);
        TrackOccupancy(wasEmpty, _slots.SlotAt(relative).IsEmpty);
    }

    /// <summary>Seats the decoded page stamp, which is the header's <c>ContentVersion</c>. The load path's
    /// door, so it never dirties the page either.</summary>
    /// <param name="contentVersion">The stamp the stored header declared.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="contentVersion"/> is negative.</exception>
    public void SeatStamp(int contentVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentVersion);
        ContentVersion = contentVersion;
    }

    /// <summary>
    /// Writes one slot as an OPERATION, and answers whether the page changed. A write that leaves the slot
    /// holding what it already held dirties nothing, which is why this asks the STORED state rather than
    /// trusting the caller's intent: the door sanitises, so a value and what it seats are not always the
    /// same thing.
    /// </summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <param name="value">The whole slot to seat. Its payload bytes are copied.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is not in this page.</exception>
    /// <exception cref="ArgumentException">The slot breaks one of spec 4.7's four invariants.</exception>
    public bool Write(int containerSlot, ItemSlot value)
    {
        int relative = Relative(containerSlot);
        ItemSlot before = _slots.SlotAt(relative);
        _slots.SetSlotAt(relative, value);
        ItemSlot after = _slots.SlotAt(relative);
        TrackOccupancy(before.IsEmpty, after.IsEmpty);
        if (before == after) return false;

        IsDirty = true;
        return true;
    }

    /// <summary>Empties one slot and answers what it held, which is an operation and dirties the page when
    /// the slot held anything.</summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is not in this page.</exception>
    public ItemSlot Take(int containerSlot)
    {
        int relative = Relative(containerSlot);
        ItemSlot taken = _slots.TakeSlotAt(relative);
        if (taken.IsEmpty) return taken;

        _entryCount--;
        IsDirty = true;
        return taken;
    }

    /// <summary>
    /// The member the remap pass calls, spec 5.5 step 2 and 3: it rewrites one entry and answers whether
    /// anything changed. A rule that changes nothing is a SCAN rather than a rewrite, which is almost every
    /// rule on almost every page, so the common answer is false and the page stays clean.
    /// <para>
    /// The stamp moves only when something changed, and only UPWARD. Moving it on a scan would leave a
    /// clean page claiming a version no stored byte carries, and the page is rewritten lazily (spec 5.6) so
    /// that claim would be lost on the next load anyway. Never lowering it is spec 5.5's policy for a page
    /// whose stamp is NEWER than the active version: that page is not an error and is never rewound, so it
    /// is already correct when the newer version returns.
    /// </para>
    /// </summary>
    /// <param name="containerSlot">The absolute container slot.</param>
    /// <param name="rewritten">The entry as the rule set left it, re-encoded rather than patched in place,
    /// because a replacement id can change a varint's width.</param>
    /// <param name="activeContentVersion">The content version the rule set brought this page up to.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is not in this page, or
    /// <paramref name="activeContentVersion"/> is negative.</exception>
    /// <exception cref="ArgumentException">The rewritten entry breaks one of spec 4.7's four invariants.</exception>
    public bool ApplyRemap(int containerSlot, ItemSlot rewritten, int activeContentVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeContentVersion);
        int relative = Relative(containerSlot);
        ItemSlot before = _slots.SlotAt(relative);
        _slots.SetSlotAt(relative, rewritten);
        ItemSlot after = _slots.SlotAt(relative);
        TrackOccupancy(before.IsEmpty, after.IsEmpty);
        if (before == after) return false;

        IsDirty = true;
        if (activeContentVersion > ContentVersion) ContentVersion = activeContentVersion;
        return true;
    }

    /// <summary>Clears the dirty flag, which the commit builder owes the page once the commit carrying it
    /// has landed.</summary>
    public void MarkClean() => IsDirty = false;

    /// <summary>
    /// Copies this page's occupied entries out in ascending slot order, ready for the codec, and answers
    /// how many there were. The slots are ABSOLUTE, which is what <see cref="PageSlotInput"/> takes, and
    /// the payloads are windows over this page's own bytes rather than copies.
    /// <para>
    /// A hole costs ZERO bytes here and downstream: it is the absence of an entry rather than an empty one,
    /// exactly as version 1's sparse form already had it, so nothing on this path can renumber a container
    /// by accident.
    /// </para>
    /// </summary>
    /// <param name="destination">At least <see cref="EntryCount"/> long.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public int CopyEntriesTo(Span<PageSlotInput> destination)
    {
        if (destination.Length < _entryCount)
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Page {PageIndex} holds {_entryCount} entries and the span holds {destination.Length}."),
                nameof(destination));

        int count = 0;
        for (int relative = 0; relative < SlotCount; relative++)
        {
            ItemSlot slot = _slots.SlotAt(relative);
            if (slot.IsEmpty) continue;

            destination[count++] = new PageSlotInput(
                FirstSlot + relative,
                slot.Quarantined ? ItemContainerPageCodec.EntryFlagQuarantined : 0u,
                slot.Stack.ItemId,
                slot.Stack.Count,
                slot.Stack.InstanceId,
                slot.Payload);
        }

        return count;
    }

    int Relative(int containerSlot)
    {
        if (!Holds(containerSlot))
            throw new ArgumentOutOfRangeException(
                nameof(containerSlot),
                containerSlot,
                FormattableString.Invariant(
                    $"Page {PageIndex} holds slots {FirstSlot} to {FirstSlot + SlotCount - 1}."));

        return containerSlot - FirstSlot;
    }

    void TrackOccupancy(bool wasEmpty, bool isEmpty)
    {
        if (wasEmpty == isEmpty) return;
        _entryCount += wasEmpty ? 1 : -1;
    }
}
