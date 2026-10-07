using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>The atomic release that leaves a later draft's freeze intact.</summary>
public sealed partial class SqlServerContentAuthoringStore : IContentDraftFreezeStore
{
    /// <inheritdoc />
    Task IContentDraftFreezeStore.ClearDraftFreezeAsync(int expectedBaseVersion, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBaseVersion);
        return WriteAsync(
            async (scope, token) =>
            {
                await using SqlCommand command = Command(
                    scope,
                    """
                    UPDATE dbo.catalog_draft SET frozen_for_base_version = NULL, updated_at_utc = @now
                    WHERE draft_key = 1 AND frozen_for_base_version = @base;
                    """);
                BindInt(command, "@base", expectedBaseVersion);
                BindTime(command, "@now", _clock());
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }
}
