using System;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerLoadFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>Newly refused scalar values survive the real validator, load, page encode and reload paths.</summary>
public class ContainerLoadScalarQuarantineTests
{
    [Theory]
    [InlineData(new byte[] { 0x01, 0x01, 0x08 })]
    [InlineData(new byte[] { 0x02, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x02, 0x06, 0x80, 0x80, 0x80, 0x80, 0x80, 0x20 })]
    [InlineData(new byte[] { 0x03, 0x03, 0x80, 0x80, 0x04 })]
    [InlineData(new byte[] { 0x04, 0x06, 0x00, 0x80, 0x80, 0x80, 0x80, 0x10 })]
    [InlineData(new byte[] { 0x05, 0x04, 0x80, 0x80, 0x04, 0x00 })]
    [InlineData(new byte[] { 0x06, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x80, 0x01, 0x0B, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })]
    public void An_illegal_scalar_is_quarantined_and_its_original_bytes_survive_storage(byte[] original)
    {
        // Omitting the per-kind refusal must leave this live and fail the first assertion.
        ContentTypeRegistry types = Types();
        JournalProjectionSection stored = Page(0, ActiveVersion,
            Slot(0, Sword, instanceId: Instance, payload: original), Slot(1, Sword));
        byte[] before = stored.Data.ToArray();

        ContainerLoadResult result = ContainerLoad.Load([stored], Snapshot(types), Context(types, Properties()));

        ItemContainerPage page = result.PageAt(0);
        Assert.True(page.SlotAt(0).Quarantined, Describe(result));
        Assert.Equal(Instance, page.SlotAt(0).Stack.InstanceId);
        Assert.True(QuarantineWrapper.TryUnwrap(
            page.SlotAt(0).Payload.Span, out ReadOnlySpan<byte> kept, out string? reason, out int stamp));
        Assert.Equal(original, kept.ToArray());
        Assert.Equal("field-malformed", reason);
        Assert.Equal(7, stamp);
        Assert.True(result.Reports[0].TryGetQuarantine(0, out InstanceValidationFinding finding));
        Assert.Equal(3, finding.Check);
        Assert.Equal("field-malformed", finding.Reason);
        Assert.Equal(InstanceValidationOutcome.Quarantined, finding.Outcome);
        Assert.Equal(InstanceValidationOutcome.Valid, result.Reports[0].OutcomeAt(1));
        Assert.False(page.SlotAt(1).Quarantined);
        Assert.Equal(1, result.QuarantinedRecords);
        Assert.Empty(result.OfKind(ContainerLoadFindingKind.PageQuarantined));
        Assert.False(page.IsDirty);
        Assert.Empty(result.Dirty);
        Assert.Equal(before, stored.Data.ToArray());

        var entries = new PageSlotInput[PageSlots];
        int count = page.CopyEntriesTo(entries);
        JournalProjectionSection persisted = Section(0, ItemContainerPageCodec.Encode(
            0, 0, PageSlots, page.ContentVersion, entries.AsSpan(0, count)));
        ContainerLoadResult reloaded = ContainerLoad.Load([persisted], Snapshot(types), Context(types, Properties()));

        Assert.True(reloaded.PageAt(0).SlotAt(0).Quarantined, Describe(reloaded));
        Assert.Equal(page.SlotAt(0).Payload.ToArray(), reloaded.PageAt(0).SlotAt(0).Payload.ToArray());
        Assert.True(QuarantineWrapper.TryUnwrap(
            reloaded.PageAt(0).SlotAt(0).Payload.Span, out kept, out reason, out stamp));
        Assert.Equal(original, kept.ToArray());
        Assert.Equal("field-malformed", reason);
        Assert.Equal(7, stamp);
        Assert.Equal(1, reloaded.QuarantinedRecords);
        Assert.Empty(reloaded.OfKind(ContainerLoadFindingKind.EntryRescued));
        Assert.False(reloaded.PageAt(0).IsDirty);
        Assert.Empty(reloaded.Dirty);
    }
}
