using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The COMPANION ROLLBACK of the in-memory store: one draft restoring an earlier version's row values AND its
/// text values, reviewed and published like any other. The legacy <see cref="RollbackToAsync"/> keeps
/// refusing a version that holds text, and this is the route that does not.
/// <para>
/// Both versions' complete text is read first, so an unknown version refuses with
/// <see cref="ContentAuthoringException.TextProvenanceUnknownReason"/> before anything is written. The row
/// half is the ordinary rollback plan, whose irreversible retirement blockers stay in force, and the text half
/// is <see cref="ContentTextRollback"/>. No old version changes, every currently declared language stays
/// declared, and nothing here unretires a row.
/// </para>
/// </summary>
public sealed partial class InMemoryContentAuthoringStore
{
    /// <inheritdoc />
    /// <remarks>
    /// The versions, the plan, the audit and the changes share ONE gate, so a publish cannot land between the
    /// check and the apply. The rollback's own audit row is staged before the changes and committed after
    /// them, so the ledger reads in the order the actions happened.
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

        lock (_gate)
        {
            if (FindVersion(targetVersion) is null)
            {
                throw UnknownVersion(targetVersion);
            }

            int from = _activeVersion;
            ContentVersionTextSnapshot current = TextSnapshotAt(from);
            ContentVersionTextSnapshot target = TextSnapshotAt(targetVersion);
            List<ContentRowRevision> targetRows = LiveAt(targetVersion);
            List<ContentRowRevision> currentRows = LiveAt(from);
            ContentRollbackPlan plan = ContentRollback.Prepare(
                targetVersion, targetRows, from, currentRows, _rules, _registry);
            if (plan.IsBlocked)
            {
                throw ContentRollback.Refusal(plan);
            }

            var changes = new ContentAuthoringChanges(
                plan.Edits, ContentTextRollback.Changes(target, current, targetRows, currentRows));
            var staged = new List<ContentAuditEntry>(1);
            _audit.Stage(
                staged,
                ContentAuditActions.Rollback,
                actor,
                operatorId,
                default,
                0,
                default,
                string.Empty,
                InMemoryContentAuditLog.Render(from),
                InMemoryContentAuditLog.Render(targetVersion),
                0,
                note);

            ContentDraft draft = ApplyChangesLocked(changes, actor, operatorId, note);
            _audit.Commit(staged);
            return Task.FromResult(draft);
        }
    }
}
