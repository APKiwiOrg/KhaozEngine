using System;
using System.Buffers.Binary;

namespace KhaozEngine.Benchmarks.Items;

internal readonly record struct PageHeader(int PageIndex, int FirstSlot, int SlotCount, int ContentVersion, int EntryCount);

/// <summary>One decoded entry. Payload bounds point back into the page buffer, so a decode copies nothing.</summary>
internal readonly record struct PageEntry(
    int Slot,
    uint Flags,
    int DefinitionId,
    int Count,
    ulong InstanceId,
    int PayloadStart,
    int PayloadLength);

/// <summary>One entry on the way in. The payload is borrowed, never copied, until the page is written.</summary>
internal readonly record struct PageSlotInput(
    int Slot,
    uint Flags,
    int DefinitionId,
    int Count,
    ulong InstanceId,
    ReadOnlyMemory<byte> Payload);

/// <summary>
/// Container codec version 2, spec 4.4, byte for byte. Little endian through
/// <see cref="BinaryPrimitives"/> on both sides, which is contracts 15. Byte 0 is the legacy dispatch
/// byte: the value 1 would mean the version 1 format, and the spike only ever writes 2.
/// </summary>
internal static class ContainerPageCodec
{
    internal const ushort FormatVersion = 2;
    internal const int ContainerPageSlots = 100;
    internal const int MaximumEntries = 256;

    internal static string SectionName(string container, int pageIndex)
        => pageIndex < 100
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{container}/p{pageIndex:D2}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{container}/p{pageIndex}");

    internal static int EncodedSize(int pageIndex, int firstSlot, int slotCount, int contentVersion, ReadOnlySpan<PageSlotInput> entries)
    {
        int size = 2
            + Varint.Size((ulong)(uint)pageIndex)
            + Varint.Size((ulong)(uint)firstSlot)
            + 2
            + Varint.Size(contentVersion)
            + Varint.Size(entries.Length);
        foreach (PageSlotInput entry in entries) size += EntrySize(entry, firstSlot);
        _ = slotCount;
        return size;
    }

    internal static int EntrySize(in PageSlotInput entry, int firstSlot)
        => Varint.Size((ulong)(uint)(entry.Slot - firstSlot))
            + Varint.Size(entry.Flags)
            + Varint.Size(entry.DefinitionId)
            + Varint.Size(entry.Count)
            + Varint.Size(entry.InstanceId)
            + Varint.Size((ulong)(uint)entry.Payload.Length)
            + entry.Payload.Length;

    /// <summary>The entry body a page delta carries, spec 7.5: everything but the leading slot varint.</summary>
    internal static int DeltaEntryBodySize(in PageSlotInput entry, int firstSlot)
        => EntrySize(entry, firstSlot) - Varint.Size((ulong)(uint)(entry.Slot - firstSlot));

    internal static int Encode(
        Span<byte> destination,
        int pageIndex,
        int firstSlot,
        int slotCount,
        int contentVersion,
        ReadOnlySpan<PageSlotInput> entries)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, FormatVersion);
        int written = 2;
        written += Varint.Write(destination[written..], (ulong)(uint)pageIndex);
        written += Varint.Write(destination[written..], (ulong)(uint)firstSlot);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[written..], (ushort)slotCount);
        written += 2;
        written += Varint.Write(destination[written..], (ulong)(uint)contentVersion);
        written += Varint.Write(destination[written..], (ulong)(uint)entries.Length);
        foreach (PageSlotInput entry in entries) written += WriteEntry(destination[written..], entry, firstSlot);
        return written;
    }

    internal static int WriteEntry(Span<byte> destination, in PageSlotInput entry, int firstSlot)
    {
        int written = Varint.Write(destination, (ulong)(uint)(entry.Slot - firstSlot));
        written += WriteEntryBody(destination[written..], entry);
        return written;
    }

    internal static int WriteEntryBody(Span<byte> destination, in PageSlotInput entry)
    {
        int written = Varint.Write(destination, entry.Flags);
        written += Varint.Write(destination[written..], (ulong)(uint)entry.DefinitionId);
        written += Varint.Write(destination[written..], (ulong)(uint)entry.Count);
        written += Varint.Write(destination[written..], entry.InstanceId);
        written += Varint.Write(destination[written..], (ulong)(uint)entry.Payload.Length);
        entry.Payload.Span.CopyTo(destination[written..]);
        return written + entry.Payload.Length;
    }

    /// <summary>
    /// Refuses rather than throws, the same door <c>ItemContainerCodec.TryDecode</c> is. The redundant
    /// <c>FirstSlot</c> check of spec 4.4 runs here, which is what catches a page written into the
    /// wrong section.
    /// </summary>
    internal static bool TryDecode(
        ReadOnlySpan<byte> page,
        int expectedPageSlots,
        Span<PageEntry> entries,
        out PageHeader header,
        out int entryCount,
        out string? reason)
    {
        header = default;
        entryCount = 0;
        reason = null;
        if (page.Length < 8)
        {
            reason = "page-truncated";
            return false;
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(page);
        if (version != FormatVersion)
        {
            reason = "page-version";
            return false;
        }

        int offset = 2;
        if (!Varint.TryRead(page, ref offset, Varint.MaximumBytes32, out ulong pageIndex, out reason)) return false;
        if (!Varint.TryRead(page, ref offset, Varint.MaximumBytes32, out ulong firstSlot, out reason)) return false;
        if (offset + 2 > page.Length)
        {
            reason = "page-truncated";
            return false;
        }

        int slotCount = BinaryPrimitives.ReadUInt16LittleEndian(page[offset..]);
        offset += 2;
        if (!Varint.TryRead(page, ref offset, Varint.MaximumBytes32, out ulong contentVersion, out reason)) return false;
        if (!Varint.TryRead(page, ref offset, Varint.MaximumBytes32, out ulong declaredEntries, out reason)) return false;
        if (firstSlot != (ulong)((long)pageIndex * expectedPageSlots))
        {
            reason = "page-slot-origin";
            return false;
        }

        if (declaredEntries > (ulong)entries.Length)
        {
            reason = "page-entry-count";
            return false;
        }

        header = new PageHeader((int)pageIndex, (int)firstSlot, slotCount, (int)contentVersion, (int)declaredEntries);
        long previousSlot = -1;
        for (ulong index = 0; index < declaredEntries; index++)
        {
            if (!Varint.TryRead(page, ref offset, Varint.MaximumBytes32, out ulong slot, out reason)) return false;
            if ((long)slot <= previousSlot)
            {
                reason = "page-slot-order";
                return false;
            }

            previousSlot = (long)slot;
            if (!Varint.TryRead(page, ref offset, Varint.MaximumBytes32, out ulong flags, out reason)) return false;
            if (!Varint.TryRead(page, ref offset, Varint.MaximumBytes32, out ulong definitionId, out reason)) return false;
            if (!Varint.TryRead(page, ref offset, Varint.MaximumBytes32, out ulong count, out reason)) return false;
            if (!Varint.TryRead(page, ref offset, Varint.MaximumBytes64, out ulong instanceId, out reason)) return false;
            if (!Varint.TryRead(page, ref offset, Varint.MaximumBytes32, out ulong payloadLength, out reason)) return false;
            if (payloadLength > (ulong)(page.Length - offset))
            {
                reason = "page-truncated";
                return false;
            }

            if (definitionId == 0 || count == 0)
            {
                reason = "page-entry-malformed";
                return false;
            }

            entries[(int)index] = new PageEntry(
                (int)firstSlot + (int)slot,
                (uint)flags,
                (int)definitionId,
                (int)count,
                instanceId,
                offset,
                (int)payloadLength);
            offset += (int)payloadLength;
            entryCount++;
        }

        if (offset != page.Length)
        {
            reason = "page-trailing-bytes";
            return false;
        }

        return true;
    }
}
