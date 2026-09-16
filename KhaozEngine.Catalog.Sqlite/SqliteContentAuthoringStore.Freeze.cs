using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The DRAFT FREEZE of spec 6.2: <c>catalog_draft.frozen_for_base_version</c>, the marker a publish sets at
/// step 1, the release it runs on every exit path, and the refusal every draft write reads.
/// <para>
/// <b>It is a column rather than the row lock the spec describes, and that is forced rather than chosen.</b>
/// This provider leases its one connection per call, and step 9 writes the whole pack outside any lease, so
/// no lock it can take spans steps 1 to 10. A durable marker does, and it survives the process too, which a
/// lock never could: a publish killed after its commit leaves one behind, and that case has to be
/// recoverable rather than merely rare.
/// </para>
/// <para>
/// <b>A marker naming a base version the database no longer stands at is STALE.</b> Only a publish that
/// committed and then died before its own release can leave one, and the draft it names would otherwise
/// refuse every edit forever, so the baseline read every publish starts with clears it.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore
{
    /// <inheritdoc />
    public async Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseVersion);

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();

        // It OVERWRITES rather than refusing an already frozen draft. A marker a dead publish left behind
        // must not block the retry, and the retry is exactly what an operator does to recover.
        using (SqliteCommand command = Command(
            "UPDATE catalog_draft SET frozen_for_base_version = $base WHERE draft_key = 1;", transaction))
        {
            Bind(command, "$base", (long)baseVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new ContentAuthoringException(
                    "There is no open draft to freeze, so there is nothing to publish.",
                    default,
                    0,
                    ContentAuthoringException.NoOpenDraftReason);
            }
        }

        transaction.Commit();
    }

    /// <inheritdoc />
    public async Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        await ClearFreezeAsync(transaction, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    /// <summary>The freeze released, leaving the draft and its edits alone. The caller owns the transaction.</summary>
    /// <param name="transaction">The caller's transaction.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    async Task ClearFreezeAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            "UPDATE catalog_draft SET frozen_for_base_version = NULL WHERE draft_key = 1;", transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A marker naming a base version this database no longer stands at, cleared. The caller holds the lease
    /// and this runs on the baseline read rather than on the draft read: a console polling the draft should
    /// not be the thing that repairs it, and the publish is what needs it repaired.
    /// </summary>
    /// <param name="cancellationToken">Cancels the statements.</param>
    async Task ClearStaleFreezeAsync(CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using (SqliteCommand command = Command(
            """
            UPDATE catalog_draft SET frozen_for_base_version = NULL
            WHERE draft_key = 1
              AND frozen_for_base_version IS NOT NULL
              AND frozen_for_base_version <>
                  (SELECT active_version FROM catalog_metadata WHERE metadata_key = 1);
            """,
            transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    /// <summary>
    /// The refusal every draft write runs first. The caller holds the lease, and the read is inside the
    /// caller's own transaction where it has one, so the marker cannot be written between the check and the
    /// write it guards.
    /// </summary>
    /// <param name="member">The member being refused, which is what the message names.</param>
    /// <param name="transaction">The caller's transaction, or null to read outside one.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    async Task RequireNotFrozenAsync(
        string member,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            "SELECT frozen_for_base_version FROM catalog_draft WHERE draft_key = 1;", transaction);
        object? held = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (held is null or DBNull)
        {
            return;
        }

        throw new ContentAuthoringException(
            FormattableString.Invariant(
                $"{member} is refused: a publish standing on version {Convert.ToInt64(held, System.Globalization.CultureInfo.InvariantCulture)} holds this draft frozen. The change set that publish read at step 1 is the one it will delete, so an edit arriving now would either be published without review or be deleted unpublished."),
            default,
            0,
            ContentAuthoringException.PublishInProgressReason);
    }

    /// <summary>
    /// Step 10's draft delete, scoped to the edits the plan FROZE. The caller owns the transaction.
    /// <para>
    /// The freeze is what makes that set the whole draft, so an edit surviving here means the marker did not
    /// hold. It is kept rather than deleted: an edit published without review is a defect and an edit deleted
    /// unpublished is a lost afternoon, and the draft carrying it forward is the only outcome that is
    /// neither. The survivor's base version moves to the version that just committed, because that is what
    /// its edits now sit on top of, and the marker goes either way.
    /// </para>
    /// <para>
    /// The edit's field rows go with it through the cascade the schema declares, which is why the bootstrap
    /// turns foreign keys on.
    /// </para>
    /// </summary>
    /// <param name="plan">The plan committing, whose frozen edits leave the draft.</param>
    /// <param name="transaction">The commit's one transaction.</param>
    /// <param name="cancellationToken">Cancels the statements.</param>
    async Task DeleteFrozenEditsAsync(
        ContentPublishPlan plan,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentEdit> frozen = plan.FrozenEdits;
        for (int i = 0; i < frozen.Count; i++)
        {
            ContentEditTarget target = ContentChangeSet.TargetOf(frozen[i]);
            using SqliteCommand command = Command(
                """
                DELETE FROM catalog_draft_edit
                WHERE type_id = $type AND definition_id = $id AND content_key = $key;
                """,
                transaction);
            Bind(command, "$type", (long)target.Type.Value);
            Bind(command, "$id", (long)target.DefinitionId);
            Bind(command, "$key", target.Key.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long survivors = await ReadLongAsync(
            "SELECT COUNT(*) FROM catalog_draft_edit;", transaction, cancellationToken).ConfigureAwait(false);
        if (survivors == 0)
        {
            using SqliteCommand draft = Command("DELETE FROM catalog_draft;", transaction);
            await draft.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using SqliteCommand rebase = Command(
            """
            UPDATE catalog_draft SET base_version = $version, frozen_for_base_version = NULL
            WHERE draft_key = 1;
            """,
            transaction);
        Bind(rebase, "$version", (long)plan.VersionNumber);
        await rebase.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
