using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>Base-scoped cleanup preserves the current draft and releases only the draft it names.</summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>A previous attempt cannot admit edits or a discard into a newer frozen draft.</summary>
    [Fact]
    public virtual async Task AReleaseForAnEarlierBaseCannotUnfreezeTheCurrentDraft()
    {
        IContentAuthoringStore store = await OpenAsync();
        var scoped = Assert.IsAssignableFrom<IContentDraftFreezeStore>(store);
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await ApplyAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(22)));
        await store.FreezeDraftAsync(1);

        await scoped.ClearDraftFreezeAsync(0);
        Assert.True((await store.GetOpenDraftAsync())!.IsFrozen);
        ContentAuthoringException edit = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ApplyAsync(store, ContentEdit.Add(Thing, new ContentKey("late"), CatalogFixtures.Fields(33))));
        Assert.Equal(ContentAuthoringException.PublishInProgressReason, edit.Reason);
        ContentAuthoringException discard = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.DiscardDraftAsync(CatalogFixtures.Actor, CatalogFixtures.Operator));
        Assert.Equal(ContentAuthoringException.PublishInProgressReason, discard.Reason);

        await scoped.ClearDraftFreezeAsync(1);
        Assert.False((await store.GetOpenDraftAsync())!.IsFrozen);
        await ApplyAsync(store, ContentEdit.Add(Thing, new ContentKey("late"), CatalogFixtures.Fields(33)));
        Assert.Equal(2, (await store.GetOpenDraftAsync())!.EditCount);
        await store.DiscardDraftAsync(CatalogFixtures.Actor, CatalogFixtures.Operator);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Single(await store.ListVersionsAsync());
    }
}
