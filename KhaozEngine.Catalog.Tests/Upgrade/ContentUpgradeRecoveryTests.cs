using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// Interruption recovery and the two operator holds. Each of these leaves the catalog in a state a killed
/// process really produces, and each one has to come back to exactly one published version per definition.
/// </summary>
public sealed class ContentUpgradeRecoveryTests
{
    /// <summary>An edit no definition plans, which is what makes a draft carrying it somebody else's.</summary>
    static ContentEdit Foreign
        => ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("leftover"), PublishFixtures.Fields(77));

    /// <summary>
    /// A run killed between its edits and its publish leaves an UNFROZEN draft holding exactly this build's
    /// plan. The next run publishes THAT draft rather than discarding it and redoing the work, so the one
    /// edit write in the whole run belongs to the second definition.
    /// </summary>
    [Fact]
    public async Task AnInterruptedRunsOwnUnfrozenDraftIsPublishedRatherThanRedone()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        await LeaveInterruptedDraftAsync(harness, freeze: false);
        var counting = new CountingContentAuthoringStore(harness.Store);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            counting, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        await AssertRecoveredAsync(harness, report);
        Assert.Equal(1, counting.EditWrites);
        Assert.Equal(0, counting.Discards);
        Assert.Equal(0, counting.FreezeClears);
    }

    /// <summary>
    /// A run killed between the publish's FREEZE and its commit leaves a FROZEN draft, and nothing in the
    /// seam says whether the publisher that set the marker is dead or live. The runner publishes the draft
    /// as it stands, which is the store's own stated recovery, and the catalog still ends at exactly one
    /// version per definition.
    /// </summary>
    [Fact]
    public async Task AnInterruptedRunsOwnFrozenDraftIsPublishedAndEndsAsExactlyOneVersion()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        await LeaveInterruptedDraftAsync(harness, freeze: true);
        Assert.True((await harness.Store.GetOpenDraftAsync())!.IsFrozen);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        await AssertRecoveredAsync(harness, report);
    }

    /// <summary>
    /// The freeze a RIVAL publisher holds is never cleared by this runner. Waiting cannot outlast a pack
    /// write to blob storage and nothing tells a live marker from a dead one, so clearing one would let an
    /// operator's edit land in a draft the rival's commit then deletes. The only thing the runner does to a
    /// frozen draft it can prove holds its own plan is PUBLISH it.
    /// </summary>
    [Fact]
    public async Task TheRunnerNeverClearsAFreezeItDidNotSetItself()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        await LeaveInterruptedDraftAsync(harness, freeze: true);
        var counting = new CountingContentAuthoringStore(harness.Store);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            counting, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(0, counting.FreezeClearsBeforeFirstPublish);
        Assert.Equal(0, counting.FreezeClears);
        Assert.Equal(0, counting.Discards);
    }

    /// <summary>
    /// A host that dies AFTER the commit point leaves the version live, the ledger row written and the draft
    /// deleted, and the caller sees only an exception. The runner reads the LEDGER rather than assuming, so
    /// the upgrade is never published a second time and the run carries on to the next definition.
    /// </summary>
    [Fact]
    public async Task ACrashAfterTheCommitPointIsResolvedFromTheLedgerAndPublishesOnce()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        var crashing = new CrashAfterCommitStore(harness.Store, harness.Packs, harness.Registry);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            crashing, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(3, report.ActiveVersionAfter);
        Assert.Equal(2, crashing.Commits);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => string.Equals(
                diagnostic.Code, ContentUpgradeCodes.AppliedConcurrently, StringComparison.Ordinal));
        await AssertRecoveredAsync(harness, report);
    }

    /// <summary>
    /// An open draft the runner cannot prove is its own is OPERATOR work. It is left exactly as it stands,
    /// edits and all, and the run refuses rather than publishing over it.
    /// </summary>
    [Fact]
    public async Task AnOperatorDraftStopsTheRunAndIsLeftUntouched()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        await harness.Store.ApplyEditsAsync(
            [ContentEdit.Update(
                UpgradeFixtures.Thing, 1, new ContentKey("old_row"), PublishFixtures.Fields(55))],
            "a-human-operator",
            "oid:human",
            "autumn price pass");
        CatalogFootprint before = await harness.FootprintAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.OperatorDraftOpen, report.Outcome);
        Assert.False(report.Success);
        Assert.Equal(before, await harness.FootprintAsync());
        Assert.Equal(1, (await harness.Store.GetOpenDraftAsync())!.EditCount);
        Assert.Equal(ContentUpgradeCodes.OperatorDraftOpen, report.Diagnostics[0].Code);
    }

    /// <summary>
    /// A draft the runner opened for an upgrade whose id is NO LONGER pending is not its to discard either.
    /// The note names an upgrade the ledger already holds, so the draft belongs to something else entirely.
    /// </summary>
    [Fact]
    public async Task ADraftNamingAnUpgradeThatIsNoLongerPendingIsNotTheRunnersToDiscard()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        await harness.Store.RecordUpgradeAsync(
            harness.First.Stamp,
            ContentUpgradeDisposition.Adopted,
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator);
        await harness.Store.ApplyEditsAsync(
            [Foreign],
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator,
            ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId));

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.OperatorDraftOpen, report.Outcome);
        Assert.Equal(1, (await harness.Store.GetOpenDraftAsync())!.EditCount);
    }

    /// <summary>
    /// An EXTRA edit in an interrupted run's draft is an operator's work, whatever the actor and the note
    /// still say. The draft is left standing with both edits and the run refuses.
    /// </summary>
    [Fact]
    public async Task AnExtraEditInTheRunnersOwnDraftMakesItOperatorWork()
        => await AssertDraftLeftAloneAsync(async harness =>
        {
            await LeaveInterruptedDraftAsync(harness, freeze: false);
            await harness.Store.ApplyEditsAsync(
                [Foreign], "a-human-operator", "oid:human", string.Empty);
            return 2;
        });

    /// <summary>
    /// A REMOVED edit is the same answer. An operator who discarded half the work and re-added one edit
    /// leaves a draft that reproduces no plan.
    /// </summary>
    [Fact]
    public async Task ADraftHoldingFewerEditsThanThePlanIsOperatorWork()
        => await AssertDraftLeftAloneAsync(async harness =>
        {
            await harness.Store.ApplyEditsAsync(
                [Foreign],
                UpgradeFixtures.Actor,
                UpgradeFixtures.Operator,
                ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId));
            return 1;
        });

    /// <summary>
    /// A changed FIELD VALUE under an unchanged target is the case an actor-and-note proof cannot see at
    /// all: the edit count, the targets and the operations are identical and only the value moved.
    /// </summary>
    [Fact]
    public async Task AChangedFieldValueInTheRunnersOwnDraftMakesItOperatorWork()
        => await AssertDraftLeftAloneAsync(async harness =>
        {
            IReadOnlyList<ContentEdit> planned = await PlannedEditsAsync(harness, harness.First);
            ContentEdit only = Assert.Single(planned);
            await harness.Store.ApplyEditsAsync(
                [ContentEdit.Import(only.Type, only.DefinitionId, only.Key, PublishFixtures.Fields(99))],
                UpgradeFixtures.Actor,
                UpgradeFixtures.Operator,
                ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId));
            return 1;
        });

    /// <summary>
    /// One older catalog, one draft left in the state the arrange step describes, and the two things that
    /// have to hold: the run refuses and the draft still carries every edit it carried.
    /// </summary>
    /// <param name="arrange">Leaves the draft and answers how many edits it should still hold.</param>
    static async Task AssertDraftLeftAloneAsync(Func<UpgradeHarness, Task<int>> arrange)
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        int edits = await arrange(harness);
        CatalogFootprint before = await harness.FootprintAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.OperatorDraftOpen, report.Outcome);
        Assert.Equal(ContentUpgradeCodes.OperatorDraftOpen, report.Diagnostics[0].Code);
        Assert.Equal(before, await harness.FootprintAsync());
        Assert.Equal(edits, (await harness.Store.GetOpenDraftAsync())!.EditCount);
    }

    /// <summary>
    /// An operator draft opened AFTER the step 5 gate is still operator work. It is reported at once rather
    /// than mistaken for another publisher and waited out until the attempt budget is spent.
    /// </summary>
    [Fact]
    public async Task AnOperatorDraftOpenedAfterTheGateStopsTheRunAtOnce()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        var appearing = new DraftAppearsAfterTheGateStore(
            harness.Store, "a-human-operator", "autumn price pass", Foreign);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            appearing, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.OperatorDraftOpen, report.Outcome);
        Assert.Equal(2, appearing.Looks);
        Assert.Single(await harness.Store.ListVersionsAsync());
        Assert.Equal(1, (await harness.Store.GetOpenDraftAsync())!.EditCount);
    }

    /// <summary>
    /// A draft another RUNNER holds is waited out, not refused. It carries the runner actor and a note
    /// naming a pending upgrade, so it belongs to a publish in flight, and a boot that gave up on one would
    /// be an outage where an ordinary two-replica deploy is enough.
    /// </summary>
    [Fact]
    public async Task ARivalRunnersDraftOpenedAfterTheGateIsWaitedOut()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        var appearing = new DraftAppearsAfterTheGateStore(
            harness.Store,
            UpgradeFixtures.Actor,
            ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId),
            Foreign,
            withdrawAtLook: 4);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            appearing, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.True(appearing.Looks >= 4, "the run stood off rather than refusing at the first look.");
        await AssertRecoveredAsync(harness, report);
    }

    /// <summary>
    /// A rival that keeps MAKING PROGRESS is waited out past one whole attempt budget. The patience is spent
    /// on a catalog that is not moving, not on a clock, which is what stops a loaded machine turning a
    /// correct run into a failure. Each attempt costs two looks at the draft, one for the publish pre-flight
    /// and one for the progress reading, so ninety-five of them is comfortably past the forty attempts a
    /// still catalog buys.
    /// </summary>
    [Fact]
    public async Task ARivalThatKeepsMovingIsWaitedOutPastOneAttemptBudget()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        var appearing = new DraftAppearsAfterTheGateStore(
            harness.Store,
            UpgradeFixtures.Actor,
            ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId),
            Foreign,
            withdrawAtLook: 95,
            churn: true);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            appearing, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.True(appearing.Looks >= 95, "the run gave up while the catalog was still moving.");
    }

    /// <summary>
    /// A pin on the ACTIVE version does not block the publish and is never moved. The report names the
    /// version to repin to, because the pin is what a restart will serve.
    /// </summary>
    [Fact]
    public async Task APinOnTheActiveVersionPublishesAndLeavesThePinWithAnInstruction()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        await harness.Store.SetPinnedVersionAsync(1, "a-human-operator", "oid:human");

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(3, report.ActiveVersionAfter);
        Assert.Equal(1, await harness.Store.GetPinnedVersionAsync());
        ContentUpgradeDiagnostic held = Assert.Single(
            report.Diagnostics,
            diagnostic => string.Equals(diagnostic.Code, ContentUpgradeCodes.PinHeld, StringComparison.Ordinal));
        Assert.Contains("version 3", held.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pin on ANOTHER version means the baseline is not what a restart serves, so the run refuses and
    /// changes nothing.
    /// </summary>
    [Fact]
    public async Task APinOnAnotherVersionStopsTheRunWithNothingChanged()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        await harness.Store.ApplyEditsAsync(
            [ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("another_row"), PublishFixtures.Fields(12))],
            "a-human-operator",
            "oid:human",
            "a second version");
        await harness.Store.PublishAsync(PublishFixtures.Request(1));
        await harness.Store.SetPinnedVersionAsync(1, "a-human-operator", "oid:human");
        CatalogFootprint before = await harness.FootprintAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.PinnedElsewhere, report.Outcome);
        Assert.Equal(before, await harness.FootprintAsync());
        Assert.Equal(1, await harness.Store.GetPinnedVersionAsync());
    }

    /// <summary>
    /// Leaves the store exactly as a run killed between its edits and its publish would: the runner's actor,
    /// the runner's note, and the EDITS that run's definition plans, which is the third half of the proof.
    /// </summary>
    static async Task LeaveInterruptedDraftAsync(UpgradeHarness harness, bool freeze)
    {
        await harness.Store.ApplyEditsAsync(
            await PlannedEditsAsync(harness, harness.First),
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator,
            ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId));
        if (freeze)
        {
            await harness.Store.FreezeDraftAsync(1);
        }
    }

    /// <summary>The edits one definition plans against the catalog as it stands.</summary>
    static async Task<IReadOnlyList<ContentEdit>> PlannedEditsAsync(
        UpgradeHarness harness,
        ContentUpgradeDefinition definition)
    {
        int active = await harness.Store.GetActiveVersionAsync();
        ContentBundle baseline = await harness.Store.ExportBundleAsync(active);
        return definition.Plan(new ContentUpgradeContext(active, baseline, harness.Registry)).Edits;
    }

    /// <summary>
    /// Exactly one published version per definition, both ledger rows, no draft left standing, and EXACTLY
    /// the committed rows in the catalog: no leftover of the interrupted run and no row twice.
    /// </summary>
    static async Task AssertRecoveredAsync(UpgradeHarness harness, ContentUpgradeReport report)
    {
        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(3, report.ActiveVersionAfter);
        Assert.Equal(3, (await harness.Store.ListVersionsAsync()).Count);
        Assert.Equal(2, (await harness.Store.ListUpgradesAsync()).Count);
        Assert.Null(await harness.Store.GetOpenDraftAsync());

        ContentRowPage things = await harness.Store.ListRowsAsync(
            UpgradeFixtures.Thing, 0, null, true, 0, 50);
        Assert.Equal(["old_row"], Keys(things));
        ContentRowPage others = await harness.Store.ListRowsAsync(
            UpgradeFixtures.Other, 0, null, true, 0, 50);
        Assert.Equal(["new_row", "second_new_row"], Keys(others));

        IReadOnlyList<ContentUpgradeRecord> ledger = await harness.Store.ListUpgradesAsync();
        Assert.Equal([2, 3], VersionNumbers(ledger));
    }

    static string[] Keys(ContentRowPage page)
    {
        var keys = new string[page.Rows.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = page.Rows[i].Key.ToString();
        }

        return keys;
    }

    static int[] VersionNumbers(IReadOnlyList<ContentUpgradeRecord> records)
    {
        var numbers = new int[records.Count];
        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = records[i].VersionNumber;
        }

        return numbers;
    }
}
