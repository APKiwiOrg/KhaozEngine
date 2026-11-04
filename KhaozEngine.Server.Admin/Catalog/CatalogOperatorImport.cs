using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>Binds the host's operator policy without changing trusted server seed calls.</summary>
internal static class CatalogOperatorImport
{
    internal static Task<ContentPublishResult> ImportAsync(
        IContentAuthoringStore store,
        ContentBundle bundle,
        string operatorId,
        string note,
        IContentPublishPolicy? policy,
        CancellationToken cancellationToken)
    {
        if (policy is not null)
        {
            if (store is not IContentPolicyImportStore guarded)
            {
                throw new ContentAuthoringException(
                    "This authoring store cannot bind the host's policy to an operator import. Use a policy-aware provider or a trusted server seed path.",
                    default, 0, IContentPolicyImportStore.UnavailableReason);
            }

            return guarded.ImportWithPolicyAsync(bundle,
                new ContentPolicyImportRequest(policy, CatalogAdminActions.Actor, operatorId, note), cancellationToken);
        }

        return store is IContentTextAuthoringStore text
            ? text.ImportTextBundleAsync(bundle, CatalogAdminActions.Actor, operatorId, note, cancellationToken)
            : store.ImportBundleAsync(bundle, CatalogAdminActions.Actor, operatorId, note, cancellationToken);
    }
}
