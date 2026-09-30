using System;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.EntryOrderFixtures;
using static KhaozEngine.Tests.ItemInstances.Remap.RemapFixtures;

namespace KhaozEngine.Tests.ItemInstances.Remap;

public class InstanceEntryOrderRemapTests
{
    static readonly InstanceSlotKind[] MarkedEntry = [InstanceSlotKind.Byte, InstanceSlotKind.Varint];
    static readonly InstanceSlotKind[] IdEntry = [InstanceSlotKind.Varint];

    [Fact]
    public void A_custom_sorted_codec_restores_order_by_its_declared_reference_slot_and_stacks()
    {
        InstancePropertyRegistry properties = Register(InstanceCountWidth.Byte, marker: true);
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Payload([500, 501], [17, 44]));
        Seat(page, 1, Sword, Payload([501, 600], [44, 17]), instanceId: Instance + 1);

        InstanceRemapOutcome result = Move(page, properties, 500, 600);

        Assert.Equal(1, result.EntriesTouched);
        Assert.Equal(1, result.IdsRewritten);
        Assert.Equal(0, result.EntriesAbandoned);
        Assert.Equal(Payload([501, 600], [44, 17]), PayloadAt(page, 0));
        Assert.Null(ItemInstancePayload.Validate(properties, PayloadAt(page, 0)));
        Assert.True(InstanceStacking.CanMerge(page.SlotAt(0), page.SlotAt(1), static _ => true));
    }

    [Fact]
    public void An_authored_game_list_keeps_its_sequence_when_a_reference_moves_past_its_neighbor()
    {
        InstancePropertyRegistry properties = Register(InstanceCountWidth.Byte, marker: true, authored: true);
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Payload([500, 501], [17, 44]));

        InstanceRemapOutcome result = Move(page, properties, 500, 600);

        Assert.Equal(1, result.EntriesTouched);
        Assert.Equal(Payload([600, 501], [17, 44]), PayloadAt(page, 0));
    }

    [Fact]
    public void A_varint_counted_sorted_list_above_256_entries_is_rewritten_in_full()
    {
        InstancePropertyRegistry properties = Register(InstanceCountWidth.Varint);
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Payload(Enumerable.Range(1, 257).Select(id => (ulong)id).ToArray(), width: InstanceCountWidth.Varint));
        ulong[] expected = Enumerable.Range(2, 256).Select(id => (ulong)id).Append(300UL).ToArray();

        InstanceRemapOutcome result = Move(page, properties, 1, 300);

        Assert.Equal(1, result.EntriesTouched);
        Assert.Equal(0, result.EntriesAbandoned);
        Assert.Equal(Payload(expected, width: InstanceCountWidth.Varint), PayloadAt(page, 0));
        Assert.Null(ItemInstancePayload.Validate(properties, PayloadAt(page, 0)));
    }

    [Fact]
    public void Sorted_keys_use_the_full_unsigned_varint_width()
    {
        InstancePropertyRegistry properties = Register(InstanceCountWidth.Byte, shapeOnly: true);
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Payload([ulong.MaxValue, (ulong)long.MaxValue + 1, 500]));

        InstanceRemapOutcome result = Move(page, properties, 500, 600);

        Assert.Equal(1, result.EntriesTouched);
        Assert.Equal(Payload([600, (ulong)long.MaxValue + 1, ulong.MaxValue]), PayloadAt(page, 0));
    }

    [Fact]
    public void A_codec_that_allows_equal_reference_keys_keeps_their_authored_tie_order()
    {
        InstancePropertyRegistry properties = Register(InstanceCountWidth.Byte, marker: true, duplicates: true);
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, Payload([500, 501], [17, 44]));

        InstanceRemapOutcome result = Move(page, properties, 500, 501);

        Assert.Equal(1, result.EntriesTouched);
        Assert.Equal(Payload([501, 501], [17, 44]), PayloadAt(page, 0));
    }

    [Fact]
    public void A_strict_custom_codec_abandons_a_rewrite_that_collides_reference_keys()
    {
        InstancePropertyRegistry properties = Register(InstanceCountWidth.Byte, marker: true);
        byte[] original = Payload([500, 501], [17, 44]);
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, original);

        InstanceRemapOutcome result = Move(page, properties, 500, 501);

        Assert.Equal(0, result.EntriesTouched);
        Assert.Equal(1, result.EntriesAbandoned);
        Assert.Equal(original, PayloadAt(page, 0));
        Assert.False(page.IsDirty);
    }

    [Fact]
    public void A_legacy_game_registration_using_the_shipped_affix_codec_still_restores_order()
    {
        InstancePropertyRegistry properties = Properties();
        properties.Register(InstanceKindBand.Game, GameKind, InstancePropertyCodec.AffixList,
            PropertyVisibility.Everyone, -1,
            new InstanceFieldShape(default, InstanceCountWidth.Byte,
                new[] { InstanceSlotKind.Varint, InstanceSlotKind.Byte, InstanceSlotKind.Fixed2, InstanceSlotKind.Varint }),
            new[] { new InstanceReferenceTarget(ModKey, InstanceReferenceSite.Entry, 0) });
        byte[] originalBody = [2, 0xF4, 3, 1, 17, 0, 0, 0xF5, 3, 2, 44, 0, 0];
        byte[] expectedBody = [2, 0xF5, 3, 2, 44, 0, 0, 0xD8, 4, 1, 17, 0, 0];
        ItemContainerPage page = Page();
        Seat(page, 0, Sword, new ItemInstancePayloadBuilder().Add(GameKind, originalBody).ToArray());

        InstanceRemapOutcome result = Move(page, properties, 500, 600);

        Assert.Equal(1, result.EntriesTouched);
        Assert.Equal(new ItemInstancePayloadBuilder().Add(GameKind, expectedBody).ToArray(), PayloadAt(page, 0));
        Assert.Null(ItemInstancePayload.Validate(properties, PayloadAt(page, 0)));
    }

    static InstancePropertyRegistry Register(InstanceCountWidth width, bool marker = false,
        bool authored = false, bool duplicates = false, bool shapeOnly = false)
    {
        InstancePropertyRegistry properties = Properties();
        InstanceSlotKind[] entry = marker ? MarkedEntry : IdEntry;
        InstanceFieldShape shape = authored ? new InstanceFieldShape(default, width, entry) : Shape(default, width, entry);
        properties.Register(InstanceKindBand.Game, GameKind,
            authored || shapeOnly ? InstancePropertyCodec.ShapeOnly : new OrderedCodec(width, marker, duplicates),
            PropertyVisibility.Everyone, -1, shape,
            new[] { new InstanceReferenceTarget(ModKey, InstanceReferenceSite.Entry, marker ? 1 : 0) });
        return properties;
    }

    static byte[] Payload(ulong[] ids, byte[]? markers = null, InstanceCountWidth width = InstanceCountWidth.Byte)
    {
        byte[] body = new byte[ItemInstancePayload.MaxInstancePayloadBytes];
        int written = width == InstanceCountWidth.Byte ? 1 : ContentVarint.Write(body, (uint)ids.Length);
        if (width == InstanceCountWidth.Byte) body[0] = checked((byte)ids.Length);
        for (int index = 0; index < ids.Length; index++)
        {
            if (markers is not null) body[written++] = markers[index];
            written += ContentVarint.WriteUInt64(body.AsSpan(written), ids[index]);
        }

        return new ItemInstancePayloadBuilder().Add(GameKind, body.AsSpan(0, written)).ToArray();
    }

    static InstanceRemapOutcome Move(ItemContainerPage page, InstancePropertyRegistry properties, int from, int to)
    {
        ContentTypeRegistry types = Types();
        return InstanceRemapPass.Apply(page, Rules(Replaced(1, 2, Type(types, ModKey), from, to)), 0, properties, types);
    }
}
