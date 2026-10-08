using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>One publish's cleanup debt, settled by its commit or released only on its own base version.</summary>
sealed class ContentDraftFreezeRelease(IContentAuthoringStore store, int baseVersion)
{
    bool _committed;

    /// <summary>The transaction consumed this attempt's draft, so no later cleanup is owed.</summary>
    internal void Committed() => _committed = true;

    /// <summary>Releases an unfinished attempt, including a freeze that committed before its acknowledgement failed.</summary>
    internal Task ReleaseAsync()
        => _committed ? Task.CompletedTask : ClearAsync(store, baseVersion, CancellationToken.None);

    /// <summary>
    /// Uses atomic base-scoped release when supplied. Legacy stores retain their original unscoped release,
    /// which cannot guarantee that a concurrent later draft is preserved.
    /// </summary>
    internal static Task ClearAsync(IContentAuthoringStore store, int baseVersion, CancellationToken cancellationToken)
        => store is IContentDraftFreezeStore scoped
            ? scoped.ClearDraftFreezeAsync(baseVersion, cancellationToken)
            : store.ClearDraftFreezeAsync(cancellationToken);
}
