using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>Checks temporal metadata, order and active-version semantics on real whole-version reads.</summary>
internal static class CatalogVersionRowsReadAssertions
{
    public static async Task EmptyAsync(IContentAuthoringStore store)
    {
        IContentVersionRowSource source = Assert.IsAssignableFrom<IContentVersionRowSource>(store);
        Assert.Empty(await source.ReadVersionRowsAsync(0));
        Assert.Empty(await source.ReadVersionRowsAsync(1));
    }

    public static async Task SeededAsync(IContentAuthoringStore store)
    {
        IContentVersionRowSource source = Assert.IsAssignableFrom<IContentVersionRowSource>(store);
        var first = await source.ReadVersionRowsAsync(1);
        var second = await source.ReadVersionRowsAsync(2);
        Assert.Equal(1002, first.Count);
        Assert.Equal(1003, second.Count);
        Assert.Equal(Enumerable.Range(1, 501), first.Take(501).Select(revision => revision.Row.Id));
        Assert.All(first.Take(501), revision => Assert.Equal(CatalogFixtures.Thing, revision.Row.Type));
        Assert.Equal(Enumerable.Range(1, 501), first.Skip(501).Select(revision => revision.Row.Id));
        Assert.All(first.Skip(501), revision => Assert.Equal(CatalogFixtures.Other, revision.Row.Type));
        Assert.All(first, revision =>
        {
            Assert.Equal(revision.Row.Id, revision.Row.Fields[0].Number);
            Assert.True(revision.Row.Fields[1].IsAbsent);
            Assert.Equal(1, revision.ValidFromVersion);
            Assert.Null(revision.FamilyId);
        });
        ContentRowRevision old = first[500];
        Assert.Equal(2, old.ReplacedInVersion);
        ContentRowRevision changed = second[500];
        Assert.Equal(501, changed.Row.Id);
        Assert.Equal(9001, changed.Row.Fields[0].Number);
        Assert.Equal(2, changed.ValidFromVersion);
        Assert.Null(changed.ReplacedInVersion);
        Assert.Equal(502, second[501].Row.Id);
        Assert.Equal(2, second[501].ValidFromVersion);
        Assert.Equal(CatalogFixtures.Other, second[502].Row.Type);

        await store.ApplyEditsAsync(
            [ContentEdit.Update(CatalogFixtures.Thing, 1, CatalogVersionRowsFixture.Key("thing", 1), CatalogFixtures.Fields(42))],
            CatalogFixtures.Actor, CatalogFixtures.Operator, "unpublished edit");
        await store.SetPinnedVersionAsync(1, CatalogFixtures.Actor, CatalogFixtures.Operator);
        var active = await source.ReadVersionRowsAsync(0);
        Assert.Equal(1003, active.Count);
        Assert.Equal(1, active[0].Row.Fields[0].Number);
        Assert.Equal(9001, active[500].Row.Fields[0].Number);
        Assert.Equal(2, await store.GetActiveVersionAsync());
        await store.DiscardDraftAsync(CatalogFixtures.Actor, CatalogFixtures.Operator);
        await store.SetPinnedVersionAsync(null, CatalogFixtures.Actor, CatalogFixtures.Operator);
    }

    public static async Task RetiredAsync(IContentAuthoringStore store)
    {
        IContentVersionRowSource source = Assert.IsAssignableFrom<IContentVersionRowSource>(store);
        var target = await source.ReadVersionRowsAsync(1);
        var current = await source.ReadVersionRowsAsync(3);
        Assert.Equal(1002, target.Count);
        Assert.Equal(1003, current.Count);
        ContentRowRevision old = target[499];
        Assert.False(old.Row.IsRetired);
        Assert.Equal(1, old.ValidFromVersion);
        Assert.Equal(3, old.ReplacedInVersion);
        ContentRowRevision retired = current[499];
        Assert.True(retired.Row.IsRetired);
        Assert.Equal(500, retired.Row.Fields[0].Number);
        Assert.Equal(3, retired.ValidFromVersion);
        Assert.Null(retired.ReplacedInVersion);
        Assert.True(current[^1].Row.IsRetired);
        Assert.Equal(CatalogFixtures.Other, current[^1].Row.Type);
        Assert.Equal(501, current[^1].Row.Id);
    }
}
