using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The GUARDED freeze and its recorded-base release on <c>dbo.catalog_draft.frozen_for_base_version</c>. Each member
/// runs inside one Serializable write scope that holds every read deciding its write, the write itself and the draft
/// it answers with, so no other transaction can interleave with any of them.
/// <para>
/// <b>No stale sweep runs here.</b> The text freeze and the baseline read still clear a marker naming an older base
/// in a scope of their own, but a refused guarded freeze must write nothing, a stale marker included. A successful
/// one replaces it in its own update.
/// </para>
/// <para>
/// <b>Gated coverage against a real instance.</b> Both statements mirror the SQLite provider's. The SQL Server
/// conformance and timestamp classes override the shared facts under their gated attribute, so they run only when
/// <c>KE_CATALOG_SQLSERVER</c> names an instance, which the catalog SQL Server CI job sets. A local run without it
/// skips them, so this provider's guarded freeze is proven only by a run that executed them.
/// </para>
/// <para>
/// <b>A base-version-moved refusal is not always a moved base here.</b> A deadlock victim or a snapshot conflict
/// inside either member's write scope surfaces with the same reason, because the scope maps provider contention to
/// it, as it does for every other write. The upgrade runner already reads that reason as contention, and its
/// classification is unchanged.
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore : IContentConditionalDraftFreeze
{
    /// <inheritdoc />
    public Task<ContentDraft> FreezeDraftForBaseAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBaseVersion);

        return WriteAsync(
            async (scope, token) =>
            {
                int active = await ReadActiveVersionAsync(scope, token).ConfigureAwait(false);
                if (expectedBaseVersion != active)
                {
                    throw Moved(FormattableString.Invariant(
                        $"The publish expects base version {expectedBaseVersion} and the store stands at {active}. Another publish landed in between, so this draft is against a version that is no longer the base."));
                }

                if (await ReadDraftAsync(scope, token).ConfigureAwait(false) is not ContentDraft open
                    || open.EditCount == 0)
                {
                    throw new ContentAuthoringException(
                        "There is no open draft with pending row edits, so there is nothing to publish.",
                        default,
                        0,
                        ContentAuthoringException.NoOpenDraftReason);
                }

                await RequireRowOnlyRepresentableAsync(scope, open, nameof(FreezeDraftForBaseAsync), token)
                    .ConfigureAwait(false);

                // The legacy freeze's statement, so a marker already naming this base keeps its update time.
                await using (SqlCommand command = Command(
                    scope,
                    """
                    UPDATE dbo.catalog_draft
                    SET frozen_for_base_version = @base,
                        updated_at_utc = CASE WHEN frozen_for_base_version = @base THEN updated_at_utc ELSE @now END
                    WHERE draft_key = 1;
                    """))
                {
                    BindInt(command, "@base", expectedBaseVersion);
                    BindTime(command, "@now", _clock());
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                return await RequireDraftAsync(scope, token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ReleaseDraftFreezeForBaseAsync(int frozenForBaseVersion, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frozenForBaseVersion);

        return WriteAsync(
            async (scope, token) =>
            {
                await using SqlCommand command = Command(
                    scope,
                    """
                    UPDATE dbo.catalog_draft SET frozen_for_base_version = NULL, updated_at_utc = @now
                    WHERE draft_key = 1 AND frozen_for_base_version = @base;
                    """);
                BindInt(command, "@base", frozenForBaseVersion);
                BindTime(command, "@now", _clock());
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 0;
            },
            cancellationToken);
    }
}
