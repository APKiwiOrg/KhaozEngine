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
    /// <summary>The edits an interrupted run left behind, which must never reach a published version.</summary>
    static ContentEdit Leftover
        => ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("leftover"), PublishFixtures.Fields(77));

    /// <summary>
    /// A run killed between its edits and its publish leaves an UNFROZEN draft. The next run proves it is its
    /// own from the actor and the note, discards it, replans and publishes.
    /// </summary>
    [Fact]
    public async Task AnInterruptedRunsUnfrozenDraftIsDiscardedAndTheUpgradeReplanned()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        await LeaveInterruptedDraftAsync(harness, freeze: false);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        await AssertRecoveredAsync(harness, report);
    }

    /// <summary>
    /// A run killed between the publish's FREEZE and its commit leaves a frozen draft, which refuses every
    /// later edit and every later discard. The next run clears the marker first and then discards, because a
    /// draft nothing can write to and nothing can discard is a wedged catalog.
    /// </summary>
    [Fact]
    public async Task AnInterruptedRunsFrozenDraftHasItsFreezeClearedAndIsThenDiscarded()
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
            [Leftover],
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator,
            ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId));

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.OperatorDraftOpen, report.Outcome);
        Assert.Equal(1, (await harness.Store.GetOpenDraftAsync())!.EditCount);
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

    /// <summary>Leaves the store exactly as a run killed part way through would, with the runner's own note.</summary>
    static async Task LeaveInterruptedDraftAsync(UpgradeHarness harness, bool freeze)
    {
        await harness.Store.ApplyEditsAsync(
            [Leftover],
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator,
            ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId));
        if (freeze)
        {
            await harness.Store.FreezeDraftAsync(1);
        }
    }

    /// <summary>
    /// Exactly one published version per definition, both ledger rows, no draft left standing, and none of
    /// the interrupted run's own edits anywhere in the catalog.
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
        Assert.DoesNotContain(
            things.Rows,
            row => string.Equals(row.Key.ToString(), "leftover", StringComparison.Ordinal));

        IReadOnlyList<ContentUpgradeRecord> ledger = await harness.Store.ListUpgradesAsync();
        Assert.Equal([2, 3], VersionNumbers(ledger));
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
