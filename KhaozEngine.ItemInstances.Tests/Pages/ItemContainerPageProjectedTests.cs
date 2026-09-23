using System;
using System.IO;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Tests.ItemInstances.Visibility;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Pages;

/// <summary>
/// The whole page encoded for ONE viewer, and the one projection it shares with the page delta
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/1049). The delta's own facts are
/// <see cref="ContainerPageDeltaTests"/>, and these hold the page to the same answer for the same entries.
/// </summary>
public class ItemContainerPageProjectedTests
{
    const int PageSlots = ItemContainerPageCodec.ContainerPageSlots;

    /// <summary>The 3.8 PoE row's definition id.</summary>
    const int PoeDefinitionId = 4200;

    /// <summary>The page stamp every page here carries.</summary>
    const int PageStamp = 100;

    /// <summary>Spec 3.8's rare, the same checked in golden the delta facts read.</summary>
    static byte[] Rare { get; } = File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Payload", "Goldens", "spec-3-8-poe-greatsword.bin"));

    static ContainerPageChange Item(int slot, byte[] payload, bool identified, ulong revealedMask, uint flags = 0) =>
        ContainerPageChange.Occupied(
            new PageSlotInput(slot, flags, PoeDefinitionId, 1, InstanceIdAllocator.Pack(0, 9_000 + slot), payload),
            identified,
            revealedMask);

    static ContainerPageChange Stack(int slot, int count) =>
        ContainerPageChange.Occupied(new PageSlotInput(slot, 0, 1, count, 0, default), identified: false, revealedMask: 0);

    static byte[] Projected(InstancePropertyRegistry registry, PropertyVisibility level, ReadOnlySpan<ContainerPageChange> entries) =>
        ItemContainerPageCodec.EncodeProjected(registry, level, pageIndex: 0, firstSlot: 0, PageSlots, PageStamp, entries);

    static PageEntry[] Decode(byte[] page, out int count)
    {
        var entries = new PageEntry[PageSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(page, PageSlots, entries, out _, out count, out string? reason), reason);
        return entries;
    }

    static ReadOnlySpan<byte> PayloadOf(byte[] page, in PageEntry entry) => page.AsSpan(entry.PayloadStart, entry.PayloadLength);

    [Fact]
    public void A_projected_page_hides_exactly_what_PublicView_hides()
    {
        // The unidentified item carries every kind the fixture registry holds: a server-only one, an owner-only
        // one gated on identification, a public gated one, and the v1 owner-only kinds 4, 5 and 6. Each
        // viewer's page must carry the bytes PublicView answers for that viewer, and nothing else.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] stored = VisibilityFixtures.EveryKind(identified: false, revealedMask: 0);
        ContainerPageChange[] entries = [Item(12, stored, identified: false, revealedMask: 0)];

        foreach (PropertyVisibility level in (PropertyVisibility[])[PropertyVisibility.OwnerOnly, PropertyVisibility.Everyone])
        {
            byte[] page = Projected(registry, level, entries);
            PageEntry[] decoded = Decode(page, out int count);
            Assert.Equal(1, count);

            byte[] expected = new byte[stored.Length];
            int expectedBytes = ItemInstanceVisibility.PublicView(registry, stored, level, false, 0, expected);
            Assert.True(expectedBytes > 0);
            Assert.True(PayloadOf(page, decoded[0]).SequenceEqual(expected.AsSpan(0, expectedBytes)));

            // Server-only never leaves the server, and the gate withholds a gated owner-only kind from the
            // owner too, because the item is not identified.
            Assert.False(VisibilityFixtures.Carries(registry, PayloadOf(page, decoded[0]), VisibilityFixtures.ServerSecret));
            Assert.False(VisibilityFixtures.Carries(registry, PayloadOf(page, decoded[0]), VisibilityFixtures.OwnerSecret));
            Assert.Equal(
                level == PropertyVisibility.OwnerOnly,
                VisibilityFixtures.Carries(registry, PayloadOf(page, decoded[0]), InstancePropertyKind.BoundTo));
            Assert.Equal(
                level == PropertyVisibility.OwnerOnly,
                VisibilityFixtures.Carries(registry, PayloadOf(page, decoded[0]), InstancePropertyKind.Durability));
        }

        // The raw encoder projects nothing, which is exactly why a viewer never receives its bytes.
        byte[] raw = ItemContainerPageCodec.Encode(0, 0, PageSlots, PageStamp, [entries[0].Entry]);
        PageEntry[] rawDecoded = Decode(raw, out _);
        Assert.True(VisibilityFixtures.Carries(registry, PayloadOf(raw, rawDecoded[0]), VisibilityFixtures.ServerSecret));
    }

    [Fact]
    public void A_projected_page_and_a_delta_carry_the_same_bytes_for_the_same_entries()
    {
        // The one projection, reached from both doors: for every entry, the delta's body is the page's entry
        // less its slot field, payload included. A rare, an unidentified item carrying every kind, a plain
        // stack and a payload that does not project, at both viewer levels.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] notCanonical = [0x05, 0x02, 0x5A, 0x64, 0x02, 0x01, 0x44];
        ContainerPageChange[] entries =
        [
            Item(3, Rare, identified: true, revealedMask: ulong.MaxValue),
            Item(5, VisibilityFixtures.EveryKind(identified: false, revealedMask: 0), identified: false, revealedMask: 0),
            Stack(7, 500),
            Item(9, notCanonical, identified: true, revealedMask: ulong.MaxValue),
        ];

        foreach (PropertyVisibility level in (PropertyVisibility[])[PropertyVisibility.OwnerOnly, PropertyVisibility.Everyone])
        {
            byte[] page = Projected(registry, level, entries);
            PageEntry[] decoded = Decode(page, out int count);
            Assert.Equal(entries.Length, count);

            byte[] delta = new byte[ContainerPageDelta.MaxBytes];
            int written = ContainerPageDelta.TryBuild(delta, registry, level, 1, 0, 0, PageSlots, entries);
            Assert.True(written > 0);

            int offset = ContainerPageDelta.HeaderBytes;
            for (int index = 0; index < count; index++)
            {
                PageEntry entry = decoded[index];
                Assert.True(ContentVarint.TryRead(delta, ref offset, out uint slot, out _));
                Assert.Equal((uint)entry.Slot, slot);
                Assert.Equal((byte)0x01, delta[offset++]);

                byte[] body = new byte[ItemContainerPageCodec.EntryBodySize(
                    entry.Flags, entry.DefinitionId, entry.Count, entry.InstanceId, entry.PayloadLength)];
                ItemContainerPageCodec.WriteEntryBody(
                    body, entry.Flags, entry.DefinitionId, entry.Count, entry.InstanceId, PayloadOf(page, entry));
                Assert.True(delta.AsSpan(offset, body.Length).SequenceEqual(body));
                offset += body.Length;
            }

            Assert.Equal(written, offset);
            Assert.Equal(0, decoded[2].PayloadLength);
            Assert.Equal(0, decoded[3].PayloadLength);
        }
    }

    [Fact]
    public void For_the_owner_of_an_identified_item_with_no_server_only_kind_the_projected_page_is_the_stored_one()
    {
        // The v1 kinds hold no server-only field, so the owner of an identified rare sees all 58 bytes, and the
        // viewer door writes the stored page byte for byte. What changed is WHICH door, not what the owner sees.
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        ContainerPageChange[] entries =
        [
            Stack(0, 500),
            Item(3, Rare, identified: true, revealedMask: ulong.MaxValue),
            Item(40, Rare, identified: true, revealedMask: ulong.MaxValue),
        ];

        PageSlotInput[] stored = [entries[0].Entry, entries[1].Entry, entries[2].Entry];
        Assert.Equal(
            ItemContainerPageCodec.Encode(0, 0, PageSlots, PageStamp, stored),
            Projected(registry, PropertyVisibility.OwnerOnly, entries));

        // And to anyone else the same page is four bytes shorter per rare, which is the durability field.
        Assert.Equal(
            ItemContainerPageCodec.Encode(0, 0, PageSlots, PageStamp, stored).Length - 8,
            Projected(registry, PropertyVisibility.Everyone, entries).Length);
    }

    [Fact]
    public void A_quarantined_entry_crosses_hollow_carrying_its_reason_and_stamp_and_no_preserved_bytes()
    {
        // The delta abandons on a quarantined entry and sends the caller to the whole page, so the page has to
        // carry it. Not the wrapper, whose original is unprojected by construction, and not the flag over zero
        // bytes, which the codec refuses at both doors: a wrapper that verifies and preserves nothing.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] original = VisibilityFixtures.EveryKind(identified: true, revealedMask: uint.MaxValue);
        byte[] wrapper = QuarantineWrapper.Wrap(InstanceQuarantineReason.UnknownDefinition, 37, original);
        ContainerPageChange[] entries =
        [
            Item(2, Rare, identified: true, revealedMask: ulong.MaxValue),
            Item(4, wrapper, identified: true, revealedMask: ulong.MaxValue, ItemContainerPageCodec.EntryFlagQuarantined),
        ];

        Assert.Equal(-1, ContainerPageDelta.TryBuild(new byte[ContainerPageDelta.MaxBytes], registry, PropertyVisibility.OwnerOnly, 1, 0, 0, PageSlots, entries));

        foreach (PropertyVisibility level in (PropertyVisibility[])[PropertyVisibility.OwnerOnly, PropertyVisibility.Everyone])
        {
            byte[] page = Projected(registry, level, entries);
            PageEntry[] decoded = Decode(page, out int count);
            Assert.Equal(2, count);
            Assert.True(decoded[1].Quarantined);

            ReadOnlySpan<byte> hollow = PayloadOf(page, decoded[1]);
            Assert.True(QuarantineWrapper.Verify(hollow));
            Assert.True(QuarantineWrapper.TryUnwrap(hollow, out ReadOnlySpan<byte> preserved, out string? reason, out int stamp));
            Assert.Equal(InstanceQuarantineReason.UnknownDefinition, reason);
            Assert.Equal(37, stamp);
            Assert.True(preserved.IsEmpty);
            Assert.Equal(QuarantineWrapper.Size(InstanceQuarantineReason.UnknownDefinition, 37, 0), hollow.Length);
        }

        // The raw encoder ships the whole wrapper, original included, which is the leak the viewer door closes.
        byte[] raw = ItemContainerPageCodec.Encode(0, 0, PageSlots, PageStamp, [entries[1].Entry]);
        Assert.Equal(wrapper.Length, Decode(raw, out _)[0].PayloadLength);
    }

    [Fact]
    public void An_entry_the_projection_cannot_carry_is_a_caller_bug()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        byte[] notAWrapper = [0x4B, 0x45, 0x43, 0x51, 0x09];

        // A quarantined flag over bytes that are not a wrapper, which no container seats.
        Assert.Throws<ArgumentException>(() => Projected(
            registry,
            PropertyVisibility.OwnerOnly,
            [Item(4, notAWrapper, identified: true, revealedMask: 0, ItemContainerPageCodec.EntryFlagQuarantined)]));

        // An emptied change, because a page carries a hole as the absence of an entry.
        Assert.Throws<ArgumentException>(() => Projected(
            registry, PropertyVisibility.OwnerOnly, [ContainerPageChange.Emptied(4)]));

        // Entries out of order, exactly as the raw encoder refuses them.
        Assert.Throws<ArgumentException>(() => Projected(
            registry, PropertyVisibility.OwnerOnly, [Stack(6, 1), Stack(5, 1)]));

        // And the helper itself refuses a destination shorter than the stored payload rather than truncating.
        ContainerPageChange rare = Item(3, Rare, identified: true, revealedMask: ulong.MaxValue);
        Assert.Throws<ArgumentException>(() => ContainerPageProjection.ProjectPayload(
            registry, rare, PropertyVisibility.OwnerOnly, new byte[Rare.Length - 1]));
    }

    [Fact]
    public void An_empty_page_projects_to_the_stored_empty_page()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        Assert.Equal(
            ItemContainerPageCodec.Encode(0, 0, PageSlots, PageStamp, ReadOnlySpan<PageSlotInput>.Empty),
            Projected(registry, PropertyVisibility.Everyone, ReadOnlySpan<ContainerPageChange>.Empty));
    }
}
