using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>The optional whole-version read using the shared two-query revision reader.</summary>
public sealed partial class SqlServerContentAuthoringStore : IContentVersionRowSource
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ContentRowRevision>> ReadVersionRowsAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => ReadAsync(
            async (scope, token) =>
            {
                int at = versionNumber == 0
                    ? await ReadActiveVersionAsync(scope, token).ConfigureAwait(false)
                    : versionNumber;
                return await ReadRevisionsAsync(scope, default, null, at, token, registeredTypesOnly: true)
                    .ConfigureAwait(false);
            },
            cancellationToken);
}
