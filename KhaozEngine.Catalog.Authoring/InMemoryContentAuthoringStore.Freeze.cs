using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The DRAFT FREEZE of spec 6.2 on the in-memory store: the marker a publish sets at step 1, the release it
/// runs on every exit path, and the refusal every draft write reads.
/// <para>
/// <b>It is a field on the draft rather than a held lock, which is the providers' shape and not a
/// simplification of it.</b> A publish spans steps 1 to 10, step 9 writes the whole pack, and no provider
/// here holds a row lock across that. The reference store could hold its one gate for the duration, and does
/// not, deliberately: a reference that made the publish exclusive would answer a question no provider can
/// answer the same way, and the conformance suite would then be pinning behaviour only this store has.
/// </para>
/// <para>
/// <b>A marker naming a version this store no longer stands at is STALE.</b> Only a publish that committed
/// and then died before its own release can leave one, and the draft it names would otherwise refuse every
/// edit forever, so the baseline read every publish starts with clears it.
/// </para>
/// </summary>
public sealed partial class InMemoryContentAuthoringStore
{
    /// <inheritdoc />
    public Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseVersion);

        lock (_gate)
        {
            ContentDraft open = _draft
                ?? throw new ContentAuthoringException(
                    "There is no open draft to freeze, so there is nothing to publish.",
                    default,
                    0,
                    ContentAuthoringException.NoOpenDraftReason);

            // It OVERWRITES rather than refusing an already frozen draft. A marker a dead publish left behind
            // must not block the retry, and the retry is exactly what an operator does to recover.
            _draft = Reframe(open, open.BaseVersion, open.Changes, baseVersion);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ClearFreeze();
            return Task.CompletedTask;
        }
    }

    /// <summary>The freeze released, leaving the draft and its edits alone. The caller already holds the gate.</summary>
    void ClearFreeze()
    {
        if (_draft is { IsFrozen: true } frozen)
        {
            _draft = Reframe(frozen, frozen.BaseVersion, frozen.Changes, null);
        }
    }

    /// <summary>
    /// A marker naming a base version this store no longer stands at, cleared. The caller already holds the
    /// gate, and this runs on the baseline read rather than on the draft read: a console polling the draft
    /// should not be the thing that repairs it, and the publish is what needs it repaired.
    /// </summary>
    void ClearStaleFreeze()
    {
        if (_draft is { FrozenForBaseVersion: int frozen } && frozen != _activeVersion)
        {
            ClearFreeze();
        }
    }

    /// <summary>
    /// The refusal every draft write runs first. The caller already holds the gate.
    /// </summary>
    /// <param name="member">The member being refused, which is what the message names.</param>
    void RequireNotFrozen(string member)
    {
        if (_draft is { FrozenForBaseVersion: int frozen })
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"{member} is refused: a publish standing on version {frozen} holds this draft frozen. The change set that publish read at step 1 is the one it will delete, so an edit arriving now would either be published without review or be deleted unpublished."),
                default,
                0,
                ContentAuthoringException.PublishInProgressReason);
        }
    }

    /// <summary>The same draft with a different change set, base version and freeze marker.</summary>
    /// <param name="draft">The draft whose opener, stamp and note are kept.</param>
    /// <param name="baseVersion">The base version the rebuilt draft names.</param>
    /// <param name="changes">The change set the rebuilt draft carries.</param>
    /// <param name="frozenForBaseVersion">The freeze marker, or null for a released draft.</param>
    static ContentDraft Reframe(
        ContentDraft draft,
        int baseVersion,
        ContentChangeSet changes,
        int? frozenForBaseVersion)
        => new(
            baseVersion, draft.OpenedBy, draft.OpenedAtUtc, draft.Note, changes, frozenForBaseVersion);

    /// <summary>
    /// Step 10's draft delete, scoped to the edits the plan FROZE. The caller already holds the gate.
    /// <para>
    /// The freeze is what makes that set the whole draft, so an edit surviving here means the marker did not
    /// hold. It is kept rather than deleted: an edit published without review is a defect and an edit deleted
    /// unpublished is a lost afternoon, and the draft carrying it forward is the only outcome that is
    /// neither. The survivor's base version moves to the version that just committed, because that is what
    /// its edits now sit on top of.
    /// </para>
    /// </summary>
    /// <param name="plan">The plan committing, whose frozen edits leave the draft.</param>
    void DeleteFrozenEdits(ContentPublishPlan plan)
    {
        if (_draft is not ContentDraft open)
        {
            return;
        }

        var published = new HashSet<ContentEditTarget>();
        for (int i = 0; i < plan.FrozenEdits.Count; i++)
        {
            published.Add(ContentChangeSet.TargetOf(plan.FrozenEdits[i]));
        }

        var survivors = new List<ContentEdit>();
        IReadOnlyList<ContentEdit> standing = open.Changes.Edits;
        for (int i = 0; i < standing.Count; i++)
        {
            if (!published.Contains(ContentChangeSet.TargetOf(standing[i])))
            {
                survivors.Add(standing[i]);
            }
        }

        _draft = survivors.Count == 0
            ? null
            : Reframe(open, plan.VersionNumber, new ContentChangeSet(survivors), null);
    }
}
