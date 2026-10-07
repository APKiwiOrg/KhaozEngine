using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>The atomic release that leaves a later draft's freeze intact.</summary>
public sealed partial class SqliteContentAuthoringStore : IContentDraftFreezeStore
{
    /// <inheritdoc />
    async Task IContentDraftFreezeStore.ClearDraftFreezeAsync(int expectedBaseVersion, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBaseVersion);
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand command = Command(
            """
            UPDATE catalog_draft SET frozen_for_base_version = NULL, updated_at_utc = $now
            WHERE draft_key = 1 AND frozen_for_base_version = $base;
            """,
            transaction);
        Bind(command, "$base", (long)expectedBaseVersion);
        Bind(command, "$now", Millis(_clock()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }
}
