using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>Checks and conditionally commits the host's guarded operator version selection.</summary>
internal static class CatalogPinPolicy
{
    internal static async Task<ContentValidationReport> ApplyAsync(
        IContentAuthoringStore store,
        ContentTypeRegistry registry,
        IContentPublishPolicy policy,
        int? wanted,
        string operatorId,
        CancellationToken cancellationToken)
    {
        if (store is not IContentVersionSelectionStore conditional)
        {
            return Unavailable("This store cannot conditionally confirm the checked version selection.");
        }

        ContentVersionSelection selection;
        try
        {
            selection = new ContentVersionSelection(
                await store.GetStoreEpochAsync(cancellationToken).ConfigureAwait(false),
                await store.GetActiveVersionAsync(cancellationToken).ConfigureAwait(false),
                await store.GetPinnedVersionAsync(cancellationToken).ConfigureAwait(false));
            int target = wanted ?? selection.ActiveVersion;
            if (target != selection.EffectiveVersion)
            {
                ContentSnapshot baseline = await ReadAsync(store, registry, selection.EffectiveVersion, cancellationToken).ConfigureAwait(false);
                ContentSnapshot candidate = await ReadAsync(store, registry, target, cancellationToken).ConfigureAwait(false);
                ContentValidationReport report = ContentPublishPolicyEvaluation.Validate(candidate, baseline, registry, policy);
                if (!report.IsValid)
                {
                    return report;
                }
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return Unavailable(FormattableString.Invariant(
                $"The published selection could not be checked safely: {failure.GetType().Name}: {failure.Message}"));
        }

        // A write may commit before its acknowledgement fails. Keep the store's operational error
        // semantics rather than reporting that uncertain outcome as a policy refusal with no write.
        return await conditional.TrySetPinnedVersionAsync(wanted, selection, CatalogAdminActions.Actor,
            operatorId, cancellationToken).ConfigureAwait(false)
            ? new ContentValidationReport(true, [])
            : Unavailable("The catalog epoch, active version or pin moved during the policy check. Read the selection again.");
    }

    static async Task<ContentSnapshot> ReadAsync(
        IContentAuthoringStore store, ContentTypeRegistry registry, int version, CancellationToken cancellationToken)
    {
        if (version <= 0)
        {
            throw new InvalidOperationException("The selection names no published version.");
        }

        ContentVersionRecord record = await store.GetVersionAsync(version, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected version record is unavailable.");
        ContentSnapshot snapshot = await store.LoadSnapshotAsync(version, registry, cancellationToken).ConfigureAwait(false);
        if (snapshot.VersionNumber != version
            || !string.Equals(snapshot.Identity.ManifestHash, record.ServerManifestHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The loaded snapshot does not identify the selected published version.");
        }

        return snapshot;
    }

    static ContentValidationReport Unavailable(string reason)
        => new(false, [new ContentFinding(default, 0, IContentVersionSelectionStore.UnavailableCode, reason)]);
}
