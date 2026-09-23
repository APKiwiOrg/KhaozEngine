using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The ONE answer to "which payload bytes does this viewer receive for this stored entry", which every
/// container sync message writes through. <see cref="ContainerPageDelta.TryBuild"/> calls it for each
/// change and <see cref="ItemContainerPageCodec.EncodeProjected"/> calls it for each entry of a whole page,
/// so the delta and the page cannot come to disagree about what one viewer sees
/// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1049">#1049</see>).
/// <para>
/// <b>A live payload goes through <see cref="ItemInstanceVisibility.PublicView"/></b> at the viewer's level
/// for that item. A payload this process cannot project carries NO bytes, which is the fail-closed direction
/// an unregistered kind already takes, and the entry crosses as a live entry with an empty payload, which is
/// a shape spec 4.4 defines.
/// </para>
/// <para>
/// <b>A QUARANTINED entry crosses HOLLOW.</b> Its stored payload is a <c>KECQ</c> wrapper, whose original
/// bytes are unprojected by construction, so sending the wrapper would hand a viewer every field of the item
/// inside, server-only ones included
/// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/932">#932</see>). The flag over zero bytes is
/// not the other option either, because spec 4.4 says a quarantined entry's payload IS the wrapper and the
/// page codec refuses that shape at both doors. So the viewer receives a wrapper carrying the stored reason
/// and stamped version over an EMPTY original: it verifies, it seats, the item is visibly broken on the
/// client exactly as spec 12.3 wants, and the preserved bytes never leave the server.
/// </para>
/// </summary>
public static class ContainerPageProjection
{
    /// <summary>
    /// Writes the payload one viewer receives for one stored entry and answers the bytes written.
    /// </summary>
    /// <param name="registry">The property kinds this process knows, which the projection reads.</param>
    /// <param name="entry">The entry as stored, carrying its FULL payload, plus the identification state the
    /// projection needs. An <see cref="ContainerPageChange.Emptied"/> change carries no payload and
    /// answers 0.</param>
    /// <param name="viewerLevel">This viewer's clearance for this item:
    /// <see cref="PropertyVisibility.OwnerOnly"/> for its owner, <see cref="PropertyVisibility.Everyone"/>
    /// for anyone else.</param>
    /// <param name="destination">Where the bytes go. As long as the stored payload always suffices, because
    /// a view is never longer than what it filters and a hollow wrapper is never longer than the wrapper it
    /// hollows.</param>
    /// <returns>The bytes written: 0 for a plain stack and for a live payload that does not project, the
    /// projected view for a live payload, and the hollow wrapper for a quarantined one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the stored
    /// payload, or the entry is flagged quarantined over bytes that are not a well formed wrapper. Both are
    /// caller bugs: a container seats no such entry, and the page codec refuses the empty case at both of its
    /// doors.</exception>
    public static int ProjectPayload(
        InstancePropertyRegistry registry,
        in ContainerPageChange entry,
        PropertyVisibility viewerLevel,
        Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ReadOnlySpan<byte> payload = entry.Entry.Payload.Span;
        if (destination.Length < payload.Length)
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"slot {entry.Slot} stores {payload.Length} payload bytes and the destination holds {destination.Length}"),
                nameof(destination));

        if (entry.Entry.Quarantined) return Hollow(entry.Slot, payload, destination);
        if (payload.IsEmpty) return 0;

        int view = ItemInstanceVisibility.PublicView(
            registry, payload, viewerLevel, entry.Identified, entry.RevealedMask, destination);
        return view < 0 ? 0 : view;
    }

    /// <summary>The stored wrapper's reason and stamp over an empty original, which is everything a viewer
    /// may know about a quarantined item beyond the entry's own definition, count and instance id.</summary>
    static int Hollow(int slot, ReadOnlySpan<byte> wrapper, Span<byte> destination)
    {
        if (!QuarantineWrapper.TryUnwrap(wrapper, out _, out string? reason, out int stampedVersion) || reason is null)
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"slot {slot} is flagged quarantined over {wrapper.Length} bytes that are not a well formed wrapper"),
                "entry");

        return QuarantineWrapper.Wrap(reason, stampedVersion, ReadOnlySpan<byte>.Empty, destination);
    }
}
