using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;
using Xunit.Sdk;
using RaceGate = KhaozEngine.Tests.Catalog.ConditionalFreezeRaceStore.OnceGate;

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

    /// <summary>How long a race participant may take to reach a gate or to finish before the fact fails.</summary>
    static readonly TimeSpan RaceWatchdog = TimeSpan.FromSeconds(60);

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

    /// <summary>
    /// T6, the hosted sequence on every provider. Runner B parks at its first text commit on base 1. Runner A
    /// recovers B's draft, publishes version 2, freezes its second upgrade at 2 and parks at that commit. B resumes,
    /// is refused, releases base 1 twice without clearing A's marker, and is held at its failure-resolution draft
    /// read. A publishes version 3, then B adopts both upgrades.
    /// </summary>
    [Fact]
    public virtual async Task ConditionalFreeze_RunnersRecoverWithoutCrossVersionCleanup()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        ConditionalFreezeRaceStore runnerA = RaceParticipant(store);
        ConditionalFreezeRaceStore runnerB = RaceParticipant(store);
        using var bCommit = new RaceGate();
        using var bRecovery = new RaceGate();
        using var aCommit = new RaceGate();
        runnerB.ArmTextCommitEntry(bCommit, 1, RaceFirstId);
        runnerB.ArmRecoveryRead(bRecovery);
        runnerA.ArmTextCommitEntry(aCommit, 2, RaceSecondId);
        ContentUpgradeSet set = RaceSet();

        Task<ContentUpgradeReport> runB = Task.Run(
            () => ContentUpgradeRunner.RunAsync(runnerB, Registry, set, UpgradeOptions(ContentUpgradeMode.Apply)));
        await ReachAsync(bCommit, runB, "B's first text commit at base 1");
        Task<ContentUpgradeReport> runA = Task.Run(
            () => ContentUpgradeRunner.RunAsync(runnerA, Registry, set, UpgradeOptions(ContentUpgradeMode.Apply)));
        await ReachAsync(aCommit, runA, "A's second upgrade text commit at base 2");

        bCommit.Resume();
        await ReachAsync(bRecovery, runB, "B's failure-resolution draft read");
        int? markerAfterBCleanup = (await store.GetOpenDraftAsync())?.FrozenForBaseVersion;
        aCommit.Resume();
        ContentUpgradeReport a = await runA.WaitAsync(RaceWatchdog);
        bRecovery.Resume();
        ContentUpgradeReport b = await runB.WaitAsync(RaceWatchdog);

        Assert.True(a.Success, string.Join(" | ", a.Lines));
        Assert.True(b.Success, string.Join(" | ", b.Lines));
        Assert.Equal(2, markerAfterBCleanup);
        Assert.Equal(2, runnerA.TextCommitCalls);
        Assert.Equal(1, runnerB.TextCommitCalls);
        Assert.Equal(0, runnerA.RowCommitCalls + runnerB.RowCommitCalls);
        Assert.Equal(new[] { 1, 1 }, runnerB.ReleaseBases);
        Assert.Equal(new[] { false, false }, runnerB.ReleaseResults);
        Assert.All(runnerB.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.Equal(0, runnerA.LegacyFreezeCalls + runnerA.LegacyClearCalls);
        Assert.Equal(0, runnerB.LegacyFreezeCalls + runnerB.LegacyClearCalls);
        Assert.Contains(a.Diagnostics, d => d.Code == ContentUpgradeCodes.DraftRecovered);
        // B adopts the first upgrade through the ledger once its own publish failed, then re-reads version 3,
        // where the second plans as already satisfied and is recorded without a publish attempt.
        Assert.Single(b.Diagnostics, d => d.Code == ContentUpgradeCodes.AppliedConcurrently);
        Assert.Equal(ContentUpgradeStepState.Adopted, b.Steps[^1].State);

        Assert.Equal(new[] { 1, 2, 3 }, (await store.ListVersionsAsync()).Select(v => v.VersionNumber).Order());
        IReadOnlyList<ContentUpgradeRecord> ledger = await Ledger(store).ListUpgradesAsync();
        Assert.Equal(new[] { 2, 3 }, ledger.Select(r => r.VersionNumber).Order());
        Assert.All(ledger, r => Assert.Equal(ContentUpgradeDisposition.Applied, r.Disposition));
        ContentRowPage rows = await RowsAsync(store);
        Assert.Equal([1, 2, 3], Ids(rows));
        Assert.Equal(["one", "two", "three"], Keys(rows));
        Assert.Null(await store.GetOpenDraftAsync());
    }

    /// <summary>
    /// T6, a stale runner on every provider. Runner R writes its row-only plan and parks before its guarded freeze. A
    /// console publishes R's draft as version 2, changes <c>one</c> to 77, and P2 freezes that draft at 2 and parks at
    /// its commit. R's freeze at 1 is refused with nothing written and its release of base 1 clears nothing, so P2
    /// commits version 3 with the operator's value and R adopts the upgrade there.
    /// </summary>
    [Fact]
    public virtual async Task ConditionalFreeze_StaleRunnerCannotOverwriteNewerMarker()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        ConditionalFreezeRaceStore runner = RaceParticipant(store);
        ConditionalFreezeRaceStore console = RaceParticipant(store);
        ConditionalFreezeRaceStore winner = RaceParticipant(store);
        using var runnerFreeze = new RaceGate();
        using var runnerRelease = new RaceGate();
        using var winnerCommit = new RaceGate();
        runner.ArmRunnerFreezeEntry(runnerFreeze, 1);
        runner.ArmFirstReleaseExit(runnerRelease);
        winner.ArmTextCommitEntry(winnerCommit, 2);

        Task<ContentUpgradeReport> run = Task.Run(
            () => ContentUpgradeRunner.RunAsync(runner, Registry, UpgradeSet(), UpgradeOptions(ContentUpgradeMode.Apply)));
        await ReachAsync(runnerFreeze, run, "R's guarded freeze at base 1");
        Assert.Equal(UpgradeActor, (await DraftAsync(store)).OpenedBy);
        Assert.Equal(2, (await console.PublishAsync(Request(1)).WaitAsync(RaceWatchdog)).VersionNumber);

        await ApplyAsync(store, ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(77)));
        Task<ContentPublishResult> newer = Task.Run(() => winner.PublishAsync(Request(2)));
        await ReachAsync(winnerCommit, newer, "P2's text commit at base 2");

        runnerFreeze.Resume();
        await ReachAsync(runnerRelease, run, "R's first release exit");
        int? markerAfterRunnerCleanup = (await store.GetOpenDraftAsync())?.FrozenForBaseVersion;
        winnerCommit.Resume();
        ContentPublishResult winnerResult = await newer.WaitAsync(RaceWatchdog);
        runnerRelease.Resume();
        ContentUpgradeReport report = await run.WaitAsync(RaceWatchdog);

        Assert.Equal(2, markerAfterRunnerCleanup);
        Assert.Equal(1, runner.GuardedFreezeCalls);
        Assert.Equal(new[] { 1 }, runner.ReleaseBases);
        Assert.Equal(new[] { false }, runner.ReleaseResults);
        Assert.All(runner.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.Equal(0, runner.LegacyFreezeCalls + runner.LegacyClearCalls);
        Assert.Equal(1, winner.TextCommitCalls);
        Assert.Equal(0, winner.RowCommitCalls);
        Assert.Equal(3, winnerResult.VersionNumber);
        Assert.True(report.Success, string.Join(" | ", report.Lines));

        Assert.Equal(3, await store.GetActiveVersionAsync());
        ContentUpgradeRecord recorded = Assert.Single(await Ledger(store).ListUpgradesAsync());
        Assert.Equal(UpgradeId, recorded.Id);
        Assert.Equal(ContentUpgradeDisposition.Adopted, recorded.Disposition);
        Assert.Equal(3, recorded.VersionNumber);
        ContentRowPage rows = await RowsAsync(store, 3);
        Assert.Equal([1, 2], Ids(rows));
        Assert.Equal(77, rows.Rows[0].Fields[0].Number);
        Assert.Null(await store.GetOpenDraftAsync());
    }

    /// <summary>
    /// Every server race decorator that declares the guarded freeze refuses an inner store that does not, when it is
    /// built, before it calls a single member of it.
    /// </summary>
    [Fact]
    public virtual async Task ConditionalFreeze_RaceDecoratorsRefuseAnIncapableStore()
    {
        IContentAuthoringStore store = await OpenAsync();
        IContentAuthoringStore incapable = DispatchProxy.Create<IContentAuthoringStore, IncapableStoreProxy>();

        Assert.Throws<ArgumentException>(() => new ConditionalFreezeRaceStore(incapable, Ids(store), Registry, PackOf(store)));
        Assert.Throws<ArgumentException>(() => new WriteRefusingAuthoringStore(incapable));
    }

    /// <summary>A race participant over the one provider store, publishing over itself.</summary>
    /// <param name="store">The provider store.</param>
    ConditionalFreezeRaceStore RaceParticipant(IContentAuthoringStore store)
        => new(store, Ids(store), Registry, PackOf(store));

    /// <summary>Waits for a participant to park on its gate, failing with what it did if it ended first.</summary>
    static async Task ReachAsync(RaceGate gate, Task participant, string what)
    {
        Task first = await Task.WhenAny(gate.Entered, participant).WaitAsync(RaceWatchdog);
        if (first != gate.Entered)
        {
            throw new XunitException($"the participant ended before it reached {what}: {participant.Exception?.GetBaseException().Message ?? "completed"}");
        }
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

    /// <summary>
    /// A store that implements only the released authoring seam and answers no member, which is what a decorator's
    /// constructor must refuse without calling it.
    /// </summary>
    internal class IncapableStoreProxy : DispatchProxy
    {
        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException("a decorator called the incapable store it should have refused.");
    }
}
