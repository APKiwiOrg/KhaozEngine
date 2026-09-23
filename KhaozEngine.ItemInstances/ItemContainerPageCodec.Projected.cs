using System;
using System.Buffers;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The viewer half: a whole page encoded for ONE viewer, which is what goes through the fragmenter when a
/// page delta abandons, on a cold open and on a correction resync.
/// </summary>
public static partial class ItemContainerPageCodec
{
    /// <summary>
    /// Encodes a whole page as one viewer may receive it, into a fresh array. This is the door for a page
    /// going to ANY viewer, the owner included: every payload goes through
    /// <see cref="ContainerPageProjection.ProjectPayload"/>, the same member <see cref="ContainerPageDelta"/>
    /// projects through, so a page and a delta handed the same entries carry the same bytes for each.
    /// <para>
    /// <b>The page is spec 4.4's format and decodes through <see cref="TryDecode"/> like any other.</b> Only
    /// the payloads differ from the stored page: a live one is the viewer's view of it, a plain stack carries
    /// nothing, and a quarantined one is a HOLLOW wrapper carrying the stored reason and stamp over no
    /// original bytes. The header, the flags, the definitions, the counts and the instance ids are the
    /// stored ones.
    /// </para>
    /// </summary>
    /// <param name="registry">The property kinds this process knows, which the projection reads.</param>
    /// <param name="viewerLevel">This viewer's clearance for these items:
    /// <see cref="PropertyVisibility.OwnerOnly"/> for the owner of the container,
    /// <see cref="PropertyVisibility.Everyone"/> for a trade, an inspect window or anyone else.</param>
    /// <param name="pageIndex">Which page of the container this is.</param>
    /// <param name="firstSlot">The container slot this page's slot 0 is.</param>
    /// <param name="slotCount">Slots in THIS page.</param>
    /// <param name="contentVersion">The page stamp.</param>
    /// <param name="entries">The page's OCCUPIED entries as stored, strictly ascending by slot, each built with
    /// <see cref="ContainerPageChange.Occupied"/> so it carries the identification state the projection
    /// needs. That is the same input the delta takes, so a host builds it once for either message. An
    /// <see cref="ContainerPageChange.Emptied"/> change is refused like any definition id of 0: a page
    /// carries a hole as the absence of an entry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ArgumentException">As <see cref="Encode(int,int,int,int,ReadOnlySpan{PageSlotInput})"/>
    /// over the projected entries, or an entry is flagged quarantined over bytes that are not a well formed
    /// wrapper, or the stored payloads alone total more than <see cref="MaxPageBytes"/>.</exception>
    public static byte[] EncodeProjected(
        InstancePropertyRegistry registry,
        PropertyVisibility viewerLevel,
        int pageIndex,
        int firstSlot,
        int slotCount,
        int contentVersion,
        ReadOnlySpan<ContainerPageChange> entries)
    {
        ArgumentNullException.ThrowIfNull(registry);
        long stored = 0;
        foreach (ContainerPageChange entry in entries) stored += entry.Entry.Payload.Length;
        if (stored > MaxPageBytes)
            throw new ArgumentException(
                $"the entries store {stored} payload bytes, over the {MaxPageBytes} byte page cap", nameof(entries));

        // ONE rented buffer for every view, sized by the stored payloads, because no projection is longer
        // than what it projects. Each view lands after the last, so the projected entries can borrow their
        // bytes from it until the page is written, which is where Encode copies them out.
        byte[] views = stored == 0 ? [] : ArrayPool<byte>.Shared.Rent((int)stored);
        PageSlotInput[] projected = entries.IsEmpty ? [] : ArrayPool<PageSlotInput>.Shared.Rent(entries.Length);
        try
        {
            int used = 0;
            for (int index = 0; index < entries.Length; index++)
            {
                ContainerPageChange entry = entries[index];
                int length = ContainerPageProjection.ProjectPayload(
                    registry, entry, viewerLevel, views.AsSpan(used));
                projected[index] = entry.Entry with { Payload = views.AsMemory(used, length) };
                used += length;
            }

            return Encode(pageIndex, firstSlot, slotCount, contentVersion, projected.AsSpan(0, entries.Length));
        }
        finally
        {
            if (projected.Length != 0) ArrayPool<PageSlotInput>.Shared.Return(projected, clearArray: true);
            if (views.Length != 0) ArrayPool<byte>.Shared.Return(views);
        }
    }
}
