using System;
using System.Buffers.Binary;
using KhaozEngine.Catalog;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances;

/// <summary>The reading half: the byte 0 dispatch, the version 1 bridge and the version 2 decoder.</summary>
public static partial class ItemContainerPageCodec
{
    /// <summary>
    /// Reads a stored page. Refuses rather than throws: the bytes come from a store or a peer, so every
    /// failure answers false with a stable reason rather than an exception.
    /// <para>
    /// <b>Byte 0 dispatches.</b> The value 1 runs the version 1 path, which seats every entry with instance
    /// id 0, an empty payload and the quarantined flag clear, and takes page stamp 0. Anything else is a
    /// <c>ushort</c> version, which must be <see cref="Version"/>.
    /// </para>
    /// </summary>
    /// <param name="page">The stored bytes.</param>
    /// <param name="expectedPageSlots">The page geometry this consumer runs. <c>FirstSlot</c> must be
    /// <c>PageIndex</c> times this number, <c>SlotCount</c> may not exceed it, and a version 1 blob must
    /// declare exactly this many slots.</param>
    /// <param name="entries">Where the decoded entries go. A page declaring more than this holds is
    /// refused with <see cref="ItemContainerPageReason.EntryCount"/>.</param>
    /// <param name="header">The decoded header.</param>
    /// <param name="entryCount">How many entries were written into <paramref name="entries"/>.</param>
    /// <param name="reason">The refusal reason, null on success.</param>
    public static bool TryDecode(
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
        if (page.IsEmpty)
        {
            reason = ItemContainerPageReason.Truncated;
            return false;
        }

        if (page[0] == ItemContainerCodec.Version1)
            return TryDecodeVersion1(page, expectedPageSlots, entries, out header, out entryCount, out reason);

        if (page.Length < MinimumPageBytes)
        {
            reason = ItemContainerPageReason.Truncated;
            return false;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(page) != Version)
        {
            reason = ItemContainerPageReason.Version;
            return false;
        }

        int offset = 2;
        if (!ReadUInt16Field(page, ref offset, out int pageIndex, out reason)) return false;
        if (!ReadUInt16Field(page, ref offset, out int firstSlot, out reason)) return false;
        if (offset + 2 > page.Length)
        {
            reason = ItemContainerPageReason.Truncated;
            return false;
        }

        int slotCount = BinaryPrimitives.ReadUInt16LittleEndian(page[offset..]);
        offset += 2;
        if (!ReadInt32Field(page, ref offset, out int contentVersion, out reason)) return false;
        if (!ReadInt32Field(page, ref offset, out int declaredEntries, out reason)) return false;
        if (firstSlot != (long)pageIndex * expectedPageSlots)
        {
            reason = ItemContainerPageReason.SlotOrigin;
            return false;
        }

        // The origin check bounds where the page STARTS and says nothing about how far it runs, so a page
        // declaring 65,535 slots used to seat an entry at container slot 60,000 against a hundred slot
        // geometry. The bound is ONE SIDED rather than an equality, because a short last page is legal
        // (5.2) and this reader is not the one that decides whether it stays legal.
        if (slotCount > expectedPageSlots)
        {
            reason = ItemContainerPageReason.SlotOrigin;
            return false;
        }

        if (declaredEntries > entries.Length)
        {
            reason = ItemContainerPageReason.EntryCount;
            return false;
        }

        header = new PageHeader(pageIndex, firstSlot, slotCount, contentVersion, declaredEntries);
        if (!ReadEntries(page, ref offset, slotCount, firstSlot, declaredEntries, entries, out reason))
        {
            header = default;
            return false;
        }

        if (offset != page.Length)
        {
            header = default;
            reason = ItemContainerPageReason.TrailingBytes;
            return false;
        }

        entryCount = declaredEntries;
        return true;
    }

    static bool ReadEntries(
        ReadOnlySpan<byte> page,
        ref int offset,
        int slotCount,
        int firstSlot,
        int declaredEntries,
        Span<PageEntry> entries,
        out string? reason)
    {
        long previousSlot = -1;
        for (int index = 0; index < declaredEntries; index++)
        {
            if (!ReadUInt16Field(page, ref offset, out int slot, out reason)) return false;
            if (slot <= previousSlot)
            {
                reason = ItemContainerPageReason.SlotOrder;
                return false;
            }

            previousSlot = slot;
            if (slot >= slotCount)
            {
                reason = ItemContainerPageReason.EntryMalformed;
                return false;
            }

            if (!ContentVarint.TryRead(page, ref offset, out uint flags, out reason)) return false;
            if (!ReadInt32Field(page, ref offset, out int definitionId, out reason)) return false;
            if (!ReadInt32Field(page, ref offset, out int count, out reason)) return false;
            if (!InstanceIdAllocator.TryReadId(page, ref offset, out long instanceId, out reason)) return false;
            if (!ReadInt32Field(page, ref offset, out int payloadLength, out reason)) return false;
            if (definitionId == 0 || count == 0)
            {
                reason = ItemContainerPageReason.EntryMalformed;
                return false;
            }

            if (payloadLength > page.Length - offset)
            {
                reason = ItemContainerPageReason.Truncated;
                return false;
            }

            bool quarantined = (flags & EntryFlagQuarantined) != 0;
            if (!PayloadWithinBounds(page.Length, payloadLength, quarantined))
            {
                reason = InstancePayloadReason.PayloadTooLong;
                return false;
            }

            // The quarantined bound only caps from ABOVE. Spec 4.4 says a quarantined entry's payload IS
            // the wrapper and spec 12.4 pairs the two, so the flag over zero bytes is a shape no spec
            // defines: it preserves nothing, nothing can rescue it, and a reader that accepted it would
            // have to decide between seating it live, which clears the flag, and dropping the entry.
            if (quarantined && payloadLength == 0)
            {
                reason = ItemContainerPageReason.EntryMalformed;
                return false;
            }

            entries[index] = new PageEntry(
                firstSlot + slot, flags, definitionId, count, instanceId, offset, payloadLength);
            offset += payloadLength;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Spec 4.4's two bounds, which contradict on purpose and are settled in favour of the quarantine
    /// path. A NON-quarantined payload is capped at <see cref="ItemSlot.MaxPayloadBytes"/>. A QUARANTINED
    /// one is the wrapper, whose bound is the page's own: the projection section cap less the rest of the
    /// page. The exception is guarded by the entry's own flag rather than by sniffing the payload for the
    /// <c>KECQ</c> magic, because <c>K</c> is 0x4B and a perfectly legal property kind varint.
    /// </summary>
    static bool PayloadWithinBounds(int pageLength, int payloadLength, bool quarantined) =>
        quarantined
            ? payloadLength <= MaxPageBytes - (pageLength - payloadLength)
            : payloadLength <= ItemSlot.MaxPayloadBytes;

    /// <summary>Reads a field declared <c>varint uint16</c>, refusing a value that does not fit one.</summary>
    static bool ReadUInt16Field(ReadOnlySpan<byte> page, ref int offset, out int value, out string? reason)
    {
        value = 0;
        if (!ContentVarint.TryRead(page, ref offset, out uint raw, out reason)) return false;
        if (raw > ushort.MaxValue)
        {
            reason = ItemContainerPageReason.EntryMalformed;
            return false;
        }

        value = (int)raw;
        return true;
    }

    /// <summary>
    /// Reads a field declared <c>varint int32</c>. Those fields are written as UNSIGNED varints and are
    /// never zig-zagged (contracts 15), so a value above <see cref="int.MaxValue"/> is expressible and is
    /// refused AT THE DOOR rather than wrapped into a negative int.
    /// </summary>
    static bool ReadInt32Field(ReadOnlySpan<byte> page, ref int offset, out int value, out string? reason)
    {
        value = 0;
        if (!ContentVarint.TryRead(page, ref offset, out uint raw, out reason)) return false;
        if (raw > int.MaxValue)
        {
            reason = ItemContainerPageReason.EntryMalformed;
            return false;
        }

        value = (int)raw;
        return true;
    }

    /// <summary>
    /// The version 1 bridge. It runs the existing version 1 reader VERBATIM rather than a second copy of
    /// its rules, so all eleven of <see cref="ItemContainerCodec.Validate"/>'s refusals still bind,
    /// including the refusal of a blob whose declared slot count is not the caller's. That one is load
    /// bearing for a consumer whose bag-widening helpers exist precisely because of it.
    /// <para>
    /// A version 1 blob carries no stamp, so the page takes 0, which is older than every published version
    /// and therefore takes the FULL remap rule set on the first load (contracts 8.3). The first ordinary
    /// commit after that writes the page back as version 2, so a container migrates when a player touches
    /// it rather than through a migration pass.
    /// </para>
    /// <para>
    /// The array copy is the legacy path's one cost. The version 1 reader takes a <c>byte[]</c>, and the
    /// alternative is a second implementation of the rules the fixtures pin, which is exactly the refactor
    /// spec 4.5 forbids. It happens once per container, on a first load only.
    /// </para>
    /// </summary>
    static bool TryDecodeVersion1(
        ReadOnlySpan<byte> page,
        int expectedPageSlots,
        Span<PageEntry> entries,
        out PageHeader header,
        out int entryCount,
        out string? reason)
    {
        header = default;
        entryCount = 0;
        byte[] blob = page.ToArray();
        reason = ItemContainerCodec.Validate(blob, expectedPageSlots);
        if (reason is not null) return false;
        if (!ItemContainerCodec.TryDecode(blob, expectedPageSlots, NeverStacks, out ItemContainer decoded))
        {
            reason = ItemContainerPageReason.EntryMalformed;
            return false;
        }

        int count = 0;
        for (int slot = 0; slot < decoded.SlotCount; slot++)
        {
            ItemStack stack = decoded[slot];
            if (stack.IsEmpty) continue;
            if (count >= entries.Length)
            {
                reason = ItemContainerPageReason.EntryCount;
                return false;
            }

            entries[count++] = new PageEntry(slot, 0, stack.ItemId, stack.Count, 0, 0, 0);
        }

        header = new PageHeader(0, 0, expectedPageSlots, 0, count);
        entryCount = count;
        return true;
    }

    // The version 1 decode seats through SetAt, which never consults the stackable rule, so the predicate
    // the container is built with cannot change a single decoded byte. Answering false makes that explicit.
    static bool NeverStacks(int itemId) => false;
}
