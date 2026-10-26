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
            throw new InvalidOperationException($"Catalog draft observation bound {draftBound} exceeded at read {DraftReads}.");
        }

        return CatalogPhaseWatch.ObserveAsync("read open draft", () => base.GetOpenDraftAsync(cancellationToken));
    }

    public override Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
    {
        if (_freezes >= freezeBases.Length || freezeBases[_freezes++] != baseVersion)
        {
            throw new InvalidOperationException("Catalog freeze transition repeated or moved outside its script.");
        }

        return CatalogPhaseWatch.ObserveAsync("freeze runner draft", () => base.FreezeDraftAsync(baseVersion, cancellationToken));
    }

    public override Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("read active version", () => base.GetActiveVersionAsync(cancellationToken));

    public override Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("read upgrade ledger", () => base.ListUpgradesAsync(cancellationToken));

    public override Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("read operator pin", () => base.GetPinnedVersionAsync(cancellationToken));

    public override Task<ContentBundle> ExportBundleAsync(int versionNumber, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("export baseline", () => base.ExportBundleAsync(versionNumber, cancellationToken));

    public override Task<ContentVersionRecord?> GetVersionAsync(int versionNumber, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("read baseline version", () => base.GetVersionAsync(versionNumber, cancellationToken));

    public override Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("list sweep versions", () => base.ListVersionsAsync(cancellationToken));

    public override Task<ContentDraft> ApplyEditsAsync(IReadOnlyList<ContentEdit> edits,
        string actor, string operatorId, string note, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("write runner draft", () => base.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken));

    public override Task<ContentPublishResult> PublishAsync(ContentPublishRequest request, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("publish runner draft", () => base.PublishAsync(request, cancellationToken));

    public override Task DiscardDraftAsync(string actor, string operatorId, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("discard runner draft", () => base.DiscardDraftAsync(actor, operatorId, cancellationToken));

    public override Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("release runner freeze", () => base.ClearDraftFreezeAsync(cancellationToken));

    public virtual Task ClearDraftFreezeAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("release scoped runner freeze", () => Inner is IContentDraftFreezeStore scoped
            ? scoped.ClearDraftFreezeAsync(expectedBaseVersion, cancellationToken)
            : Inner.ClearDraftFreezeAsync(cancellationToken));
}
