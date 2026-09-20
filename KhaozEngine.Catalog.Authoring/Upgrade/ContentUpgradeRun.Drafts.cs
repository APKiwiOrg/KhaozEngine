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
/// <b>A proven own draft is PUBLISHED, never discarded, and no marker is cleared ON SIGHT.</b> A marker
/// found standing here is either a live publish or a dead one and nothing on the seam tells them apart, so
/// clearing one where it is found would let a later edit land in a draft a live publisher's commit then
/// deletes. The store's own rule is that a publish is how a dead freeze is recovered, because
/// <see cref="IContentAuthoringStore.FreezeDraftAsync"/> overwrites the marker. So the publish path freezes
/// the draft for ITSELF and proves it again under that marker before publishing it, and it releases what
/// stands there on every attempt that froze and did not publish. What it releases is not always what it set:
/// the freeze overwrites and carries no identity, so a publisher that froze the same draft in the gap
/// between this run's read and its own freeze has already lost its marker to this one. A rival RUNNER loses
/// nothing by that, because its own re-proof under its own freeze refuses a contaminated draft exactly as
/// this one does, and an admin console publish carries no plan proof at all, which is the third residual
/// window the README names. Carried ids make two runners' plans for one definition identical and the
/// commit's version confirmation lets exactly one of them win.
/// </para>
/// </summary>
sealed partial class ContentUpgradeRun
{
    /// <summary>
    /// The definition an open draft was PROVEN to belong to at step 5, which the apply loop publishes before
    /// anything else. It is null on the ordinary run that found no draft.
    /// </summary>
    ContentUpgradeDefinition? _recovered;

    /// <summary>
    /// The pending definitions in the order this run will work them: the definition a recovered draft
    /// belongs to FIRST, then the rest ascending by order.
    /// <para>
    /// A recovered draft is only publishable by the definition that opened it, and the apply loop holds one
    /// draft at a time, so a run that started at a lower-ordered definition instead would find the standing
    /// draft, read it as a rival publisher's, and spend its whole stand-off budget against a catalog that
    /// cannot move until it gives up blaming another runner. The order is informational (the id is the
    /// identity), so moving one definition to the front costs a diagnostic and nothing else.
    /// </para>
    /// </summary>
    /// <param name="pending">The pending definitions, ascending by order.</param>
    internal IReadOnlyList<ContentUpgradeDefinition> RecoveredFirst(
        IReadOnlyList<ContentUpgradeDefinition> pending)
    {
        if (_recovered is not ContentUpgradeDefinition recovered
            || ReferenceEquals(pending[0], recovered))
        {
            return pending;
        }

        var ordered = new List<ContentUpgradeDefinition>(pending.Count) { recovered };
        var below = new List<string>();
        for (int i = 0; i < pending.Count; i++)
        {
            if (ReferenceEquals(pending[i], recovered))
            {
                continue;
            }

            ordered.Add(pending[i]);
            if (pending[i].Order < recovered.Order)
            {
                below.Add(pending[i].Id);
            }
        }

        Note(ContentUpgradeOrderNotes.RecoveredFirst(recovered, below));
        return ordered;
    }

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

        // Held so the apply loop publishes THIS definition first. Nothing else can publish the draft that
        // stands, and every other pending definition would read it as a rival publisher's.
        _recovered = named;
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
    /// <para>
    /// The plan match is not the only proof it accepts. A write that merged with a rival's leaves a draft
    /// that is nobody's plan, and leaving that behind is what wedged two runners against each other, so a
    /// draft <see cref="ReadDraftAsync"/> can account for edit by edit is cleared too.
    /// </para>
    /// </summary>
    /// <param name="draft">The standing draft, as the caller's own read just saw it.</param>
    /// <param name="note">The note this definition's draft carries.</param>
    /// <param name="planned">The edits this definition's plan produced.</param>
    async Task<bool> DiscardOwnDraftAsync(
        ContentDraft draft,
        string note,
        IReadOnlyList<ContentEdit> planned)
        => await DisposeProvenDraftAsync(draft, note, planned).ConfigureAwait(false)
            == ContentUpgradeDraftDisposal.RivalHoldsIt;

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
        OperationId = definition.Id;
        Operation = "export the baseline bundle";
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

        if (plan is null || plan.Kind != ContentUpgradePlanKind.Changes)
        {
            return null;
        }

        _known.Remember(plan.Edits);
        return plan.Edits;
    }

    /// <summary>
    /// Whether a draft is SOME upgrade run's rather than an operator's: the runner actor and a note naming
    /// one of the upgrades still pending. Two replicas of one deploy carry the same actor, so this is the
    /// most that can be said about a draft that is not provably this run's own.
    /// </summary>
    /// <param name="draft">The standing draft.</param>
    bool IsRunnersDraft(ContentDraft draft) => NamedPending(draft, _pending) is not null;

    /// <summary>
    /// Stops the run on an operator's draft that appeared after the gate. The definition is recorded as not
    /// run, because nothing of it was applied.
    /// </summary>
    /// <param name="definition">The definition that was being applied.</param>
    /// <param name="draft">The draft that appeared.</param>
    bool StopForOperatorDraft(ContentUpgradeDefinition definition, ContentDraft draft)
    {
        _steps.Add(ContentUpgradeStepResult.Pending(definition));
        Add(
            ContentUpgradeCodes.OperatorDraftOpen,
            FormattableString.Invariant(
                $"an open draft of {draft.EditCount} edit(s) on version {draft.BaseVersion} opened by '{draft.OpenedBy}' appeared while upgrade '{definition.Id}' was being applied, and this run cannot prove it belongs to an upgrade run. Publish or discard it, then run the remaining upgrade(s). The draft was left untouched."));
        _stopped = ContentUpgradeOutcome.OperatorDraftOpen;
        return true;
    }

    /// <summary>The whole proof for one definition: the actor, the note and the edits.</summary>
    bool IsOwn(ContentDraft draft, string note, IReadOnlyList<ContentEdit> planned)
        => string.Equals(draft.OpenedBy, Options.Actor, StringComparison.Ordinal)
            && string.Equals(draft.Note, note, StringComparison.Ordinal)
            && ContentUpgradeDraftMatch.IsPlan(draft, planned);
}
