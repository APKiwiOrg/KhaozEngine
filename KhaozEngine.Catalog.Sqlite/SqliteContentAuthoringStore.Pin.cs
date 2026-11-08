using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>The trusted pin write and its guarded optimistic confirmation share one transaction.</summary>
public sealed partial class SqliteContentAuthoringStore : IContentVersionSelectionStore
{
    /// <inheritdoc />
    public Task<bool> TrySetPinnedVersionAsync(
        int? version,
        ContentVersionSelection expected,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => SetPinAsync(version, actor, operatorId, expected, cancellationToken);

    /// <summary>The common trusted and conditional pin transaction.</summary>
    async Task<bool> SetPinAsync(
        int? version,
        string actor,
        string operatorId,
        ContentVersionSelection? expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();

        if (expected is ContentVersionSelection selection)
        {
            using SqliteCommand check = Command(
                "SELECT store_epoch, active_version, pinned_version FROM catalog_metadata WHERE metadata_key = 1;", transaction);
            using SqliteDataReader reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || !string.Equals(reader.GetString(0), selection.StoreEpoch, StringComparison.Ordinal)
                || reader.GetInt64(1) != selection.ActiveVersion
                || (reader.IsDBNull(2) ? null : (int?)reader.GetInt64(2)) != selection.PinnedVersion)
            {
                return false;
            }
        }

        if (version is int held && !await VersionExistsAsync(held, transaction, cancellationToken).ConfigureAwait(false))
        {
            // The reason token is the one the in-memory reference store answers with, so a console that keys
            // on it reads the same answer whichever provider is behind the seam.
            throw new ContentAuthoringException(
                ContentAuthoringRefusal.VersionUnknown,
                FormattableString.Invariant($"Version {held} does not exist, so it cannot be pinned."),
                default,
                0,
                ContentAuthoringException.UnknownTypeReason) { VersionNumber = held };
        }

        int? before;
        using (SqliteCommand read = Command(
            "SELECT pinned_version FROM catalog_metadata WHERE metadata_key = 1;", transaction))
        {
            object? raw = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            before = raw is long pinned ? (int)pinned : null;
        }

        using (SqliteCommand write = Command(
            """
            UPDATE catalog_metadata
            SET pinned_version = $pinned, updated_at_utc = $now
            WHERE metadata_key = 1;
            """,
            transaction))
        {
            Bind(write, "$pinned", version);
            Bind(write, "$now", Millis(_clock()));
            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AppendAuditAsync(
            transaction,
            ContentAuditActions.Pin,
            actor,
            operatorId,
            default,
            0,
            default,
            string.Empty,
            Render(before),
            Render(version),
            0,
            string.Empty,
            cancellationToken).ConfigureAwait(false);

        transaction.Commit();
        return true;
    }
}
