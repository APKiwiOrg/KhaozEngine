using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Facts 18, 19, 20 and 24, the BUNDLE half: the seeding format is also the lossless export format, it lands
/// into an empty database or not at all, and a refusal takes its own staging with it.
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

    /// <summary>
    /// FACT 24. An import refused while it STAGES leaves nothing staged. The families, the id marks and the
    /// restamped rules are written before the publish because the edits and the baseline both need them, so
    /// the window between them and the publish is the one place an import can leave a database it was
    /// required to find empty carrying half a bundle.
    /// <para>
    /// The refusal is a row naming a family the bundle does not declare, which is raised while the edits are
    /// being built: INSIDE the staging block rather than after it, which is the difference that matters. A
    /// refusal after the staging was already covered, and it is the one the earlier facts drive.
    /// </para>
    /// <para>
    /// The rule list is read through the BASELINE deliberately. A provider holds the restamped rules in
    /// memory until the publish commits them, because a rule stamped at version 1 cannot be written before
    /// version 1 exists, and a refused import that leaves them cached poisons the next ordinary publish with
    /// rules for a version nothing ever published.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task Fact24_AnImportRefusedWhileItStagesLeavesNothingStaged()
    {
        IContentAuthoringStore store = await ResetToEmptyAsync();

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ImportBundleAsync(
                SeedBundle("no_such_family"), CatalogFixtures.Actor, CatalogFixtures.Operator, "seed"));
        Assert.Equal(ContentAuthoringException.UnknownFamilyReason, refused.Reason);

        Assert.Empty(await store.ListFamiliesAsync(default));
        Assert.Empty(await RulesAsync(store));
        Assert.Null(await store.GetOpenDraftAsync());
        ContentIdHighWater mark = await Ids(store).ReadHighWaterAsync(Thing);
        Assert.Equal(0, mark.ReservedThrough);
        Assert.Equal(0, mark.IssuedThrough);

        // The corrected bundle into the SAME store. A family row the refusal left behind would collide on
        // its own primary key here, and a rule list it left behind would come back doubled.
        ContentPublishResult imported = await store.ImportBundleAsync(
            SeedBundle(FamilyKey), CatalogFixtures.Actor, CatalogFixtures.Operator, "seed");

        Assert.Equal(1, imported.VersionNumber);
        Assert.Single(await store.ListFamiliesAsync(default));
        Assert.Single(await RulesAsync(store));
        Assert.Equal(new[] { 40, 41 }, Ids(await RowsAsync(store, includeRetired: true)));

        // And an ordinary publish on top of it still works, which is what a store carrying a stale cached
        // rule list could not do.
        ContentPublishResult published = await PublishAsync(
            store, ContentEdit.Update(Thing, 40, new ContentKey("forty"), CatalogFixtures.Fields(99)));
        Assert.Equal(2, published.VersionNumber);
        Assert.Single(await RulesAsync(store));
    }

    /// <summary>The family key fact 24's bundle declares, and the one its rows are supposed to name.</summary>
    const string FamilyKey = "seeded";

    /// <summary>
    /// Fact 24's bundle: one family with an aligned block covering both rows, one live row and one retired
    /// row with the rule that retired it, and the retired row naming <paramref name="familyKey"/>.
    /// </summary>
    /// <param name="familyKey">The family the second row names, which is the bundle's declared one or not.</param>
    static ContentBundle SeedBundle(string familyKey)
        => new(
            ContentBundle.CurrentFormatVersion,
            "hand-authored",
            0,
            [],
            [
                new ContentBundleRow(
                    Thing, 40, new ContentKey("forty"), false, FamilyKey, CatalogFixtures.Fields(40)),
                new ContentBundleRow(
                    Thing, 41, new ContentKey("forty_one"), true, familyKey, CatalogFixtures.Fields(41)),
            ],
            [
                new ContentFamily(
                    1,
                    Thing,
                    FamilyKey,
                    16,
                    false,
                    1,
                    [new ContentFamilyBlock(1, 0, 32, 16, 42, 1)]),
            ],
            [
                new RemapRule(
                    1,
                    1,
                    Thing,
                    RemapRuleKind.Retired,
                    41,
                    0,
                    [RemapRule.RetirePolicyPlaceholder]),
            ]);
}
