using System;
using System.Buffers.Binary;
using System.IO;
using KhaozEngine.Items;
using Xunit;

namespace KhaozEngine.Tests.Items;

/// <summary>
/// Spec 17 row 4 over spec 4.5, the half that needs nothing above <c>KhaozEngine.Items</c>: the version 1
/// path itself, its eleven <see cref="ItemContainerCodec.Validate"/> rules, and the byte 0 dispatch.
/// <para>
/// The blobs are CHECKED IN, written by the version 1 encoder before the version 2 codec existed. That is
/// the point of them: the version 1 reader is frozen, and a fixture is the only thing that can say so
/// after the encoder and the reader have both been read by the same pair of eyes. The other half of row
/// 4, a version 1 blob read THROUGH the version 2 page reader, lives in
/// <c>KhaozEngine.ItemInstances.Tests</c>, because the page codec sits in the package above this one.
/// </para>
/// </summary>
public class ItemContainerCodecVersionTests
{
    const int BagSlots = 28;

    public static TheoryData<string> Fixtures() => new()
    {
        "container-v1-empty.blob",
        "container-v1-one-stack.blob",
        "container-v1-multi.blob",
        "container-v1-full-bag.blob",
    };

    internal static byte[] Load(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Items", "Fixtures", name));

    static bool Stackable(int itemId) => true;

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A_checked_in_version_1_blob_is_still_what_the_version_1_encoder_writes(string name)
    {
        byte[] stored = Load(name);

        Assert.Null(ItemContainerCodec.Validate(stored, BagSlots));
        Assert.True(ItemContainerCodec.TryDecode(stored, BagSlots, Stackable, out ItemContainer decoded));
        Assert.Equal(stored, ItemContainerCodec.Encode(decoded));
        Assert.Equal((byte)1, stored[0]);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_slot_from_a_version_1_blob_seats_instance_id_0_empty_payload_and_flag_clear(string name)
    {
        Assert.True(ItemContainerCodec.TryDecode(Load(name), BagSlots, Stackable, out ItemContainer decoded));

        for (int slot = 0; slot < decoded.SlotCount; slot++)
        {
            ItemSlot seated = decoded.SlotAt(slot);
            Assert.Equal(0L, seated.Stack.InstanceId);
            Assert.True(seated.Payload.IsEmpty);
            Assert.False(seated.Quarantined);
        }
    }

    [Fact]
    public void A_version_1_blob_whose_declared_slot_count_differs_is_still_refused_whole()
    {
        byte[] stored = Load("container-v1-multi.blob");

        // Load bearing for Grimhollow, whose WidenBag and NarrowBag helpers exist precisely because of
        // this refusal. Do not improve it.
        Assert.NotNull(ItemContainerCodec.Validate(stored, BagSlots - 1));
        Assert.NotNull(ItemContainerCodec.Validate(stored, BagSlots + 1));
        Assert.False(ItemContainerCodec.TryDecode(stored, BagSlots + 1, Stackable, out _));
        Assert.Null(ItemContainerCodec.Validate(stored, BagSlots));
    }

    [Fact]
    public void The_version_constant_is_a_ushort_at_2_and_the_legacy_one_is_a_byte_at_1()
    {
        Assert.IsType<ushort>(ItemContainerCodec.Version);
        Assert.Equal((ushort)2, ItemContainerCodec.Version);
        Assert.IsType<byte>(ItemContainerCodec.Version1);
        Assert.Equal((byte)1, ItemContainerCodec.Version1);
    }

    [Fact]
    public void Byte_0_value_1_dispatches_to_version_1_and_anything_else_to_the_ushort_reader()
    {
        byte[] stored = Load("container-v1-multi.blob");
        Assert.Equal(ItemContainerCodec.Version1, stored[0]);
        Assert.Null(ItemContainerCodec.Validate(stored, BagSlots));

        // Byte 0 is 2, so the version field is a ushort and its value is 2. This entry point does not read
        // it, and the refusal names the number the ushort reader found rather than the byte.
        byte[] version2 = (byte[])stored.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(version2, 2);
        string reason = ItemContainerCodec.Validate(version2, BagSlots) ?? string.Empty;
        Assert.Contains("version 2", reason, StringComparison.Ordinal);
        Assert.False(ItemContainerCodec.TryDecode(version2, BagSlots, Stackable, out _));

        // A ushort whose low byte is 3 and whose high byte is 1 is version 259, not version 3: the reader
        // is reading two bytes, which is the whole content of the dispatch rule.
        byte[] version259 = (byte[])stored.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(version259, 259);
        string far = ItemContainerCodec.Validate(version259, BagSlots) ?? string.Empty;
        Assert.Contains("version 259", far, StringComparison.Ordinal);
    }

    /// <summary>
    /// The eleven rules of <c>ItemContainerCodec.cs:74-104</c>, each named and each still firing. The
    /// eleventh is the tolerance rather than a refusal: no state is not a fault.
    /// </summary>
    [Fact]
    public void A_version_1_blobs_eleven_Validate_rules_are_all_still_enforced()
    {
        byte[] stored = Load("container-v1-multi.blob");

        // 1. Shorter than its own three byte header.
        Assert.NotNull(ItemContainerCodec.Validate(new byte[] { 1, 28 }, BagSlots));

        // 2. A version byte this reader does not know.
        byte[] wrongVersion = (byte[])stored.Clone();
        wrongVersion[0] = 9;
        Assert.NotNull(ItemContainerCodec.Validate(wrongVersion, BagSlots));

        // 3. A declared slot count that is not the caller's geometry.
        Assert.NotNull(ItemContainerCodec.Validate(stored, 20));

        // 4. A body that is not a whole number of ten byte entries.
        Assert.NotNull(ItemContainerCodec.Validate([.. stored, (byte)0], BagSlots));

        // 5. More entries than the blob declares slots.
        byte[] tooManyEntries = (byte[])stored.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(tooManyEntries.AsSpan(1), 2);
        Assert.NotNull(ItemContainerCodec.Validate(tooManyEntries, 2));

        // 6. Entries out of ascending slot order.
        byte[] disordered = (byte[])stored.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(disordered.AsSpan(3 + 10), 0);
        Assert.NotNull(ItemContainerCodec.Validate(disordered, BagSlots));

        // 7. An entry naming a slot past the declared count.
        byte[] pastEnd = (byte[])stored.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(pastEnd.AsSpan(3 + (3 * 10)), BagSlots);
        Assert.NotNull(ItemContainerCodec.Validate(pastEnd, BagSlots));

        // 8. An occupied entry carrying the empty item id.
        byte[] zeroId = (byte[])stored.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(zeroId.AsSpan(3 + 2), 0);
        Assert.NotNull(ItemContainerCodec.Validate(zeroId, BagSlots));

        // 9. An item id below zero.
        byte[] negativeId = (byte[])stored.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(negativeId.AsSpan(3 + 2), -4);
        Assert.NotNull(ItemContainerCodec.Validate(negativeId, BagSlots));

        // 10. A count that is not positive.
        byte[] zeroCount = (byte[])stored.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(zeroCount.AsSpan(3 + 6), 0);
        Assert.NotNull(ItemContainerCodec.Validate(zeroCount, BagSlots));

        // 11. No stored state at all is not a fault, which is what lets a first login seat a fresh bag.
        Assert.Null(ItemContainerCodec.Validate(null, BagSlots));
        Assert.Null(ItemContainerCodec.Validate([], BagSlots));
    }
}
