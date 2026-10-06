using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The GUARDED freeze and its recorded-base release on <c>catalog_draft.frozen_for_base_version</c>. Each member holds
/// one lease and one transaction for every read that decides its write, the write itself and the draft it answers
/// with, so no other caller on this database can interleave with any of them.
/// <para>
/// <b>No stale sweep runs here.</b> The text freeze and the baseline read still clear a marker naming an older base
/// in a transaction of their own, but a refused guarded freeze must write nothing, a stale marker included. A
/// successful one replaces it in its own update.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore : IContentConditionalDraftFreeze
{
    /// <inheritdoc />
    public async Task<ContentDraft> FreezeDraftForBaseAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBaseVersion);

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        int active = await ReadActiveAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (expectedBaseVersion != active)
        {
            throw Moved(FormattableString.Invariant(
                $"The publish expects base version {expectedBaseVersion} and the store stands at {active}. Another publish landed in between, so this draft is against a version that is no longer the base."));
        }

        if (await ReadDraftAsync(transaction, cancellationToken).ConfigureAwait(false) is not ContentDraft open
            || open.EditCount == 0)
        {
            throw new ContentAuthoringException(
                "There is no open draft with pending row edits, so there is nothing to publish.",
                default,
                0,
                ContentAuthoringException.NoOpenDraftReason);
        }

        await RequireRowOnlyRepresentableAsync(open, nameof(FreezeDraftForBaseAsync), transaction, cancellationToken)
            .ConfigureAwait(false);

        // The legacy freeze's statement, so a marker already naming this base keeps its update time.
        using (SqliteCommand command = Command(
            """
            UPDATE catalog_draft
            SET frozen_for_base_version = $base,
                updated_at_utc = CASE WHEN frozen_for_base_version IS $base THEN updated_at_utc ELSE $now END
            WHERE draft_key = 1;
            """,
            transaction))
        {
            Bind(command, "$base", (long)expectedBaseVersion);
            Bind(command, "$now", Millis(_clock()));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        ContentDraft frozen = await RequireDraftAsync(transaction, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return frozen;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseDraftFreezeForBaseAsync(
        int frozenForBaseVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frozenForBaseVersion);

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand command = Command(
            """
            UPDATE catalog_draft SET frozen_for_base_version = NULL, updated_at_utc = $now
            WHERE draft_key = 1 AND frozen_for_base_version = $base;
            """,
            transaction);
        Bind(command, "$base", (long)frozenForBaseVersion);
        Bind(command, "$now", Millis(_clock()));
        bool cleared = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 0;
        transaction.Commit();
        return cleared;
    }
}
