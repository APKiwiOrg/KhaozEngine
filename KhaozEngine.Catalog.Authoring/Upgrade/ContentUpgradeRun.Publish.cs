using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The publish half of a run: one definition's edits published as one version, and design step 9's failure
/// resolution, which READS the ledger rather than reasoning about where an exception was thrown.
/// <para>
/// <b>Contention with a second runner is waited out rather than reported.</b> Two replicas booting together
/// is an ordinary deployment, and a boot that lost a race is a real outage. The catalog offers no lock that
/// spans a whole publish (the freeze marker is durable and overwritable by design), so the runner stands off
/// while another publish holds the one draft and replans when it is free. The ledger's primary key is what
/// makes standing off safe: an upgrade that DID land cannot be published a second time.
/// </para>
/// </summary>
sealed partial class ContentUpgradeRun
{
    /// <summary>
    /// Whether the freeze standing over the draft is the one THIS attempt set for its own publish. It is
    /// what lets a failure path tell a marker this run owes a release from one that arrived from somewhere
    /// else, which the seam itself cannot, because the marker carries no identity.
    /// </summary>
    bool _frozenForPublish;

    /// <summary>
    /// The publish of one definition's edits, stamped with its id so the ledger row lands inside the commit.
    /// It answers whether the definition is SETTLED, and a false answer asks for another attempt.
    /// </summary>
    async Task<bool> PublishAsync(
        ContentUpgradeDefinition definition,
        ContentUpgradePlan plan,
        ContentUpgradeStandOff standOff)
    {
        string note = ContentUpgradeRunner.NoteFor(definition.Id);
        Operation = "read the open draft";

        // A draft standing here is either the one an interrupted run left holding exactly this plan ON this
        // baseline, which this run publishes as it stands, or it is not this run's to publish at all.
        // Applying into another runner's would put two publishes over one change set, and a second set of
        // edits in one draft is not this plan any more.
        ContentDraft? standing = await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false);
        bool standingIsOwn = standing is not null
            && standing.BaseVersion == Active
            && IsOwn(standing, note, plan.Edits);
        if (standing is not null && !standingIsOwn)
        {
            // Resolution comes BEFORE any judgement about whose draft it is: a draft this run can itself
            // prove holds only its own planned work is one it clears, and standing off against that is how
            // two runners used to wedge each other.
            return await ResolveObstructionAsync(
                definition,
                plan,
                note,
                standOff,
                FormattableString.Invariant(
                    $"another publisher holds the open draft of {standing.EditCount} edit(s) on version {standing.BaseVersion}."),
                standing)
                .ConfigureAwait(false);
        }

        if (!standingIsOwn)
        {
            // The window between here and the write is what the whole obstruction path exists for, so it is
            // narrowed first: an id a rival recorded while this run was planning costs nothing to notice now
            // and a stale write to undo later.
            Operation = "read the upgrade ledger";
            ContentUpgradeRecord? already = await FindRecordAsync(definition.Id).ConfigureAwait(false);
            if (already is not null)
            {
                return await AdoptRecordedAsync(definition, plan, already).ConfigureAwait(false);
            }
        }

        Operation = "read the baseline version row";
        ContentVersionRecord? baseline = await Store
            .GetVersionAsync(Active, CancellationToken)
            .ConfigureAwait(false);

        // The minimums RISE to this build's ordinals and never fall below the baseline's, so an older build
        // running an upgrade cannot lower the bar a client is admitted over.
        int minimumServerBuild = Math.Max(baseline?.MinimumServerBuild ?? 0, Options.ServerBuild);
        int minimumClientBuild = Math.Max(baseline?.MinimumClientBuild ?? 0, Options.ClientBuild);

        try
        {
            if (!standingIsOwn)
            {
                Operation = "write its edits into the draft";
                ContentDraft written = await Store
                    .ApplyEditsAsync(plan.Edits, Options.Actor, Options.Operator, note, CancellationToken)
                    .ConfigureAwait(false);

                // What came BACK decides, not what went in. The store appends into whatever draft is open,
                // so a draft that opened inside the window between the read above and this write carries
                // both change sets, and a draft that opened on a different baseline carries this plan
                // against content it was not computed from. Neither is this plan and neither is published.
                if (!ContentUpgradeDraftMatch.IsPlan(written, plan.Edits) || written.BaseVersion != Active)
                {
                    return await ResolveObstructionAsync(
                        definition,
                        plan,
                        note,
                        standOff,
                        FormattableString.Invariant(
                            $"the open draft this run wrote into holds {written.EditCount} edit(s) on version {written.BaseVersion}, which is not this upgrade's change set on version {Active}."),
                        written)
                        .ConfigureAwait(false);
                }
            }

            // The publish window CLOSED. Everything above read the draft and then stopped looking, and the
            // store's publish does not freeze until its own first step, so an operator's edit arriving in
            // between was published without review. The freeze is taken here instead and the draft is proved
            // again UNDER it, which is the only order in which what is proved is what is published.
            //
            // THE RULE for the frozen region below: the marker this attempt set is released on EVERY exit
            // from it except the one that handed the draft to the store's own publish and got a version back.
            // The finally is what makes that true of a cancelled token and of a fault this package does not
            // answer with a report, neither of which reaches the handler underneath. A marker left standing
            // names the ACTIVE version, so the stale-marker sweep a publish starts with will never clear it,
            // and the draft it stands over can be neither edited nor discarded by anyone.
            try
            {
                ContentDraft frozen = await FreezeForPublishAsync().ConfigureAwait(false);
                if (!ContentUpgradeDraftMatch.IsPlan(frozen, plan.Edits) || frozen.BaseVersion != Active)
                {
                    // Released HERE rather than left to the finally, because the obstruction path DISCARDS
                    // and the store refuses a discard while any marker stands. The release is idempotent, so
                    // the finally then does nothing.
                    //
                    // The marker cleared may not be the one this attempt set: the seam's freeze OVERWRITES
                    // and carries no identity, so a console publish that froze this same draft in the gap
                    // between the read above and this attempt's own freeze has already lost its marker to
                    // this one, and releasing takes it away. That is the third residual window, and it is
                    // narrow enough to accept next to the other two: a rival RUNNER's own re-proof under its
                    // own freeze refuses this contaminated draft exactly as this one just did, and only the
                    // admin console publishes a draft with no plan proof at all. Leaving the marker instead
                    // wedges the draft for certain, every time, which is worse.
                    await ReleaseFreezeAsync().ConfigureAwait(false);
                    return await ResolveObstructionAsync(
                        definition,
                        plan,
                        note,
                        standOff,
                        FormattableString.Invariant(
                            $"the frozen draft holds {frozen.EditCount} edit(s) on version {frozen.BaseVersion}, which is not this upgrade's change set on version {Active}."),
                        frozen)
                        .ConfigureAwait(false);
                }

                Operation = "publish its edits as a new version";
                ContentPublishResult published = await Store.PublishAsync(
                    new ContentPublishRequest(
                        Options.Actor,
                        Options.Operator,
                        note,
                        Active,
                        minimumServerBuild,
                        minimumClientBuild)
                    {
                        Upgrade = definition.Stamp,
                    },
                    CancellationToken).ConfigureAwait(false);

                // The ONE exit that owes nothing. The publish took the marker over: it overwrites it with its
                // own at its first step and releases it on every exit path of its own, and the draft it just
                // committed is gone, so a release here would be a no-op anyway.
                _frozenForPublish = false;
                RecordPublished(published.VersionNumber);
                _steps.Add(
                    ContentUpgradeStepResult.Applied(definition, published.VersionNumber, plan.ChangeLines));
                return true;
            }
            finally
            {
                // Every other exit, including a publish that entered the store's pipeline and failed inside
                // it. That one released its own marker on its own way out, so this is a no-op except in the
                // gap another publisher's freeze can land in, which is the same third window named above.
                // A failure before the freeze released nothing at all: the flag says which.
                await ReleaseFreezeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (ContentUpgradeFault.IsHandled(failure))
        {
            // The finally above already ran: an outer filter is evaluated before the inner finally, and the
            // handler body after it, so everything here meets a draft with no marker of this run's on it.
            return await ResolveFailureAsync(definition, plan, note, failure, standOff).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Steps (a) and (b) of the publish window: the draft FROZEN for the version this plan was computed
    /// against, then read back under that marker. While the marker stands the store refuses
    /// <see cref="IContentAuthoringStore.ApplyEditsAsync"/> and
    /// <see cref="IContentAuthoringStore.DiscardDraftAsync"/>, so what comes back here is what the publish
    /// will take. The store's own publish overwrites the marker with its own, so freezing first costs the
    /// publish nothing.
    /// <para>
    /// A draft gone after a freeze that succeeded is a rival that discarded it in the one moment it could,
    /// which is contention. It is raised as such and resolved through the ledger like any other.
    /// </para>
    /// </summary>
    async Task<ContentDraft> FreezeForPublishAsync()
    {
        Operation = "freeze the draft for its own publish";

        // Set BEFORE the call and not after. A freeze that commits its marker and then reports a failure, an
        // acknowledgement lost on a dropped connection, leaves a DURABLE marker behind a call that said it
        // failed, and a flag set afterwards would leave nobody owing it a release. Setting it early costs one
        // release nothing needed on the freeze that really did fail, and that release is a no-op.
        _frozenForPublish = true;
        await Store.FreezeDraftAsync(Active, CancellationToken).ConfigureAwait(false);

        Operation = "read the frozen draft back";
        return await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false)
            ?? throw new ContentAuthoringException(
                "the draft this run froze for its own publish is no longer open.",
                default,
                0,
                ContentAuthoringException.NoOpenDraftReason);
    }

    /// <summary>
    /// The freeze THIS attempt set, released, and nothing otherwise: a marker no attempt of this run set
    /// belongs to a publisher that may be live, and the whole obstruction path reads one standing over a
    /// draft as exactly that. The flag is what says which, because the marker itself carries no identity.
    /// <para>
    /// <b>It is idempotent and it is reached from a finally</b>, so the cleanup is not a promise about where
    /// the attempt threw. The release itself runs on <see cref="CancellationToken.None"/>: the token this run
    /// was given is exactly what is likely to be cancelled on the path that needs the release most, and a
    /// token that went away must not leave a marker standing over a draft nobody is publishing.
    /// </para>
    /// </summary>
    async Task ReleaseFreezeAsync()
    {
        if (!_frozenForPublish)
        {
            return;
        }

        _frozenForPublish = false;
        Operation = "release the freeze it set";
        await Store.ClearDraftFreezeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Step 9. The run's own draft is discarded first, so no failure path leaves one behind, and then the
    /// ledger is READ rather than reasoned about. A row means the upgrade landed, here or in a concurrent
    /// runner, and either way it is never published twice. An exception after the commit point and a refusal
    /// before it look identical from here, and only the ledger tells them apart.
    /// <para>
    /// <b>A refusal over a draft that is not this plan says nothing about this plan.</b> A rival's write that
    /// merged into the one draft inside the publish window is what makes a valid change set publish as an
    /// invalid one, so the draft is resolved first and the upgrade is tried again rather than blamed.
    /// </para>
    /// </summary>
    async Task<bool> ResolveFailureAsync(
        ContentUpgradeDefinition definition,
        ContentUpgradePlan plan,
        string note,
        Exception failure,
        ContentUpgradeStandOff standOff)
    {
        Operation = "read the open draft";
        ContentDraft? over = await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false);
        if (over is not null && !IsOwn(over, note, plan.Edits))
        {
            return await ResolveObstructionAsync(definition, plan, note, standOff, failure.Message, over)
                .ConfigureAwait(false);
        }

        Operation = "discard its own draft";
        bool rivalIsLive = over is not null
            && await DiscardOwnDraftAsync(over, note, plan.Edits).ConfigureAwait(false);

        Operation = "read the upgrade ledger";
        ContentUpgradeRecord? landed = await FindRecordAsync(definition.Id).ConfigureAwait(false);
        if (landed is not null)
        {
            // An exception after the commit point and a refusal before it look identical from here, and only
            // the ledger tells them apart.
            return await AdoptRecordedAsync(definition, plan, landed).ConfigureAwait(false);
        }

        // A live rival on this run's own draft is contention whatever the exception said, because the draft
        // it would have to republish into is not free yet.
        return rivalIsLive || ContentUpgradeFault.IsContention(failure)
            ? await StandOffAsync(definition, standOff, failure.Message).ConfigureAwait(false)
            : Fail(definition, failure.Message);
    }

    /// <summary>
    /// Waits out another publisher and asks for another attempt, or gives up when the patience is spent. The
    /// patience is spent on a catalog that is NOT MOVING: every wait records the ledger, the active version
    /// and the open draft, and an attempt budget that ran out while all three stood still is the only way to
    /// give up. The wait itself grows with the attempt, because a publish holds the draft for its whole pack
    /// write.
    /// </summary>
    /// <param name="definition">The definition being applied.</param>
    /// <param name="standOff">This definition's patience.</param>
    /// <param name="what">What the rival did, which a refusal quotes.</param>
    async Task<bool> StandOffAsync(
        ContentUpgradeDefinition definition,
        ContentUpgradeStandOff standOff,
        string what)
    {
        if (!standOff.Observe(await ProgressAsync().ConfigureAwait(false)))
        {
            return Fail(definition, what);
        }

        await Task.Delay(standOff.Delay, CancellationToken).ConfigureAwait(false);
        return await RetryOrStopAsync(definition).ConfigureAwait(false);
    }

    /// <summary>
    /// The catalog's three moving parts as ONE comparable value: the ledger, the active version and the open
    /// draft. It is a string rather than a record because the only question asked of it is whether it is the
    /// same as last time, and a string makes that one ordinal comparison over everything at once.
    /// </summary>
    async Task<string> ProgressAsync()
    {
        Operation = "read what the catalog is doing";
        IReadOnlyList<ContentUpgradeRecord> records = await Ledger
            .ListUpgradesAsync(CancellationToken)
            .ConfigureAwait(false);
        int active = await Store.GetActiveVersionAsync(CancellationToken).ConfigureAwait(false);
        ContentDraft? draft = await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false);

        var progress = new StringBuilder();
        progress.Append(CultureInfo.InvariantCulture, $"v{active}");
        for (int i = 0; i < records.Count; i++)
        {
            ContentUpgradeRecord record = records[i];
            progress.Append(CultureInfo.InvariantCulture, $"|{record.Id}@{record.VersionNumber}");
            progress.Append(ContentUpgradeDispositions.Token(record.Disposition));
        }

        progress.Append(draft is null
            ? "|no-draft"
            : FormattableString.Invariant(
                $"|{draft.OpenedBy}@{draft.OpenedAtUtc.UtcTicks}:{draft.EditCount}:{draft.FrozenForBaseVersion}:{draft.Note}"));
        return progress.ToString();
    }

    /// <summary>The refusal step and its diagnostic for a publish the ledger says did not land.</summary>
    bool Fail(ContentUpgradeDefinition definition, string what)
    {
        _steps.Add(ContentUpgradeStepResult.Refused(definition, what));
        Add(
            ContentUpgradeCodes.PublishFailed,
            FormattableString.Invariant(
                $"upgrade '{definition.Id}' failed to publish onto catalog version {Active} and the ledger shows it did not land: {what} The prior version remains in force and this run's draft was discarded."));
        _stopped = ContentUpgradeOutcome.Failed;
        return true;
    }

    /// <summary>One ledger row by id, or null when the ledger does not hold the upgrade.</summary>
    async Task<ContentUpgradeRecord?> FindRecordAsync(string id)
    {
        IReadOnlyList<ContentUpgradeRecord> records = await Ledger
            .ListUpgradesAsync(CancellationToken)
            .ConfigureAwait(false);
        for (int i = 0; i < records.Count; i++)
        {
            if (string.Equals(records[i].Id, id, StringComparison.Ordinal))
            {
                return records[i];
            }
        }

        return null;
    }
}
