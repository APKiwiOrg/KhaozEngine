using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The two-runner interleaving that wedged a hosted boot, made deterministic and proved on every provider.
/// <para>
/// A runner reads the open draft, finds none, and writes its edits some time later. The store's write
/// APPENDS into whatever draft is open by then, so a second runner that planned against the older baseline
/// and reached its own write inside that window leaves the one draft holding TWO definitions' edits under
/// one actor. It is nobody's plan, so nobody may publish it, and a run that could not discard it either
/// stood off against it until its patience ran out.
/// </para>
/// <para>
/// It is here rather than only over the reference store because the draft is rebuilt from a stored edit
/// table on a real provider, and the discard proof compares those rebuilt edits against plans held in
/// memory. A provider that rounded a value or reordered a field set would break the proof and nothing else
/// would notice.
/// </para>
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>The id of the first definition of the race set.</summary>
    protected const string RaceFirstId = "conformance-race-adds-two";

    /// <summary>The id of the second definition of the race set, which plans on the first one's result.</summary>
    protected const string RaceSecondId = "conformance-race-adds-three";

    /// <summary>
    /// A stale write landing in the second upgrade's window still ends with both upgrades published exactly
    /// once, no draft standing and exactly the committed rows.
    /// </summary>
    [Fact]
    public virtual async Task TheUpgradeRunnerRecoversFromAStaleWriteInsideItsOwnPublishWindow()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        var race = new UpgradeDraftRaceStore(
            store,
            ContentUpgradeRunner.NoteFor(RaceFirstId),
            ContentUpgradeRunner.NoteFor(RaceSecondId));

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            race, Registry, RaceSet(), UpgradeOptions(ContentUpgradeMode.Apply));

        Assert.True(race.Raced, "the rival write never landed, so the interleaving was not exercised.");
        Assert.True(report.Success, string.Join(" | ", report.Lines));
        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);

        IReadOnlyList<ContentUpgradeRecord> ledger = await Ledger(store).ListUpgradesAsync();
        Assert.Equal(2, ledger.Count);
        Assert.Null(await store.GetOpenDraftAsync());

        // Exactly the committed rows under exactly the committed ids. A stale write that reached a published
        // version would show here as a duplicated or a renumbered row rather than as a failed run.
        ContentRowPage rows = await RowsAsync(store);
        Assert.Equal(["one", "two", "three"], Keys(rows));
        Assert.Equal([1, 2, 3], Ids(rows));
    }

    /// <summary>
    /// The PUBLISH window on every provider: an operator's edit lands after the runner inspected what its own
    /// write returned and before the publish freezes the draft. Nothing the operator wrote reaches a version,
    /// the run stops for them, and the draft is left holding both edits and unfrozen.
    /// <para>
    /// The operator's edit UPDATES a row the catalog already holds rather than adding one. An add would take
    /// an id from the allocator, collide with the id the upgrade's plan carries, and be refused at publish for
    /// a reason that has nothing to do with the window under test.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task TheUpgradeRunnerNeverPublishesAnOperatorEditThatLandsInItsPublishWindow()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        var race = new UpgradeDraftRaceStore(
            store,
            string.Empty,
            ContentUpgradeRunner.NoteFor(UpgradeId),
            ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(77)),
            "a-human-operator");

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            race, Registry, UpgradeSet(), UpgradeOptions(ContentUpgradeMode.Apply));

        Assert.True(race.Raced, "the operator's edit never landed, so the interleaving was not exercised.");

        // The evidence first: the operator's value inside a version the upgrade published.
        IReadOnlyList<ContentVersionRecord> versions = await store.ListVersionsAsync();
        ContentRowPage published = await RowsAsync(store, versions[0].VersionNumber);
        Assert.Equal(11, published.Rows[0].Fields[0].Number);

        Assert.Equal(ContentUpgradeOutcome.OperatorDraftOpen, report.Outcome);
        Assert.Single(versions);
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Empty(await Ledger(store).ListUpgradesAsync());

        ContentDraft draft = await DraftAsync(store);
        Assert.Equal(2, draft.EditCount);
        Assert.False(draft.IsFrozen, "the operator cannot edit or discard a draft the run left frozen.");
    }

    /// <summary>The two definitions the race ships, each adding one row of the committed target bundle.</summary>
    protected ContentUpgradeSet RaceSet()
    {
        ContentBundle target = UpgradeTarget(
            new ContentBundleRow(Thing, 1, new ContentKey("one"), false, null, CatalogFixtures.Fields(11)),
            new ContentBundleRow(Thing, 2, new ContentKey("two"), false, null, CatalogFixtures.Fields(22)),
            new ContentBundleRow(Thing, 3, new ContentKey("three"), false, null, CatalogFixtures.Fields(33)));
        return new ContentUpgradeSet(
            new ContentUpgradeDefinition(
                RaceFirstId,
                1,
                "adds the fixture type's second row",
                context => new ContentUpgradePlanBuilder(context, target)
                    .AddRow(Thing, new ContentKey("two"))
                    .Build()),
            new ContentUpgradeDefinition(
                RaceSecondId,
                2,
                "adds the fixture type's third row",
                context => new ContentUpgradePlanBuilder(context, target)
                    .AddRow(Thing, new ContentKey("three"))
                    .Build()));
    }
}
