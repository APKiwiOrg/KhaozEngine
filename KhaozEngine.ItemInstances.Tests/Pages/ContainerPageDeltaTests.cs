using System;
using System.IO;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Tests.ItemInstances.Visibility;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Pages;

/// <summary>
/// Spec 7.5's delta, byte for byte, and the measure-then-abandon rule that keeps it inside one frame.
/// The FRAME facts, which compose this builder with the fragmenter and the game message envelope, are
/// <c>PageSyncFrameBoundTests</c> in <c>KhaozEngine.TileWorld.Netcode.Tests</c>: the budget arithmetic is
/// here because only the page codec can write the entry body, and the frame is over there because only the
/// netcode package owns the envelope.
/// </summary>
public class ContainerPageDeltaTests
{
    const int PageSlots = ItemContainerPageCodec.ContainerPageSlots;

    /// <summary>The 3.8 PoE row's definition id, two varint bytes.</summary>
    const int PoeDefinitionId = 4200;

    /// <summary>The counter the 3.8 PoE row's instance id is written at, five varint bytes.</summary>
    const long PoeFirstCounter = 4_000_000_000;

    /// <summary>Spec 3.8's rare, read from the checked in payload golden rather than rebuilt, so budget 8's
    /// 73 bytes is measured against the same 58 bytes budget 1 is.</summary>
    static byte[] Rare { get; } = File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Payload", "Goldens", "spec-3-8-poe-greatsword.bin"));

    static ContainerPageChange RareAt(int slot, int count = 1) =>
        ContainerPageChange.Occupied(
            new PageSlotInput(slot, 0, PoeDefinitionId, count, InstanceIdAllocator.Pack(0, PoeFirstCounter + slot), Rare),
            identified: true,
            revealedMask: ulong.MaxValue);

    static ContainerPageChange[] Rares(int howMany)
    {
        var changes = new ContainerPageChange[howMany];
        for (int i = 0; i < howMany; i++) changes[i] = RareAt(i);
        return changes;
    }

    [Fact]
    public void The_delta_is_7_5s_bytes_in_7_5s_order()
    {
        // Page 2 of the container, so its slots are 200 to 299 and the delta writes them RELATIVE to 200.
        ContainerPageChange[] changes = [RareAt(203), ContainerPageChange.Emptied(209)];
        Span<byte> buffer = stackalloc byte[ContainerPageDelta.MaxBytes];
        int written = ContainerPageDelta.TryBuild(
            buffer,
            InstancePropertyRegistry.CreateV1(),
            PropertyVisibility.OwnerOnly,
            containerId: 5,
            pageIndex: 2,
            firstSlot: 200,
            slotCount: PageSlots,
            changes);

        // [ContainerId][PageIndex][ChangedCount], then per change a slot varint RELATIVE to the page's
        // first slot and either 0x00 or 0x01 plus the 4.4 entry body without its slot field.
        Assert.Equal((byte)5, buffer[0]);
        Assert.Equal((byte)2, buffer[1]);
        Assert.Equal((byte)2, buffer[2]);

        int offset = ContainerPageDelta.HeaderBytes;
        Assert.True(ContentVarint.TryRead(buffer, ref offset, out uint slot, out _));
        Assert.Equal(3u, slot);
        Assert.Equal((byte)0x01, buffer[offset++]);

        PageSlotInput body = RareAt(203).Entry;
        Span<byte> expected = stackalloc byte[ItemContainerPageCodec.EntrySize(body, firstSlot: 203)];
        int bodyBytes = ItemContainerPageCodec.WriteEntryBody(
            expected, body.Flags, body.DefinitionId, body.Count, body.InstanceId, Rare);
        Assert.True(buffer.Slice(offset, bodyBytes).SequenceEqual(expected[..bodyBytes]));
        offset += bodyBytes;

        Assert.True(ContentVarint.TryRead(buffer, ref offset, out slot, out _));
        Assert.Equal(9u, slot);
        Assert.Equal((byte)0x00, buffer[offset++]);
        Assert.Equal(offset, written);
    }

    [Fact]
    public void A_single_craft_costs_73_bytes()
    {
        // Spec 16 budget 8, and spec 7.5's own arithmetic: 3 + 1 + 1 + 68 = 73. The frame half of this
        // budget is PageSyncFrameBoundTests, which is where a game message envelope exists.
        Span<byte> buffer = stackalloc byte[ContainerPageDelta.MaxBytes];
        int written = ContainerPageDelta.TryBuild(
            buffer,
            InstancePropertyRegistry.CreateV1(),
            PropertyVisibility.OwnerOnly,
            containerId: 0,
            pageIndex: 0,
            firstSlot: 0,
            slotCount: PageSlots,
            [RareAt(7)]);

        Assert.Equal(73, written);
        Assert.Equal(58, Rare.Length);
        Assert.Equal(68, ItemContainerPageCodec.EntryBodySize(0, PoeDefinitionId, 1, InstanceIdAllocator.Pack(0, PoeFirstCounter + 7), 58));
    }

    [Fact]
    public void Fourteen_changed_rare_slots_fit_and_the_fifteenth_abandons()
    {
        // Spec 7.5 point 2: a change to an occupied slot costs the slot varint plus the tag plus the entry
        // body, which is 70 at 3.8's rare, and 1,017 bytes of budget holds fourteen of them.
        Span<byte> buffer = stackalloc byte[ContainerPageDelta.MaxBytes];
        int fourteen = Build(buffer, Rares(14));
        Assert.Equal(ContainerPageDelta.HeaderBytes + (14 * 70), fourteen);
        Assert.True(fourteen <= ContainerPageDelta.MaxBytes);

        Assert.Equal(-1, Build(buffer, Rares(15)));
        Assert.True(ContainerPageDelta.HeaderBytes + (15 * 70) > ContainerPageDelta.MaxBytes);
    }

    [Fact]
    public void A_100_slot_reorder_abandons()
    {
        // The case the rule exists for: a sort, a multi slot move or a cascading deposit changes the whole
        // page, and the caller sends the page through the fragmenter rather than a second delta frame.
        Span<byte> buffer = stackalloc byte[ContainerPageDelta.MaxBytes];
        Assert.Equal(-1, Build(buffer, Rares(PageSlots)));
    }

    [Fact]
    public void An_OwnerOnly_delta_and_an_Everyone_delta_differ_only_in_the_stripped_kinds()
    {
        // Spec 7.4: the bodies are PER VIEWER, projected through PublicView at the viewer's level for the
        // item. The 3.8 rare carries one owner-only field, durability, four bytes of it (budget 11), so the
        // public delta is four bytes shorter and identical everywhere else.
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        ContainerPageChange[] changes = [RareAt(7)];

        Span<byte> owner = stackalloc byte[ContainerPageDelta.MaxBytes];
        Span<byte> everyone = stackalloc byte[ContainerPageDelta.MaxBytes];
        int ownerBytes = ContainerPageDelta.TryBuild(
            owner, registry, PropertyVisibility.OwnerOnly, 0, 0, 0, PageSlots, changes);
        int publicBytes = ContainerPageDelta.TryBuild(
            everyone, registry, PropertyVisibility.Everyone, 0, 0, 0, PageSlots, changes);

        Assert.Equal(73, ownerBytes);
        Assert.Equal(69, publicBytes);

        Span<byte> ownerView = stackalloc byte[Rare.Length];
        Span<byte> publicView = stackalloc byte[Rare.Length];
        int ownerViewBytes = ItemInstanceVisibility.PublicView(
            registry, Rare, PropertyVisibility.OwnerOnly, identified: true, ulong.MaxValue, ownerView);
        int publicViewBytes = ItemInstanceVisibility.PublicView(
            registry, Rare, PropertyVisibility.Everyone, identified: true, ulong.MaxValue, publicView);
        Assert.Equal(58, ownerViewBytes);
        Assert.Equal(54, publicViewBytes);
        Assert.Equal(ownerBytes - publicBytes, ownerViewBytes - publicViewBytes);

        // Everything but the payload is the same bytes: the header, the slot, the tag and the entry body
        // down to the payload length, which is the only field the projection can move.
        int prefix = ContainerPageDelta.HeaderBytes + 1 + 1
            + ContentVarint.Size(0)
            + ContentVarint.Size(PoeDefinitionId)
            + ContentVarint.Size(1);
        Assert.True(owner[..prefix].SequenceEqual(everyone[..prefix]));

        // And the two views differ in the STRIPPED KINDS and in nothing else: durability is owner-only
        // (spec 3.3), and every other kind the rare carries is in both.
        Assert.True(VisibilityFixtures.Carries(registry, ownerView[..ownerViewBytes], InstancePropertyKind.Durability));
        Assert.False(VisibilityFixtures.Carries(registry, publicView[..publicViewBytes], InstancePropertyKind.Durability));
        foreach (ushort kind in (ushort[])[
            InstancePropertyKind.ItemLevel,
            InstancePropertyKind.Identification,
            InstancePropertyKind.Rarity,
            InstancePropertyKind.Affixes,
            InstancePropertyKind.Sockets,
            InstancePropertyKind.RareName])
        {
            Assert.True(VisibilityFixtures.Carries(registry, ownerView[..ownerViewBytes], kind));
            Assert.True(VisibilityFixtures.Carries(registry, publicView[..publicViewBytes], kind));
        }
    }

    [Fact]
    public void An_emptied_slot_costs_its_slot_varint_and_one_byte()
    {
        // Spec 7.5 point 3: an emptied slot costs two or three bytes, so bytes are not what binds there.
        Span<byte> buffer = stackalloc byte[ContainerPageDelta.MaxBytes];
        Assert.Equal(ContainerPageDelta.HeaderBytes + 2, Build(buffer, [ContainerPageChange.Emptied(3)]));
        Assert.Equal(ContainerPageDelta.HeaderBytes + 3, Build(buffer, [ContainerPageChange.Emptied(150)], firstSlot: 0, slotCount: 200));

        ContainerPageChange[] wholePage = new ContainerPageChange[PageSlots];
        for (int i = 0; i < PageSlots; i++) wholePage[i] = ContainerPageChange.Emptied(i);
        Assert.Equal(ContainerPageDelta.HeaderBytes + (PageSlots * 2), Build(buffer, wholePage));
    }

    [Fact]
    public void More_changes_than_the_byte_count_field_holds_abandons()
    {
        // ChangedCount is a byte, so 255 is the format ceiling. A page of 100 slots is under it either way,
        // and a caller handing more abandons to the fragmenter rather than writing a count that wrapped.
        var changes = new ContainerPageChange[256];
        for (int i = 0; i < changes.Length; i++) changes[i] = ContainerPageChange.Emptied(i);

        Span<byte> buffer = stackalloc byte[ContainerPageDelta.MaxBytes];
        Assert.Equal(-1, Build(buffer, changes, firstSlot: 0, slotCount: 300));
        Assert.Equal(255, ContainerPageDelta.MaxChanges);
    }

    [Fact]
    public void A_payload_that_does_not_project_carries_nothing()
    {
        // A quarantine wrapper is not a canonical payload and never decodes, which is the whole reason it
        // exists. Spec 7.5 does not say what the delta does with one, so the encoder fails CLOSED in the
        // same direction ItemInstanceVisibility already does for an unregistered kind: a payload this
        // process cannot project carries no bytes, the flag still says quarantined, and the client renders
        // khaoz.item.quarantined off the flag rather than off bytes nothing validated.
        byte[] wrapper = QuarantineWrapper.Wrap(InstanceQuarantineReason.UnknownDefinition, 100, Rare);
        var quarantined = ContainerPageChange.Occupied(
            new PageSlotInput(4, ItemContainerPageCodec.EntryFlagQuarantined, PoeDefinitionId, 1, 7, wrapper),
            identified: true,
            revealedMask: ulong.MaxValue);

        Span<byte> buffer = stackalloc byte[ContainerPageDelta.MaxBytes];
        int written = Build(buffer, [quarantined]);

        int expectedBody = ItemContainerPageCodec.EntryBodySize(
            ItemContainerPageCodec.EntryFlagQuarantined, PoeDefinitionId, 1, 7, payloadLength: 0);
        Assert.Equal(ContainerPageDelta.HeaderBytes + 1 + 1 + expectedBody, written);
        Assert.Equal((byte)0, buffer[written - 1]);
    }

    [Fact]
    public void A_change_carrying_no_payload_is_the_plain_stack_case()
    {
        // The OSRS row of spec 3.8: 500 coins, no instance and no payload. Nothing to project, and the
        // entry body is the seven byte slot entry less its slot varint.
        var coins = ContainerPageChange.Occupied(
            new PageSlotInput(2, 0, 1, 500, 0, default), identified: false, revealedMask: 0);

        Span<byte> buffer = stackalloc byte[ContainerPageDelta.MaxBytes];
        Assert.Equal(ContainerPageDelta.HeaderBytes + 1 + 1 + 6, Build(buffer, [coins]));
    }

    [Fact]
    public void The_delta_body_is_the_page_entry_without_its_slot()
    {
        // The one fact holding the two writers together: spec 7.5's body IS spec 4.4's entry minus the slot
        // field, so the delta cannot come to write an entry the page codec would not.
        var entry = new PageSlotInput(37, 0, PoeDefinitionId, 3, InstanceIdAllocator.Pack(7, 900_001), Rare);
        Span<byte> whole = stackalloc byte[ItemContainerPageCodec.EntrySize(entry, firstSlot: 0)];
        int wholeBytes = ItemContainerPageCodec.WriteEntry(whole, entry, firstSlot: 0);

        Span<byte> body = stackalloc byte[wholeBytes];
        int bodyBytes = ItemContainerPageCodec.WriteEntryBody(
            body, entry.Flags, entry.DefinitionId, entry.Count, entry.InstanceId, entry.Payload.Span);

        int slotWidth = ContentVarint.Size(37);
        Assert.Equal(wholeBytes - slotWidth, bodyBytes);
        Assert.True(whole[slotWidth..wholeBytes].SequenceEqual(body[..bodyBytes]));
        Assert.Equal(
            ItemContainerPageCodec.EntrySize(entry, firstSlot: 0) - slotWidth,
            ItemContainerPageCodec.EntryBodySize(entry.Flags, entry.DefinitionId, entry.Count, entry.InstanceId, Rare.Length));
    }

    [Fact]
    public void Changes_out_of_order_or_outside_the_page_are_a_caller_bug()
    {
        // The encoder THROWS where the codec throws: a duplicate slot is ambiguous to a client applying the
        // changes in one pass, and a slot outside the page names a slot the client does not hold. Neither is
        // a remote frame, so neither is a refusal.
        byte[] buffer = new byte[ContainerPageDelta.MaxBytes];
        Assert.Throws<ArgumentException>(() => Build(buffer, [RareAt(5), RareAt(5)]));
        Assert.Throws<ArgumentException>(() => Build(buffer, [RareAt(6), RareAt(5)]));
        Assert.Throws<ArgumentException>(() => Build(buffer, [ContainerPageChange.Emptied(PageSlots)]));
        Assert.Throws<ArgumentException>(() => Build(buffer, [ContainerPageChange.Emptied(4)], firstSlot: 5, slotCount: PageSlots));
    }

    [Fact]
    public void A_destination_shorter_than_one_frame_abandons_rather_than_overruns()
    {
        // The budget is the LESSER of the one frame budget and what the caller's buffer holds, so a short
        // buffer abandons early instead of writing past its end.
        Span<byte> small = stackalloc byte[72];
        Assert.Equal(-1, Build(small, [RareAt(7)]));

        Span<byte> exact = stackalloc byte[73];
        Assert.Equal(73, Build(exact, [RareAt(7)]));

        Span<byte> none = stackalloc byte[2];
        Assert.Equal(-1, Build(none, [ContainerPageChange.Emptied(1)]));
    }

    [Fact]
    public void An_empty_change_set_is_a_header_and_nothing_else()
    {
        Span<byte> buffer = stackalloc byte[ContainerPageDelta.MaxBytes];
        Assert.Equal(ContainerPageDelta.HeaderBytes, Build(buffer, ReadOnlySpan<ContainerPageChange>.Empty));
        Assert.Equal((byte)0, buffer[2]);
    }

    [Fact]
    public void The_budget_is_the_frame_cap_less_the_envelope_less_the_header()
    {
        // Spec 7.5 point 1, restated as the constants the encoder measures against. The frame cap itself is
        // held equal to TileProtocol.MaxGameMessageBytes by PageSyncFrameBoundTests, which is the only test
        // project that can see both packages.
        Assert.Equal(1024, ContainerPageDelta.MaxGameMessageBytes);
        Assert.Equal(4, ContainerPageDelta.GameMessageEnvelopeBytes);
        Assert.Equal(3, ContainerPageDelta.HeaderBytes);
        Assert.Equal(1017, ContainerPageDelta.MaxChangeBytes);
        Assert.Equal(1020, ContainerPageDelta.MaxBytes);
    }

    static int Build(
        Span<byte> destination,
        ReadOnlySpan<ContainerPageChange> changes,
        int firstSlot = 0,
        int slotCount = PageSlots)
        => ContainerPageDelta.TryBuild(
            destination,
            InstancePropertyRegistry.CreateV1(),
            PropertyVisibility.OwnerOnly,
            containerId: 1,
            pageIndex: 0,
            firstSlot,
            slotCount,
            changes);
}
