using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Facts 18, 19 and 20, the BUNDLE half: the seeding format is also the lossless export format, and it lands
/// into an empty database or not at all.
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// FACT 18. An import into an empty database succeeds and REPRODUCES the source ids. That is contracts
    /// 5.1's narrow exception, licensed only into an empty database because nothing is there to collide with,
    /// and it is what makes adopting a bundle a no-op for data already stored against those ids.
    /// </summary>
    [Fact]
    public virtual async Task Fact18_AnImportIntoAnEmptyDatabaseKeepsTheSourceIds()
    {
        IContentAuthoringStore store = await ResetToEmptyAsync();

        ContentBundle bundle = new(
            ContentBundle.CurrentFormatVersion,
            "hand-authored",
            0,
            [],
            [
                new ContentBundleRow(Thing, 40, new ContentKey("forty"), false, null, CatalogFixtures.Fields(40)),
                new ContentBundleRow(Thing, 41, new ContentKey("forty_one"), false, null, CatalogFixtures.Fields(41)),
            ],
            [],
            []);

        ContentPublishResult imported = await store.ImportBundleAsync(
            bundle, CatalogFixtures.Actor, CatalogFixtures.Operator, "seed");

        Assert.Equal(1, imported.VersionNumber);
        ContentRowPage page = await RowsAsync(store);
        Assert.Equal(new[] { 40, 41 }, Ids(page));
        Assert.Equal(new[] { "forty", "forty_one" }, Keys(page));
        Assert.Equal(40, page.Rows[0].Fields[0].Number);

        // The marks moved past the carried ids, so the next add cannot be handed one of them.
        int next = await store.AllocateAsync(Thing, 1);
        Assert.True(next > 41, FormattableString.Invariant($"The next plain id was {next}, which is at or under a carried id."));
    }

    /// <summary>
    /// FACT 19. An import into a NON-EMPTY database is refused with nothing written. That one rule is the
    /// answer to a whole class of seeding defect: a seed that runs repeatedly against live data either reverts
    /// an operator's value on the next deploy or is beaten forever by a stored row.
    /// </summary>
    [Fact]
    public virtual async Task Fact19_AnImportIntoANonEmptyDatabaseIsRefusedAndWritesNothing()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        ContentBundle bundle = new(
            ContentBundle.CurrentFormatVersion,
            "hand-authored",
            0,
            [],
            [new ContentBundleRow(Thing, 40, new ContentKey("forty"), false, null, CatalogFixtures.Fields(40))],
            [],
            []);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ImportBundleAsync(bundle, CatalogFixtures.Actor, CatalogFixtures.Operator, "seed"));

        Assert.Equal(ContentAuthoringException.CatalogNotEmptyReason, refused.Reason);
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Single(await store.ListVersionsAsync());
        Assert.Equal(new[] { 1 }, Ids(await RowsAsync(store)));
        Assert.Null(await store.GetOpenDraftAsync());
    }

    /// <summary>
    /// FACT 20. Export at version N then import into an empty store gives identical ROWS, KEYS and IDS. That
    /// is what lossless means, and it is the claim an operator relies on when they move a catalog between
    /// environments.
    /// <para>
    /// What does NOT come across is the version LINE, deliberately: an import republishes at version 1 with a
    /// fresh store epoch, because a bundle is not a backup. The two are asserted side by side so neither can
    /// be mistaken for the other.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task Fact20_AnExportAtAVersionImportsIntoAnEmptyStoreUnchanged()
    {
        IContentAuthoringStore source = await OpenAsync();
        await PublishAsync(
            source,
            ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)),
            ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)));
        await PublishAsync(
            source, ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Placeholder, 0));

        ContentBundle bundle = await source.ExportBundleAsync(2);
        string sourceEpoch = await source.GetStoreEpochAsync();
        ContentRowPage before = await RowsAsync(source, includeRetired: true);

        Assert.Equal(2, bundle.SourceVersion);
        Assert.Equal(2, bundle.Rows.Count);
        Assert.Single(bundle.Rules);

        IContentAuthoringStore target = await ResetToEmptyAsync();
        ContentPublishResult imported = await target.ImportBundleAsync(
            bundle, CatalogFixtures.Actor, CatalogFixtures.Operator, "seed");

        Assert.Equal(1, imported.VersionNumber);
        Assert.NotEqual(sourceEpoch, await target.GetStoreEpochAsync());

        ContentRowPage after = await RowsAsync(target, includeRetired: true);
        Assert.Equal(Ids(before), Ids(after));
        Assert.Equal(Keys(before), Keys(after));
        for (int i = 0; i < before.Rows.Count; i++)
        {
            Assert.Equal(before.Rows[i].IsRetired, after.Rows[i].IsRetired);
            Assert.Equal(before.Rows[i].Fields[0].Number, after.Rows[i].Fields[0].Number);
        }

        // The retired row came back retired and no SECOND rule was appended for it: the bundle already carries
        // the rule that retired it, restamped onto the new version line.
        IReadOnlyList<RemapRule> rules = await RulesAsync(target);
        RemapRule rule = Assert.Single(rules);
        Assert.Equal(RemapRuleKind.Retired, rule.Kind);
        Assert.Equal(1, rule.IntroducedIn);
    }
}
