using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Teardown proofs keep real stores and files alive until all owned runners have joined.</summary>
public sealed class CatalogRunnerCleanupTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AnAssertionAfterOlderStartedJoinsHeldWorkAndSurfacesCleanupFaults(bool sqlite, bool cleanupFault)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await UpgradeLifetimeCatalog.CreateAsync(files, registry, sqlite);
        var scenario = new UpgradePublishScenario(lease.Store, registry);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new InvalidOperationException("intentional snapshot cleanup fault");
        scenario.Sequence.BeforeSnapshot = async () =>
        {
            held.TrySetResult();
            await hold.Task;
            if (cleanupFault)
            {
                throw cleanupFailure;
            }
        };
        Task<ContentUpgradeReport> older = scenario.StartOlder();
        Exception? primary = null;
        Task? cleanup = null;
        try
        {
            await scenario.Owner.ReadyAsync(scenario.Sequence.FirstCommitted.Task, older, "first commit");
            primary = Assert.Throws<FailException>(() => Assert.Fail("intentional assertion after older started"));
            cleanup = scenario.Owner.FinishAsync(primary);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(cleanup.IsCompleted, "cleanup returned before its held runner was joined");
            hold.TrySetResult();
            if (cleanupFault)
            {
                AggregateException escaped = await Assert.ThrowsAsync<AggregateException>(() => cleanup);
                Assert.Collection(escaped.InnerExceptions,
                    error => Assert.Same(primary, error), error => Assert.Same(cleanupFailure, error));
            }
            else
            {
                Assert.Same(primary, await Assert.ThrowsAsync<FailException>(() => cleanup));
            }

            Assert.All(scenario.Owner.Runs, run => Assert.True(run.IsCompleted));
        }
        finally
        {
            hold.TrySetResult();
            await CatalogLifetimeTestSafety.JoinAsync(scenario, cleanup, primary);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingNextDuringRealPostCommitSweepJoinsTheHeldOlderRunner(bool sqlite)
    {
        using var files = new TemporaryCatalogDatabase();
        var packs = new ObservedFileSystemPacks(files.Pack());
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await UpgradeLifetimeCatalog.CreateAsync(files, registry, sqlite, packs);
        using var exercise = new CancellationTokenSource();
        var scenario = new UpgradePublishScenario(lease.Store, registry);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Sequence.BeforeOlderReturn = async () => { held.TrySetResult(); await hold.Task; };
        packs.BeforeEnumeration = async token =>
        {
            if (await lease.Store.GetActiveVersionAsync() == 3)
            {
                Assert.False(token.IsCancellationRequested);
                Assert.Equal(2, (await ((IContentUpgradeLedger)lease.Store).ListUpgradesAsync()).Count);
                await exercise.CancelAsync();
            }
        };
        Task<ContentUpgradeReport> older = scenario.StartOlder();
        Exception? primary = null;
        Task? cleanup = null;
        try
        {
            await scenario.Owner.ReadyAsync(scenario.Sequence.FirstCommitted.Task, older, "first commit");
            Task<ContentUpgradeReport> next = scenario.StartNext(exercise.Token);
            primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Owner.AwaitAsync(next));
            Assert.True(exercise.IsCancellationRequested);
            Assert.Same(packs.EnumerationCancellation, primary);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(20));
            cleanup = scenario.Owner.FinishAsync(primary);
            Assert.False(cleanup.IsCompleted, "cancelled publication escaped while its older peer was still held");
            hold.TrySetResult();
            Assert.Same(primary, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup));
            Assert.Equal(3, (await lease.Store.ListVersionsAsync()).Count);
            Assert.Null(await lease.Store.GetOpenDraftAsync());
            Assert.All(scenario.Owner.Runs, run => Assert.True(run.IsCompleted));
        }
        finally
        {
            hold.TrySetResult();
            await CatalogLifetimeTestSafety.JoinAsync(scenario, cleanup, primary);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ASnapshotFaultNotifiesThePeerAndCannotBecomeSuccessfulEvidence(bool sqlite, bool handled)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await UpgradeLifetimeCatalog.CreateAsync(files, registry, sqlite);
        var scenario = new UpgradePublishScenario(lease.Store, registry);
        Exception expected = handled
            ? new IOException("intentional handled snapshot read fault")
            : new InvalidOperationException("intentional snapshot read fault");
        scenario.Sequence.BeforeSnapshot = () => throw expected;

        Exception escaped = await Assert.ThrowsAnyAsync<Exception>(() => scenario.RunAsync());

        Assert.Same(expected, escaped);
        Assert.True(scenario.Sequence.FirstReleased.Task.IsFaulted);
        Assert.Null(scenario.Sequence.DraftAfterOlderPublish);
        Assert.True(scenario.Sequence.Aborted);
        Assert.False(scenario.Owner.Diagnostic.Task.IsCompleted);
        Assert.Equal(2, scenario.Owner.Runs.Count);
        Assert.All(scenario.Owner.Runs, run => Assert.True(run.IsCompleted));
    }
}
