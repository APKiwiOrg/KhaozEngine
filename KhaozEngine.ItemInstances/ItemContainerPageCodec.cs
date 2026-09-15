using System;
using System.Buffers.Binary;
using KhaozEngine.Catalog;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// Container codec VERSION 2, spec 4.4, byte for byte: a page of a container, sparse by slot, carrying an
/// instance id, an opaque payload and an entry flag set per occupied slot, under a header that declares
/// which page it is and which content version it was last brought up to date with.
/// <para>
/// <b>Byte 0 is the version dispatch.</b> The value 1 means the VERSION 1 format, which this type reads
/// through <see cref="ItemContainerCodec"/> and never writes, and anything else means a <c>ushort</c>
/// version whose low byte is that value. The rule and the one thing it costs are on
/// <see cref="ItemContainerCodec.Version"/>: container codec versions congruent to 1 modulo 256 are never
/// assigned.
/// </para>
/// <para>
/// Little endian through <see cref="BinaryPrimitives"/> with the endianness in the method name on BOTH
/// sides, which is contracts 15 and which fixes the asymmetry version 1 carries. <c>BitConverter</c> is
/// forbidden. Every varint is <see cref="ContentVarint"/>'s, unsigned and minimal, and the instance id is
/// an unsigned varint over the int64 bit pattern rather than a zig-zag, because
/// <c>InstanceIdAllocator.Pack(65535, counter)</c> is a negative <see cref="long"/>.
/// </para>
/// <para>
/// The decoder REFUSES rather than throws, the same door <see cref="ItemContainerCodec.TryDecode"/> is,
/// because the bytes come from a store or a peer. The encoder THROWS, because its caller has already
/// validated and a violation there is a caller bug.
/// </para>
/// </summary>
public static partial class ItemContainerPageCodec
{
    /// <summary>The format version this type writes, contracts 15's public <c>ushort</c> constant. It is
    /// <see cref="ItemContainerCodec.Version"/>, named again here so a page test pins the number it
    /// actually writes.</summary>
    public const ushort Version = ItemContainerCodec.Version;

    /// <summary>Slots in a full container page, spec 5.2. One hundred rather than 128, so slot 743 is page
    /// 7 slot 43 and an operator reading a section name can do the arithmetic in their head.</summary>
    public const int ContainerPageSlots = 100;

    /// <summary>Entry flags bit 0: the payload is a quarantine wrapper rather than an instance payload.
    /// Bits 1 to 31 are reserved and 0 in v1.</summary>
    public const uint EntryFlagQuarantined = 1u;

    /// <summary>
    /// The largest page this codec writes or reads: the journal's projection SECTION cap, because a page
    /// is written as one section.
    /// <para>
    /// The number is COPIED rather than referenced.
    /// <c>KhaozEngine.WorldStore/Journal/JournalLimits.cs:16</c> declares it as
    /// <c>EngineMaximumProjectionSectionBytes</c>, and <c>KhaozEngine.WorldStore</c> is a Server package
    /// while this one is Foundation, so no reference and therefore no test can hold the two equal. Moving
    /// one means moving the other by hand.
    /// </para>
    /// <para>
    /// Nothing realistic approaches it. Spec 5.4: a hundred entries at the maximum non-quarantined entry
    /// size is 53,209 bytes, and a page every one of whose entries quarantines a payload written under a
    /// raised cap is about 105,500, which is 5 percent of this number.
    /// </para>
    /// </summary>
    public const int MaxPageBytes = 2 * 1024 * 1024;

    const int MinimumPageBytes = 8; // version 2, page index 1, first slot 1, slot count 2, stamp 1, entry count 1

    /// <summary>The bytes <see cref="Encode(Span{byte},int,int,int,int,ReadOnlySpan{PageSlotInput})"/>
    /// would write, so a caller can size a buffer without writing.</summary>
    /// <param name="pageIndex">Which page of the container this is.</param>
    /// <param name="firstSlot">The container slot this page's slot 0 is.</param>
    /// <param name="slotCount">Slots in THIS page.</param>
    /// <param name="contentVersion">The page stamp.</param>
    /// <param name="entries">The occupied entries, ascending by slot.</param>
    public static int EncodedSize(
        int pageIndex, int firstSlot, int slotCount, int contentVersion, ReadOnlySpan<PageSlotInput> entries)
    {
        int size = 2
            + ContentVarint.Size((uint)pageIndex)
            + ContentVarint.Size((uint)firstSlot)
            + 2
            + ContentVarint.Size((uint)contentVersion)
            + ContentVarint.Size((uint)entries.Length);
        foreach (PageSlotInput entry in entries) size += EntrySize(entry, firstSlot);
        _ = slotCount;
        return size;
    }

    /// <summary>The bytes one entry costs, which is what the byte budgets of spec 3.8 are measured in.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="firstSlot">The page's first container slot, which the entry's slot is written relative to.</param>
    public static int EntrySize(in PageSlotInput entry, int firstSlot)
        => ContentVarint.Size((uint)(entry.Slot - firstSlot))
            + ContentVarint.Size(entry.Flags)
            + ContentVarint.Size((uint)entry.DefinitionId)
            + ContentVarint.Size((uint)entry.Count)
            + InstanceIdAllocator.SizeOf(entry.InstanceId)
            + ContentVarint.Size((uint)entry.Payload.Length)
            + entry.Payload.Length;

    /// <summary>Encodes a page into a fresh array.</summary>
    /// <param name="pageIndex">Which page of the container this is. 0 for a whole container.</param>
    /// <param name="firstSlot">The container slot this page's slot 0 is.</param>
    /// <param name="slotCount">Slots in THIS page.</param>
    /// <param name="contentVersion">The page stamp, the content version NUMBER (contracts 7.2).</param>
    /// <param name="entries">The occupied entries, strictly ascending by slot.</param>
    /// <exception cref="ArgumentException">An entry is out of order, names a slot outside the page, carries
    /// a non-positive definition id or count, or carries a non-quarantined payload over
    /// <see cref="ItemSlot.MaxPayloadBytes"/>, or the whole page would exceed <see cref="MaxPageBytes"/>.</exception>
    public static byte[] Encode(
        int pageIndex, int firstSlot, int slotCount, int contentVersion, ReadOnlySpan<PageSlotInput> entries)
    {
        byte[] page = new byte[EncodedSize(pageIndex, firstSlot, slotCount, contentVersion, entries)];
        Encode(page, pageIndex, firstSlot, slotCount, contentVersion, entries);
        return page;
    }

    /// <summary>Encodes a page into a caller's buffer and answers the bytes written.</summary>
    /// <param name="destination">At least <see cref="EncodedSize"/> bytes.</param>
    /// <param name="pageIndex">Which page of the container this is. 0 for a whole container.</param>
    /// <param name="firstSlot">The container slot this page's slot 0 is.</param>
    /// <param name="slotCount">Slots in THIS page.</param>
    /// <param name="contentVersion">The page stamp, the content version NUMBER (contracts 7.2).</param>
    /// <param name="entries">The occupied entries, strictly ascending by slot.</param>
    /// <exception cref="ArgumentException">As the array overload.</exception>
    public static int Encode(
        Span<byte> destination,
        int pageIndex,
        int firstSlot,
        int slotCount,
        int contentVersion,
        ReadOnlySpan<PageSlotInput> entries)
    {
        VetHeader(pageIndex, firstSlot, slotCount, contentVersion);
        VetEntries(firstSlot, slotCount, entries);
        int size = EncodedSize(pageIndex, firstSlot, slotCount, contentVersion, entries);
        if (size > MaxPageBytes)
            throw new ArgumentException(
                $"a page of {size} bytes is over the {MaxPageBytes} byte projection section cap", nameof(entries));

        BinaryPrimitives.WriteUInt16LittleEndian(destination, Version);
        int written = 2;
        written += ContentVarint.Write(destination[written..], (uint)pageIndex);
        written += ContentVarint.Write(destination[written..], (uint)firstSlot);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[written..], (ushort)slotCount);
        written += 2;
        written += ContentVarint.Write(destination[written..], (uint)contentVersion);
        written += ContentVarint.Write(destination[written..], (uint)entries.Length);
        foreach (PageSlotInput entry in entries) written += WriteEntry(destination[written..], entry, firstSlot);
        return written;
    }

    /// <summary>Writes one entry and answers the bytes written, so a page delta can reuse the entry shape
    /// without re-deriving it.</summary>
    /// <param name="destination">At least <see cref="EntrySize"/> bytes.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="firstSlot">The page's first container slot.</param>
    public static int WriteEntry(Span<byte> destination, in PageSlotInput entry, int firstSlot)
    {
        int written = ContentVarint.Write(destination, (uint)(entry.Slot - firstSlot));
        written += ContentVarint.Write(destination[written..], entry.Flags);
        written += ContentVarint.Write(destination[written..], (uint)entry.DefinitionId);
        written += ContentVarint.Write(destination[written..], (uint)entry.Count);
        written += InstanceIdAllocator.WriteId(destination[written..], entry.InstanceId);
        written += ContentVarint.Write(destination[written..], (uint)entry.Payload.Length);
        entry.Payload.Span.CopyTo(destination[written..]);
        return written + entry.Payload.Length;
    }

    /// <summary>
    /// Vets a stored page for a persistence layer. A non-null return is the refusal reason. An empty page
    /// is not a fault, exactly as an empty version 1 blob is not: it is a container nobody has stored yet.
    /// </summary>
    /// <param name="page">The stored bytes.</param>
    /// <param name="expectedPageSlots">The page geometry this consumer runs, which the page's own
    /// <c>FirstSlot</c> is checked against.</param>
    public static string? Validate(ReadOnlySpan<byte> page, int expectedPageSlots)
    {
        if (page.IsEmpty) return null;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedPageSlots);
        var entries = new PageEntry[expectedPageSlots];
        return TryDecode(page, expectedPageSlots, entries, out _, out _, out string? reason) ? null : reason;
    }

    static void VetHeader(int pageIndex, int firstSlot, int slotCount, int contentVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageIndex, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegative(firstSlot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstSlot, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slotCount, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegative(contentVersion);
    }

    static void VetEntries(int firstSlot, int slotCount, ReadOnlySpan<PageSlotInput> entries)
    {
        int previousSlot = -1;
        foreach (PageSlotInput entry in entries)
        {
            if (entry.Slot <= previousSlot)
                throw new ArgumentException(
                    $"page entries must be strictly ascending by slot, and slot {entry.Slot} follows {previousSlot}",
                    nameof(entries));
            previousSlot = entry.Slot;
            if (entry.Slot < firstSlot || entry.Slot >= firstSlot + slotCount)
                throw new ArgumentException(
                    $"slot {entry.Slot} is outside the page's {slotCount} slots from {firstSlot}", nameof(entries));
            if (entry.DefinitionId <= 0)
                throw new ArgumentException(
                    $"slot {entry.Slot} is an occupied entry with definition id {entry.DefinitionId}", nameof(entries));
            if (entry.Count <= 0)
                throw new ArgumentException(
                    $"slot {entry.Slot} carries count {entry.Count}, not positive", nameof(entries));

            // The two bounds of spec 4.4, which contradict on purpose. A NON-quarantined payload is capped
            // at MaxPayloadBytes. A QUARANTINED one is the wrapper, which by construction may be larger
            // than the cap the thing it preserves broke, so its bound is the page's own.
            if (!entry.Quarantined && entry.Payload.Length > ItemSlot.MaxPayloadBytes)
                throw new ArgumentException(
                    $"slot {entry.Slot} carries {entry.Payload.Length} payload bytes over the " +
                    $"{ItemSlot.MaxPayloadBytes} byte cap and is not quarantined", nameof(entries));
        }
    }
}
