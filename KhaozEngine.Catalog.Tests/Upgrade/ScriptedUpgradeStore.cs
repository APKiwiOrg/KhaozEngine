using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Finite runner observations and freeze transitions for a known two-definition script.</summary>
internal class ScriptedUpgradeStore(IContentAuthoringStore inner, int draftBound, params int[] freezeBases)
    : ForwardingUpgradeStore(inner), IContentDraftFreezeStore
{
    int _freezes;
    public int DraftReads { get; private set; }

    public override Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
    {
        DraftReads++;
        if (DraftReads > draftBound)
        {
            throw new CatalogScriptException(CatalogScriptRefusal.ObservationBoundExceeded,
                $"Catalog draft observation bound {draftBound} exceeded at read {DraftReads}.")
            {
                MaximumReads = draftBound,
                ActualReads = DraftReads,
            };
        }

        return CatalogPhaseWatch.ObserveAsync(CatalogPhase.ReadOpenDraft, () => base.GetOpenDraftAsync(cancellationToken));
    }

    public override Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
    {
        int index = _freezes;
        if (_freezes >= freezeBases.Length || freezeBases[_freezes++] != baseVersion)
        {
            bool exhausted = index >= freezeBases.Length;
            throw new CatalogScriptException(exhausted ? CatalogScriptRefusal.FreezeCountExceeded
                : CatalogScriptRefusal.FreezeBaseVersionMismatch,
                "Catalog freeze transition repeated or moved outside its script.")
            {
                ActualIndex = index,
                MaximumCount = exhausted ? freezeBases.Length : null,
                ExpectedBaseVersion = exhausted ? null : freezeBases[index],
                ActualBaseVersion = exhausted ? null : baseVersion,
            };
        }

        return CatalogPhaseWatch.ObserveAsync(CatalogPhase.FreezeRunnerDraft, () => base.FreezeDraftAsync(baseVersion, cancellationToken));
    }

    public override Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync(CatalogPhase.ReadActiveVersion, () => base.GetActiveVersionAsync(cancellationToken));

    public override Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync(CatalogPhase.ReadUpgradeLedger, () => base.ListUpgradesAsync(cancellationToken));

    public override Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync(CatalogPhase.ReadOperatorPin, () => base.GetPinnedVersionAsync(cancellationToken));

    public override Task<ContentBundle> ExportBundleAsync(int versionNumber, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync(CatalogPhase.ExportBaseline, () => base.ExportBundleAsync(versionNumber, cancellationToken));

    public override Task<ContentVersionRecord?> GetVersionAsync(int versionNumber, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync(CatalogPhase.ReadBaselineVersion, () => base.GetVersionAsync(versionNumber, cancellationToken));

    public override Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync(CatalogPhase.ListSweepVersions, () => base.ListVersionsAsync(cancellationToken));

    public override Task<ContentDraft> ApplyEditsAsync(IReadOnlyList<ContentEdit> edits,
        string actor, string operatorId, string note, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync(CatalogPhase.WriteRunnerDraft, () => base.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken));

    public override Task<ContentPublishResult> PublishAsync(ContentPublishRequest request, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync(CatalogPhase.PublishRunnerDraft, () => base.PublishAsync(request, cancellationToken));

    public override Task DiscardDraftAsync(string actor, string operatorId, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync(CatalogPhase.DiscardRunnerDraft, () => base.DiscardDraftAsync(actor, operatorId, cancellationToken));

    public override Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync(CatalogPhase.ReleaseRunnerFreeze, () => base.ClearDraftFreezeAsync(cancellationToken));

    public virtual Task ClearDraftFreezeAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync(CatalogPhase.ReleaseScopedRunnerFreeze, () => Inner is IContentDraftFreezeStore scoped
            ? scoped.ClearDraftFreezeAsync(expectedBaseVersion, cancellationToken)
            : Inner.ClearDraftFreezeAsync(cancellationToken));
}
