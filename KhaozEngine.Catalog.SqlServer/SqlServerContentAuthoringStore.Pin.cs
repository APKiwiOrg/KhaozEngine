using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>The trusted pin write and its guarded optimistic confirmation share one transaction.</summary>
public sealed partial class SqlServerContentAuthoringStore : IContentVersionSelectionStore
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
    Task<bool> SetPinAsync(
        int? version,
        string actor,
        string operatorId,
        ContentVersionSelection? expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        return WriteAsync<bool>(
            async (scope, token) =>
            {
                if (expected is ContentVersionSelection selection)
                {
                    await using SqlCommand check = Command(scope,
                        "SELECT store_epoch, active_version, pinned_version FROM dbo.catalog_metadata WITH (UPDLOCK, HOLDLOCK) WHERE metadata_key = 1;");
                    await using SqlDataReader reader = await check.ExecuteReaderAsync(token).ConfigureAwait(false);
                    if (!await reader.ReadAsync(token).ConfigureAwait(false)
                        || !string.Equals(reader.GetString(0), selection.StoreEpoch, StringComparison.Ordinal)
                        || reader.GetInt32(1) != selection.ActiveVersion
                        || (reader.IsDBNull(2) ? null : (int?)reader.GetInt32(2)) != selection.PinnedVersion)
                    {
                        return false;
                    }
                }

                if (version is int held
                    && !await VersionExistsAsync(scope, held, token).ConfigureAwait(false))
                {
                    // The reason token is the unknown-version token every other unknown-version refusal and
                    // the in-memory reference store answer with, so a console that keys on it reads the same
                    // answer whichever provider is behind the seam.
                    throw new ContentAuthoringException(
                        ContentAuthoringRefusal.VersionUnknown,
                        FormattableString.Invariant($"Version {held} does not exist, so it cannot be pinned."),
                        default,
                        0,
                        ContentAuthoringException.UnknownVersionReason)
                    { VersionNumber = held };
                }

                int? before;
                await using (SqlCommand read = Command(
                    scope, "SELECT pinned_version FROM dbo.catalog_metadata WHERE metadata_key = 1;"))
                {
                    object? raw = await read.ExecuteScalarAsync(token).ConfigureAwait(false);
                    before = raw is int pinned ? pinned : null;
                }

                await using (SqlCommand write = Command(
                    scope,
                    """
                    UPDATE dbo.catalog_metadata
                    SET pinned_version = @pinned, updated_at_utc = @now
                    WHERE metadata_key = 1;
                    """))
                {
                    BindInt(write, "@pinned", version);
                    BindTime(write, "@now", _clock());
                    await write.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await AppendAuditAsync(
                    scope,
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
                    token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }
}
