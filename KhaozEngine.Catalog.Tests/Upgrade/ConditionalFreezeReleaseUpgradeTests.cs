using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.ConditionalFreeze;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// Upgrade runners racing each other and console publishers across a version boundary, each interleaving
/// forced once with gates. Two defects meet here: an older runner's release clears the marker a newer
/// publisher set on the next draft, and a runner whose plan carries no text freezes through the row-only path,
/// which overwrites a newer marker without comparing it with the active version.
/// <para>
/// <b>Every plan here carries no text</b>, which each case pins on the public plan before it runs, so the
/// runner really takes its row-only freeze. Every participant whose commit must check the marker declares the
/// whole text companion and the ledger and publishes over itself, and its text commit calls are asserted.
/// Each case runs on the in-memory store and on SQLite replicas of one file.
/// </para>
/// </summary>
/// <param name="output">Where each participant's outcome is written before anything is asserted.</param>
public sealed class ConditionalFreezeReleaseUpgradeTests(ITestOutputHelper output)
{
    /// <summary>
    /// T3, the hosted sequence. Runner B parks at its first commit on base 1. Runner A recovers B's draft,
    /// publishes version 2, freezes its second upgrade at 2 and parks at that commit. B resumes, is refused,
    /// releases twice and is held at its first failure-resolution draft read. A resumes and must publish its
    /// second upgrade, then B adopts both.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is SQLite replicas rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T3_TwoRunnersPreserveNewerTextWinner(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        ContentUpgradeSet set = await RowOnlySetAsync(fixture, fixture.First, fixture.Second);

        FreezeRaceStore runnerA = await fixture.TextParticipantAsync();
        FreezeRaceStore runnerB = await fixture.TextParticipantAsync();
        OnceGate bCommit = fixture.Gate();
        OnceGate bRecovery = fixture.Gate();
        OnceGate aCommit = fixture.Gate();
        runnerB.ArmTextCommitEntry(bCommit, 1, UpgradeHarness.FirstId);
        runnerB.ArmRecoveryRead(bRecovery);
        runnerA.ArmTextCommitEntry(aCommit, 2, UpgradeHarness.SecondId);

        Task<ContentUpgradeReport> runB = fixture.Start(
            () => ContentUpgradeRunner.RunAsync(runnerB, fixture.Registry, set, UpgradeFixtures.Apply()));
        await FreezeRaceFixture.ReachAsync(bCommit, runB, "B's first text commit at base 1");

        Task<ContentUpgradeReport> runA = fixture.Start(
            () => ContentUpgradeRunner.RunAsync(runnerA, fixture.Registry, set, UpgradeFixtures.Apply()));
        await FreezeRaceFixture.ReachAsync(aCommit, runA, "A's second upgrade text commit at base 2");

        bCommit.Resume();
        await FreezeRaceFixture.ReachAsync(bRecovery, runB, "B's failure-resolution draft read");
        Assert.Equal(2, runnerB.ReleaseTokens.Count);

        aCommit.Resume();
        (ContentUpgradeReport? reportA, Exception? failureA) = await FreezeRaceFixture.OutcomeAsync(runA);
        bRecovery.Resume();
        (ContentUpgradeReport? reportB, Exception? failureB) = await FreezeRaceFixture.OutcomeAsync(runB);

        output.WriteLine("A " + Describe(reportA, failureA, runnerA));
        output.WriteLine("B " + Describe(reportB, failureB, runnerB));
        Assert.Null(failureA);
        Assert.Null(failureB);
        ContentUpgradeReport a = reportA!;
        ContentUpgradeReport b = reportB!;
        IReadOnlyList<ContentUpgradeRecord> ledger = await ((IContentUpgradeLedger)inner).ListUpgradesAsync();
        ContentRowPage rows = await inner.ListRowsAsync(UpgradeFixtures.Other, 0, null, false, 0, 50);

        // The route guards first, which hold on any code: both runners published through the text commit.
        Assert.Equal(2, runnerA.TextCommitCalls);
        Assert.Equal(0, runnerA.RowCommitCalls);
        Assert.Equal(0, runnerB.RowCommitCalls);
        Assert.True(a.Success, Describe(a, null, runnerA));
        Assert.True(b.Success, Describe(b, null, runnerB));
        Assert.Equal(1, runnerB.TextCommitCalls);
        Assert.Contains(a.Diagnostics, d => d.Code == "KECU0010");
        Assert.DoesNotContain(a.Diagnostics, d => d.Code == "KECU0009");
        Assert.Single(b.Diagnostics, d => d.Code == "KECU0011");
        Assert.Equal(2, b.Steps.Count);
        Assert.Equal(UpgradeHarness.FirstId, b.Steps[0].Id);
        Assert.Equal(ContentUpgradeStepState.Applied, b.Steps[0].State);
        Assert.Equal(2, b.Steps[0].PublishedVersion);
        Assert.Equal(UpgradeHarness.SecondId, b.Steps[1].Id);
        Assert.Equal(ContentUpgradeStepState.Adopted, b.Steps[1].State);
        Assert.Null(b.Steps[1].PublishedVersion);
        Assert.Equal(new int?[] { 1, 1 }, runnerB.ReleaseBases);
        Assert.Equal(new bool?[] { false, false }, runnerB.ReleaseResults);
        Assert.All(runnerB.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.Equal(0, runnerB.LegacyFreezeCalls);
        Assert.Equal(0, runnerB.LegacyClearCalls);
        Assert.Equal(0, runnerA.LegacyFreezeCalls);
        Assert.Equal(0, runnerA.LegacyClearCalls);
        Assert.Equal(3, (await inner.ListVersionsAsync()).Count);
        Assert.Equal(new[] { 2, 3 }, ledger.OrderBy(r => r.VersionNumber).Select(r => r.VersionNumber));
        Assert.All(ledger, r => Assert.Equal(ContentUpgradeDisposition.Applied, r.Disposition));
        Assert.Equal(new[] { 1, 2 }, rows.Rows.Select(r => r.Id));
        Assert.Equal(new[] { "new_row", "second_new_row" }, rows.Rows.Select(r => r.Key.ToString()));
        Assert.Null(await inner.GetOpenDraftAsync());
    }

    /// <summary>
    /// T3o. Runner R writes its row-only plan, freezes at 1 and parks AFTER the freeze returned. Console P1
    /// commits that draft as version 2, the console opens a row-only D2, and P2 freezes D2 at 2 and parks at
    /// its commit. R resumes and is held after its first release. P2 must still commit version 3.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is SQLite replicas rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T3o_AfterRunnerFreezePreservesNewerTextWinner(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        FreezeRaceStore runner = await fixture.TextParticipantAsync();
        await RunnerAgainstConsoleAsync(fixture, runner, gate => runner.ArmRunnerFreezeExit(gate, 1));
    }

    /// <summary>
    /// T4. As T3o, with R parked BEFORE its freeze, after writing its draft. R's freeze then runs against D2,
    /// which already carries P2's marker 2. P2 must still commit version 3.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is SQLite replicas rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T4_BeforeRunnerFreezePreservesNewerTextWinner(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        FreezeRaceStore runner = await fixture.TextParticipantAsync();
        await RunnerAgainstConsoleAsync(fixture, runner, gate => runner.ArmRunnerFreezeEntry(gate, 1));
    }

    /// <summary>
    /// T4r. As T4, with R a row store plus ledger that forwards its publish to the inner engine store, the
    /// shape of a game's upgrade wrapper. The console publishers keep the whole text companion.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is SQLite replicas rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T4r_ForwardingRowRunnerPreservesNewerTextWinner(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        RowFreezeRaceStore runner = await fixture.RowParticipantAsync(forwardPublish: true);
        await RunnerAgainstConsoleAsync(fixture, runner, gate => runner.ArmRunnerFreezeEntry(gate, 1));
    }

    /// <summary>
    /// The one body T3o, T4 and T4r share: R runs the first upgrade alone and parks at its freeze gate, an
    /// ordinary console publish with no upgrade stamp commits R's draft as version 2, a console edit opens
    /// D2, P2 parks at its commit on base 2, R resumes and is held after its first release, then P2 and R
    /// finish. P2 is the winner, and R records the upgrade once by adoption after version 3.
    /// </summary>
    async Task RunnerAgainstConsoleAsync(
        FreezeRaceFixture fixture,
        FreezeRaceParticipant runner,
        Action<OnceGate> armFreeze)
    {
        IContentAuthoringStore inner = fixture.Inner;
        ContentUpgradeSet set = await RowOnlySetAsync(fixture, fixture.First);
        FreezeRaceStore p1 = await fixture.TextParticipantAsync();
        FreezeRaceStore winner = await fixture.TextParticipantAsync();
        OnceGate runnerFreeze = fixture.Gate();
        OnceGate runnerRelease = fixture.Gate();
        OnceGate winnerCommit = fixture.Gate();
        armFreeze(runnerFreeze);
        runner.ArmFirstReleaseExit(runnerRelease);
        winner.ArmTextCommitEntry(winnerCommit, 2);

        Task<ContentUpgradeReport> run = fixture.Start(
            () => ContentUpgradeRunner.RunAsync(runner, fixture.Registry, set, UpgradeFixtures.Apply()));
        await FreezeRaceFixture.ReachAsync(runnerFreeze, run, "R's freeze at base 1");
        ContentDraft written = Assert.IsType<ContentDraft>(await inner.GetOpenDraftAsync());
        Assert.Equal(UpgradeFixtures.Actor, written.OpenedBy);

        (ContentPublishResult? second, Exception? secondFailure) = await FreezeRaceFixture.OutcomeAsync(
            fixture.Start(() => p1.PublishAsync(PublishFixtures.Request(1))));
        output.WriteLine("P1 " + FreezeRaceFixture.Describe(secondFailure));
        Assert.Null(secondFailure);
        Assert.Equal(2, second!.VersionNumber);

        await fixture.EditAsync(fixture.OldRow(33));
        Task<ContentPublishResult> newer = fixture.Start(() => winner.PublishAsync(PublishFixtures.Request(2)));
        await FreezeRaceFixture.ReachAsync(winnerCommit, newer, "P2's text commit at base 2");

        runnerFreeze.Resume();
        await FreezeRaceFixture.ReachAsync(runnerRelease, run, "R's first release exit");
        int? markerAfterOlderExit = (await inner.GetOpenDraftAsync())?.FrozenForBaseVersion;
        output.WriteLine("D2 marker after R's first release: " + FreezeRaceFixture.Marker(markerAfterOlderExit));

        winnerCommit.Resume();
        (ContentPublishResult? winnerResult, Exception? winnerFailure) = await FreezeRaceFixture.OutcomeAsync(newer);
        output.WriteLine("P2 " + FreezeRaceFixture.Describe(winnerFailure));
        runnerRelease.Resume();
        (ContentUpgradeReport? report, Exception? runFailure) = await FreezeRaceFixture.OutcomeAsync(run);
        output.WriteLine("R " + Describe(report, runFailure, runner));
        output.WriteLine(FormattableString.Invariant(
            $"R releases {string.Join(", ", runner.ReleaseBases.Zip(runner.ReleaseResults, (b, r) => FreezeRaceFixture.Marker(b) + ":" + (r?.ToString() ?? "none")))} | guarded freezes {runner.GuardedFreezeCalls} | text commits {runner.TextCommitCalls}"));

        Assert.Equal(1, winner.TextCommitCalls);
        Assert.Equal(0, winner.RowCommitCalls);
        Assert.Null(winnerFailure);
        Assert.Equal(2, markerAfterOlderExit);
        Assert.Equal(3, winnerResult!.VersionNumber);
        Assert.Null(await inner.GetOpenDraftAsync());
        ContentRowPage operatorRow = await inner.ListRowsAsync(UpgradeFixtures.Thing, 3, "old_row", false, 0, 10);
        Assert.Equal(33, Assert.Single(operatorRow.Rows).Fields[0].Number);

        Assert.Null(runFailure);
        Assert.DoesNotContain(report!.Diagnostics, d => d.Code == "KECU0009");
        ContentUpgradeRecord recorded = Assert.Single(await ((IContentUpgradeLedger)inner).ListUpgradesAsync());
        Assert.Equal(UpgradeHarness.FirstId, recorded.Id);
        Assert.Equal(ContentUpgradeDisposition.Adopted, recorded.Disposition);
        Assert.Equal(3, recorded.VersionNumber);
        Assert.Equal(3, (await inner.ListVersionsAsync()).Count);
        Assert.Equal(0, runner.LegacyFreezeCalls);
        Assert.Equal(0, runner.LegacyClearCalls);
        Assert.Equal(0, winner.LegacyFreezeCalls);
        Assert.Equal(0, winner.LegacyClearCalls);
        Assert.Equal(1, runner.GuardedFreezeCalls);
    }

    /// <summary>
    /// The upgrade set, after pinning that every definition's plan over version 1 carries no text edit. That
    /// public fact is what sends the runner through its row-only freeze.
    /// </summary>
    static async Task<ContentUpgradeSet> RowOnlySetAsync(
        FreezeRaceFixture fixture,
        params ContentUpgradeDefinition[] definitions)
    {
        ContentBundle baseline = await fixture.Inner.ExportBundleAsync(1);
        foreach (ContentUpgradeDefinition definition in definitions)
        {
            ContentUpgradePlan plan = definition.Plan(new ContentUpgradeContext(1, baseline, fixture.Registry));
            Assert.Empty(plan.TextEdits);
        }

        return new ContentUpgradeSet(definitions);
    }

    /// <summary>A run's report lines and the refusals its store gave its commits, as one line.</summary>
    static string Describe(ContentUpgradeReport? report, Exception? failure, FreezeRaceParticipant participant)
    {
        var lines = new List<string>();
        if (failure is not null)
        {
            lines.Add(FreezeRaceFixture.Describe(failure));
        }

        if (report is not null)
        {
            lines.AddRange(report.Lines);
        }

        lines.AddRange(participant.CommitRefusals.Select(reason => "commit refused " + reason));
        return string.Join(" | ", lines);
    }
}
