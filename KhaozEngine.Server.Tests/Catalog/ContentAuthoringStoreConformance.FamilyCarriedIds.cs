using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>A direct import protects its slot from same-draft and later family allocations.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public virtual async Task ACarriedFamilyIdAdvancesItsOwnBlockCursor(bool? importFirst)
    {
        // Issue #1552. The type mark already covers this block, but its cursor can still issue the carried id.
        IContentAuthoringStore store = await OpenAsync();
        ContentFamily family = await store.CreateFamilyAsync(
            Thing, "swords", ContentFamily.MinBlockSize, CatalogFixtures.Actor, CatalogFixtures.Operator);
        await PublishAsync(
            store, ContentEdit.Add(Thing, new ContentKey("existing"), CatalogFixtures.Fields(11), family.FamilyId));
        ContentFamilyBlock block = Assert.Single(Assert.Single(await store.ListFamiliesAsync(Thing)).Blocks);
        int carriedId = block.NextFreeId;

        ContentEdit imported = ContentEdit.Import(
            Thing, carriedId, new ContentKey("carried"), CatalogFixtures.Fields(22), family.FamilyId);
        ContentEdit added = ContentEdit.Add(Thing, new ContentKey("added"), CatalogFixtures.Fields(33), family.FamilyId);
        if (importFirst is bool first)
        {
            await PublishAsync(store, first ? [imported, added] : [added, imported]);
        }
        else
        {
            await PublishAsync(store, imported);
            await PublishAsync(store, added);
        }

        ContentRowPage published = await RowsAsync(store);
        Assert.Equal(3, published.Total);
        Assert.Equal([carriedId - 1, carriedId, carriedId + 1], Ids(published));
        Assert.Equal(["existing", "carried", "added"], Keys(published));
        Assert.Equal(carriedId + 2, await store.AllocateInFamilyAsync(family.FamilyId));
    }

    /// <summary>Each affected block advances independently, including multiple carries in one block.</summary>
    [Fact]
    public virtual async Task CarriedFamilyIdsOnlyAdvanceTheirMatchingBlocks()
    {
        IContentAuthoringStore store = await OpenAsync();
        ContentFamily target = await store.CreateFamilyAsync(
            Thing, "swords", ContentFamily.MinBlockSize, CatalogFixtures.Actor, CatalogFixtures.Operator);
        ContentFamily sibling = await store.CreateFamilyAsync(
            Thing, "shields", ContentFamily.MinBlockSize, CatalogFixtures.Actor, CatalogFixtures.Operator);
        ContentFamilyBlock first = target.Blocks[0];
        ContentFamilyBlock second = await new ContentIdAllocator(Ids(store)).ReserveBlockAsync(target.FamilyId);
        // Publish carries in reverse id order, with both blocks still open.
        await PublishAsync(store,
            ContentEdit.Import(Thing, second.BaseId + 5, new ContentKey("third"), CatalogFixtures.Fields(33), target.FamilyId),
            ContentEdit.Import(Thing, first.BaseId + 7, new ContentKey("second"), CatalogFixtures.Fields(22), target.FamilyId),
            ContentEdit.Import(Thing, first.BaseId + 3, new ContentKey("first"), CatalogFixtures.Fields(11), target.FamilyId));

        ContentFamily updated = (await Ids(store).ReadFamilyAsync(target.FamilyId))!;
        Assert.Equal(first.BaseId + 8, updated.Blocks[0].NextFreeId);
        Assert.Equal(second.BaseId + 6, updated.Blocks[1].NextFreeId);
        Assert.Equal(sibling.Blocks[0].BaseId, await store.AllocateInFamilyAsync(sibling.FamilyId));
        Assert.Equal(1, await store.AllocateAsync(Capped, 1));
    }

    /// <summary>A seed planned from an earlier cursor preserves a rival's higher advance.</summary>
    [Fact]
    public virtual async Task ACarriedFamilySeedPreservesARivalAdvance()
    {
        IContentAuthoringStore store = await OpenAsync();
        ContentFamily family = await store.CreateFamilyAsync(
            Thing, "swords", ContentFamily.MinBlockSize, CatalogFixtures.Actor, CatalogFixtures.Operator);
        IContentIdPersistence ids = Ids(store);
        ContentFamilyBlock block = family.Blocks[0];
        int rivalId = block.BaseId + 4;
        var rivalFirst = new RivalFirstIdPersistence(ids,
            () => ids.CommitFamilyCarriedThroughAsync(Thing, family.FamilyId, block.BlockOrdinal, rivalId));
        var commit = new ContentPublishCommit(store, PackOf(store), new ContentPublisher(store, rivalFirst, Registry));
        await ApplyAsync(store, ContentEdit.Import(
            Thing, block.BaseId, new ContentKey("carried"), CatalogFixtures.Fields(11), family.FamilyId));

        await commit.PublishAsync(Request(0));

        Assert.Equal(block.BaseId, Assert.Single((await RowsAsync(store)).Rows).Id);
        Assert.Equal(rivalId + 1, await store.AllocateInFamilyAsync(family.FamilyId));
    }
}
