using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Owns the two real publishers before awaiting any readiness signal.</summary>
internal sealed class UpgradePublishScenario
{
    readonly ContentTypeRegistry _registry;
    readonly ContentUpgradeSet _set;
    public UpgradePublishSequence Sequence { get; } = new();
    public CatalogRunnerLifetime Owner { get; }
    public InterleavedUpgradePublishStore Older { get; }
    public InterleavedUpgradePublishStore Next { get; }

    public UpgradePublishScenario(IContentAuthoringStore inner, ContentTypeRegistry registry,
        bool sweepFails = false, TimeProvider? clock = null, TimeProvider? safetyClock = null)
    {
        _registry = registry;
        _set = UpgradeLifetimeCatalog.SetFor(registry);
        Owner = new CatalogRunnerLifetime(clock, Sequence.Fail, safetyClock);
        Sequence.Lifetime = Owner.Token;
        // Older reads initial/preflight/frozen plus recovery or the already-recorded second preflight.
        // Next reads only initial/preflight/frozen. No scripted rival remains after those observations.
        Older = new InterleavedUpgradePublishStore(inner, registry, Sequence, true, sweepFails);
        Next = new InterleavedUpgradePublishStore(inner, registry, Sequence, false, false);
    }

    public Task<ContentUpgradeReport> StartOlder(CancellationToken exercise = default)
        => Owner.Start(token => RunAsync(Older, token, exercise, next: false));

    public Task<ContentUpgradeReport> StartNext(CancellationToken exercise = default)
        => Owner.Start(token => RunAsync(Next, token, exercise, next: true));

    public Task<(ContentUpgradeReport Older, ContentUpgradeReport Next)> RunAsync(CancellationToken nextExercise = default)
        => Owner.ExecuteAsync(async () =>
        {
            Task<ContentUpgradeReport> older = StartOlder();
            await Owner.ReadyAsync(Sequence.FirstCommitted.Task, older, "first commit");
            Task<ContentUpgradeReport> next = StartNext(nextExercise);
            await Owner.ReadyAsync(Sequence.SecondFrozen.Task, next, "next complete freeze");
            ContentUpgradeReport nextReport = await Owner.AwaitAsync(next);
            ContentUpgradeReport olderReport = await Owner.AwaitAsync(older);
            return (olderReport, nextReport);
        });

    async Task<ContentUpgradeReport> RunAsync(InterleavedUpgradePublishStore store,
        CancellationToken lifetime, CancellationToken exercise, bool next)
    {
        if (next)
        {
            Sequence.NextStarted();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, exercise);
        try
        {
            ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
                store, _registry, _set, UpgradeFixtures.Apply(), linked.Token);
            Sequence.ThrowIfFailed();
            if (next)
            {
                Sequence.Finished();
            }

            return report;
        }
        catch (Exception failure)
        {
            Sequence.Fail(failure);
            throw;
        }
    }
}
