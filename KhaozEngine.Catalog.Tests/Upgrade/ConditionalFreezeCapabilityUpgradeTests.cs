using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.ConditionalFreeze;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// An upgrade run over a store without the guarded freeze companion, and the freeze lifecycle a run over one with
/// it owes on every exit. An Apply with pending work stops before it reads the draft, adoption included, because
/// every way it could continue ends in a freeze or a ledger write it cannot guard. A Preview writes nothing, so it
/// keeps its result and says what an Apply would do. The gates that stop a run before pending work is known keep
/// their place in front of this one.
/// <para>
/// The lifecycle cases run on the in-memory store and on SQLite. A guarded freeze whose answer is lost still owes
/// the release of the base the run recorded before the call, a cleanup that runs twice cannot clear a newer
/// publisher's marker, and a release that faults is resolved like any other publish fault.
/// </para>
/// </summary>
public sealed class ConditionalFreezeCapabilityUpgradeTests
{
    /// <summary>A catalog state an incapable run meets once pending work is known.</summary>
    public enum PendingState
    {
        /// <summary>An older catalog, so the first definition would publish.</summary>
        PendingPublish,

        /// <summary>A catalog already carrying the shipped rows, so the first definition would only be adopted.</summary>
        AlreadySatisfied,

        /// <summary>An operator's draft is open.</summary>
        OperatorDraft,

        /// <summary>The operator pin names a version other than the active one.</summary>
        PinnedElsewhere,

        /// <summary>The caller expects a version that is not active.</summary>
        StaleExpectedVersion,
    }

    /// <summary>A gate that stops a run before pending work is known.</summary>
    public enum EarlierGate
    {
        /// <summary>The store keeps no upgrade ledger.</summary>
        Ledgerless,

        /// <summary>The catalog has no active version.</summary>
        NoCatalog,

        /// <summary>The ledger holds an upgrade this build does not ship.</summary>
        CatalogAhead,

        /// <summary>Every shipped upgrade is recorded.</summary>
        NoPending,
    }

    /// <summary>An edit no definition plans, under an identity that is nobody's runner.</summary>
    static ContentEdit OperatorEdit
        => ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("operator_row"), PublishFixtures.Fields(77));

    /// <summary>
    /// An incapable Apply with pending work stops <see cref="ContentUpgradeOutcome.Unsupported"/> with KECU0017 after
    /// reading only the active version and the ledger, whatever the draft, pin, expectation or plan would have said.
    /// No planner runs, nothing is written, adoption included, and the draft keeps its marker.
    /// </summary>
    /// <param name="state">The catalog state the run meets.</param>
    [Theory]
    [InlineData(PendingState.PendingPublish)]
    [InlineData(PendingState.AlreadySatisfied)]
    [InlineData(PendingState.OperatorDraft)]
    [InlineData(PendingState.PinnedElsewhere)]
    [InlineData(PendingState.StaleExpectedVersion)]
    public async Task IncapableApply_StopsBeforeDraftGate(PendingState state)
    {
        using var harness = new UpgradeHarness();
        int? expected = await ArrangeAsync(harness, state);
        InMemoryContentAuthoringStore inner = harness.Store;
        int plannerCalls = 0;
        ContentUpgradeSet set = Counted(harness.Set, () => plannerCalls++);
        var legacy = new LegacyFreezeLedgerView(inner);
        CatalogFootprint before = await UpgradeFixtures.FootprintAsync(inner);
        int? beforeDraftMarker = (await inner.GetOpenDraftAsync())?.FrozenForBaseVersion;

        ContentUpgradeReport apply = await ContentUpgradeRunner.RunAsync(
            legacy, harness.Registry, set, UpgradeFixtures.Apply(expected));

        Assert.Equal(ContentUpgradeOutcome.Unsupported, apply.Outcome);
        Assert.Contains(apply.Diagnostics, d => d.Code == "KECU0017");
        Assert.DoesNotContain(apply.Diagnostics, d => d.Code is "KECU0004" or "KECU0005" or "KECU0007" or "KECU0010");
        Assert.Equal(new[] { "GetActiveVersionAsync", "ListUpgradesAsync" }, legacy.Calls);
        Assert.Equal(0, plannerCalls);
        Assert.Equal(before, await UpgradeFixtures.FootprintAsync(inner));
        Assert.Equal(beforeDraftMarker, (await inner.GetOpenDraftAsync())?.FrozenForBaseVersion);

        string message = apply.Diagnostics.Single(d => d.Code == "KECU0017").Message;
        Assert.Contains(nameof(LegacyFreezeLedgerView), message, StringComparison.Ordinal);
        Assert.Contains(nameof(IContentConditionalDraftFreeze), message, StringComparison.Ordinal);
        Assert.Contains(nameof(IContentConditionalDraftFreeze.FreezeDraftForBaseAsync), message, StringComparison.Ordinal);
        Assert.Contains(nameof(IContentConditionalDraftFreeze.ReleaseDraftFreezeForBaseAsync), message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An incapable Preview with pending work keeps the outcome, steps and change lines a capable Preview reports,
    /// adds exactly one KECU0017 note naming the Apply refusal, and writes nothing.
    /// </summary>
    /// <param name="state">The catalog state the run meets.</param>
    [Theory]
    [InlineData(PendingState.PendingPublish)]
    [InlineData(PendingState.AlreadySatisfied)]
    public async Task IncapablePreview_KeepsPlanAndAddsCapabilityNote(PendingState state)
    {
        using var harness = new UpgradeHarness();
        await ArrangeAsync(harness, state);
        InMemoryContentAuthoringStore inner = harness.Store;
        CatalogFootprint before = await UpgradeFixtures.FootprintAsync(inner);

        ContentUpgradeReport capablePreview = await ContentUpgradeRunner.RunAsync(
            inner, harness.Registry, harness.Set, UpgradeFixtures.Preview());
        ContentUpgradeReport preview = await ContentUpgradeRunner.RunAsync(
            new LegacyFreezeLedgerView(inner), harness.Registry, harness.Set, UpgradeFixtures.Preview());

        Assert.Equal(capablePreview.Outcome, preview.Outcome);
        Assert.Equal(ContentUpgradeOutcome.PreviewOnly, preview.Outcome);
        Assert.Equal(capablePreview.Steps.Select(s => s.State), preview.Steps.Select(s => s.State));
        Assert.Equal(capablePreview.Steps.SelectMany(s => s.ChangeLines), preview.Steps.SelectMany(s => s.ChangeLines));
        Assert.Contains(preview.Diagnostics, d => d.Code == "KECU0013");
        Assert.Single(preview.Diagnostics, d => d.Code == "KECU0017");
        Assert.Contains(
            "an apply on this store is refused before any write",
            preview.Diagnostics.Single(d => d.Code == "KECU0017").Message);
        Assert.Equal(before, await UpgradeFixtures.FootprintAsync(inner));
        Assert.Equal(
            state == PendingState.AlreadySatisfied
                ? ContentUpgradeStepState.WouldAdopt
                : ContentUpgradeStepState.WouldPublish,
            preview.Steps[0].State);
    }

    /// <summary>
    /// An incapable Preview that meets an operator draft, a pin elsewhere or a moved expectation reports exactly
    /// what a capable Preview reports, under the gate's own code, plus the one capability note.
    /// </summary>
    /// <param name="state">The catalog state the run meets.</param>
    /// <param name="code">The code the capable Preview's gate reports.</param>
    [Theory]
    [InlineData(PendingState.OperatorDraft, "KECU0004")]
    [InlineData(PendingState.PinnedElsewhere, "KECU0005")]
    [InlineData(PendingState.StaleExpectedVersion, "KECU0007")]
    public async Task IncapablePreview_KeepsEarlierGateOutcomeAndAddsCapabilityNote(PendingState state, string code)
    {
        using var harness = new UpgradeHarness();
        int? expected = await ArrangeAsync(harness, state);
        InMemoryContentAuthoringStore inner = harness.Store;
        CatalogFootprint before = await UpgradeFixtures.FootprintAsync(inner);

        ContentUpgradeReport capablePreview = await ContentUpgradeRunner.RunAsync(
            inner, harness.Registry, harness.Set, UpgradeFixtures.Preview(expected));
        ContentUpgradeReport preview = await ContentUpgradeRunner.RunAsync(
            new LegacyFreezeLedgerView(inner), harness.Registry, harness.Set, UpgradeFixtures.Preview(expected));

        Assert.Equal(capablePreview.Outcome, preview.Outcome);
        Assert.Contains(capablePreview.Diagnostics, d => d.Code == code);
        Assert.Contains(preview.Diagnostics, d => d.Code == code);
        Assert.Single(preview.Diagnostics, d => d.Code == "KECU0017");
        Assert.DoesNotContain(capablePreview.Diagnostics, d => d.Code == "KECU0017");
        Assert.Equal(before, await UpgradeFixtures.FootprintAsync(inner));
    }

    /// <summary>
    /// A ledgerless store, an empty catalog, a catalog ahead of this build and a catalog with nothing pending each
    /// stop under their own code in both modes, ahead of the capability gate. A baseline is still recorded through
    /// an incapable store, with nothing but its ledger writes.
    /// </summary>
    /// <param name="gate">The earlier gate the run meets.</param>
    /// <param name="preview">Whether the run previews rather than applies.</param>
    [Theory]
    [InlineData(EarlierGate.Ledgerless, false)]
    [InlineData(EarlierGate.Ledgerless, true)]
    [InlineData(EarlierGate.NoCatalog, false)]
    [InlineData(EarlierGate.NoCatalog, true)]
    [InlineData(EarlierGate.CatalogAhead, false)]
    [InlineData(EarlierGate.CatalogAhead, true)]
    [InlineData(EarlierGate.NoPending, false)]
    [InlineData(EarlierGate.NoPending, true)]
    public async Task IncapableRunner_PreservesEarlierGatePrecedence(EarlierGate gate, bool preview)
    {
        using var harness = new UpgradeHarness();
        InMemoryContentAuthoringStore inner = harness.Store;
        IContentAuthoringStore store = new LegacyFreezeLedgerView(inner);
        (ContentUpgradeOutcome outcome, string code) = gate switch
        {
            EarlierGate.Ledgerless => (ContentUpgradeOutcome.Unsupported, "KECU0001"),
            EarlierGate.NoCatalog => (ContentUpgradeOutcome.NoCatalog, "KECU0002"),
            EarlierGate.CatalogAhead => (ContentUpgradeOutcome.CatalogAheadOfBuild, "KECU0003"),
            _ => (ContentUpgradeOutcome.UpToDate, "KECU0012"),
        };

        switch (gate)
        {
            case EarlierGate.Ledgerless:
                await harness.SeedOlderCatalogAsync();
                store = new LegacyFreezeStoreView(inner);
                break;
            case EarlierGate.CatalogAhead:
                await harness.SeedOlderCatalogAsync();
                ContentUpgradeDefinition newer = UpgradeFixtures.Refuses("shipped-by-a-newer-build", 9, "never planned");
                await inner.RecordUpgradeAsync(
                    newer.Stamp, ContentUpgradeDisposition.Adopted, UpgradeFixtures.Actor, UpgradeFixtures.Operator);
                break;
            case EarlierGate.NoPending:
                await harness.SeedCurrentCatalogAsync();
                var recorder = new LegacyFreezeLedgerView(inner);
                await ContentUpgradeRunner.RecordBaselineAsync(
                    recorder, harness.Set, UpgradeFixtures.Actor, UpgradeFixtures.Operator);
                Assert.Equal(new[] { "RecordUpgradeAsync", "RecordUpgradeAsync" }, recorder.Calls);
                Assert.All(
                    await inner.ListUpgradesAsync(),
                    record => Assert.Equal(ContentUpgradeDisposition.Baseline, record.Disposition));
                break;
        }

        CatalogFootprint before = await UpgradeFixtures.FootprintAsync(inner);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store,
            harness.Registry,
            harness.Set,
            preview ? UpgradeFixtures.Preview() : UpgradeFixtures.Apply());

        Assert.Equal(outcome, report.Outcome);
        Assert.Contains(report.Diagnostics, d => d.Code == code);
        Assert.DoesNotContain(report.Diagnostics, d => d.Code == "KECU0017");
        Assert.Equal(before, await UpgradeFixtures.FootprintAsync(inner));
    }

    /// <summary>
    /// The guarded freeze writes the marker and its answer is lost to the run's own cancellation. The run still
    /// releases the base it recorded before the call, with <see cref="CancellationToken.None"/>, and the draft is
    /// left open and unfrozen for the operator.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is a SQLite file rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runner_LostFreezeAcknowledgementReleasesCapturedBase(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore lostFreezeAckInner = fixture.Inner;
        using var cancel = new CancellationTokenSource();
        var lostFreezeAckRunner = new LostFreezeAcknowledgementStore(
            lostFreezeAckInner, (IContentIdPersistence)lostFreezeAckInner, fixture.Registry, fixture.Packs, cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ContentUpgradeRunner.RunAsync(
                lostFreezeAckRunner, fixture.Registry, new ContentUpgradeSet(fixture.First), UpgradeFixtures.Apply(), cancel.Token));

        Assert.True(lostFreezeAckRunner.Lost, "the freeze never lost its answer, so the case was not exercised.");
        Assert.Equal(1, lostFreezeAckRunner.GuardedFreezeCalls);
        Assert.Equal(new int?[] { 1 }, lostFreezeAckRunner.ReleaseBases);
        Assert.Equal(new bool?[] { true }, lostFreezeAckRunner.ReleaseResults);
        Assert.All(lostFreezeAckRunner.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.Null((await lostFreezeAckInner.GetOpenDraftAsync())!.FrozenForBaseVersion);
        Assert.Equal(0, lostFreezeAckRunner.LegacyFreezeCalls);
        Assert.Equal(0, lostFreezeAckRunner.LegacyClearCalls);
        Assert.Single(await lostFreezeAckInner.ListVersionsAsync());
    }

    /// <summary>
    /// A wrapper runner R, which forwards its publish to the engine store, freezes at 1 and parks after the freeze
    /// returned. A console publishes R's draft as version 2, opens D2 and P2 freezes D2 at 2 and parks at its commit.
    /// R's forwarded publish is refused and released by the engine pipeline, then R's own cleanup releases base 1
    /// again. Neither clears P2's marker, P2 commits version 3 with the operator's value, and R adopts.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is SQLite replicas rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runner_DuplicateCleanupCannotReleaseNewerBase(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        RowFreezeRaceStore runner = await fixture.RowParticipantAsync(forwardPublish: true);
        FreezeRaceStore console = await fixture.TextParticipantAsync();
        FreezeRaceStore winner = await fixture.TextParticipantAsync();
        OnceGate runnerFreeze = fixture.Gate();
        OnceGate runnerRelease = fixture.Gate();
        OnceGate winnerCommit = fixture.Gate();
        runner.ArmRunnerFreezeExit(runnerFreeze, 1);
        runner.ArmFirstReleaseExit(runnerRelease);
        winner.ArmTextCommitEntry(winnerCommit, 2);

        Task<ContentUpgradeReport> run = fixture.Start(
            () => ContentUpgradeRunner.RunAsync(
                runner, fixture.Registry, new ContentUpgradeSet(fixture.First), UpgradeFixtures.Apply()));
        await FreezeRaceFixture.ReachAsync(runnerFreeze, run, "R's freeze exit at base 1");
        (ContentPublishResult? second, Exception? secondFailure) = await FreezeRaceFixture.OutcomeAsync(
            fixture.Start(() => console.PublishAsync(PublishFixtures.Request(1))));
        Assert.Null(secondFailure);
        Assert.Equal(2, second!.VersionNumber);

        await fixture.EditAsync(fixture.OldRow(33));
        Task<ContentPublishResult> newer = fixture.Start(() => winner.PublishAsync(PublishFixtures.Request(2)));
        await FreezeRaceFixture.ReachAsync(winnerCommit, newer, "P2's text commit at base 2");

        runnerFreeze.Resume();
        await FreezeRaceFixture.ReachAsync(runnerRelease, run, "R's first release exit");
        int? markerAfterCleanup = (await inner.GetOpenDraftAsync())?.FrozenForBaseVersion;

        winnerCommit.Resume();
        (ContentPublishResult? winnerResult, Exception? winnerFailure) = await FreezeRaceFixture.OutcomeAsync(newer);
        runnerRelease.Resume();
        (ContentUpgradeReport? report, Exception? runFailure) = await FreezeRaceFixture.OutcomeAsync(run);

        Assert.Null(winnerFailure);
        Assert.Null(runFailure);
        Assert.Equal(2, markerAfterCleanup);
        Assert.Equal(new int?[] { 1 }, runner.ReleaseBases);
        Assert.Equal(new bool?[] { false }, runner.ReleaseResults);
        Assert.All(runner.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.Equal(1, runner.GuardedFreezeCalls);
        Assert.Equal(0, runner.LegacyFreezeCalls);
        Assert.Equal(0, runner.LegacyClearCalls);
        Assert.Equal(1, winner.TextCommitCalls);
        Assert.Equal(3, winnerResult!.VersionNumber);
        ContentRowPage operatorRow = await inner.ListRowsAsync(UpgradeFixtures.Thing, 3, "old_row", false, 0, 10);
        Assert.Equal(33, Assert.Single(operatorRow.Rows).Fields[0].Number);
        Assert.True(report!.Success, string.Join(" | ", report.Lines));
        ContentUpgradeRecord recorded = Assert.Single(await ((IContentUpgradeLedger)inner).ListUpgradesAsync());
        Assert.Equal(ContentUpgradeDisposition.Adopted, recorded.Disposition);
        Assert.Equal(3, recorded.VersionNumber);
        Assert.Null(await inner.GetOpenDraftAsync());
    }

    /// <summary>
    /// The guarded freeze writes its marker and loses its answer to a fault this package reports, and the release
    /// that cleanup owes then faults too. The run resolves that fault exactly as it resolves any publish fault: its
    /// own draft is discarded, the ledger is read, and a non-contention fault fails the run under KECU0009.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is a SQLite file rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runner_ReleaseFailureRetainsExistingFaultBehavior(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        var runner = new ReleaseFaultStore(inner, (IContentIdPersistence)inner, fixture.Registry, fixture.Packs);

        ContentUpgradeReport releaseFaultReport = await ContentUpgradeRunner.RunAsync(
            runner, fixture.Registry, new ContentUpgradeSet(fixture.First), UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Failed, releaseFaultReport.Outcome);
        Assert.Contains(releaseFaultReport.Diagnostics, d => d.Code == "KECU0009");
        Assert.Contains(
            releaseFaultReport.Diagnostics,
            d => d.Code == "KECU0009" && d.Message.Contains(ReleaseFaultStore.ReleaseFault, StringComparison.Ordinal));
        Assert.Equal(new int?[] { 1 }, runner.ReleaseBases);
        Assert.Equal(new bool?[] { true }, runner.ReleaseResults);
        Assert.All(runner.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.Equal(0, runner.LegacyFreezeCalls);
        Assert.Equal(0, runner.LegacyClearCalls);
        Assert.Null(await inner.GetOpenDraftAsync());
        Assert.Empty(await ((IContentUpgradeLedger)inner).ListUpgradesAsync());
        Assert.Single(await inner.ListVersionsAsync());
    }

    /// <summary>Brings the harness catalog to <paramref name="state"/> and answers the expected version to run with.</summary>
    static async Task<int?> ArrangeAsync(UpgradeHarness harness, PendingState state)
    {
        if (state == PendingState.AlreadySatisfied)
        {
            await harness.SeedCurrentCatalogAsync();
            return null;
        }

        await harness.SeedOlderCatalogAsync();
        switch (state)
        {
            case PendingState.OperatorDraft:
                await harness.Store.ApplyEditsAsync(
                    [OperatorEdit], "an-operator", "oid:human", "an operator's own afternoon");
                return null;
            case PendingState.PinnedElsewhere:
                ContentRowPage rows = await harness.Store.ListRowsAsync(UpgradeFixtures.Thing, 0, "old_row", false, 0, 10);
                await harness.Store.ApplyEditsAsync(
                    [ContentEdit.Update(UpgradeFixtures.Thing, rows.Rows[0].Id, new ContentKey("old_row"), PublishFixtures.Fields(12))],
                    "an-operator",
                    "oid:human",
                    "an operator's retune");
                await harness.Store.PublishAsync(PublishFixtures.Request(1));
                await harness.Store.SetPinnedVersionAsync(1, "an-operator", "oid:human");
                return null;
            case PendingState.StaleExpectedVersion:
                return 7;
            default:
                return null;
        }
    }

    /// <summary>The same definitions, each counting its planner calls before planning.</summary>
    static ContentUpgradeSet Counted(ContentUpgradeSet set, Action planned)
        => new(set.Definitions
            .Select(definition => new ContentUpgradeDefinition(
                definition.Id,
                definition.Order,
                definition.Description,
                context =>
                {
                    planned();
                    return definition.Plan(context);
                }))
            .ToArray());

    /// <summary>A text participant whose guarded freeze writes the marker and then loses its answer to cancellation.</summary>
    sealed class LostFreezeAcknowledgementStore(
        IContentAuthoringStore inner,
        IContentIdPersistence ids,
        ContentTypeRegistry registry,
        IPackStore packs,
        CancellationTokenSource cancel)
        : FreezeRaceStore(inner, ids, registry, packs)
    {
        public bool Lost { get; private set; }

        public override async Task<ContentDraft> FreezeDraftForBaseAsync(
            int expectedBaseVersion,
            CancellationToken cancellationToken = default)
        {
            ContentDraft frozen = await base.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);
            Lost = true;
            await cancel.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return frozen;
        }
    }

    /// <summary>
    /// A text participant whose guarded freeze writes the marker and loses its answer to a reported fault, and whose
    /// guarded release clears the marker and then faults.
    /// </summary>
    sealed class ReleaseFaultStore(
        IContentAuthoringStore inner,
        IContentIdPersistence ids,
        ContentTypeRegistry registry,
        IPackStore packs)
        : FreezeRaceStore(inner, ids, registry, packs)
    {
        public const string ReleaseFault = "the guarded release faulted after it cleared the marker.";

        public override async Task<ContentDraft> FreezeDraftForBaseAsync(
            int expectedBaseVersion,
            CancellationToken cancellationToken = default)
        {
            await base.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);
            throw new IOException("the guarded freeze's acknowledgement was lost.");
        }

        public override async Task<bool> ReleaseDraftFreezeForBaseAsync(
            int frozenForBaseVersion,
            CancellationToken cancellationToken = default)
        {
            await base.ReleaseDraftFreezeForBaseAsync(frozenForBaseVersion, cancellationToken);
            throw new IOException(ReleaseFault);
        }
    }
}
