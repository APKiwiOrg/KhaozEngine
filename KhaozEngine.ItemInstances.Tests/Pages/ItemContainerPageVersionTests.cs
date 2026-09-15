using System;
using System.Buffers.Binary;
using System.IO;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Pages;

/// <summary>
/// Spec 17 row 4 over spec 4.5, the half that needs the version 2 page reader: a stored version 1 blob
/// read THROUGH it, unchanged, and a version 2 writer that never produces one.
/// <para>
/// The blobs are the same checked-in fixtures <c>KhaozEngine.Foundation.Tests</c> pins, linked in rather
/// than copied, so there is one set of version 1 bytes in the repository and both suites read it.
/// </para>
/// </summary>
public class ItemContainerPageVersionTests
{
    const int BagSlots = 28;

    public static TheoryData<string> Fixtures() => new()
    {
        "container-v1-empty.blob",
        "container-v1-one-stack.blob",
        "container-v1-multi.blob",
        "container-v1-full-bag.blob",
    };

    static byte[] Load(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Items", "Fixtures", name));

    static bool Stackable(int itemId) => true;

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A_version_1_blob_decodes_through_the_version_2_reader_unchanged(string name)
    {
        byte[] stored = Load(name);
        Assert.True(ItemContainerCodec.TryDecode(stored, BagSlots, Stackable, out ItemContainer direct));

        Span<PageEntry> entries = stackalloc PageEntry[BagSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(stored, BagSlots, entries, out PageHeader header, out int count, out string? reason));
        Assert.Null(reason);

        int seen = 0;
        for (int slot = 0; slot < BagSlots; slot++)
        {
            ItemStack stack = direct[slot];
            if (stack.IsEmpty) continue;
            Assert.Equal(slot, entries[seen].Slot);
            Assert.Equal(stack.ItemId, entries[seen].DefinitionId);
            Assert.Equal(stack.Count, entries[seen].Count);
            seen++;
        }

        Assert.Equal(seen, count);
        Assert.Equal(seen, header.EntryCount);
        Assert.Equal(0, header.PageIndex);
        Assert.Equal(0, header.FirstSlot);
        Assert.Equal(BagSlots, header.SlotCount);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_slot_from_a_version_1_blob_seats_instance_id_0_empty_payload_and_flag_clear(string name)
    {
        Span<PageEntry> entries = stackalloc PageEntry[BagSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(Load(name), BagSlots, entries, out _, out int count, out _));

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(0L, entries[i].InstanceId);
            Assert.Equal(0, entries[i].PayloadLength);
            Assert.False(entries[i].Quarantined);
            Assert.Equal(0u, entries[i].Flags);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A_version_1_blob_takes_content_version_stamp_0_so_every_rule_applies(string name)
    {
        Span<PageEntry> entries = stackalloc PageEntry[BagSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(Load(name), BagSlots, entries, out PageHeader header, out _, out _));

        // Stamp 0 is older than every published version, so the FULL remap rule set applies on the first
        // load (contracts 8.3). That is free: rule application is a scan that is a no-op on a page holding
        // no remapped id.
        Assert.Equal(0, header.ContentVersion);
    }

    [Fact]
    public void A_version_1_blob_whose_declared_slot_count_differs_is_still_refused_whole()
    {
        byte[] stored = Load("container-v1-multi.blob");

        Span<PageEntry> entries = stackalloc PageEntry[64];
        // The version 1 path's own refusal travels out as the version 1 validator spelled it, rather than
        // being flattened into a page token: the seven page reasons belong to the version 2 page, and a
        // version 1 blob is refused by the reader that owns it.
        Assert.False(ItemContainerPageCodec.TryDecode(stored, BagSlots - 1, entries, out _, out _, out string? narrow));
        Assert.Contains("item container blob", narrow ?? string.Empty, StringComparison.Ordinal);
        Assert.False(ItemContainerPageCodec.TryDecode(stored, BagSlots + 1, entries, out _, out _, out _));
        Assert.True(ItemContainerPageCodec.TryDecode(stored, BagSlots, entries, out _, out _, out _));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A_version_2_writer_never_produces_a_version_1_blob(string name)
    {
        byte[] stored = Load(name);
        Span<PageEntry> entries = stackalloc PageEntry[BagSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(stored, BagSlots, entries, out PageHeader header, out int count, out _));

        var rewritten = new PageSlotInput[count];
        for (int i = 0; i < count; i++)
            rewritten[i] = new PageSlotInput(
                entries[i].Slot, entries[i].Flags, entries[i].DefinitionId, entries[i].Count, entries[i].InstanceId, default);

        byte[] page = ItemContainerPageCodec.Encode(header.PageIndex, header.FirstSlot, header.SlotCount, 300, rewritten);

        Assert.NotEqual(ItemContainerCodec.Version1, page[0]);
        Assert.Equal(ItemContainerPageCodec.Version, BinaryPrimitives.ReadUInt16LittleEndian(page));
        Assert.NotNull(ItemContainerCodec.Validate(page, BagSlots));
        Assert.Null(ItemContainerPageCodec.Validate(page, BagSlots));
    }

    [Fact]
    public void Byte_0_value_1_dispatches_to_version_1_and_anything_else_to_the_ushort_reader()
    {
        byte[] stored = Load("container-v1-multi.blob");
        Span<PageEntry> entries = stackalloc PageEntry[BagSlots];

        Assert.Equal(ItemContainerCodec.Version1, stored[0]);
        Assert.True(ItemContainerPageCodec.TryDecode(stored, BagSlots, entries, out _, out _, out _));

        // Byte 0 of 3 means a ushort version whose low byte is 3, which this build does not read. The
        // version 1 path is NOT reached, so the refusal is the page reader's rather than the legacy one's.
        byte[] other = (byte[])stored.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(other, 3);
        Assert.False(ItemContainerPageCodec.TryDecode(other, BagSlots, entries, out _, out _, out string? reason));
        Assert.Equal(ItemContainerPageReason.Version, reason);

        // And 257 is congruent to 1 modulo 256, which is the version number the dispatch costs: byte 0 is
        // 1, so the reader takes it for a version 1 blob and never sees the 257. That is why no container
        // codec version congruent to 1 modulo 256 is ever assigned.
        byte[] congruent = (byte[])stored.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(congruent, 257);
        Assert.Equal((byte)1, congruent[0]);
        Assert.False(ItemContainerPageCodec.TryDecode(congruent, BagSlots, entries, out _, out _, out string? legacy));
        Assert.Contains("item container blob", legacy ?? string.Empty, StringComparison.Ordinal);
    }
}
