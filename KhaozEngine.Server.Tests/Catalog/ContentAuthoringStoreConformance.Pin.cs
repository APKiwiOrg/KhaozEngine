using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>The operator's pin refuses a version the store does not hold the same way on every provider.</summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// A pin of a version the store does not hold is refused with the unknown-version refusal, its number and
    /// the unknown-version reason token, and leaves no pin behind. The token is what an operator log line, a
    /// counter and the admin 400 body key on, so it matches every other unknown-version refusal. The control
    /// pins the version the store does hold.
    /// </summary>
    [Fact]
    public virtual async Task APinOfAVersionTheStoreDoesNotHoldIsRefusedAsAnUnknownVersion()
    {
        // #919.
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.SetPinnedVersionAsync(2, CatalogFixtures.Actor, CatalogFixtures.Operator));
        Assert.Equal(ContentAuthoringRefusal.VersionUnknown, refused.Refusal);
        Assert.Equal(2, refused.VersionNumber);
        Assert.Equal(ContentAuthoringException.UnknownVersionReason, refused.Reason);
        Assert.Null(await store.GetPinnedVersionAsync());

        await store.SetPinnedVersionAsync(1, CatalogFixtures.Actor, CatalogFixtures.Operator);
        Assert.Equal(1, await store.GetPinnedVersionAsync());
    }
}
