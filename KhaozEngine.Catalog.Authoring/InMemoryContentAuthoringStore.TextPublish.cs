using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The TEXT COMMIT of the in-memory store: the confirmation a supplied <see cref="ContentTextPublishPlan"/>
/// meets against this store's actual state, the text the commit stages, and the row-only commit's carry of
/// the base version's text.
/// <para>
/// <b>One commit core serves both routes.</b> A text plan and a row-only plan differ only in what they stage
/// for text before <c>CommitLocked</c> runs, so the version, the rows, the text, every language mapping, the
/// audit, the upgrade ledger and the draft land in the same non-throwing tail, and the active pointer moves
/// last. A row-only commit records the base version's languages unchanged, so every version this store
/// commits is text complete.
/// </para>
/// </summary>
public sealed partial class InMemoryContentAuthoringStore
{
    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitTextPublishAsync(
        ContentTextPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            ConfirmTextPlan(plan);
            StagedText text = StageText(plan);
            return Task.FromResult(CommitLocked(plan.RowPlan, request, pointers, text, cancellationToken));
        }
    }

    /// <summary>
    /// The backend confirmation of a text plan: this store's epoch, the version and rule prefix, the active
    /// base, the actual frozen draft compared COMPLETELY with the plan's, the baseline text and languages
    /// compared with the store's own, and the eligibility of every insert. The caller already holds the gate.
    /// </summary>
    void ConfirmTextPlan(ContentTextPublishPlan plan)
    {
        if (!string.Equals(plan.StoreEpoch, _storeEpoch, StringComparison.Ordinal))
        {
            throw Mismatch("it was frozen in another store");
        }

        ConfirmNumber(plan.RowPlan);
        if (plan.BaseVersion != _activeVersion)
        {
            throw Moved(FormattableString.Invariant(
                $"The text plan stands on version {plan.BaseVersion} and the store stands at {_activeVersion}."));
        }

        if (_draft is not ContentDraft open
            || open.FrozenForBaseVersion != _activeVersion
            || !ContentTextCompatibility.SameDraft(plan.Snapshot.Draft, open))
        {
            throw Mismatch("the open draft is not the complete draft the plan froze");
        }

        if (!SameText(TextSnapshotAt(_activeVersion), plan.Snapshot.BaselineText))
        {
            throw Mismatch("the base version's text or languages are not the ones the plan read");
        }

        foreach (ContentTextRevision insert in plan.TextInserts)
        {
            _ = RequireTextTarget(new ContentTextTarget(
                insert.Type, KeyOf(plan.RowPlan, insert), insert.FieldName, insert.Language));
        }
    }

    /// <summary>The text a confirmed plan commits: the revision list after its closes and inserts.</summary>
    StagedText StageText(ContentTextPublishPlan plan)
    {
        var revisions = new List<ContentTextRevision>(_textRevisions);
        foreach (ContentTextRevision close in plan.TextCloses)
        {
            int index = revisions.IndexOf(close);
            if (index < 0)
            {
                throw Mismatch("a text close names a revision this store does not hold live");
            }

            revisions[index] = close.ClosedIn(plan.VersionNumber);
        }

        revisions.AddRange(plan.TextInserts);
        var languages = new ContentTextLanguage[plan.Languages.Count];
        for (int i = 0; i < languages.Length; i++)
        {
            languages[i] = plan.Languages[i];
        }

        return new StagedText(revisions, languages, plan.FrozenText, plan.TextCloses, plan.TextInserts);
    }

    /// <summary>
    /// The text a ROW-ONLY commit carries: the base version's languages unchanged, after refusing a draft or
    /// a frozen fork that needs text, and a plan whose manifests name other text chunks than the base
    /// recorded. The caller already holds the gate.
    /// </summary>
    StagedText StageRowOnlyText(ContentPublishPlan plan, string member)
    {
        RequireRowOnlyRepresentable(_draft, member);
        RequireNoTextFork(plan.FrozenEdits, member);

        ContentTextLanguage[] languages = [.. TextSnapshotAt(_activeVersion).Languages];
        bool agrees = plan.Languages.Count == languages.Length;
        for (int i = 0; agrees && i < languages.Length; i++)
        {
            agrees = string.Equals(plan.Languages[i].Tag, languages[i].WireTag, StringComparison.Ordinal)
                && string.Equals(plan.Languages[i].TextHash, languages[i].Hash, StringComparison.Ordinal);
        }

        if (!agrees)
        {
            throw ContentTextCompatibility.Unrepresented(member, FormattableString.Invariant(
                $"the plan names {plan.Languages.Count} text chunk(s) that are not the {languages.Length} version {_activeVersion} recorded"));
        }

        return new StagedText(null, languages, null, [], []);
    }

    /// <summary>
    /// The text half of the publish audit: one entry per string that changed, carrying its language, its old
    /// and new values abbreviated for the column, and the version. Full values stay in text history.
    /// </summary>
    void StageTextPublishAudit(
        List<ContentAuditEntry> staged,
        StagedText text,
        ContentPublishPlan plan,
        ContentPublishRequest request)
    {
        var inserted = new List<ContentTextRevision>(text.Inserts);
        foreach (ContentTextRevision close in text.Closes)
        {
            int index = inserted.FindIndex(insert => insert.IsSameString(close));
            ContentTextRevision? replacement = index < 0 ? null : inserted[index];
            if (index >= 0)
            {
                inserted.RemoveAt(index);
            }

            StageTextChange(staged, plan, request, close, close.Value, replacement?.Value);
        }

        foreach (ContentTextRevision insert in inserted)
        {
            StageTextChange(staged, plan, request, insert, null, insert.Value);
        }
    }

    void StageTextChange(
        List<ContentAuditEntry> staged,
        ContentPublishPlan plan,
        ContentPublishRequest request,
        ContentTextRevision revision,
        string? before,
        string? after)
        => _audit.Stage(
            staged,
            ContentAuditActions.Publish,
            request.Actor,
            request.Operator,
            revision.Type,
            revision.DefinitionId,
            KeyOf(plan, revision),
            revision.FieldName,
            ContentTextAuditRendering.Render(before),
            ContentTextAuditRendering.Render(after),
            plan.VersionNumber,
            request.Note,
            revision.Language);

    /// <summary>The key of the row a revision belongs to, as the plan's live rows name it.</summary>
    static ContentKey KeyOf(ContentPublishPlan plan, ContentTextRevision revision)
    {
        foreach (ContentRowRevision live in plan.LiveRows)
        {
            if (live.Row.Type == revision.Type && live.Row.Id == revision.DefinitionId)
            {
                return live.Row.Key;
            }
        }

        throw Mismatch(FormattableString.Invariant(
            $"text names type {revision.Type.Value} row {revision.DefinitionId}, which the plan holds no live row of"));
    }

    static bool SameText(ContentVersionTextSnapshot actual, ContentVersionTextSnapshot expected)
    {
        if (actual.VersionNumber != expected.VersionNumber
            || actual.Languages.Count != expected.Languages.Count
            || actual.Revisions.Count != expected.Revisions.Count)
        {
            return false;
        }

        for (int i = 0; i < actual.Languages.Count; i++)
        {
            if (!actual.Languages[i].Equals(expected.Languages[i]))
            {
                return false;
            }
        }

        var held = new List<ContentTextRevision>(actual.Revisions);
        foreach (ContentTextRevision revision in expected.Revisions)
        {
            if (!held.Remove(revision))
            {
                return false;
            }
        }

        return true;
    }

    static ContentAuthoringException Mismatch(string detail)
        => new(
            FormattableString.Invariant(
                $"The text commit is refused because {detail}. Nothing was written, and the plan is rebuilt from a fresh freeze."),
            default,
            0,
            ContentAuthoringException.TextStateMismatchReason);

    /// <summary>
    /// What one commit stages for text before the shared core runs.
    /// </summary>
    /// <param name="Revisions">The complete revision list after the commit, or null to leave text untouched.</param>
    /// <param name="Languages">The complete language record of the new version.</param>
    /// <param name="PublishedText">The frozen text state the commit consumes from the draft, or null on a row-only commit.</param>
    /// <param name="Closes">The revisions closed, for the audit.</param>
    /// <param name="Inserts">The revisions inserted, for the audit.</param>
    sealed record StagedText(
        List<ContentTextRevision>? Revisions,
        ContentTextLanguage[] Languages,
        ContentDraftTextState? PublishedText,
        IReadOnlyList<ContentTextRevision> Closes,
        IReadOnlyList<ContentTextRevision> Inserts);
}
