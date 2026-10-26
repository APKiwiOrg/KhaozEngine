using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>A real publish pipeline with gates only around its first sweep and the next complete freeze.</summary>
internal sealed class InterleavedUpgradePublishStore : ScriptedUpgradeStore, IContentTextAuthoringStore, IContentDraftFreezeStore
{
    readonly UpgradePublishSequence _sequence;
    readonly bool _older;
    readonly bool _sweepFails;
    readonly ContentPublishCommit _commit;
    bool _firstSweep;

    public bool SweepFailureInjected { get; private set; }

    public InterleavedUpgradePublishStore(
        IContentAuthoringStore inner,
        ContentTypeRegistry registry,
        UpgradePublishSequence sequence,
        bool older,
        bool sweepFails) : base(inner, older ? 4 : 3, older ? 1 : 2)
    {
        _sequence = sequence;
        _older = older;
        _sweepFails = sweepFails;
        _commit = new ContentPublishCommit(this, inner.PackStore!, new ContentPublisher(this, (IContentIdPersistence)inner, registry,
            onStep: step => CatalogPhaseWatch.Enter("publish " + step)));
    }

    IContentTextAuthoringStore Text => (IContentTextAuthoringStore)Inner;

    /// <inheritdoc />
    public override Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
    {
        // A failed scenario snapshot is evidence failure, even when the runner normally recovers from
        // its exception type. Stop recovery reads before they can obscure it with an observation guard.
        _sequence.ThrowIfFailed();
        return base.GetOpenDraftAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task ClearDraftFreezeAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("release scoped publish freeze",
            () => ((IContentDraftFreezeStore)Inner).ClearDraftFreezeAsync(expectedBaseVersion, cancellationToken));

    /// <inheritdoc />
    public override async Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request, CancellationToken cancellationToken = default)
    {
        bool first = _older && request.Upgrade?.Id == UpgradeHarness.FirstId;
        _firstSweep = first;
        Exception? publishFailure = null;
        try
        {
            return await _commit.PublishAsync(request, cancellationToken);
        }
        catch (Exception failure)
        {
            publishFailure = failure;
            throw;
        }
        finally
        {
            if (first)
            {
                try
                {
                    await _sequence.CaptureAndReleaseAsync(Inner, publishFailure);
                }
                catch (Exception cleanup) when (publishFailure is not null && !ReferenceEquals(cleanup, publishFailure))
                {
                    throw new AggregateException("Publish and cleanup both failed", publishFailure, cleanup);
                }
            }
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (_firstSweep)
        {
            _firstSweep = false;
            await _sequence.WaitForNextFreezeAsync();
            if (_sweepFails)
            {
                SweepFailureInjected = true;
                throw new IOException("the first publish's sweep failed after its commit");
            }
        }

        return await base.ListVersionsAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ContentTextPublishSnapshot> FreezeChangesAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
    {
        ContentTextPublishSnapshot snapshot = await CatalogPhaseWatch.ObserveAsync("complete publish freeze",
            () => Text.FreezeChangesAsync(expectedBaseVersion, cancellationToken));
        if (!_older && expectedBaseVersion == 2)
        {
            await _sequence.WaitForOldCleanupAsync();
        }

        return snapshot;
    }

    /// <inheritdoc />
    public Task<ContentDraft> ApplyChangesAsync(
        ContentAuthoringChanges changes, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
        => Text.ApplyChangesAsync(changes, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(int versionNumber, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("read baseline text", () => Text.ReadTextSnapshotAsync(versionNumber, cancellationToken));

    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitTextPublishAsync(
        ContentTextPublishPlan plan, ContentPublishRequest request, IPackVersionPointerStore? pointers, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("commit text publish", () => Text.CommitTextPublishAsync(plan, request, pointers, cancellationToken));

    /// <inheritdoc />
    public Task<bool> TryDiscardChangesAsync(ContentDraft expected, string actor, string operatorId, CancellationToken cancellationToken = default)
        => Text.TryDiscardChangesAsync(expected, actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public Task<ContentPublishResult> ImportTextBundleAsync(
        ContentBundle bundle, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
        => Text.ImportTextBundleAsync(bundle, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<ContentDraft> RollbackTextToAsync(
        int targetVersion, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
        => Text.RollbackTextToAsync(targetVersion, actor, operatorId, note, cancellationToken);
}
