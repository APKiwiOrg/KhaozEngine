using System;
using System.Buffers.Binary;
using System.IO;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Pages;

/// <summary>
/// Container codec version 2, spec 4.4 byte for byte, plus budgets 2 and 3 of spec 3.8. The redundant
/// <c>FirstSlot</c> check, the explicit <c>EntryCount</c>, the trailing-byte refusal and the two payload
/// bounds are each a fact here, because each one exists to catch a specific silent failure.
/// </summary>
public class ItemContainerPageCodecTests
{
    const int PageSlots = ItemContainerPageCodec.ContainerPageSlots;

    /// <summary>Spec 16 budget 3's full page, checked in under <c>Pages/Goldens</c> and copied beside the
    /// assembly. Every other version 2 fact encodes and then decodes through the same pair, which cannot
    /// see a change that moves both sides together. This file can.</summary>
    const string HundredRares = "spec-3-8-poe-page-100-rares.bin";

    /// <summary>Spec 4.4's cap-raise page: one entry whose original payload broke
    /// <c>MaxInstancePayloadBytes</c> and whose wrapper is therefore larger than the cap it broke.</summary>
    const string QuarantinedOverCap = "spec-4-4-quarantined-over-cap.bin";

    /// <summary>The 3.8 PoE row's definition id, two varint bytes, which is the realistic width for a
    /// catalog of any size (4.4).</summary>
    const int PoeDefinitionId = 4200;

    /// <summary>The counter the 3.8 PoE row's instance id is written at, five varint bytes, which is the
    /// realistic width for a node prefixed id on a live shard. A hundred consecutive counters from here all
    /// stay five bytes, so every entry of the golden page is the same 69 bytes.</summary>
    const long PoeFirstCounter = 4_000_000_000;

    /// <summary>The golden page's stamp. One varint byte, which is what makes its header the eight bytes
    /// budget 3 counts.</summary>
    const int PageStamp = 100;

    static PageSlotInput Entry(int slot, int definitionId, int count, long instanceId, byte[]? payload = null, uint flags = 0) =>
        new(slot, flags, definitionId, count, instanceId, payload ?? Array.Empty<byte>());

    static byte[] Payload(int length, byte seed = 0x2A)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(seed + i);
        return bytes;
    }

    [Fact]
    public void A_page_round_trips_every_field_of_4_4()
    {
        PageSlotInput[] entries =
        [
            Entry(200, 995, 500, 0),
            Entry(203, 21_003, 1, InstanceIdAllocator.Pack(0, 4201), Payload(58)),
            Entry(299, 1, 1, InstanceIdAllocator.Pack(65535, 9), Payload(11), ItemContainerPageCodec.EntryFlagQuarantined),
        ];
        byte[] page = ItemContainerPageCodec.Encode(2, 200, PageSlots, 300, entries);

        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(page));
        Span<PageEntry> decoded = stackalloc PageEntry[PageSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(page, PageSlots, decoded, out PageHeader header, out int count, out string? reason));
        Assert.Null(reason);

        Assert.Equal(2, header.PageIndex);
        Assert.Equal(200, header.FirstSlot);
        Assert.Equal(PageSlots, header.SlotCount);
        Assert.Equal(300, header.ContentVersion);
        Assert.Equal(3, header.EntryCount);
        Assert.Equal(3, count);

        for (int i = 0; i < entries.Length; i++)
        {
            Assert.Equal(entries[i].Slot, decoded[i].Slot);
            Assert.Equal(entries[i].Flags, decoded[i].Flags);
            Assert.Equal(entries[i].DefinitionId, decoded[i].DefinitionId);
            Assert.Equal(entries[i].Count, decoded[i].Count);
            Assert.Equal(entries[i].InstanceId, decoded[i].InstanceId);
            Assert.Equal(entries[i].Payload.Length, decoded[i].PayloadLength);
            Assert.True(entries[i].Payload.Span.SequenceEqual(
                page.AsSpan(decoded[i].PayloadStart, decoded[i].PayloadLength)));
        }

        Assert.False(decoded[1].Quarantined);
        Assert.True(decoded[2].Quarantined);
        Assert.Null(ItemContainerPageCodec.Validate(page, PageSlots));
    }

    [Fact]
    public void A_FirstSlot_that_is_not_PageIndex_times_the_page_size_is_refused()
    {
        byte[] page = ItemContainerPageCodec.Encode(2, 199, PageSlots, 1, [Entry(199, 5, 1, 0)]);

        Span<PageEntry> decoded = stackalloc PageEntry[PageSlots];
        Assert.False(ItemContainerPageCodec.TryDecode(page, PageSlots, decoded, out _, out _, out string? reason));
        Assert.Equal(ItemContainerPageReason.SlotOrigin, reason);

        // The same page written into the RIGHT section reads fine, which is what makes the two byte
        // redundancy worth carrying.
        byte[] right = ItemContainerPageCodec.Encode(2, 200, PageSlots, 1, [Entry(200, 5, 1, 0)]);
        Assert.True(ItemContainerPageCodec.TryDecode(right, PageSlots, decoded, out _, out _, out _));
    }

    [Fact]
    public void Entries_out_of_ascending_slot_order_are_refused()
    {
        byte[] page = ItemContainerPageCodec.Encode(0, 0, PageSlots, 1, [Entry(0, 5, 1, 0), Entry(1, 6, 1, 0)]);

        // Header is eight bytes and each entry six, so the second entry's slot varint is byte 14. The
        // length assertion is what keeps that arithmetic honest if the header ever changes shape.
        Assert.Equal(20, page.Length);
        page[14] = 0;

        Span<PageEntry> decoded = stackalloc PageEntry[PageSlots];
        Assert.False(ItemContainerPageCodec.TryDecode(page, PageSlots, decoded, out _, out _, out string? reason));
        Assert.Equal(ItemContainerPageReason.SlotOrder, reason);
    }

    [Fact]
    public void A_blob_that_runs_out_of_bytes_before_EntryCount_is_refused()
    {
        byte[] page = ItemContainerPageCodec.Encode(0, 0, PageSlots, 1, [Entry(0, 5, 1, 0, Payload(20)), Entry(1, 6, 7, 0)]);

        Span<PageEntry> decoded = stackalloc PageEntry[PageSlots];
        for (int cut = 1; cut < page.Length; cut++)
        {
            Assert.False(
                ItemContainerPageCodec.TryDecode(page.AsSpan(0, cut), PageSlots, decoded, out _, out _, out string? reason),
                $"a page truncated to {cut} bytes decoded");
            Assert.NotNull(reason);
        }
    }

    [Fact]
    public void Trailing_bytes_after_the_last_entry_are_refused()
    {
        byte[] page = ItemContainerPageCodec.Encode(0, 0, PageSlots, 1, [Entry(0, 5, 1, 0)]);
        byte[] padded = [.. page, (byte)0];

        Span<PageEntry> decoded = stackalloc PageEntry[PageSlots];
        Assert.False(ItemContainerPageCodec.TryDecode(padded, PageSlots, decoded, out _, out _, out string? reason));
        Assert.Equal(ItemContainerPageReason.TrailingBytes, reason);
    }

    [Fact]
    public void A_non_quarantined_entry_above_MaxInstancePayloadBytes_answers_payload_oversize()
    {
        byte[] oversize = Payload(ItemSlot.MaxPayloadBytes + 1);

        // The encoder refuses it outright, because a writer holding an oversize payload it has not
        // quarantined is a caller bug rather than bad data.
        Assert.Throws<ArgumentException>(() =>
            ItemContainerPageCodec.Encode(0, 0, PageSlots, 1, [Entry(0, 5, 1, 7, oversize)]));

        // The decoder has to answer for it as well, because the bytes can arrive from a store. Write it
        // quarantined, then clear the flag in the blob: the entry's flags varint is byte 9.
        byte[] page = ItemContainerPageCodec.Encode(
            0, 0, PageSlots, 1, [Entry(0, 5, 1, 7, oversize, ItemContainerPageCodec.EntryFlagQuarantined)]);
        Assert.Equal((byte)ItemContainerPageCodec.EntryFlagQuarantined, page[9]);
        page[9] = 0;

        Span<PageEntry> decoded = stackalloc PageEntry[PageSlots];
        Assert.False(ItemContainerPageCodec.TryDecode(page, PageSlots, decoded, out _, out _, out string? reason));
        Assert.Equal(InstancePayloadReason.PayloadTooLong, reason);
    }

    [Fact]
    public void A_QUARANTINED_entry_above_MaxInstancePayloadBytes_encodes_and_decodes()
    {
        // The cap-raise case spec 4.4 exists for. Engine 20.x raises the cap, a six socket item reaches
        // 700 bytes, a shard still on 19.x loads the page, the entry quarantines, and the wrapper is about
        // 711 bytes. It HAS to be writable or the page cannot be re-encoded and the container becomes
        // uncommittable. One oversize item must not cost a player their bank.
        byte[] wrapped = Payload(711);
        byte[] page = ItemContainerPageCodec.Encode(
            0, 0, PageSlots, 1, [Entry(0, 5, 1, 7, wrapped, ItemContainerPageCodec.EntryFlagQuarantined)]);

        Span<PageEntry> decoded = stackalloc PageEntry[PageSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(page, PageSlots, decoded, out _, out int count, out string? reason));
        Assert.Null(reason);
        Assert.Equal(1, count);
        Assert.True(decoded[0].Quarantined);
        Assert.Equal(711, decoded[0].PayloadLength);
        Assert.True(wrapped.AsSpan().SequenceEqual(page.AsSpan(decoded[0].PayloadStart, decoded[0].PayloadLength)));

        // Checked in, because an encode-then-decode pair cannot see a change that moves both sides
        // together, and this is the one page shape whose whole reason to exist is that it survives a
        // reader that would refuse the payload inside it.
        Assert.Equal(Golden(QuarantinedOverCap), page);
    }

    [Fact]
    public void An_OSRS_slot_entry_is_seven_bytes_against_version_1s_fixed_ten()
    {
        // Spec 3.8's floor row: 500 coins, definition id 1, no payload, instance id 0. Slot 1, flags 1,
        // definition 1, count 2, instance 1, payload length 1.
        Assert.Equal(7, ItemContainerPageCodec.EntrySize(Entry(3, 1, 500, 0), firstSlot: 0));
        Assert.Equal(10, 2 + 4 + 4);
    }

    [Fact]
    public void A_full_page_of_100_rares_is_budget_3s_6908_bytes()
    {
        // Spec 16 budget 3, reproduced rather than restated. The payload is the CHECKED IN 3.8 PoE golden
        // itself rather than filler of the same length, so the page cannot drift from the payload row its
        // budget is derived from. Each entry is slot 1, flags 1, definition id 2, count 1, instance id 5,
        // payload length 1 and payload 58, which is 4.4's 69, and the header is version 2, page index 1,
        // first slot 1, slot count 2, stamp 1 and entry count 1, which is 8. 8 + 100 * 69 = 6,908.
        byte[] payload = PayloadGolden("spec-3-8-poe-greatsword.bin");
        Assert.Equal(58, payload.Length);

        var entries = new PageSlotInput[PageSlots];
        for (int i = 0; i < PageSlots; i++)
            entries[i] = Entry(i, PoeDefinitionId, 1, InstanceIdAllocator.Pack(0, PoeFirstCounter + i), payload);
        Assert.All(entries, entry => Assert.Equal(69, ItemContainerPageCodec.EntrySize(entry, firstSlot: 0)));

        byte[] golden = Golden(HundredRares);
        byte[] page = ItemContainerPageCodec.Encode(0, 0, PageSlots, PageStamp, entries);

        Assert.Equal(8 + (100 * 69), golden.Length);
        Assert.Equal(6908, golden.Length);
        Assert.Equal(golden, page);
        Assert.Equal(golden.Length, ItemContainerPageCodec.EncodedSize(0, 0, PageSlots, PageStamp, entries));
        Assert.True(page.Length < 8 * 1024, $"a full page of rares is {page.Length} bytes");

        Span<PageEntry> decoded = new PageEntry[PageSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(golden, PageSlots, decoded, out _, out int count, out _));
        Assert.Equal(PageSlots, count);
    }

    [Fact]
    public void A_declared_count_above_int_MaxValue_is_refused_at_the_door()
    {
        // Contracts 15 and issue 905: ContentVersion, EntryCount and the other varint int32 fields are
        // UNSIGNED varints, so a value above int.MaxValue is expressible and has to be refused rather than
        // wrapped into a negative int.
        Span<byte> buffer = stackalloc byte[32];
        int written = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, ItemContainerPageCodec.Version);
        written += ContentVarint.Write(buffer[written..], 0);            // PageIndex
        written += ContentVarint.Write(buffer[written..], 0);            // FirstSlot
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[written..], (ushort)PageSlots);
        written += 2;
        written += ContentVarint.Write(buffer[written..], uint.MaxValue); // ContentVersion, over int.MaxValue
        written += ContentVarint.Write(buffer[written..], 0);             // EntryCount

        Span<PageEntry> decoded = stackalloc PageEntry[PageSlots];
        Assert.False(ItemContainerPageCodec.TryDecode(buffer[..written], PageSlots, decoded, out _, out _, out string? reason));
        Assert.Equal(ItemContainerPageReason.EntryMalformed, reason);
    }

    [Fact]
    public void An_entry_count_the_callers_buffer_cannot_hold_is_refused()
    {
        byte[] page = ItemContainerPageCodec.Encode(0, 0, PageSlots, 1, [Entry(0, 5, 1, 0), Entry(1, 6, 1, 0)]);

        Span<PageEntry> tooSmall = stackalloc PageEntry[1];
        Assert.False(ItemContainerPageCodec.TryDecode(page, PageSlots, tooSmall, out _, out _, out string? reason));
        Assert.Equal(ItemContainerPageReason.EntryCount, reason);
    }

    [Fact]
    public void An_occupied_entry_with_no_definition_or_no_count_is_refused()
    {
        Assert.Throws<ArgumentException>(() => ItemContainerPageCodec.Encode(0, 0, PageSlots, 1, [Entry(0, 0, 1, 0)]));
        Assert.Throws<ArgumentException>(() => ItemContainerPageCodec.Encode(0, 0, PageSlots, 1, [Entry(0, 5, 0, 0)]));

        byte[] page = ItemContainerPageCodec.Encode(0, 0, PageSlots, 1, [Entry(0, 5, 1, 0)]);
        Assert.Equal((byte)5, page[10]);
        page[10] = 0;

        Span<PageEntry> decoded = stackalloc PageEntry[PageSlots];
        Assert.False(ItemContainerPageCodec.TryDecode(page, PageSlots, decoded, out _, out _, out string? reason));
        Assert.Equal(ItemContainerPageReason.EntryMalformed, reason);
    }

    static byte[] Golden(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Pages", "Goldens", name));

    /// <summary>Reads a PAYLOAD golden, so a page fact can be built out of the payload bytes the payload
    /// facts already pin rather than out of a second copy of them.</summary>
    static byte[] PayloadGolden(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Payload", "Goldens", name));

    [Fact]
    public void The_seven_page_reasons_are_a_closed_set()
    {
        Assert.Equal(7, ItemContainerPageReason.All.Count);
        Assert.All(ItemContainerPageReason.All, token => Assert.StartsWith("page-", token, StringComparison.Ordinal));
        Assert.Equal(ItemContainerPageReason.All.Count, new System.Collections.Generic.HashSet<string>(ItemContainerPageReason.All, StringComparer.Ordinal).Count);
    }
}
