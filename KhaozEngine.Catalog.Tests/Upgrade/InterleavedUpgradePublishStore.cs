using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Finite gates for the commit, next freeze, old cleanup and next report.</summary>
internal sealed class UpgradePublishSequence
{
    public TaskCompletionSource FirstCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondFrozen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FirstReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ContentDraft? DraftAfterOlderPublish { get; set; }
}

/// <summary>A real publish pipeline with gates only around its first sweep and the next complete freeze.</summary>
internal sealed class InterleavedUpgradePublishStore : ForwardingUpgradeStore, IContentTextAuthoringStore, IContentDraftFreezeStore
{
    readonly UpgradePublishSequence _sequence;
    readonly bool _older;
    readonly bool _sweepFails;
    readonly ContentPublishCommit _commit;
    bool _firstSweep;

    public InterleavedUpgradePublishStore(
        IContentAuthoringStore inner,
        ContentTypeRegistry registry,
        UpgradePublishSequence sequence,
        bool older,
        bool sweepFails) : base(inner)
    {
        _sequence = sequence;
        _older = older;
        _sweepFails = sweepFails;
        _commit = new ContentPublishCommit(this, inner.PackStore!, new ContentPublisher(this, (IContentIdPersistence)inner, registry));
    }

    IContentTextAuthoringStore Text => (IContentTextAuthoringStore)Inner;

    /// <inheritdoc />
    public Task ClearDraftFreezeAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
        => ((IContentDraftFreezeStore)Inner).ClearDraftFreezeAsync(expectedBaseVersion, cancellationToken);

    /// <inheritdoc />
    public override async Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request, CancellationToken cancellationToken = default)
    {
        bool first = _older && request.Upgrade?.Id == UpgradeHarness.FirstId;
        _firstSweep = first;
        try
        {
            return await _commit.PublishAsync(request, cancellationToken);
        }
        finally
        {
            if (first)
            {
                _sequence.DraftAfterOlderPublish = await Inner.GetOpenDraftAsync(cancellationToken);
                _sequence.FirstReleased.TrySetResult();
                // Hold this runner before it starts the second definition. The next runner's report owns
                // the next transition, so neither scheduling nor a polling delay chooses the outcome.
                await _sequence.SecondFinished.Task.WaitAsync(cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (_firstSweep)
        {
            _firstSweep = false;
            _sequence.FirstCommitted.TrySetResult();
            await _sequence.SecondFrozen.Task.WaitAsync(cancellationToken);
            if (_sweepFails)
            {
                throw new IOException("the first publish's sweep failed after its commit");
            }
        }

        return await base.ListVersionsAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ContentTextPublishSnapshot> FreezeChangesAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
    {
        ContentTextPublishSnapshot snapshot = await Text.FreezeChangesAsync(expectedBaseVersion, cancellationToken);
        if (!_older && expectedBaseVersion == 2)
        {
            _sequence.SecondFrozen.TrySetResult();
            await _sequence.FirstReleased.Task.WaitAsync(cancellationToken);
        }

        return snapshot;
    }

    /// <inheritdoc />
    public Task<ContentDraft> ApplyChangesAsync(
        ContentAuthoringChanges changes, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
        => Text.ApplyChangesAsync(changes, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(int versionNumber, CancellationToken cancellationToken = default)
        => Text.ReadTextSnapshotAsync(versionNumber, cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitTextPublishAsync(
        ContentTextPublishPlan plan, ContentPublishRequest request, IPackVersionPointerStore? pointers, CancellationToken cancellationToken = default)
        => Text.CommitTextPublishAsync(plan, request, pointers, cancellationToken);

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
