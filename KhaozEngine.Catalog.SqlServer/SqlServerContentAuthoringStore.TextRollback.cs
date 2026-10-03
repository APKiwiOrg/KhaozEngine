using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The COMPANION ROLLBACK of the SQL Server store: one draft restoring an earlier version's row values AND its
/// text values, reviewed and published like any other. The legacy <see cref="RollbackToAsync"/> keeps
/// refusing a version that holds text, and this is the route that does not.
/// <para>
/// <b>One Serializable transaction.</b> The target is checked, the active version is read, both versions'
/// complete text is read, so an unknown version with no read-only proof refuses with
/// <see cref="ContentAuthoringException.TextProvenanceUnknownReason"/> before anything is written, the row plan
/// is prepared with its irreversible retirement blockers in force, and the changes and the audit are applied
/// together. The active version is read inside that transaction, unlike the legacy rollback's separate plan
/// read, so the version the plan reads is the one the changes land against: a publish moving it would have to
/// write the metadata row this transaction already holds. No old version changes, every
/// currently declared language stays declared, and nothing here unretires a row.
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore
{
    /// <inheritdoc />
    /// <remarks>
    /// The changes run through the same body <see cref="ApplyChangesAsync"/> uses, and the rollback's own audit
    /// row lands after them, so the ledger reads in the order the actions happened.
    /// </remarks>
    public Task<ContentDraft> RollbackTextToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        return WriteAsync(
            async (scope, token) =>
            {
                if (!await VersionExistsAsync(scope, targetVersion, token).ConfigureAwait(false))
                {
                    throw UnknownVersion(targetVersion);
                }

                int from = await ReadActiveVersionAsync(scope, token).ConfigureAwait(false);
                ContentVersionTextSnapshot current = await ReadTextSnapshotAtAsync(scope, from, token)
                    .ConfigureAwait(false);
                ContentVersionTextSnapshot target = await ReadTextSnapshotAtAsync(scope, targetVersion, token)
                    .ConfigureAwait(false);
                IReadOnlyList<ContentRowRevision> targetRows = await ReadRevisionsAsync(
                    scope, default, null, targetVersion, token).ConfigureAwait(false);
                IReadOnlyList<ContentRowRevision> currentRows = await ReadRevisionsAsync(
                    scope, default, null, from, token).ConfigureAwait(false);
                ContentRollbackPlan plan = ContentRollback.Prepare(
                    targetVersion,
                    targetRows,
                    from,
                    currentRows,
                    await ReadRulesAsync(scope, token).ConfigureAwait(false),
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
                ContentDraft draft = await ApplyChangesInAsync(scope, changes, actor, operatorId, note, token)
                    .ConfigureAwait(false);
                await AppendAuditAsync(
                    scope,
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
                    token).ConfigureAwait(false);
                return draft;
            },
            cancellationToken);
    }
}
