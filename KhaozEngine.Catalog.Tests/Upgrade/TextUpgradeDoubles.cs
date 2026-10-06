using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// A store exposing the text companion and the upgrade ledger over the reference store, with controlled
/// sequencing hooks for the moments a text-aware upgrade run has to survive: a rival landing just before the
/// atomic discard, a publish that fails, an unknown text provenance and a baseline rebuilt without its text.
/// </summary>
/// <param name="inner">The reference store.</param>
internal sealed class TextUpgradeStore(InMemoryContentAuthoringStore inner)
    : ForwardingContentAuthoringStore(inner), IContentTextAuthoringStore, IContentUpgradeLedger
{
    IContentTextAuthoringStore Text => (IContentTextAuthoringStore)Inner;

    IContentUpgradeLedger Ledger => (IContentUpgradeLedger)Inner;

    /// <summary>Runs once, inside the store, immediately before the expected-draft discard compares.</summary>
    public Func<Task>? BeforeTryDiscard { get; set; }

    /// <summary>When set, the next publish fails with this refusal and is cleared.</summary>
    public ContentAuthoringException? FailNextPublish { get; set; }

    /// <summary>When true, every exact-version text read refuses as unknown provenance.</summary>
    public bool ProvenanceUnknown { get; set; }

    /// <summary>When true, the export is rebuilt through the OLD row-only constructor as format 1.</summary>
    public bool RebuildExportAsFormatOne { get; set; }

    /// <summary>How many expected-draft discards were asked for, and how many succeeded.</summary>
    public (int Asked, int Discarded) Discards { get; private set; }

    /// <inheritdoc />
    public override Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (FailNextPublish is ContentAuthoringException failure)
        {
            FailNextPublish = null;
            throw failure;
        }

        return base.PublishAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<ContentBundle> ExportBundleAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        ContentBundle bundle = await base.ExportBundleAsync(versionNumber, cancellationToken);
        return RebuildExportAsFormatOne
            ? new ContentBundle(1, bundle.StoreEpoch, bundle.SourceVersion, bundle.Types, bundle.Rows, bundle.Families, bundle.Rules)
            : bundle;
    }

    /// <inheritdoc />
    public Task<ContentDraft> ApplyChangesAsync(
        ContentAuthoringChanges changes, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
        => Text.ApplyChangesAsync(changes, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<ContentTextPublishSnapshot> FreezeChangesAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
        => Text.FreezeChangesAsync(expectedBaseVersion, cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(int versionNumber, CancellationToken cancellationToken = default)
        => ProvenanceUnknown
            ? throw new ContentAuthoringException(
                FormattableString.Invariant($"Version {versionNumber} has no complete text record."),
                default,
                0,
                ContentAuthoringException.TextProvenanceUnknownReason)
            : Text.ReadTextSnapshotAsync(versionNumber, cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitTextPublishAsync(
        ContentTextPublishPlan plan, ContentPublishRequest request, IPackVersionPointerStore? pointers, CancellationToken cancellationToken = default)
        => Text.CommitTextPublishAsync(plan, request, pointers, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TryDiscardChangesAsync(
        ContentDraft expected, string actor, string operatorId, CancellationToken cancellationToken = default)
    {
        if (BeforeTryDiscard is Func<Task> rival)
        {
            BeforeTryDiscard = null;
            await rival();
        }

        bool discarded = await Text.TryDiscardChangesAsync(expected, actor, operatorId, cancellationToken);
        Discards = (Discards.Asked + 1, Discards.Discarded + (discarded ? 1 : 0));
        return discarded;
    }

    /// <inheritdoc />
    public Task<ContentPublishResult> ImportTextBundleAsync(
        ContentBundle bundle, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
        => Text.ImportTextBundleAsync(bundle, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<ContentDraft> RollbackTextToAsync(
        int targetVersion, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
        => Text.RollbackTextToAsync(targetVersion, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(CancellationToken cancellationToken = default)
        => Ledger.ListUpgradesAsync(cancellationToken);

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp, ContentUpgradeDisposition disposition, string actor, string operatorId, CancellationToken cancellationToken = default)
        => Ledger.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);
}

/// <summary>
/// An OLD wrapper: it exposes only the row-only seam and the ledger, and it rebuilds every draft through the
/// old constructor, so text the backend holds is invisible to whatever reads through it. It can also land a
/// rival translation straight into the backend right after the run's own write, which is the moment an
/// operator's console would.
/// <para>
/// Its guarded freeze companion is inherited unchanged and forwards the backend's genuine complete snapshot,
/// text state included, because that freeze answers from inside the store. Only the draft reads and writes this
/// wrapper answers itself are rebuilt.
/// </para>
/// </summary>
/// <param name="inner">The reference store, which keeps the text and the ledger.</param>
internal sealed class OldWrapperStore(InMemoryContentAuthoringStore inner)
    : ForwardingContentAuthoringStore(inner), IContentUpgradeLedger
{
    IContentUpgradeLedger Ledger => (IContentUpgradeLedger)Inner;

    /// <summary>Runs once, against the backend, right after the next row write lands.</summary>
    public Func<Task>? AfterNextWrite { get; set; }

    /// <inheritdoc />
    public override async Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
        => Strip(await base.GetOpenDraftAsync(cancellationToken));

    /// <inheritdoc />
    public override async Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
    {
        ContentDraft written = await base.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken);
        if (AfterNextWrite is Func<Task> rival)
        {
            AfterNextWrite = null;
            await rival();
            written = (await base.GetOpenDraftAsync(cancellationToken))!;
        }

        return Strip(written)!;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(CancellationToken cancellationToken = default)
        => Ledger.ListUpgradesAsync(cancellationToken);

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp, ContentUpgradeDisposition disposition, string actor, string operatorId, CancellationToken cancellationToken = default)
        => Ledger.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);

    static ContentDraft? Strip(ContentDraft? draft)
        => draft is null
            ? null
            : new ContentDraft(
                draft.BaseVersion, draft.OpenedBy, draft.OpenedAtUtc, draft.Note, draft.Changes, draft.FrozenForBaseVersion);
}
