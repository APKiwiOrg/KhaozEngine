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
/// <para>
/// <b>A proven own draft is PUBLISHED, never cleared and never discarded.</b> A freeze marker naming the
/// active version is either a live publish or a dead one and nothing on the seam tells them apart, so
/// clearing one would let a later edit land in a draft a live publisher's commit then deletes. The store's
/// own rule is that a publish is how a dead freeze is recovered, because
/// <see cref="IContentAuthoringStore.FreezeDraftAsync"/> overwrites the marker, so the runner publishes the
/// draft as it stands. Carried ids make two runners' plans for one definition identical and the commit's
/// version confirmation lets exactly one of them win.
/// </para>
/// </summary>
sealed partial class ContentUpgradeRun
{
    /// <summary>
    /// Step 5. Answers the outcome the run stops with, or null to carry on. It WRITES NOTHING: a draft this
    /// run can prove is its own is left standing for the apply loop to publish, and anything else is an
    /// operator's to resolve.
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

        string frozen = draft.IsFrozen ? "frozen " : string.Empty;
        Add(
            ContentUpgradeCodes.DraftRecovered,
            Options.Mode == ContentUpgradeMode.Preview
                ? FormattableString.Invariant(
                    $"an interrupted run of upgrade '{named.Id}' left a {frozen}draft of {draft.EditCount} edit(s) on version {draft.BaseVersion} that still holds exactly this build's plan. An apply publishes that draft as it stands. Nothing was written.")
                : FormattableString.Invariant(
                    $"an interrupted run of upgrade '{named.Id}' left a {frozen}draft of {draft.EditCount} edit(s) on version {draft.BaseVersion} that still holds exactly this build's plan, so this run publishes that draft as it stands."));
        return null;
    }

    /// <summary>
    /// Discards the draft this run itself opened for one definition, after ITS OWN publish failed. It is
    /// scoped to this definition's plan, and it answers whether a RIVAL publisher is live on that draft,
    /// which is the one thing a failure path cannot resolve by itself.
    /// <para>
    /// A frozen draft and a <c>publish-in-progress</c> refusal say the same thing: this run's own publish
    /// releases its freeze on every exit path, so a marker standing here belongs to someone else. The draft
    /// is left exactly as it is and the caller re-reads the ledger and stands off.
    /// </para>
    /// </summary>
    /// <param name="note">The note this definition's draft carries.</param>
    /// <param name="planned">The edits this definition's plan produced.</param>
    async Task<bool> DiscardOwnDraftAsync(string note, IReadOnlyList<ContentEdit> planned)
    {
        ContentDraft? draft = await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false);
        if (draft is null || !IsOwn(draft, note, planned))
        {
            return false;
        }

        if (draft.IsFrozen)
        {
            return true;
        }

        try
        {
            await Store.DiscardDraftAsync(Options.Actor, Options.Operator, CancellationToken)
                .ConfigureAwait(false);
        }
        catch (ContentAuthoringException refused)
            when (refused.Reason == ContentAuthoringException.PublishInProgressReason)
        {
            return true;
        }

        return false;
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
