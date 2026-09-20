using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// The interleaving the looping concurrency tests only reach by luck: a rival's write lands in the window
/// between one runner's no-draft check and its own write, so the ONE draft ends up holding two definitions'
/// edits and is therefore nobody's plan.
/// <para>
/// <b>Nobody publishes a draft like that and nobody used to discard it either.</b> It matches no plan, so the
/// exact-match proof says it is not this run's, and its note names a pending upgrade, so the run reads it as
/// a live rival's and waits. Both runners wait, the catalog stands still, and the run fails when the patience
/// runs out. That is the hosted-runner failure these tests pin down.
/// </para>
/// <para>
/// Every run here is given a HARD deadline well under the stand-off budget, because the defect's signature is
/// a stall rather than a wrong answer and a test that hangs for the whole budget reports nothing useful.
/// </para>
/// </summary>
public sealed class ContentUpgradeStaleApplyTests
{
    /// <summary>The deadline a run is given. The stand-off budget is a little over thirty seconds.</summary>
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>The stale write OPENS the draft and the runner's own write appends into it.</summary>
    [Fact]
    public Task AStaleApplyBeforeTheRunnersWriteStillPublishesEveryUpgradeInMemory()
        => StalePlanRacesAsync(DraftRaceMoment.BeforeTheRunnersWrite, sqlite: false);

    /// <summary>The runner's write opens the draft and the stale write appends into it.</summary>
    [Fact]
    public Task AStaleApplyAfterTheRunnersWriteStillPublishesEveryUpgradeInMemory()
        => StalePlanRacesAsync(DraftRaceMoment.AfterTheRunnersWrite, sqlite: false);

    /// <summary>The same interleaving on a real SQLite file, where a publish also writes a pack to disk.</summary>
    [Fact]
    public Task AStaleApplyBeforeTheRunnersWriteStillPublishesEveryUpgradeOnSqlite()
        => StalePlanRacesAsync(DraftRaceMoment.BeforeTheRunnersWrite, sqlite: true);

    /// <summary>The mirrored interleaving on a real SQLite file.</summary>
    [Fact]
    public Task AStaleApplyAfterTheRunnersWriteStillPublishesEveryUpgradeOnSqlite()
        => StalePlanRacesAsync(DraftRaceMoment.AfterTheRunnersWrite, sqlite: true);

    /// <summary>
    /// An operator's edit under ANOTHER actor, merged into the runner's draft. Nothing about it is this run's
    /// work, so the run stops and the draft keeps both edits.
    /// </summary>
    [Fact]
    public Task AMergedOperatorEditUnderAnotherActorIsLeftAlone()
        => ForeignEditRacesAsync("an-operator", ContentUpgradeRunner.NoteFor(UpgradeHarness.SecondId));

    /// <summary>
    /// An edit under the RUNNER's own actor and note that no shipped definition plans, which is the operator
    /// who edited an interrupted run's draft without a note. The actor proves nothing and the content decides.
    /// </summary>
    [Fact]
    public Task AMergedEditUnderTheRunnerActorThatNoPlanProducesIsLeftAlone()
        => ForeignEditRacesAsync(
            UpgradeFixtures.Actor, ContentUpgradeRunner.NoteFor(UpgradeHarness.SecondId));

    /// <summary>
    /// One run over a catalog where the rival replays this run's OWN earlier plan into the second upgrade's
    /// window. Whatever the interleaving, both upgrades land exactly once and no draft is left standing.
    /// </summary>
    static async Task StalePlanRacesAsync(DraftRaceMoment moment, bool sqlite)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await LeaseAsync(files, registry, sqlite);
        var race = new DraftRaceStore(
            lease.Store,
            ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId),
            ContentUpgradeRunner.NoteFor(UpgradeHarness.SecondId),
            moment);

        ContentUpgradeReport report = await RunAsync(race, registry);

        Assert.True(race.Raced, "the rival write never landed, so the interleaving was not exercised.");
        Assert.True(report.Success, string.Join(" | ", report.Lines));
        IReadOnlyList<ContentUpgradeRecord> ledger = await race.ListUpgradesAsync();
        Assert.Equal(2, ledger.Count);
        Assert.Null(await race.GetOpenDraftAsync());

        // Exactly the committed ids, once each. A stale write that reached a published version would show up
        // here as a duplicated or a renumbered row rather than as a failed run.
        ContentRowPage rows = await race.ListRowsAsync(UpgradeFixtures.Other, 0, null, false, 0, 50);
        Assert.Equal(2, rows.Total);
        Assert.Equal("new_row", rows.Rows[0].Key.ToString());
        Assert.Equal("second_new_row", rows.Rows[1].Key.ToString());
    }

    /// <summary>
    /// A rival write of content NO shipped definition plans, landing in the same window. The run stops for the
    /// operator and the draft keeps every edit it held.
    /// </summary>
    static async Task ForeignEditRacesAsync(string rivalActor, string rivalNote)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await LeaseAsync(files, registry, sqlite: false);
        var race = new DraftRaceStore(
            lease.Store,
            ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId),
            DraftRaceMoment.BeforeTheRunnersWrite,
            rivalActor,
            rivalNote,
            [ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("operator_row"), PublishFixtures.Fields(77))]);

        ContentUpgradeReport report = await RunAsync(race, registry);

        Assert.True(race.Raced, "the rival write never landed, so the interleaving was not exercised.");
        Assert.Equal(ContentUpgradeOutcome.OperatorDraftOpen, report.Outcome);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == ContentUpgradeCodes.OperatorDraftOpen);

        ContentDraft? draft = await race.GetOpenDraftAsync();
        Assert.NotNull(draft);
        Assert.Equal(2, draft.EditCount);
    }

    /// <summary>One apply run under a hard deadline, so a stall fails fast instead of spending the budget.</summary>
    static async Task<ContentUpgradeReport> RunAsync(DraftRaceStore race, ContentTypeRegistry registry)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        return await ContentUpgradeRunner.RunAsync(
            race, registry, SetFor(registry), UpgradeFixtures.Apply(), deadline.Token);
    }

    /// <summary>An OLDER catalog at version 1, in memory or on a real SQLite file.</summary>
    static async Task<ContentAuthoringStoreLease> LeaseAsync(
        TemporaryCatalogDatabase files,
        ContentTypeRegistry registry,
        bool sqlite)
    {
        IContentAuthoringStore store = sqlite
            ? new SqliteContentAuthoringStore(files.ConnectionString, registry, files.Pack())
            : new InMemoryContentAuthoringStore(registry, files.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.ApplyEditsAsync(
            [ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("old_row"), PublishFixtures.Fields(11))],
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator,
            "seed the older catalog");
        await store.PublishAsync(PublishFixtures.Request(0));
        return new ContentAuthoringStoreLease(store);
    }

    /// <summary>The two definitions this build ships, over a registry, with their committed target bundle.</summary>
    static ContentUpgradeSet SetFor(ContentTypeRegistry registry)
    {
        ContentBundle target = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "old_row", 11),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 1, "new_row", 22),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 2, "second_new_row", 33));
        return new ContentUpgradeSet(
            UpgradeFixtures.Adds(
                UpgradeHarness.FirstId, 1, target, UpgradeFixtures.Identity(UpgradeFixtures.Other, "new_row")),
            UpgradeFixtures.Adds(
                UpgradeHarness.SecondId,
                2,
                target,
                UpgradeFixtures.Identity(UpgradeFixtures.Other, "second_new_row")));
    }
}

/// <summary>One store held for the length of a test, disposed when it owns something disposable.</summary>
/// <param name="store">The store under test.</param>
internal sealed class ContentAuthoringStoreLease(IContentAuthoringStore store) : IDisposable
{
    /// <summary>The store.</summary>
    public IContentAuthoringStore Store => store;

    /// <inheritdoc />
    public void Dispose() => (store as IDisposable)?.Dispose();
}
