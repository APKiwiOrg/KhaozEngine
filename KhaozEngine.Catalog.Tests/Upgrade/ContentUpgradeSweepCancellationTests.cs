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
            Assert.False(token.IsCancellationRequested);
            Assert.Equal(2, await lease.Store.GetActiveVersionAsync());
            ContentUpgradeRecord record = Assert.Single(await ((IContentUpgradeLedger)lease.Store).ListUpgradesAsync());
            Assert.Equal(UpgradeHarness.FirstId, record.Id);
            Assert.Equal(2, record.VersionNumber);
            await exercise.CancelAsync();
            Assert.True(token.IsCancellationRequested);
        };

        OperationCanceledException escaped = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(lease.Store, registry, exercise.Token, new CatalogRunnerLifetime()));

        Assert.True(exercise.IsCancellationRequested);
        Assert.Same(packs.EnumerationCancellation, escaped);
        Assert.Contains(nameof(FileSystemPackStore), escaped.StackTrace, StringComparison.Ordinal);
        Assert.Equal(2, packs.Enumerations);
        Assert.Equal(2, (await lease.Store.ListVersionsAsync()).Count);
        Assert.Null(await lease.Store.GetOpenDraftAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHeldCharacterizationBeforeSweepHasANamedDiagnosticAndJoinsItsRunner(bool sqlite)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await UpgradeLifetimeCatalog.CreateAsync(files, registry, sqlite);
        using var exercise = new CancellationTokenSource();
        var clock = new ManualCatalogPhaseClock();
        var owner = new CatalogRunnerLifetime(clock);
        var held = new HeldInitialVersionStore(lease.Store);
        Task<ContentUpgradeReport> run = RunAsync(held, registry, exercise.Token, owner);
        try
        {
            // This setup safety does not depend on the deliberately held phase clock.
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            clock.ExpireActive();
            Assert.True(owner.Diagnostic.Task.IsCompletedSuccessfully,
                "the cancellation characterization ran outside its independent phase diagnostic");
            var failure = Assert.IsType<TimeoutException>(await owner.Diagnostic.Task);
            Assert.Contains("read active version", failure.Message, StringComparison.Ordinal);
            Assert.False(exercise.IsCancellationRequested);
            Task<ContentUpgradeReport> owned = Assert.Single(owner.Runs);
            Assert.False(owned.IsCompleted);
            Assert.False(run.IsCompleted, "the characterization returned before its held runner joined");
            held.Release.TrySetResult();
            Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() => run));
            Assert.All(owner.Runs, owned => Assert.True(owned.IsCompleted));
        }
        finally
        {
            // Keep the outer proof safe when the missing ownership assertion is deliberately red.
            held.Release.TrySetResult();
            await exercise.CancelAsync();
            try
            {
                await run;
            }
            catch (Exception)
            {
                // The proof checks the outcome above. Its runner must still be observed during RED.
            }
            finally
            {
                try { await owner.FinishAsync(); }
                catch (Exception) { }
            }
        }
    }

    static Task<ContentUpgradeReport> RunAsync(IContentAuthoringStore store, ContentTypeRegistry registry,
        CancellationToken exercise, CatalogRunnerLifetime owner)
        => ContentUpgradeFreezeReleaseTests.RunAsync(store, registry, exercise: exercise, owner: owner);

    sealed class HeldInitialVersionStore(IContentAuthoringStore inner) : ForwardingUpgradeStore(inner)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return await base.GetActiveVersionAsync(cancellationToken);
        }
    }

}
