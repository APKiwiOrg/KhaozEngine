using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>The operator seed route, carrying a required host policy to the existing frozen publisher.</summary>
public sealed partial class InMemoryContentAuthoringStore : IContentPolicyImportStore
{
    /// <inheritdoc />
    public Task<ContentPublishResult> ImportWithPolicyAsync(
        ContentBundle bundle,
        ContentPolicyImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(request);
        ContentBundleTextState text = ContentBundleTextCompatibility.TextOf(bundle, nameof(ImportWithPolicyAsync));
        return ImportAsync(bundle, text, request.Actor, request.Operator, request.Note,
            nameof(ImportWithPolicyAsync), cancellationToken, request.Policy);
    }
}
