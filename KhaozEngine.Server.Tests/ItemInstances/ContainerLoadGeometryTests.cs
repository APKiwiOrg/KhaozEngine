using System;
using System.Buffers.Binary;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerLoadFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

public sealed class ContainerLoadGeometryTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(30, false)]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void VersionTwoLoadRequiresFullGeometryIncludingEmptyPages(int width, bool accepted)
    {
        byte[] bytes = ItemContainerPageCodec.Encode(0, 0, width == 0 ? PageSlots : width,
            ActiveVersion, Array.Empty<PageSlotInput>());
        if (width == 0) SetSlotCount(bytes, 0);

        var section = Section(0, bytes);
        ContentTypeRegistry types = Types();
        ContainerLoadResult result = ContainerLoad.Load([section], Snapshot(types), Context(types, Properties()));

        if (accepted)
        {
            ItemContainerPage page = Assert.Single(result.Pages);
            Assert.Equal(PageSlots, page.SlotCount);
            Assert.False(page.IsDirty);
            Assert.Empty(result.Findings);
        }
        else
        {
            Assert.Empty(result.Pages);
            Assert.Equal(ItemContainerPageReason.SlotOrigin, Assert.Single(result.Findings).Reason);
            Assert.True(result.HasQuarantine);
        }
        Assert.Equal(bytes, section.Data.ToArray());
        Assert.Empty(result.Dirty);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(99)]
    public void ShortOccupiedPageIsRefusedBeforeSeatingAndAValidLaterSiblingSurvives(int width)
    {
        byte[] bytes = ItemContainerPageCodec.Encode(0, 0, width, ActiveVersion,
            [Slot(width - 1, Sword, count: 2)]);
        var shortPage = Section(0, bytes);
        var sibling = Page(1, ActiveVersion, Slot(100, Sword, count: 5));
        byte[] siblingBefore = sibling.Data.ToArray();
        ContentTypeRegistry types = Types();

        ContainerLoadResult result = ContainerLoad.Load([shortPage, sibling], Snapshot(types), Context(types, Properties()));

        ItemContainerPage live = Assert.Single(result.Pages);
        Assert.Equal(1, live.PageIndex);
        Assert.Equal(5, live.SlotAt(100).Stack.Count);
        ContainerLoadFinding finding = Assert.Single(result.OfKind(ContainerLoadFindingKind.PageQuarantined));
        Assert.Equal(0, finding.PageIndex);
        Assert.Equal("bank/p00", finding.SectionName);
        Assert.Equal(ContainerLoadFinding.NoSlot, finding.Slot);
        Assert.Equal(ActiveVersion, finding.StampedVersion);
        Assert.Equal(ItemContainerPageReason.SlotOrigin, finding.Reason);
        Assert.Equal(bytes, shortPage.Data.ToArray());
        Assert.Equal(siblingBefore, sibling.Data.ToArray());
        Assert.Empty(result.Dirty);
    }

    [Fact]
    public void EntryExactlyAtDeclaredShortBoundKeepsTheCodecMalformedReason()
    {
        byte[] bytes = ItemContainerPageCodec.Encode(0, 0, PageSlots, ActiveVersion, [Slot(30, Sword)]);
        SetSlotCount(bytes, 30);

        Assert.Equal(ItemContainerPageReason.EntryMalformed, Refusal(bytes));
    }

    [Fact]
    public void SectionMismatchPrecedesTheNewShortGeometryCheck()
    {
        byte[] bytes = ItemContainerPageCodec.Encode(1, 100, 30, ActiveVersion, [Slot(129, Sword)]);

        Assert.Equal(ContainerLoadReason.PageSectionMismatch, Refusal(bytes));
    }

    [Fact]
    public void IncorrectOriginPrecedesSectionAndShortGeometryChecks()
    {
        byte[] bytes = ItemContainerPageCodec.Encode(1, 99, 30, ActiveVersion, Array.Empty<PageSlotInput>());

        Assert.Equal(ItemContainerPageReason.SlotOrigin, Refusal(bytes));
    }

    [Fact]
    public void OverWideGeometryStillPrecedesSectionChecking()
    {
        byte[] bytes = ItemContainerPageCodec.Encode(1, 100, 101, ActiveVersion, Array.Empty<PageSlotInput>());

        Assert.Equal(ItemContainerPageReason.SlotOrigin, Refusal(bytes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(99)]
    public void GeneralCodecStillAcceptsShortVersionTwoPages(int width)
    {
        byte[] bytes = ItemContainerPageCodec.Encode(0, 0, width, ActiveVersion, [Slot(width - 1, Sword)]);
        Span<PageEntry> entries = stackalloc PageEntry[PageSlots];

        Assert.True(ItemContainerPageCodec.TryDecode(bytes, PageSlots, entries, out PageHeader header,
            out int count, out string? reason), reason);
        Assert.Equal(width, header.SlotCount);
        Assert.Equal(1, count);
        Assert.Equal(width - 1, entries[0].Slot);
    }

    static string? Refusal(byte[] bytes)
    {
        var section = Section(0, bytes);
        ContentTypeRegistry types = Types();
        ContainerLoadResult result = ContainerLoad.Load([section], Snapshot(types), Context(types, Properties()));
        Assert.Empty(result.Pages);
        Assert.Equal(bytes, section.Data.ToArray());
        return Assert.Single(result.OfKind(ContainerLoadFindingKind.PageQuarantined)).Reason;
    }

    static void SetSlotCount(byte[] bytes, ushort count)
    {
        // Version is uint16, then PageIndex and FirstSlot are varints. SlotCount follows those fields.
        int offset = sizeof(ushort);
        Assert.True(ContentVarint.TryRead(bytes, ref offset, out _, out string? reason), reason);
        Assert.True(ContentVarint.TryRead(bytes, ref offset, out _, out reason), reason);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), count);
    }
}
