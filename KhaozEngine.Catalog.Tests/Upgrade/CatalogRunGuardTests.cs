using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Negative proofs distinguish finite semantic progress from a never-completing phase.</summary>
public sealed class CatalogRunGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeerProgressDoesNotBecomeATotalPublishDeadline(bool sqlite)
    {
        using var files = new TemporaryCatalogDatabase();
        var packs = new ObservedFileSystemPacks(files.Pack());
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await UpgradeLifetimeCatalog.CreateAsync(files, registry, sqlite, packs);
        var clock = new ManualCatalogPhaseClock();
        var scenario = new UpgradePublishScenario(lease.Store, registry, clock: clock);
        // Each actual filesystem read advances less than one phase budget. Several reads exceed the
        // entire old budget while the waiting peer must keep observing its producer's real progress.
        packs.BeforeRead = () => clock.Advance(TimeSpan.FromSeconds(11));

        (ContentUpgradeReport older, ContentUpgradeReport next) = await scenario.RunAsync();

        Assert.True(older.Success);
        Assert.True(next.Success);
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(20));
        Assert.False(scenario.Owner.Diagnostic.Task.IsCompleted);
        Assert.Equal(4, scenario.Sequence.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARepeatedFrozenRivalHitsTheTwoDefinitionObservationBound(bool sqlite)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await UpgradeLifetimeCatalog.CreateAsync(files, registry, sqlite);
        ContentUpgradeSet set = UpgradeLifetimeCatalog.SetFor(registry);
        var rival = new HeldRivalDraftStore(lease.Store, registry, set.Definitions[1]);
        var bounded = new ScriptedUpgradeStore(rival, 5, 1, 2);
        var owner = new CatalogRunnerLifetime();

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ExecuteAsync(async () =>
        {
            Task<ContentUpgradeReport> run = owner.Start(token => ContentUpgradeRunner.RunAsync(
                bounded, registry, set, UpgradeFixtures.Apply(), token));
            return await owner.AwaitAsync(run);
        }));

        Assert.Contains("observation bound 5 exceeded at read 6", failure.Message, StringComparison.Ordinal);
        Assert.Equal(6, bounded.DraftReads);
        Assert.True(rival.RepeatedReads > 1);
        Assert.True((await lease.Store.GetOpenDraftAsync())!.IsFrozen);
        Assert.Single(await lease.Store.ListVersionsAsync());
        Assert.Empty(await ((IContentUpgradeLedger)lease.Store).ListUpgradesAsync());
        Assert.False(owner.Diagnostic.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMissingNextFreezeTripsItsIndependentPhaseGuardAndJoinsHeldWork(bool sqlite)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await UpgradeLifetimeCatalog.CreateAsync(files, registry, sqlite);
        var clock = new ManualCatalogPhaseClock();
        var scenario = new UpgradePublishScenario(lease.Store, registry, clock: clock);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Sequence.BeforeSnapshot = async () => { held.TrySetResult(); await hold.Task; };
        Task<ContentUpgradeReport> older = scenario.StartOlder();
        Exception? primary = null;
        Task? cleanup = null;
        try
        {
            await scenario.Owner.ReadyAsync(scenario.Sequence.OlderWaiting.Task, older, "older sweep wait");
            Task missing = scenario.Owner.ReadyAsync(scenario.Sequence.SecondFrozen.Task, older, "next complete freeze");
            clock.ExpireActive();
            Assert.True(scenario.Owner.Diagnostic.Task.IsCompletedSuccessfully, "the independent diagnostic never expired");
            var failure = await Assert.ThrowsAsync<TimeoutException>(() => missing);
            primary = failure;
            Assert.Contains("older sweep waiting for next complete freeze", failure.Message, StringComparison.Ordinal);
            Assert.False(scenario.Owner.Token.IsCancellationRequested, "the diagnostic itself cancelled normal IO");
            cleanup = scenario.Owner.FinishAsync(failure);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(cleanup.IsCompleted, "cleanup returned while an owned snapshot operation was held");
            hold.TrySetResult();
            Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() => cleanup));
            Assert.All(scenario.Owner.Runs, run => Assert.True(run.IsCompleted));
        }
        finally
        {
            hold.TrySetResult();
            await CatalogLifetimeTestSafety.JoinAsync(scenario, cleanup, primary);
        }
    }

    sealed class HeldRivalDraftStore(IContentAuthoringStore inner, ContentTypeRegistry registry,
        ContentUpgradeDefinition rival) : ForwardingUpgradeStore(inner)
    {
        int _reads;
        ContentDraft? _held;
        public int RepeatedReads { get; private set; }

        public override async Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
        {
            if (++_reads == 2)
            {
                ContentBundle baseline = await Inner.ExportBundleAsync(1, cancellationToken);
                ContentUpgradePlan plan = rival.Plan(new ContentUpgradeContext(1, baseline, registry));
                await Inner.ApplyEditsAsync(plan.Edits, UpgradeFixtures.Actor, UpgradeFixtures.Operator,
                    ContentUpgradeRunner.NoteFor(rival.Id), cancellationToken);
                await Inner.FreezeDraftAsync(1, cancellationToken);
            }

            ContentDraft? draft = await base.GetOpenDraftAsync(cancellationToken);
            if (draft is not null)
            {
                _held ??= draft;
                Assert.Equal(_held.OpenedAtUtc, draft.OpenedAtUtc);
                Assert.Equal(_held.Note, draft.Note);
                Assert.True(draft.IsFrozen);
                RepeatedReads++;
            }

            return draft;
        }
    }
}
