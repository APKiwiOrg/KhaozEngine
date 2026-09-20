using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The draft half of a run: how an interrupted run's own draft is PROVEN to be its own, and what is done with
/// it.
/// <para>
/// <b>The proof is the actor, the note AND the edits.</b> A draft carries the actor that opened it
/// (<see cref="ContentDraft.OpenedBy"/>) and the note of the last write into it
/// (<see cref="ContentDraft.Note"/>), and neither is enough on its own. Both stores KEEP the standing note
/// when a writer passes an empty one, the shipped admin edit action takes an optional note, and no store
/// rewrites the identity that opened a draft, so an operator's edit lands in an interrupted run's draft
/// under the runner's actor and the runner's note. The third half closes it: the draft's expanded edits have
/// to be EXACTLY what a fresh plan of that definition produces against the current active baseline, which
/// <see cref="ContentUpgradeDraftMatch"/> decides.
/// </para>
/// <para>
/// <b>Anything else is operator work</b> and is left exactly as it stands, because everything the runner
/// would do to a draft it believed was its own is irreversible.
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

        ContentUpgradeDefinition? named = NamedPending(draft, pending);
        if (named is null)
        {
            Add(
                ContentUpgradeCodes.OperatorDraftOpen,
                FormattableString.Invariant(
                    $"this catalog has an open draft of {draft.EditCount} edit(s) on version {draft.BaseVersion} opened by '{draft.OpenedBy}', which this run cannot prove is its own. Publish or discard it, then run the {pending.Count} pending upgrade(s). The draft was left untouched."));
            return ContentUpgradeOutcome.OperatorDraftOpen;
        }

        if (!await HoldsPlanOfAsync(draft, named).ConfigureAwait(false))
        {
            Add(
                ContentUpgradeCodes.OperatorDraftOpen,
                FormattableString.Invariant(
                    $"this catalog has an open draft of {draft.EditCount} edit(s) on version {draft.BaseVersion} that an upgrade run opened for '{named.Id}' and that has since been changed, so it is no longer that run's to publish or discard. Review it, then publish or discard it and run the {pending.Count} pending upgrade(s). The draft was left untouched."));
            return ContentUpgradeOutcome.OperatorDraftOpen;
        }

        if (Options.Mode == ContentUpgradeMode.Preview)
        {
            Add(
                ContentUpgradeCodes.DraftRecovered,
                FormattableString.Invariant(
                    $"an interrupted run of upgrade '{named.Id}' left a draft of {draft.EditCount} edit(s) on version {draft.BaseVersion} that still holds exactly this build's plan. An apply discards it and replans. Nothing was written."));
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
                $"an interrupted run of upgrade '{named.Id}' left a {(draft.IsFrozen ? "frozen " : string.Empty)}draft of {draft.EditCount} edit(s) on version {draft.BaseVersion}, which this run discarded before replanning."));
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
    /// definition's plan and skips a FROZEN draft, for one reason each: a draft that is not this plan belongs
    /// to another run or to an operator, and a frozen draft belongs to a publish in flight, since this run's
    /// own publish releases its freeze on every exit path.
    /// </summary>
    /// <param name="note">The note this definition's draft carries.</param>
    /// <param name="planned">The edits this definition's plan produced.</param>
    async Task DiscardOwnDraftAsync(string note, IReadOnlyList<ContentEdit> planned)
    {
        ContentDraft? draft = await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false);
        if (draft is null || draft.IsFrozen || !IsOwn(draft, note, planned))
        {
            return;
        }

        await Store.DiscardDraftAsync(Options.Actor, Options.Operator, CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The PENDING definition a draft's actor and note name, or null when the draft names none of them. It
    /// is the cheap half of the proof and never the whole of it.
    /// </summary>
    ContentUpgradeDefinition? NamedPending(
        ContentDraft draft,
        IReadOnlyList<ContentUpgradeDefinition> pending)
    {
        if (!string.Equals(draft.OpenedBy, Options.Actor, StringComparison.Ordinal))
        {
            return null;
        }

        for (int i = 0; i < pending.Count; i++)
        {
            if (string.Equals(
                draft.Note, ContentUpgradeRunner.NoteFor(pending[i].Id), StringComparison.Ordinal))
            {
                return pending[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the draft holds EXACTLY what one definition plans right now. A planner that refuses or that
    /// finds the catalog already satisfied answers no, because neither produces a change set the draft could
    /// be reproducing.
    /// </summary>
    async Task<bool> HoldsPlanOfAsync(ContentDraft draft, ContentUpgradeDefinition definition)
    {
        IReadOnlyList<ContentEdit>? planned = await ReplanAsync(definition).ConfigureAwait(false);
        return planned is not null && ContentUpgradeDraftMatch.IsPlan(draft, planned);
    }

    /// <summary>
    /// One definition planned again against the CURRENT active version, for comparison only: no step is
    /// recorded and no refusal stops the run, because this is a question about a draft rather than an
    /// attempt at the upgrade.
    /// </summary>
    async Task<IReadOnlyList<ContentEdit>?> ReplanAsync(ContentUpgradeDefinition definition)
    {
        ContentBundle baseline = await Store.ExportBundleAsync(Active, CancellationToken).ConfigureAwait(false);
        var context = new ContentUpgradeContext(Active, baseline, Registry);

        ContentUpgradePlan? plan;
        try
        {
            plan = definition.Plan(context);
        }
        catch (Exception refused) when (refused is InvalidDataException
            or ContentAuthoringException
            or ArgumentException)
        {
            return null;
        }

        return plan is not null && plan.Kind == ContentUpgradePlanKind.Changes ? plan.Edits : null;
    }

    /// <summary>The whole proof for one definition: the actor, the note and the edits.</summary>
    bool IsOwn(ContentDraft draft, string note, IReadOnlyList<ContentEdit> planned)
        => string.Equals(draft.OpenedBy, Options.Actor, StringComparison.Ordinal)
            && string.Equals(draft.Note, note, StringComparison.Ordinal)
            && ContentUpgradeDraftMatch.IsPlan(draft, planned);
}
