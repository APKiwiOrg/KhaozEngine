using System;

namespace KhaozEngine.ItemInstances;

/// <summary>One decoded page header, spec 4.4's six leading fields.</summary>
/// <param name="PageIndex">Which page of the container this is. 0 for a whole container.</param>
/// <param name="FirstSlot">The container slot this page's slot 0 is. Redundant against
/// <paramref name="PageIndex"/> ON PURPOSE, which is what catches a page written into the wrong
/// section.</param>
/// <param name="SlotCount">Slots in THIS page, which is fewer than a full page on a container's last one.</param>
/// <param name="ContentVersion">The page stamp: the content version NUMBER this page was last brought up
/// to date with (contracts 7.2). A version 1 blob takes 0, which is older than every published version.</param>
/// <param name="EntryCount">Occupied entries, declared rather than derived, because version 2's entries
/// are variable length.</param>
public readonly record struct PageHeader(
    int PageIndex,
    int FirstSlot,
    int SlotCount,
    int ContentVersion,
    int EntryCount);

/// <summary>
/// One decoded entry. The payload is a WINDOW into the page buffer rather than a copy, so a decode
/// allocates nothing per entry and a caller that wants to keep the bytes copies them itself.
/// </summary>
/// <param name="Slot">The CONTAINER slot, already absolute: the decoder adds
/// <see cref="PageHeader.FirstSlot"/> to the relative slot the page stores.</param>
/// <param name="Flags">The entry flags. Bit 0 is quarantined, bits 1 to 31 are reserved and 0 in v1.</param>
/// <param name="DefinitionId">The content definition. Never 0 on an occupied entry.</param>
/// <param name="Count">How many. Always positive.</param>
/// <param name="InstanceId">The durable instance id, 0 for a plain stack.</param>
/// <param name="PayloadStart">Where the payload begins in the page buffer.</param>
/// <param name="PayloadLength">How many payload bytes.</param>
public readonly record struct PageEntry(
    int Slot,
    uint Flags,
    int DefinitionId,
    int Count,
    long InstanceId,
    int PayloadStart,
    int PayloadLength)
{
    /// <summary>Whether the payload is a quarantine wrapper rather than an instance payload, read from the
    /// entry's own flag rather than by sniffing the bytes for the <c>KECQ</c> magic: <c>K</c> is 0x4B,
    /// which is a perfectly legal property kind varint, so the sniff is ambiguous and this flag is what
    /// exists to avoid it.</summary>
    public bool Quarantined => (Flags & ItemContainerPageCodec.EntryFlagQuarantined) != 0;
}

/// <summary>One entry on the way IN. The payload is borrowed, never copied, until the page is written.</summary>
/// <param name="Slot">The CONTAINER slot, absolute. The encoder writes it relative to the page's first slot.</param>
/// <param name="Flags">The entry flags. Bit 0 is quarantined.</param>
/// <param name="DefinitionId">The content definition. Must be positive.</param>
/// <param name="Count">How many. Must be positive.</param>
/// <param name="InstanceId">The durable instance id, 0 for a plain stack.</param>
/// <param name="Payload">The canonical instance payload, or the quarantine wrapper when
/// <paramref name="Flags"/> sets bit 0. Empty for a plain stack.</param>
public readonly record struct PageSlotInput(
    int Slot,
    uint Flags,
    int DefinitionId,
    int Count,
    long InstanceId,
    ReadOnlyMemory<byte> Payload)
{
    /// <summary>Whether <see cref="Payload"/> is a quarantine wrapper, which is what lifts the
    /// <c>MaxPayloadBytes</c> bound off this entry.</summary>
    public bool Quarantined => (Flags & ItemContainerPageCodec.EntryFlagQuarantined) != 0;
}
