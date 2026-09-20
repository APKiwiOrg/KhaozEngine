using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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

        // A draft standing here is either the one an interrupted run left holding exactly this plan, which
        // this run publishes as it stands, or another runner's. Applying into another runner's would put two
        // publishes over one change set, and a second set of edits in one draft is not this plan any more.
        ContentDraft? standing = await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false);
        bool standingIsOwn = standing is not null && IsOwn(standing, note, plan.Edits);
        if (standing is not null && !standingIsOwn)
        {
            // Only a RUNNER's draft is worth waiting out. An operator opens one between the gate and here as
            // readily as before it, and waiting that out spends the whole attempt budget to report a state
            // the first look already knew.
            return IsRunnersDraft(standing)
                ? await StandOffAsync(
                    definition,
                    standOff,
                    FormattableString.Invariant(
                        $"another publisher holds the open draft of {standing.EditCount} edit(s) on version {standing.BaseVersion}."))
                    .ConfigureAwait(false)
                : StopForOperatorDraft(definition, standing);
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
                await Store
                    .ApplyEditsAsync(plan.Edits, Options.Actor, Options.Operator, note, CancellationToken)
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

            RecordPublished(published.VersionNumber);
            _steps.Add(ContentUpgradeStepResult.Applied(definition, published.VersionNumber, plan.ChangeLines));
            return true;
        }
        catch (Exception failure) when (ContentUpgradeFault.IsHandled(failure))
        {
            return await ResolveFailureAsync(definition, plan, note, failure, standOff).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Step 9. The run's own draft is discarded first, so no failure path leaves one behind, and then the
    /// ledger is READ rather than reasoned about. A row means the upgrade landed, here or in a concurrent
    /// runner, and either way it is never published twice. An exception after the commit point and a refusal
    /// before it look identical from here, and only the ledger tells them apart.
    /// </summary>
    async Task<bool> ResolveFailureAsync(
        ContentUpgradeDefinition definition,
        ContentUpgradePlan plan,
        string note,
        Exception failure,
        ContentUpgradeStandOff standOff)
    {
        Operation = "discard its own draft";
        bool rivalIsLive = await DiscardOwnDraftAsync(note, plan.Edits).ConfigureAwait(false);

        Operation = "read the upgrade ledger";
        ContentUpgradeRecord? landed = await FindRecordAsync(definition.Id).ConfigureAwait(false);
        if (landed is not null)
        {
            _steps.Add(ContentUpgradeStepResult.Applied(definition, landed.VersionNumber, plan.ChangeLines));
            Add(
                ContentUpgradeCodes.AppliedConcurrently,
                FormattableString.Invariant(
                    $"upgrade '{definition.Id}' was published as version {landed.VersionNumber} by another runner, so this run adopted that result and continued."));
            await RereadActiveAsync().ConfigureAwait(false);
            return true;
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
        if (!await RereadActiveAsync().ConfigureAwait(false))
        {
            // The version moved to one nobody previewed. The definition is left where it stands and the
            // report says so, because publishing onto it is the refusal the expected version exists for.
            _steps.Add(ContentUpgradeStepResult.Pending(definition));
            return true;
        }

        return false;
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
