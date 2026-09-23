using System;
using System.Buffers;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// One changed slot on the way into a page delta: the entry the page would store, plus the identification
/// state the per-viewer projection needs. A change to an EMPTIED slot is
/// <see cref="Emptied"/>, which is the one case carrying no entry at all.
/// </summary>
/// <param name="Entry">The entry as the page codec would write it, carrying the item's FULL payload. The
/// delta writes the part this viewer may see, so the bytes handed in here are the stored ones and never a
/// view somebody else already filtered.</param>
/// <param name="Identified">Whether the item is identified, which is kind 128's state byte. The gate needs
/// it because a gated field is withheld from the owner too (spec 12.7).</param>
/// <param name="RevealedMask">Kind 128's revealed mask, which un-gates individual kinds on a partially
/// identified item.</param>
public readonly record struct ContainerPageChange(PageSlotInput Entry, bool Identified, ulong RevealedMask)
{
    /// <summary>A slot that is now EMPTY, which the delta writes as its slot varint and a single 0x00. A
    /// definition id of 0 is what marks it, the same value spec 4.4 forbids on an occupied entry.</summary>
    /// <param name="slot">The CONTAINER slot, absolute. The delta writes it relative to the page's first slot.</param>
    public static ContainerPageChange Emptied(int slot) =>
        new(new PageSlotInput(slot, 0, 0, 0, 0, default), Identified: false, RevealedMask: 0);

    /// <summary>A slot that now holds something, which the delta writes as its slot varint, a 0x01 and the
    /// entry body of spec 4.4 without its slot field, projected for the viewer.</summary>
    /// <param name="entry">The entry the page would store, carrying the FULL payload.</param>
    /// <param name="identified">Whether the item is identified.</param>
    /// <param name="revealedMask">Kind 128's revealed mask.</param>
    public static ContainerPageChange Occupied(in PageSlotInput entry, bool identified, ulong revealedMask) =>
        new(entry, identified, revealedMask);

    /// <summary>The CONTAINER slot this change names.</summary>
    public int Slot => Entry.Slot;

    /// <summary>Whether the slot is now empty, which is a definition id of 0.</summary>
    public bool IsEmptied => Entry.DefinitionId == 0;
}

/// <summary>
/// Spec 7.5's page delta: the one frame message that says which slots of one page changed and what they hold
/// now, so a craft costs 73 bytes rather than a 6.9 KB page.
/// <code>
/// [ContainerId: byte][PageIndex: byte][ChangedCount: byte]
/// [ per change: [Slot: varint uint16] then either
///               [0x00] for "now empty"
///               or [0x01] then the entry body of 4.4 without its Slot field ]
/// </code>
/// <para>
/// <b>It MEASURES as it writes and ABANDONS rather than truncates.</b> Nothing bounds how many slots one
/// operation changes, and a sort over a hundred slot page produces a delta many times the frame cap. That is
/// not a truncated message, it is a THROW: the game message encoder throws above its cap, the delta is sent
/// from inside the per-viewer serve loop, and nothing in the netcode catches around it, so an unmeasured
/// delta takes the tick down for every player on the server. When the next change would not fit,
/// <see cref="TryBuild"/> answers -1 and the caller sends the WHOLE PAGE through the fragmenter. Never a
/// second delta frame: two deltas for one page would have to be applied in order by a client that may have
/// missed the first, which is the reassembly problem the fragmenter already solves once.
/// </para>
/// <para>
/// <b>The bodies are PER VIEWER.</b> There is one door and it projects, so a caller cannot build a delta that
/// skipped the filter: every payload goes through
/// <see cref="ContainerPageProjection.ProjectPayload"/>, which is
/// <see cref="ItemInstanceVisibility.PublicView"/> at the viewer's level for that item, and an owner-only
/// field reaches the owner and nobody else (spec 7.4). A payload this process cannot project carries NO
/// bytes, which is the same fail-closed direction an unregistered kind takes. The whole page this delta
/// falls back to is <see cref="ItemContainerPageCodec.EncodeProjected"/>, which projects through the SAME
/// member, so the two cannot disagree about what one viewer sees.
/// </para>
/// <para>
/// <b>A QUARANTINED entry abandons the delta</b> rather than riding it. A quarantine wrapper is not a
/// canonical payload and never decodes, so the projection has nothing to hand back, and writing the entry
/// anyway produced a shape no spec defines: the quarantined flag over a payload of zero bytes, which spec
/// 4.4 contradicts (a quarantined entry's payload IS the wrapper), which spec 12.4 pairs with a wrapper, and
/// which <c>ItemContainer</c> refuses to seat. Sending the wrapper's own bytes is not the other option
/// either: they are unprojected by construction, so a non-owner would receive the owner-only fields of the
/// item inside (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/932">#932</see>). Abandoning is
/// rule 4 of this same message, already written for the case where the next change does not fit, and the
/// caller sends the whole page through the fragmenter, encoded by
/// <see cref="ItemContainerPageCodec.EncodeProjected"/>, which carries the entry HOLLOW.
/// </para>
/// <para>
/// <b>What the CLIENT does with one, which this message leans on.</b> A client REFUSES a delta for a page it
/// has not fully received and asks for a full page sync instead
/// (<see cref="ContainerPageSyncRequest"/>), so a delta can never be applied to bytes the client guessed at.
/// On the last chunk of a fragmented page the assembled bytes go through the SAME decoder the server encoded
/// with, <see cref="ItemContainerPageCodec.TryDecode"/>, and a failure quarantines rather than throwing: a
/// client that trusted its own reassembly would draw a bank from bytes nothing validated.
/// </para>
/// <para>
/// <b>The frame cap is COPIED, not referenced.</b> <c>TileProtocol.MaxGameMessageBytes</c> lives in
/// <c>KhaozEngine.TileWorld.Netcode</c>, a Server package, and this one is Foundation, so the edge cannot
/// exist in this direction and spec 2.2 forbids it in the other. <c>PageSyncFrameBoundTests</c> in
/// <c>KhaozEngine.TileWorld.Netcode.Tests</c> is the one place that sees both and holds the copy equal.
/// </para>
/// </summary>
public static class ContainerPageDelta
{
    /// <summary>The game message payload cap this delta is sized against, copied from
    /// <c>TileProtocol.MaxGameMessageBytes</c>. See the type doc for why it is a copy.</summary>
    public const int MaxGameMessageBytes = 1024;

    /// <summary>The game message envelope, <c>[tag:1][kind:2][flags:1]</c>, copied from
    /// <c>TileProtocol</c>'s own <c>GameMessageHeader</c>.
    /// <para>Subtracting it is what makes the delta fit a 1,024 byte DATAGRAM rather than a 1,024 byte
    /// payload, which is four bytes tighter than the encoder's own throw needs
    /// (KhaozEngine issue 923). Spec 7.5 budgets it this way, so this does too, and the four bytes are cheap
    /// insurance on a path whose failure mode is the whole tick.</para></summary>
    public const int GameMessageEnvelopeBytes = 4;

    /// <summary>The delta's own header: container id, page index, changed count.</summary>
    public const int HeaderBytes = 3;

    /// <summary>The most changes one delta can name, because <c>ChangedCount</c> is a byte. A page of
    /// <see cref="ItemContainerPageCodec.ContainerPageSlots"/> slots is under it either way, and a caller
    /// handing more gets an abandoned delta rather than a count that wrapped.</summary>
    public const int MaxChanges = 255;

    /// <summary>The bytes of CHANGES one delta may carry: the frame cap less the envelope less the header.
    /// Spec 7.5's 1,017.</summary>
    public const int MaxChangeBytes = MaxGameMessageBytes - GameMessageEnvelopeBytes - HeaderBytes;

    /// <summary>The largest delta this builder ever writes, header included, which is what a caller sizes its
    /// buffer with.</summary>
    public const int MaxBytes = HeaderBytes + MaxChangeBytes;

    /// <summary>
    /// Builds one viewer's delta for one page and answers the bytes written, or <c>-1</c> when it would not
    /// fit one frame and the caller should send the whole page through the fragmenter instead.
    /// </summary>
    /// <param name="destination">Where the delta is written. <see cref="MaxBytes"/> always suffices, and a
    /// shorter buffer simply lowers the budget: the builder measures against the LESSER of
    /// <see cref="MaxBytes"/> and what this holds, so a short buffer abandons early rather than overruns.
    /// <para>On <c>-1</c> the contents are UNSPECIFIED. The builder measures as it writes rather than sizing
    /// twice, so an abandoned delta leaves the bytes it had written by then, and a caller that sends anyway
    /// sends a truncated message. Answer -1, send the page.</para></param>
    /// <param name="registry">The property kinds this process knows, which the projection reads.</param>
    /// <param name="viewerLevel">This viewer's clearance for these items:
    /// <see cref="PropertyVisibility.OwnerOnly"/> for the owner of the container,
    /// <see cref="PropertyVisibility.Everyone"/> for anyone else. A container page normally goes to its
    /// owner, and the level is a parameter because a trade or an inspect window is the same message to a
    /// viewer who is not.</param>
    /// <param name="containerId">Which container, the GAME's number for it.</param>
    /// <param name="pageIndex">Which page of that container.</param>
    /// <param name="firstSlot">The container slot this page's slot 0 is. Every change's slot is written
    /// relative to it.</param>
    /// <param name="slotCount">Slots in THIS page, which is fewer than a full page on a container's last one.
    /// It bounds the slots a change may name and is not written: the page the client applies this to already
    /// declares its own geometry (spec 4.4).</param>
    /// <param name="changes">The changed slots, STRICTLY ASCENDING by slot. Ascending because the client
    /// applies them in one pass and a slot named twice in one delta is ambiguous, which is the same reason
    /// spec 4.4 orders a page's entries.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstSlot"/> is negative or
    /// <paramref name="slotCount"/> is not positive.</exception>
    /// <exception cref="ArgumentException">A change is out of order or names a slot outside the page. Both
    /// are caller bugs rather than remote frames, so both get a stack trace, exactly as the page encoder's
    /// are.</exception>
    public static int TryBuild(
        Span<byte> destination,
        InstancePropertyRegistry registry,
        PropertyVisibility viewerLevel,
        byte containerId,
        byte pageIndex,
        int firstSlot,
        int slotCount,
        ReadOnlySpan<ContainerPageChange> changes)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfNegative(firstSlot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        int longestPayload = VetChanges(firstSlot, slotCount, changes, out bool quarantined);

        int budget = Math.Min(destination.Length, MaxBytes);
        if (quarantined || changes.Length > MaxChanges || budget < HeaderBytes)
        {
            return -1;
        }

        destination[0] = containerId;
        destination[1] = pageIndex;
        destination[2] = (byte)changes.Length;
        int written = HeaderBytes;

        // ONE rented buffer for the whole delta, sized by the longest payload in it, because a view is never
        // longer than what it filters. A projection cannot write straight into the destination: its length is
        // what the entry body's length prefix declares, and the prefix goes first.
        byte[]? scratch = longestPayload == 0 ? null : ArrayPool<byte>.Shared.Rent(longestPayload);
        try
        {
            foreach (ContainerPageChange change in changes)
            {
                int slotCost = ContentVarint.Size((uint)(change.Slot - firstSlot));
                if (change.IsEmptied)
                {
                    if (written + slotCost + 1 > budget)
                    {
                        return -1;
                    }

                    written += ContentVarint.Write(destination[written..], (uint)(change.Slot - firstSlot));
                    destination[written++] = 0x00;
                    continue;
                }

                int viewBytes = ContainerPageProjection.ProjectPayload(registry, change, viewerLevel, scratch);
                ReadOnlySpan<byte> view = scratch.AsSpan(0, viewBytes);
                PageSlotInput entry = change.Entry;
                int cost = slotCost + 1 + ItemContainerPageCodec.EntryBodySize(
                    entry.Flags, entry.DefinitionId, entry.Count, entry.InstanceId, view.Length);
                if (written + cost > budget)
                {
                    return -1;
                }

                written += ContentVarint.Write(destination[written..], (uint)(change.Slot - firstSlot));
                destination[written++] = 0x01;
                written += ItemContainerPageCodec.WriteEntryBody(
                    destination[written..], entry.Flags, entry.DefinitionId, entry.Count, entry.InstanceId, view);
            }
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        return written;
    }

    /// <summary>Vets the change set and answers the longest payload in it, which sizes the one scratch
    /// buffer the projection needs, plus whether any change carries a quarantined entry, which abandons the
    /// delta before a byte is written.</summary>
    static int VetChanges(
        int firstSlot,
        int slotCount,
        ReadOnlySpan<ContainerPageChange> changes,
        out bool quarantined)
    {
        int longestPayload = 0;
        int previousSlot = -1;
        quarantined = false;
        foreach (ContainerPageChange change in changes)
        {
            quarantined |= change.Entry.Quarantined;
            if (change.Slot <= previousSlot)
                throw new ArgumentException(
                    $"delta changes must be strictly ascending by slot, and slot {change.Slot} follows {previousSlot}",
                    nameof(changes));
            previousSlot = change.Slot;
            if (change.Slot < firstSlot || change.Slot >= firstSlot + slotCount)
                throw new ArgumentException(
                    $"slot {change.Slot} is outside the page's {slotCount} slots from {firstSlot}", nameof(changes));
            if (change.Entry.Payload.Length > longestPayload) longestPayload = change.Entry.Payload.Length;
        }

        return longestPayload;
    }
}
