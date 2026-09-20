using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The draft half of a run: how an interrupted run's own draft is PROVEN to be its own, and what is done with
/// it.
/// <para>
/// <b>The proof is the two things the seam already persists.</b> A draft carries the actor that opened it
/// (<see cref="ContentDraft.OpenedBy"/>) and the note of the last write into it
/// (<see cref="ContentDraft.Note"/>), both durable and both readable without a new column. The runner's own
/// actor plus a note of <c>content upgrade &lt;id&gt;</c> naming a PENDING upgrade is a draft only this
/// runner could have left, and anything else is operator work that is left exactly as it stands.
/// </para>
/// <para>
/// <b>A frozen own draft is the harder half and it is still recoverable.</b> A run killed between the freeze
/// of step 1 and the commit of step 10 leaves a marker that refuses every later edit and every later discard,
/// so the freeze is cleared first and the draft discarded second. The marker is never left standing, because
/// a draft nothing can write to and nothing can discard is a wedged catalog.
/// </para>
/// </summary>
sealed partial class ContentUpgradeRun
{
    /// <summary>
    /// How many times a frozen draft is looked at again before it is declared a dead run's leftover. The
    /// looks are only paid when a frozen draft is actually there, which is a rare state.
    /// </summary>
    internal const int FrozenDraftLooks = 3;

    /// <summary>
    /// Step 5. Answers the outcome the run stops with, or null to carry on. A discard here is the one write a
    /// run makes before it plans anything, and only an APPLY makes it: a preview writes nothing, so it says
    /// what an apply would do instead.
    /// </summary>
    /// <param name="pending">The pending definitions, which is the set a note has to name one of.</param>
    internal async Task<ContentUpgradeOutcome?> ResolveOpenDraftAsync(
        IReadOnlyList<ContentUpgradeDefinition> pending)
    {
        ContentDraft? draft = await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return null;
        }

        if (!IsInterruptedRun(draft, pending, out string? upgradeId))
        {
            Add(
                ContentUpgradeCodes.OperatorDraftOpen,
                FormattableString.Invariant(
                    $"this catalog has an open draft of {draft.EditCount} edit(s) on version {draft.BaseVersion} opened by '{draft.OpenedBy}', which this run cannot prove is its own. Publish or discard it, then run the {pending.Count} pending upgrade(s). The draft was left untouched."));
            return ContentUpgradeOutcome.OperatorDraftOpen;
        }

        if (Options.Mode == ContentUpgradeMode.Preview)
        {
            Add(
                ContentUpgradeCodes.DraftRecovered,
                FormattableString.Invariant(
                    $"an interrupted run of upgrade '{upgradeId}' left a draft of {draft.EditCount} edit(s) on version {draft.BaseVersion}. An apply discards it and replans. Nothing was written."));
            return null;
        }

        // A FROZEN draft is either a dead run's leftover or a live publish in flight, and the marker carries
        // no owner to tell them apart. A dead one never moves, so the runner watches it: a draft that is gone
        // on a later look was live and was never this run's to touch.
        if (draft.IsFrozen && !await StillFrozenAsync(draft).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            if (draft.IsFrozen)
            {
                // A publish that died before its commit left this. Clearing the marker is exactly what the
                // next publish's own step 1 would do, and it has to come first because a frozen draft
                // refuses a discard.
                await Store.ClearDraftFreezeAsync(CancellationToken).ConfigureAwait(false);
            }

            await Store.DiscardDraftAsync(Options.Actor, Options.Operator, CancellationToken)
                .ConfigureAwait(false);
        }
        catch (ContentAuthoringException refused)
            when (refused.Reason == ContentAuthoringException.PublishInProgressReason)
        {
            // The freeze came BACK between the clear and the discard, which only a live publish can do. So
            // this is another runner's draft rather than a dead run's, and it is not this run's to take.
            // The per-definition retry waits that publish out.
            return null;
        }

        Add(
            ContentUpgradeCodes.DraftRecovered,
            FormattableString.Invariant(
                $"an interrupted run of upgrade '{upgradeId}' left a {(draft.IsFrozen ? "frozen " : string.Empty)}draft of {draft.EditCount} edit(s) on version {draft.BaseVersion}, which this run discarded before replanning."));
        return null;
    }

    /// <summary>
    /// Watches a frozen draft for as long as a publish could reasonably hold one, and answers whether it is
    /// STILL there, frozen, and unchanged. A draft that vanished or that moved belonged to a live publish,
    /// which commits it away or lets it go, and a dead run's draft never does either.
    /// </summary>
    /// <param name="draft">The frozen draft as it was first seen.</param>
    async Task<bool> StillFrozenAsync(ContentDraft draft)
    {
        for (int look = 1; look <= FrozenDraftLooks; look++)
        {
            await Task.Delay(BackoffFor(look), CancellationToken).ConfigureAwait(false);
            ContentDraft? again = await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false);
            if (again is null
                || !again.IsFrozen
                || again.OpenedAtUtc != draft.OpenedAtUtc
                || again.EditCount != draft.EditCount)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Discards the draft this run itself opened for one definition, on a failure path. It is scoped to THIS
    /// definition's note and skips a FROZEN draft, for one reason each: a note naming another definition
    /// belongs to another run that is further ahead, and a frozen draft belongs to a publish in flight, since
    /// this run's own publish releases its freeze on every exit path.
    /// </summary>
    /// <param name="note">The note this definition's draft carries.</param>
    async Task DiscardOwnDraftAsync(string note)
    {
        ContentDraft? draft = await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false);
        if (draft is null || draft.IsFrozen || !IsOwn(draft, note))
        {
            return;
        }

        await Store.DiscardDraftAsync(Options.Actor, Options.Operator, CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a draft is one of THIS runner's, left behind by a run that did not finish: the actor matches
    /// and the note names one of the upgrades still pending.
    /// </summary>
    bool IsInterruptedRun(
        ContentDraft draft,
        IReadOnlyList<ContentUpgradeDefinition> pending,
        out string? upgradeId)
    {
        upgradeId = null;
        if (!string.Equals(draft.OpenedBy, Options.Actor, StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = 0; i < pending.Count; i++)
        {
            if (IsOwn(draft, ContentUpgradeRunner.NoteFor(pending[i].Id)))
            {
                upgradeId = pending[i].Id;
                return true;
            }
        }

        return false;
    }

    bool IsOwn(ContentDraft draft, string note)
        => string.Equals(draft.OpenedBy, Options.Actor, StringComparison.Ordinal)
            && string.Equals(draft.Note, note, StringComparison.Ordinal);
}
