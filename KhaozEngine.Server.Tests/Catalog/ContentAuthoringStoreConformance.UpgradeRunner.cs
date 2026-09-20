using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The upgrade RUNNER over the seam, proved the same way on the in-memory reference, SQLite and SQL Server.
/// The runner's own edge cases live in <c>KhaozEngine.Catalog.Tests</c> against the reference store, and
/// these are the four facts that have to hold on a real provider: it publishes, it then writes nothing, it
/// records a fresh install's baseline, and it will not publish over an operator's draft.
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>The actor a conformance upgrade run carries, which also proves a draft is the runner's own.</summary>
    protected const string UpgradeActor = "conformance-upgrade-runner";

    /// <summary>The id of the one definition these facts ship.</summary>
    protected const string UpgradeId = "conformance-adds-a-row";

    /// <summary>A pending upgrade publishes as its own version and writes its ledger row inside that commit.</summary>
    [Fact]
    public virtual async Task TheUpgradeRunnerPublishesAPendingUpgradeAsItsOwnVersion()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, Registry, UpgradeSet(), UpgradeOptions(ContentUpgradeMode.Apply));

        Assert.True(report.Success, string.Join(" | ", report.Lines));
        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(1, report.ActiveVersionBefore);
        Assert.Equal(2, report.ActiveVersionAfter);
        Assert.Equal(2, await store.GetActiveVersionAsync());

        ContentUpgradeRecord record = Assert.Single(await Ledger(store).ListUpgradesAsync());
        Assert.Equal(UpgradeId, record.Id);
        Assert.Equal(ContentUpgradeDisposition.Applied, record.Disposition);
        Assert.Equal(2, record.VersionNumber);
        Assert.Equal(["one", "two"], Keys(await RowsAsync(store)));
        Assert.Null(await store.GetOpenDraftAsync());
    }

    /// <summary>
    /// A second run against the same catalog writes NOTHING: no version, no audit row, no ledger row and no
    /// draft. That is the read every boot of an already-current catalog pays.
    /// </summary>
    [Fact]
    public virtual async Task TheUpgradeRunnerWritesNothingOnceEveryUpgradeIsRecorded()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await ContentUpgradeRunner.RunAsync(
            store, Registry, UpgradeSet(), UpgradeOptions(ContentUpgradeMode.Apply));
        (int versions, int audit, int ledger) before = await FootprintAsync(store);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, Registry, UpgradeSet(), UpgradeOptions(ContentUpgradeMode.Apply));

        Assert.Equal(ContentUpgradeOutcome.UpToDate, report.Outcome);
        Assert.Equal(before, await FootprintAsync(store));
        Assert.Null(await store.GetOpenDraftAsync());
    }

    /// <summary>
    /// A FRESH install imports the shipped bundle and records the baseline, after which the runner has
    /// nothing to do and publishes no version of its own.
    /// </summary>
    [Fact]
    public virtual async Task TheUpgradeRunnerHasNothingToDoAfterAFreshInstallRecordsItsBaseline()
    {
        IContentAuthoringStore store = await ResetToEmptyAsync();
        await store.ImportBundleAsync(
            UpgradeTarget(), CatalogFixtures.Actor, CatalogFixtures.Operator, "fresh install");
        await ContentUpgradeRunner.RecordBaselineAsync(
            store, UpgradeSet(), UpgradeActor, CatalogFixtures.Operator);
        (int versions, int audit, int ledger) before = await FootprintAsync(store);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, Registry, UpgradeSet(), UpgradeOptions(ContentUpgradeMode.Apply));

        Assert.Equal(ContentUpgradeOutcome.UpToDate, report.Outcome);
        Assert.Equal(1, before.versions);
        Assert.Equal(1, before.ledger);
        Assert.Equal(before, await FootprintAsync(store));
    }

    /// <summary>
    /// An open draft the runner cannot prove is its own is operator work. It is left untouched, edits and
    /// all, and the run refuses rather than publishing over it.
    /// </summary>
    [Fact]
    public virtual async Task TheUpgradeRunnerRefusesWhileAnOperatorDraftIsOpen()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await ApplyAsync(store, ContentEdit.Update(
            Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(55)));
        (int versions, int audit, int ledger) before = await FootprintAsync(store);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, Registry, UpgradeSet(), UpgradeOptions(ContentUpgradeMode.Apply));

        Assert.Equal(ContentUpgradeOutcome.OperatorDraftOpen, report.Outcome);
        Assert.False(report.Success);
        Assert.Equal(ContentBootResult.ContentFailureExitCode, report.ExitCode);
        Assert.Equal(before, await FootprintAsync(store));
        Assert.Equal(1, (await DraftAsync(store)).EditCount);
    }

    /// <summary>
    /// The data-loss path an ownership proof of actor plus note cannot close. A run dies after its edits, an
    /// operator adds one of their own WITHOUT a note, and both stores keep the standing note and never
    /// rewrite the identity that opened the draft, so the draft still carries the runner's actor and the
    /// runner's note. The edits are what say it is no longer the runner's work.
    /// </summary>
    [Fact]
    public virtual async Task TheUpgradeRunnerLeavesItsOwnDraftAloneOnceAnOperatorHasAddedToIt()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await store.ApplyEditsAsync(
            await UpgradeEditsAsync(store),
            UpgradeActor,
            CatalogFixtures.Operator,
            ContentUpgradeRunner.NoteFor(UpgradeId));
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(55))],
            "a-human-operator",
            CatalogFixtures.Operator,
            string.Empty);

        ContentDraft standing = await DraftAsync(store);
        Assert.Equal(2, standing.EditCount);
        Assert.Equal(UpgradeActor, standing.OpenedBy);
        Assert.Equal(ContentUpgradeRunner.NoteFor(UpgradeId), standing.Note);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, Registry, UpgradeSet(), UpgradeOptions(ContentUpgradeMode.Apply));

        Assert.Equal(ContentUpgradeOutcome.OperatorDraftOpen, report.Outcome);
        Assert.Equal(2, (await DraftAsync(store)).EditCount);
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Empty(await Ledger(store).ListUpgradesAsync());
    }

    /// <summary>
    /// The edits this build's one definition plans against the catalog as it stands, which is what an
    /// interrupted run would have left in the draft.
    /// </summary>
    /// <param name="store">The store to plan against.</param>
    protected async Task<IReadOnlyList<ContentEdit>> UpgradeEditsAsync(IContentAuthoringStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        int active = await store.GetActiveVersionAsync();
        ContentBundle baseline = await store.ExportBundleAsync(active);
        ContentUpgradeDefinition definition = UpgradeSet().Definitions[0];
        return definition.Plan(new ContentUpgradeContext(active, baseline, Registry)).Edits;
    }

    /// <summary>The one definition these facts ship, which adds the second row of the fixture type.</summary>
    protected ContentUpgradeSet UpgradeSet()
    {
        ContentBundle target = UpgradeTarget();
        return new ContentUpgradeSet(new ContentUpgradeDefinition(
            UpgradeId,
            1,
            "adds the fixture type's second row",
            context => new ContentUpgradePlanBuilder(context, target)
                .AddRow(Thing, new ContentKey("two"))
                .Build()));
    }

    /// <summary>
    /// The committed bundle this build ships, built from the conformance registry's own declarations so it
    /// agrees with it by construction. That is what a game's committed bundle IS.
    /// </summary>
    protected ContentBundle UpgradeTarget() => UpgradeTarget(
        new ContentBundleRow(Thing, 1, new ContentKey("one"), false, null, CatalogFixtures.Fields(11)),
        new ContentBundleRow(Thing, 2, new ContentKey("two"), false, null, CatalogFixtures.Fields(22)));

    /// <summary>The same bundle over a caller's own rows, for a fact that ships more than one upgrade.</summary>
    /// <param name="rows">Every row the shipped catalog carries, each naming its stable id.</param>
    protected ContentBundle UpgradeTarget(params ContentBundleRow[] rows)
    {
        IReadOnlyList<ContentTypeRegistration> registered = Registry.ByTypeId;
        var types = new List<ContentBundleType>(registered.Count);
        for (int i = 0; i < registered.Count; i++)
        {
            ContentTypeRegistration registration = registered[i];
            types.Add(new ContentBundleType(
                registration.Type,
                registration.TypeKey,
                registration.DefaultVisibility,
                registration.ChunkSlots,
                registration.MaxDefinitionId,
                registration.Schema));
        }

        return new ContentBundle(
            ContentBundle.CurrentFormatVersion,
            "conformance-target",
            0,
            types,
            rows,
            [],
            []);
    }

    /// <summary>The options a conformance run carries, which name the runner's own actor.</summary>
    /// <param name="mode">Preview or apply.</param>
    protected static ContentUpgradeOptions UpgradeOptions(ContentUpgradeMode mode)
        => new(mode, UpgradeActor, CatalogFixtures.Operator, 0, 0);

    /// <summary>The three counts a "nothing was written" assertion compares.</summary>
    /// <param name="store">The store.</param>
    static async Task<(int versions, int audit, int ledger)> FootprintAsync(IContentAuthoringStore store)
    {
        IReadOnlyList<ContentVersionRecord> versions = await store.ListVersionsAsync();
        IReadOnlyList<ContentAuditEntry> audit = await store.ListAuditAsync(default, 0, 0, 500);
        IReadOnlyList<ContentUpgradeRecord> ledger = await Ledger(store).ListUpgradesAsync();
        return (versions.Count, audit.Count, ledger.Count);
    }
}
