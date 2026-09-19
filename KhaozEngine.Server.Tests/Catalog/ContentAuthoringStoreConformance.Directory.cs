using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The seam AS boot step 2 reads it. <see cref="IContentAuthoringStore"/> inherits
/// <see cref="IContentVersionDirectory"/>, so a host that boots off its authoring database assigns the store
/// itself to <c>ContentBootOptions.Directory</c>, and every provider owes the two reads THROUGH that
/// interface rather than only through its own type.
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// A store used as the boot's version directory answers the operator's hold, then the active version once
    /// the hold is gone. The two numbers are made to DIFFER first, by pinning the older of two published
    /// versions, because a provider that answered the active version from both members would pass a fact
    /// where they agree.
    /// </summary>
    [Fact]
    public virtual async Task AStoreUsedAsTheBootsVersionDirectoryAnswersThePinnedVersionThenTheActiveOne()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)));

        // The upcast is the fact: no adapter, no wrapper, the store itself in the seam the boot takes.
        IContentVersionDirectory directory = store;
        Assert.Null(await directory.GetPinnedVersionAsync());
        Assert.Equal(2, await directory.GetActiveVersionAsync());

        await store.SetPinnedVersionAsync(1, CatalogFixtures.Actor, CatalogFixtures.Operator);
        Assert.Equal(1, await directory.GetPinnedVersionAsync());
        Assert.Equal(2, await directory.GetActiveVersionAsync());

        await store.SetPinnedVersionAsync(null, CatalogFixtures.Actor, CatalogFixtures.Operator);
        Assert.Null(await directory.GetPinnedVersionAsync());
        Assert.Equal(2, await directory.GetActiveVersionAsync());
    }
}
