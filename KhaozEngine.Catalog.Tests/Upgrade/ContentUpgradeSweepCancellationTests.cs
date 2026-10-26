using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Characterizes cancellation at real post-commit pruning, without reproducing elapsed timing.</summary>
public sealed class ContentUpgradeSweepCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptedSweepCancellationEscapesFilesystemEnumerationAfterTheUpgradeCommitted(bool sqlite)
    {
        using var files = new TemporaryCatalogDatabase();
        var packs = new ObservedFileSystemPacks(files.Pack());
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await UpgradeLifetimeCatalog.CreateAsync(files, registry, sqlite, packs);
        using var exercise = new CancellationTokenSource();
        packs.BeforeEnumeration = async token =>
        {
            Assert.Equal(exercise.Token, token);
            Assert.Equal(2, await lease.Store.GetActiveVersionAsync());
            ContentUpgradeRecord record = Assert.Single(await ((IContentUpgradeLedger)lease.Store).ListUpgradesAsync());
            Assert.Equal(UpgradeHarness.FirstId, record.Id);
            Assert.Equal(2, record.VersionNumber);
            await exercise.CancelAsync();
        };

        OperationCanceledException escaped = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ContentUpgradeRunner.RunAsync(lease.Store, registry, UpgradeLifetimeCatalog.SetFor(registry),
                UpgradeFixtures.Apply(), exercise.Token));

        Assert.True(exercise.IsCancellationRequested);
        Assert.Same(packs.EnumerationCancellation, escaped);
        Assert.Contains(nameof(FileSystemPackStore), escaped.StackTrace, StringComparison.Ordinal);
        Assert.Equal(2, packs.Enumerations);
        Assert.Equal(2, (await lease.Store.ListVersionsAsync()).Count);
        Assert.Null(await lease.Store.GetOpenDraftAsync());
    }
}
