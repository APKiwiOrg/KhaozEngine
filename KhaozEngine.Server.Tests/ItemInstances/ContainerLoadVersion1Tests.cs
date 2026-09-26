using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.Items;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerLoadFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>The version 1 bridge through the journal load path.</summary>
public class ContainerLoadVersion1Tests
{
    [Theory]
    [InlineData(2)]
    [InlineData(11)]
    [InlineData(28)]
    [InlineData(30)]
    [InlineData(56)]
    public void A_stored_single_page_version_1_container_loads_as_page_0_without_pre_widening(int declaredSlots)
    {
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        var stored = new ItemContainer(declaredSlots, static definitionId => definitionId != 0);
        stored.SetAt(declaredSlots - 1, new ItemStack(Sword, 2));
        JournalProjectionSection section = Section(0, ItemContainerCodec.Encode(stored));

        ContainerLoadResult result = ContainerLoad.Load(
            [section], snapshot, Context(types, Properties()));

        ItemContainerPage page = Assert.Single(result.Pages);
        Assert.Equal(0, page.PageIndex);
        Assert.Equal(PageSlots, page.SlotCount);
        Assert.Equal(Sword, page.SlotAt(declaredSlots - 1).Stack.ItemId);
        Assert.Equal(2, page.SlotAt(declaredSlots - 1).Stack.Count);
        Assert.Equal(0, page.ContentVersion);
        Assert.Empty(result.Findings);
    }
}
