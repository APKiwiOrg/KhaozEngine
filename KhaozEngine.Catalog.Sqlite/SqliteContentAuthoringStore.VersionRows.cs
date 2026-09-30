using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>The optional whole-version read using the shared two-query revision reader.</summary>
public sealed partial class SqliteContentAuthoringStore : IContentVersionRowSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentRowRevision>> ReadVersionRowsAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        int at = versionNumber == 0
            ? (int)await ReadLongAsync(
                "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", null, cancellationToken)
                .ConfigureAwait(false)
            : versionNumber;
        return await ReadRevisionsAsync(default, null, at, null, cancellationToken, registeredTypesOnly: true)
            .ConfigureAwait(false);
    }
}
