using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What a run can say about the CONTENT of an open draft its own actor opened. The actor proves nothing on
/// its own, because both stores keep the standing note when a writer passes an empty one and no store
/// rewrites the identity that opened a draft, so only the edits decide.
/// </summary>
enum ContentUpgradeDraftReading
{
    /// <summary>Every edit came from a plan this run computed, and it is no longer a plan anyone can publish.</summary>
    SpentWork,

    /// <summary>
    /// EXACTLY the plan of a definition still pending, which is what a rival holds between its own write and
    /// its own publish. Carried ids make two runners' plans for one definition identical, so the shape is
    /// recognisable without knowing who wrote it.
    /// </summary>
    LivePlan,

    /// <summary>
    /// Some of this run's planned work AND content no plan of this run produced. Nobody will ever publish
    /// this draft and waiting it out cannot end, so it is an operator's to resolve.
    /// </summary>
    Merged,

    /// <summary>
    /// NOTHING in it came from a plan this run computed, so it is another writer's work whole. A rival
    /// running a different build is this, and so is an operator who opened one under the runner's identity.
    /// </summary>
    Foreign,
}

/// <summary>
/// What became of a draft a run offered to clear. The four answers are what every caller needs, and they are
/// apart from each other because a draft nobody could prove and a draft a live publisher holds ask for
/// opposite things: one is an operator's to resolve and the other is waited out.
/// </summary>
enum ContentUpgradeDraftDisposal
{
    /// <summary>Nothing in it is this run's. It was left untouched, and whose it is the caller's to judge.</summary>
    NotThisRuns,

    /// <summary>
    /// This run's own planned work mixed with content it cannot account for. It was left untouched and it is
    /// an operator's to resolve.
    /// </summary>
    OperatorWorkMerged,

    /// <summary>A LIVE publisher holds it, or it is one. It was left untouched and is waited out.</summary>
    RivalHoldsIt,

    /// <summary>The draft was proven and discarded, so the way is clear.</summary>
    Discarded,
}

/// <summary>
/// What a run does about a draft standing between it and its publish, when that draft is not one it may
/// publish as it stands.
/// <para>
/// <b>The window this exists for.</b> Reading the open draft and writing into it are two calls with no lock
/// across them, and the store's write APPENDS into whatever draft is open. So a second runner that planned
/// against the older baseline and reached its own write inside that window opens the one draft, and this
/// run's write then merges into it. The result carries two definitions' edits under one actor and one note:
/// it is nobody's plan, so nobody publishes it, and before this path existed nobody discarded it either.
/// Both runners then read it as the other's live work and waited until their patience ran out.
/// </para>
/// <para>
/// <b>The second proof.</b> Publishing still takes the exact match. Discarding gains a CONTENT proof: the
/// actor is this runner's, the draft is not frozen, it is not exactly the plan of a definition still pending
/// (that shape is a rival between its own write and its own publish, and it is live work), and every edit it
/// holds came from a plan this run computed. One edit outside that set means an operator's work may be in
/// there, and the draft is left exactly as it stands.
/// </para>
/// </summary>
sealed partial class ContentUpgradeRun
{
    /// <summary>Every change set this run computed, which is the only content it may prove a draft out of.</summary>
    readonly ContentUpgradeKnownPlans _known = new();

    /// <summary>
    /// The one resolution for a draft this run must NOT publish, whether it found it standing at the
    /// pre-flight or wrote into it and got something back that is not its plan. It answers whether the
    /// definition is SETTLED.
    /// </summary>
    /// <param name="definition">The definition being applied.</param>
    /// <param name="plan">Its plan against the current baseline.</param>
    /// <param name="note">The note this definition's draft carries.</param>
    /// <param name="standOff">This definition's patience.</param>
    /// <param name="what">What stands in the way, which a refusal quotes.</param>
    /// <param name="draft">The draft in the way, as the caller's own read or write just saw it.</param>
    async Task<bool> ResolveObstructionAsync(
        ContentUpgradeDefinition definition,
        ContentUpgradePlan plan,
        string note,
        ContentUpgradeStandOff standOff,
        string what,
        ContentDraft draft)
    {
        // The caller's view is the one that is proved. Reading again would not make the discard atomic, and
        // it would cost a round trip on every pre-flight that finds anything at all.
        ContentDraft? standing = draft;
        ContentUpgradeDraftDisposal disposal = await DisposeProvenDraftAsync(draft, note, plan.Edits)
            .ConfigureAwait(false);
        if (disposal == ContentUpgradeDraftDisposal.Discarded)
        {
            Add(
                ContentUpgradeCodes.DraftCleared,
                FormattableString.Invariant(
                    $"an open draft of {standing.EditCount} edit(s) on version {standing.BaseVersion} stood between this run and upgrade '{definition.Id}', and every edit in it came from a plan this run computed, so it was cleared and the upgrade was tried again. No operator work was in it."));
            standing = null;
        }

        Operation = "read the upgrade ledger";
        ContentUpgradeRecord? landed = await FindRecordAsync(definition.Id).ConfigureAwait(false);
        if (landed is not null)
        {
            return await AdoptRecordedAsync(definition, plan, landed).ConfigureAwait(false);
        }

        if (standing is not null)
        {
            // Only a RUNNER's draft is worth waiting out, and only while waiting could still end. A draft
            // holding this run's own work mixed with content nobody planned is one no publisher will ever
            // take, so waiting it out spends the whole attempt budget to report a state already known.
            return disposal != ContentUpgradeDraftDisposal.OperatorWorkMerged
                && (disposal == ContentUpgradeDraftDisposal.RivalHoldsIt || IsRunnersDraft(standing))
                ? await StandOffAsync(definition, standOff, what).ConfigureAwait(false)
                : StopForOperatorDraft(definition, standing);
        }

        // Nothing stands in the way now, so another attempt is owed. It costs budget AND the stand-off's own
        // wait: a window that keeps reopening is a livelock rather than progress, and two runners retrying
        // the instant each clears the other's draft spend the whole ceiling in one burst with no gap for
        // either to get a publish through. The backoff is what turns that burst into turns.
        return await StandOffAsync(definition, standOff, what).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards a draft this run can PROVE holds nothing but its own planned work, and says what became of
    /// it. A frozen draft is never touched: a marker standing here belongs to a live publisher, because this
    /// run's own publish releases its freeze on every exit path, and a
    /// <c>publish-in-progress</c> refusal says the same thing one moment later.
    /// <para>
    /// <b>This is check then act and it CANNOT be made atomic on this seam.</b>
    /// <see cref="IContentAuthoringStore.DiscardDraftAsync"/> is refused while a freeze stands, so the marker
    /// that closes the publish window is exactly the thing that cannot guard this one. The window is narrowed
    /// instead: the draft is read again and proved again over what THAT read returns, with nothing awaited
    /// between the read and the discard. An operator edit landing inside that last round trip is still lost,
    /// and nothing here can say otherwise. A hosted upgrade runs in a maintenance window with editing
    /// stopped, and a local automatic boot has no operator at all, which is the whole of why the residue is
    /// accepted.
    /// </para>
    /// </summary>
    /// <param name="draft">The standing draft.</param>
    /// <param name="note">The note this definition's draft carries.</param>
    /// <param name="planned">The edits this definition's plan produced.</param>
    async Task<ContentUpgradeDraftDisposal> DisposeProvenDraftAsync(
        ContentDraft draft,
        string note,
        IReadOnlyList<ContentEdit> planned)
    {
        if (!string.Equals(draft.OpenedBy, Options.Actor, StringComparison.Ordinal))
        {
            return ContentUpgradeDraftDisposal.NotThisRuns;
        }

        if (!IsOwn(draft, note, planned))
        {
            switch (await ReadDraftAsync(draft).ConfigureAwait(false))
            {
                case ContentUpgradeDraftReading.LivePlan:
                    return ContentUpgradeDraftDisposal.RivalHoldsIt;

                case ContentUpgradeDraftReading.Merged:
                    return ContentUpgradeDraftDisposal.OperatorWorkMerged;

                case ContentUpgradeDraftReading.Foreign:
                    return ContentUpgradeDraftDisposal.NotThisRuns;

                default:
                    break;
            }
        }

        if (draft.IsFrozen)
        {
            return ContentUpgradeDraftDisposal.RivalHoldsIt;
        }

        // The classification above costs a ledger read and a replan of every pending definition, so the view
        // it proved is as old as all of that. The LAST thing before the discard is a fresh read, and the
        // known-work proof again over exactly what that read returned. Nothing is awaited between it and the
        // discard, which is the narrowest this can be made.
        Operation = "re-read the draft it is about to discard";
        ContentDraft? latest = await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            // Somebody cleared it first. The way is clear either way, which is what the answer means here.
            return ContentUpgradeDraftDisposal.Discarded;
        }

        if (latest.IsFrozen)
        {
            return ContentUpgradeDraftDisposal.RivalHoldsIt;
        }

        if (!string.Equals(latest.OpenedBy, Options.Actor, StringComparison.Ordinal) || !_known.Holds(latest))
        {
            // It changed under the proof. Whoever appended, this run can no longer say it holds only work it
            // computed, so it leaves the draft exactly as it stands.
            return _known.HoldsSome(latest)
                ? ContentUpgradeDraftDisposal.OperatorWorkMerged
                : ContentUpgradeDraftDisposal.NotThisRuns;
        }

        Operation = "discard a draft this run proved holds only its own work";
        try
        {
            await Store.DiscardDraftAsync(Options.Actor, Options.Operator, CancellationToken)
                .ConfigureAwait(false);
        }
        catch (ContentAuthoringException refused)
            when (refused.Reason == ContentAuthoringException.PublishInProgressReason)
        {
            return ContentUpgradeDraftDisposal.RivalHoldsIt;
        }

        return ContentUpgradeDraftDisposal.Discarded;
    }

    /// <summary>
    /// The CONTENT reading of a draft this run's actor opened and that is not exactly this definition's
    /// plan. The known set is every plan this run computed during the run plus a fresh replan of every
    /// definition the ledger still does not hold, and a still-pending definition's plan is read whole
    /// BEFORE the subset question, because that shape is live work rather than spent work.
    /// <para>
    /// A draft holding exactly the plan of a definition the LEDGER already records is spent by definition,
    /// and clearing it is how a stale write gets out of the way of every later one.
    /// </para>
    /// </summary>
    /// <param name="draft">The standing draft.</param>
    async Task<ContentUpgradeDraftReading> ReadDraftAsync(ContentDraft draft)
    {
        string working = OperationId;

        // The replans below export at the version this run stands on, so a rival that ADVANCED it would be
        // replanned against a baseline nobody is on any more. Its live draft would then match no plan, hold
        // some of this run's own work, and read as Merged, which stops a boot with an operator draft for a
        // draft holding nothing an operator wrote.
        if (!await RereadActiveAsync().ConfigureAwait(false))
        {
            // The expected version the re-read carries says the baseline moved off the one this run was
            // given, so the run is already stopping. Nothing may be done to the draft, and a live publisher
            // is the one reading that leaves it exactly as it stands.
            OperationId = working;
            return ContentUpgradeDraftReading.LivePlan;
        }

        Operation = "read the upgrade ledger";
        IReadOnlyList<ContentUpgradeRecord> records = await Ledger
            .ListUpgradesAsync(CancellationToken)
            .ConfigureAwait(false);

        for (int i = 0; i < _pending.Count; i++)
        {
            ContentUpgradeDefinition definition = _pending[i];
            if (Recorded(records, definition.Id))
            {
                continue;
            }

            IReadOnlyList<ContentEdit>? fresh = await ReplanAsync(definition).ConfigureAwait(false);
            if (fresh is not null && ContentUpgradeDraftMatch.IsPlan(draft, fresh))
            {
                OperationId = working;
                return ContentUpgradeDraftReading.LivePlan;
            }
        }

        OperationId = working;
        if (_known.Holds(draft))
        {
            return ContentUpgradeDraftReading.SpentWork;
        }

        return _known.HoldsSome(draft)
            ? ContentUpgradeDraftReading.Merged
            : ContentUpgradeDraftReading.Foreign;
    }

    /// <summary>
    /// The ledger row wins over whatever this run was about to do. It never says WHO wrote the row, because
    /// nothing here can tell: this run's own commit landing before an interruption looks exactly like a
    /// rival's, and two runners of one deploy write the same actor.
    /// </summary>
    /// <param name="definition">The definition being applied.</param>
    /// <param name="plan">Its plan, which supplies the change lines the step reports.</param>
    /// <param name="landed">The row the ledger holds.</param>
    async Task<bool> AdoptRecordedAsync(
        ContentUpgradeDefinition definition,
        ContentUpgradePlan plan,
        ContentUpgradeRecord landed)
    {
        // The step says what the LEDGER says. A rival that found the catalog already satisfied wrote an
        // adopted row and published no version at all, so reporting it as published as version N would name
        // a version that says nothing about this upgrade.
        string token = ContentUpgradeDispositions.Token(landed.Disposition);
        _steps.Add(landed.Disposition == ContentUpgradeDisposition.Applied
            ? ContentUpgradeStepResult.Applied(definition, landed.VersionNumber, plan.ChangeLines)
            : ContentUpgradeStepResult.Adopted(
                definition,
                FormattableString.Invariant(
                    $"the ledger already records it as {token} at version {landed.VersionNumber}.")));
        Add(
            ContentUpgradeCodes.AppliedConcurrently,
            landed.Disposition == ContentUpgradeDisposition.Applied
                ? FormattableString.Invariant(
                    $"upgrade '{definition.Id}' was already published as version {landed.VersionNumber}, by this run before an interruption or by another runner, so this run adopted that result and continued.")
                : FormattableString.Invariant(
                    $"upgrade '{definition.Id}' is already held in the ledger as {token} at version {landed.VersionNumber}, recorded rather than published, by this run before an interruption or by another runner, so this run adopted that result and continued."));
        await RereadActiveAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Another attempt against the re-read baseline, or the stop a version nobody previewed forces. It
    /// answers whether the definition is SETTLED, so false is the ask for another attempt.
    /// </summary>
    /// <param name="definition">The definition being applied.</param>
    async Task<bool> RetryOrStopAsync(ContentUpgradeDefinition definition)
    {
        if (await RereadActiveAsync().ConfigureAwait(false))
        {
            return false;
        }

        // The version moved to one nobody previewed. The definition is left where it stands and the report
        // says so, because publishing onto it is the refusal the expected version exists for.
        _steps.Add(ContentUpgradeStepResult.Pending(definition));
        return true;
    }

    /// <summary>Whether the ledger holds one upgrade id.</summary>
    static bool Recorded(IReadOnlyList<ContentUpgradeRecord> records, string id)
    {
        for (int i = 0; i < records.Count; i++)
        {
            if (string.Equals(records[i].Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
