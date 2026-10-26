using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Exceptional bookkeeping must still notify and join the frozen peer.</summary>
public sealed class ContentUpgradePeerNotificationTests
{
    [Fact]
    public async Task CancelledOlderSweepStillNotifiesTheFrozenPeer()
    {
        using var files = new TemporaryCatalogDatabase();
        var packs = new ObservedFileSystemPacks(files.Pack());
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await UpgradeLifetimeCatalog.CreateAsync(files, registry, true, packs);
        using var exercise = new CancellationTokenSource();
        var scenario = new UpgradePublishScenario(lease.Store, registry);
        packs.BeforeEnumeration = async _ =>
        {
            if (await lease.Store.GetActiveVersionAsync() == 2)
            {
                await exercise.CancelAsync();
            }
        };

        Task<ContentUpgradeReport> older = scenario.StartOlder(exercise.Token);
        Exception? primary = null;
        Task? cleanup = null;
        try
        {
            await scenario.Owner.ReadyAsync(scenario.Sequence.FirstCommitted.Task, older, "first commit");
            Task<ContentUpgradeReport> next = scenario.StartNext();
            primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Owner.AwaitAsync(older));
            Assert.True(scenario.Sequence.FirstReleased.Task.IsCompletedSuccessfully,
                "the cancelled snapshot skipped peer notification and stranded the next frozen publisher");
            Assert.NotNull(scenario.Sequence.DraftAfterOlderPublish);
            Assert.True(scenario.Sequence.DraftAfterOlderPublish.IsFrozen);
            Assert.Equal(2, scenario.Sequence.DraftAfterOlderPublish.BaseVersion);
            Assert.True((await scenario.Owner.AwaitAsync(next)).Success);
            Assert.Same(packs.EnumerationCancellation, primary);
            cleanup = scenario.Owner.FinishAsync(primary);
            Assert.Same(primary, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup));
        }
        finally
        {
            await CatalogLifetimeTestSafety.JoinAsync(scenario, cleanup, primary);
        }

    }
}
