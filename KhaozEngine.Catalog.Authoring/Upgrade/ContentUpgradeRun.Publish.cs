using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
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
    async Task<bool> PublishAsync(ContentUpgradeDefinition definition, ContentUpgradePlan plan, int attempt)
    {
        string note = ContentUpgradeRunner.NoteFor(definition.Id);

        // A draft standing here belongs to ANOTHER runner: this run's step 5 left none open, and each of its
        // own publishes deletes the one it opened. Applying into it would put two publishes over one change
        // set, and two publishes that both allocate ids for one add edit issue DIFFERENT ids, so whichever
        // commits first files the row under a number the committed bundle names another row by.
        ContentDraft? standing = await Store.GetOpenDraftAsync(CancellationToken).ConfigureAwait(false);
        if (standing is not null)
        {
            return await StandOffAsync(
                definition,
                attempt,
                FormattableString.Invariant(
                    $"another publisher holds the open draft of {standing.EditCount} edit(s) on version {standing.BaseVersion}."))
                .ConfigureAwait(false);
        }

        ContentVersionRecord? baseline = await Store
            .GetVersionAsync(Active, CancellationToken)
            .ConfigureAwait(false);

        // The minimums RISE to this build's ordinals and never fall below the baseline's, so an older build
        // running an upgrade cannot lower the bar a client is admitted over.
        int minimumServerBuild = Math.Max(baseline?.MinimumServerBuild ?? 0, Options.ServerBuild);
        int minimumClientBuild = Math.Max(baseline?.MinimumClientBuild ?? 0, Options.ClientBuild);

        try
        {
            await Store.ApplyEditsAsync(plan.Edits, Options.Actor, Options.Operator, note, CancellationToken)
                .ConfigureAwait(false);
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

            Active = published.VersionNumber;
            _steps.Add(ContentUpgradeStepResult.Applied(definition, published.VersionNumber, plan.ChangeLines));
            return true;
        }
        catch (Exception failure) when (failure is ContentAuthoringException
            or ContentPackException
            or DbException
            or IOException)
        {
            return await ResolveFailureAsync(definition, plan, note, failure, attempt).ConfigureAwait(false);
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
        int attempt)
    {
        await DiscardOwnDraftAsync(note, plan.Edits).ConfigureAwait(false);

        ContentUpgradeRecord? landed = await FindRecordAsync(definition.Id).ConfigureAwait(false);
        if (landed is not null)
        {
            Active = await Store.GetActiveVersionAsync(CancellationToken).ConfigureAwait(false);
            _steps.Add(ContentUpgradeStepResult.Applied(definition, landed.VersionNumber, plan.ChangeLines));
            Add(
                ContentUpgradeCodes.AppliedConcurrently,
                FormattableString.Invariant(
                    $"upgrade '{definition.Id}' was published as version {landed.VersionNumber} by another runner, so this run adopted that result and continued."));
            return true;
        }

        return IsContention(failure)
            ? await StandOffAsync(definition, attempt, failure.Message).ConfigureAwait(false)
            : Fail(definition, failure.Message);
    }

    /// <summary>
    /// Waits out another publisher and asks for another attempt, or gives up when the patience is spent. The
    /// wait grows with the attempt, because a publish holds the draft for its whole pack write.
    /// </summary>
    async Task<bool> StandOffAsync(ContentUpgradeDefinition definition, int attempt, string what)
    {
        if (attempt >= MaxAttemptsPerDefinition)
        {
            return Fail(definition, what);
        }

        await Task.Delay(BackoffFor(attempt), CancellationToken).ConfigureAwait(false);
        Active = await Store.GetActiveVersionAsync(CancellationToken).ConfigureAwait(false);
        return false;
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

    /// <summary>
    /// Whether a failure is CONTENTION with another runner rather than a defect in the plan or the catalog.
    /// The four refusal reasons are exactly what a second publisher produces: it took the draft, it took the
    /// draft away, it moved the base version, or it recorded the upgrade first. A provider's own busy or
    /// deadlock error is the same thing one layer down.
    /// </summary>
    static bool IsContention(Exception failure) => failure switch
    {
        ContentAuthoringException refused => refused.Reason is ContentAuthoringException.PublishInProgressReason
            or ContentAuthoringException.NoOpenDraftReason
            or ContentAuthoringException.BaseVersionMovedReason
            or ContentAuthoringException.UpgradeAlreadyRecordedReason,
        DbException => true,
        _ => false,
    };

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
