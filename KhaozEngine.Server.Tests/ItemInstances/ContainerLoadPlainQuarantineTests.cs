using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.Items;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerLoadFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>Plain stacks carry a truthful quarantine flag through load, storage and recovery.</summary>
public class ContainerLoadPlainQuarantineTests
{
    const int WrapperStamp = 3;
    const int PageStamp = 6;

    [Fact]
    public void An_unknown_plain_stack_gets_a_verified_wrapper_without_dirtying_the_page()
    {
        ContentTypeRegistry types = Types();
        JournalProjectionSection stored = Page(0, ActiveVersion, Slot(0, MissingId, count: 4), Slot(1, Sword));
        byte[] before = stored.Data.ToArray();
        var counted = new List<(int Type, string Reason)>();

        ContainerLoadResult result = ContainerLoad.Load(
            [stored], Snapshot(types), Context(types, Properties(), counter: (type, token) => counted.Add((type, token))));

        ItemContainerPage page = result.PageAt(0);
        ItemSlot quarantined = page.SlotAt(0);
        Assert.True(quarantined.Quarantined, Describe(result));
        Assert.Equal(new ItemStack(MissingId, 4), quarantined.Stack);
        Assert.True(QuarantineWrapper.TryUnwrap(
            quarantined.Payload.Span, out ReadOnlySpan<byte> original, out string? reason, out int stamp));
        Assert.True(original.IsEmpty);
        Assert.Equal(InstanceQuarantineReason.UnknownDefinition, reason);
        Assert.Equal(ActiveVersion, stamp);
        Assert.False(InstanceStacking.CanMerge(quarantined, quarantined, static _ => true));
        Assert.Equal(new ItemStack(Sword, 1), page.SlotAt(1).Stack);
        Assert.False(page.SlotAt(1).Quarantined);
        Assert.True(result.Reports[0].TryGetQuarantine(0, out InstanceValidationFinding finding));
        Assert.Equal(InstanceQuarantineReason.UnknownDefinition, finding.Reason);
        Assert.Equal(1, result.QuarantinedRecords);
        Assert.Equal(((int)EngineContentTypes.ItemTypeId, InstanceQuarantineReason.UnknownDefinition), Assert.Single(counted));
        Assert.Empty(result.OfKind(ContainerLoadFindingKind.EntryUnwrappable));
        Assert.False(page.IsDirty);
        Assert.Empty(result.Dirty);
        Assert.Equal(before, stored.Data.ToArray());
    }

    [Fact]
    public void Stored_plain_wrappers_keep_their_bytes_flags_and_sparse_slots_through_the_codec()
    {
        ContentTypeRegistry types = Types();
        PageSlotInput first = Wrapped(201, MissingId, InstanceQuarantineReason.UnknownDefinition, WrapperStamp, [], 0);
        PageSlotInput second = Wrapped(207, MissingId, InstanceQuarantineReason.UnknownDefinition, WrapperStamp, [], 0);
        ContainerLoadResult result = ContainerLoad.Load(
            [Page(2, PageStamp, first, second)], Snapshot(types), Context(types, Properties()));

        ItemContainerPage page = result.PageAt(2);
        JournalProjectionSection stored = Store(page);
        byte[] bytes = stored.Data.ToArray();
        var entries = new PageEntry[PageSlots];
        Assert.True(ItemContainerPageCodec.TryDecode(bytes, PageSlots, entries, out PageHeader header, out int count, out _));
        Assert.Equal(2, count);
        Assert.Equal(PageStamp, header.ContentVersion);
        Assert.Equal(201, entries[0].Slot);
        Assert.Equal(207, entries[1].Slot);
        for (int index = 0; index < count; index++)
        {
            Assert.True(entries[index].Quarantined);
            Assert.Equal(0, entries[index].InstanceId);
            Assert.Equal(first.Payload.ToArray(), bytes.AsSpan(entries[index].PayloadStart, entries[index].PayloadLength).ToArray());
        }

        ContainerLoadResult reloaded = ContainerLoad.Load([stored], Snapshot(types), Context(types, Properties()));
        Assert.Equal(2, reloaded.QuarantinedRecords);
        Assert.Equal(2, reloaded.OfKind(ContainerLoadFindingKind.EntryQuarantined).Count);
        Assert.Equal(page.SlotAt(201), reloaded.PageAt(2).SlotAt(201));
        Assert.Equal(page.SlotAt(207), reloaded.PageAt(2).SlotAt(207));
        Assert.False(page.IsDirty);
        Assert.False(reloaded.PageAt(2).IsDirty);
        Assert.Empty(result.Dirty);
        Assert.Empty(reloaded.Dirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_live_payload_without_identity_is_preserved_in_an_entry_quarantine(bool malformed)
    {
        ContentTypeRegistry types = Types();
        byte[] original = malformed ? [0x80, 0x01, 0x05] : AffixPayload();
        JournalProjectionSection stored = Page(0, ActiveVersion, Slot(0, Sword, payload: original), Slot(1, Sword));
        byte[] before = stored.Data.ToArray();

        ContainerLoadResult result = ContainerLoad.Load([stored], Snapshot(types), Context(types, Properties()));

        ItemContainerPage page = result.PageAt(0);
        Assert.True(page.SlotAt(0).Quarantined, Describe(result));
        Assert.Equal(new ItemStack(Sword, 1), page.SlotAt(0).Stack);
        Assert.True(QuarantineWrapper.TryUnwrap(
            page.SlotAt(0).Payload.Span, out ReadOnlySpan<byte> kept, out string? reason, out int stamp));
        Assert.Equal(original, kept.ToArray());
        Assert.Equal(malformed ? InstancePayloadReason.FieldTruncated : InstanceQuarantineReason.InstanceIdMissing, reason);
        Assert.Equal(ActiveVersion, stamp);
        Assert.False(page.SlotAt(1).Quarantined);
        Assert.Equal(new ItemStack(Sword, 1), page.SlotAt(1).Stack);
        Assert.Empty(result.OfKind(ContainerLoadFindingKind.PageQuarantined));
        Assert.Equal(1, result.QuarantinedRecords);
        Assert.False(page.IsDirty);
        Assert.Empty(result.Dirty);
        Assert.Equal(before, stored.Data.ToArray());
    }

    [Fact]
    public void A_formerly_plain_stack_is_rescued_when_its_definition_returns_without_assigning_identity()
    {
        ContentTypeRegistry types = Types();
        var builder = new ContentSnapshotBuilder(types);
        builder.WithIdentity(ActiveVersion, "missing-definition");
        ContainerLoadResult missing = ContainerLoad.Load(
            [Page(0, WrapperStamp, Slot(2, Sword, count: 4))], builder.Build(), Context(types, Properties()));
        Assert.True(missing.PageAt(0).SlotAt(2).Quarantined, Describe(missing));
        JournalProjectionSection stored = Store(missing.PageAt(0));
        byte[] before = stored.Data.ToArray();

        ContainerLoadResult restored = ContainerLoad.Load([stored], Snapshot(types), Context(types, Properties()));

        ItemContainerPage page = restored.PageAt(0);
        Assert.Equal(new ItemStack(Sword, 4), page.SlotAt(2).Stack);
        Assert.True(page.SlotAt(2).Payload.IsEmpty);
        Assert.False(page.SlotAt(2).Quarantined, Describe(restored));
        Assert.True(page.IsDirty);
        Assert.Single(restored.Dirty);
        Assert.Equal(0, restored.QuarantinedRecords);
        ContainerLoadFinding finding = Assert.Single(restored.OfKind(ContainerLoadFindingKind.EntryRescued));
        Assert.Equal(WrapperStamp, finding.StampedVersion);
        Assert.Equal(InstanceQuarantineReason.UnknownDefinition, finding.Reason);
        Assert.Equal(before, stored.Data.ToArray());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(Instance)]
    public void An_empty_original_can_be_rescued_with_its_existing_identity(long instanceId)
    {
        ContentTypeRegistry types = Types();
        ContainerLoadResult result = ContainerLoad.Load(
            [Page(0, PageStamp, Wrapped(0, Sword, InstanceQuarantineReason.UnknownDefinition, WrapperStamp, [], instanceId))],
            Snapshot(types), Context(types, Properties()));

        ItemContainerPage page = result.PageAt(0);
        Assert.False(page.SlotAt(0).Quarantined, Describe(result));
        Assert.Equal(new ItemStack(Sword, 1, instanceId), page.SlotAt(0).Stack);
        Assert.True(page.SlotAt(0).Payload.IsEmpty);
        Assert.True(page.IsDirty);
        Assert.Equal(PageStamp, page.ContentVersion);
        Assert.Single(result.Dirty);
        Assert.Single(result.OfKind(ContainerLoadFindingKind.EntryRescued));
        Assert.Equal(0, result.QuarantinedRecords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stored_nonempty_original_without_identity_cannot_be_rescued_as_a_live_payload(bool malformed)
    {
        ContentTypeRegistry types = Types();
        byte[] original = malformed ? [0x80, 0x01, 0x05] : AffixPayload();
        PageSlotInput wrapped = Wrapped(0, Sword, InstanceQuarantineReason.UnknownDefinition, WrapperStamp, original, 0);

        ContainerLoadResult result = ContainerLoad.Load(
            [Page(0, PageStamp, wrapped)], Snapshot(types), Context(types, Properties()));

        ItemContainerPage page = result.PageAt(0);
        Assert.True(page.SlotAt(0).Quarantined, Describe(result));
        Assert.Equal(0, page.SlotAt(0).Stack.InstanceId);
        Assert.Equal(wrapped.Payload.ToArray(), page.SlotAt(0).Payload.ToArray());
        Assert.Single(result.OfKind(ContainerLoadFindingKind.EntryQuarantined));
        Assert.Empty(result.OfKind(ContainerLoadFindingKind.EntryRescued));
        Assert.False(page.IsDirty);
        Assert.Empty(result.Dirty);
    }

    static JournalProjectionSection Store(ItemContainerPage page)
    {
        var slots = new PageSlotInput[PageSlots];
        int count = page.CopyEntriesTo(slots);
        return Section(page.PageIndex, ItemContainerPageCodec.Encode(
            page.PageIndex, page.FirstSlot, page.SlotCount, page.ContentVersion, slots.AsSpan(0, count)));
    }
}
