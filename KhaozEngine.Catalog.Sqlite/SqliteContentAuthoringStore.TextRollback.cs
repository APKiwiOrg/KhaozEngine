using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The COMPANION ROLLBACK of the SQLite store: one draft restoring an earlier version's row values AND its
/// text values, reviewed and published like any other. The legacy <see cref="RollbackToAsync"/> keeps
/// refusing a version that holds text, and this is the route that does not.
/// <para>
/// <b>One transaction under the one lease.</b> The target is checked, the active version is read, both
/// versions' complete text is read, so an unknown version with no read-only proof refuses with
/// <see cref="ContentAuthoringException.TextProvenanceUnknownReason"/> before anything is written, the row plan
/// is prepared with its irreversible retirement blockers in force, and the changes and the audit are applied
/// together. No publish can land between the read and the apply. No old version changes, every currently
/// declared language stays declared, and nothing here unretires a row.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore
{
    /// <inheritdoc />
    /// <remarks>
    /// The changes run through the same body <see cref="ApplyChangesAsync"/> uses, and the rollback's own audit
    /// row lands after them, so the ledger reads in the order the actions happened.
    /// </remarks>
    public async Task<ContentDraft> RollbackTextToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        if (!await VersionExistsAsync(targetVersion, transaction, cancellationToken).ConfigureAwait(false))
        {
            throw UnknownVersion(targetVersion);
        }

        int from = await ReadActiveAsync(transaction, cancellationToken).ConfigureAwait(false);
        ContentVersionTextSnapshot current = await ReadTextSnapshotAtAsync(from, transaction, cancellationToken)
            .ConfigureAwait(false);
        ContentVersionTextSnapshot target = await ReadTextSnapshotAtAsync(targetVersion, transaction, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ContentRowRevision> targetRows = await ReadRevisionsAsync(
            default, null, targetVersion, transaction, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ContentRowRevision> currentRows = await ReadRevisionsAsync(
            default, null, from, transaction, cancellationToken).ConfigureAwait(false);
        ContentRollbackPlan plan = ContentRollback.Prepare(
            targetVersion,
            targetRows,
            from,
            currentRows,
            await ReadRulesAsync(transaction, cancellationToken).ConfigureAwait(false),
            _registry);
        if (plan.IsBlocked)
        {
            throw ContentRollback.Refusal(plan);
        }

        for (int i = 0; i < plan.Edits.Count; i++)
        {
            CheckAgainstSchema(plan.Edits[i]);
        }

        var changes = new ContentAuthoringChanges(
            plan.Edits, ContentTextRollback.Changes(target, current, targetRows, currentRows));
        ContentDraft draft = await ApplyChangesInAsync(changes, actor, operatorId, note, transaction, cancellationToken)
            .ConfigureAwait(false);
        await AppendAuditAsync(
            transaction,
            ContentAuditActions.Rollback,
            actor,
            operatorId,
            default,
            0,
            default,
            string.Empty,
            Render(from),
            Render(targetVersion),
            0,
            note,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return draft;
    }
}
