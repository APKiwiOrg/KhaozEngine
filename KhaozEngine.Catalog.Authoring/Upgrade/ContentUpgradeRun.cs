using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// ONE run of <see cref="ContentUpgradeRunner"/>, holding the state the gates and the apply loop share: the
/// store and its ledger, the options, the version the run currently stands on, and the steps and diagnostics
/// the report is built from.
/// <para>
/// It is internal because it is the runner's own bookkeeping. Everything a caller sees is on
/// <see cref="ContentUpgradeReport"/>, which is a value and holds no store.
/// </para>
/// </summary>
sealed partial class ContentUpgradeRun
{
    /// <summary>
    /// The attempts past the first exist for exactly one reason: a SECOND runner against the same catalog
    /// holds the one draft, or moves the base version, between this run's plan and its commit, and the plan
    /// is then stale through no fault of the catalog. Two replicas booting together is an ordinary deployment
    /// and a boot that lost the race is a real outage, so the contention is waited out rather than reported.
    /// <para>
    /// The retry is safe because the ledger's primary key is: an upgrade that DID land cannot be published a
    /// second time, and the ledger is re-read before every decision. How long the patience lasts and what
    /// ends it are <see cref="ContentUpgradeStandOff"/>'s.
    /// </para>
    /// </summary>
    readonly List<ContentUpgradeStepResult> _steps = [];
    readonly List<ContentUpgradeDiagnostic> _diagnostics = [];
    readonly HashSet<int> _published = [];
    IReadOnlyList<ContentUpgradeDefinition> _pending = [];
    ContentUpgradeOutcome? _stopped;

    internal ContentUpgradeRun(
        IContentAuthoringStore store,
        IContentUpgradeLedger ledger,
        ContentTypeRegistry registry,
        ContentUpgradeOptions options,
        int activeVersion,
        CancellationToken cancellationToken)
    {
        Store = store;
        Ledger = ledger;
        Registry = registry;
        Options = options;
        ActiveBefore = activeVersion;
        Active = activeVersion;
        CancellationToken = cancellationToken;
    }

    internal IContentAuthoringStore Store { get; }

    internal IContentUpgradeLedger Ledger { get; }

    internal ContentTypeRegistry Registry { get; }

    internal ContentUpgradeOptions Options { get; }

    internal CancellationToken CancellationToken { get; }

    /// <summary>The active version the run found, which every "nothing changed" assertion compares against.</summary>
    internal int ActiveBefore { get; }

    /// <summary>The active version the run currently stands on, which each publish advances.</summary>
    internal int Active { get; private set; }

    /// <summary>Whether the operator pin sits on the active version, which does not block a publish.</summary>
    internal bool PinnedOnActive { get; set; }

    /// <summary>
    /// What the run is doing RIGHT NOW, as a verb phrase, which is what a fault report names. It is tracked
    /// rather than derived because every store call in a run is behind one handler, and a message that said
    /// only "the catalog refused" would leave an operator guessing which call it was.
    /// </summary>
    internal string Operation { get; set; } = "read the catalog";

    /// <summary>The upgrade the current operation belongs to, empty while the run is still at its gates.</summary>
    internal string OperationId { get; set; } = string.Empty;

    /// <summary>
    /// The report for a fault the run could not resolve: a <see cref="ContentUpgradeOutcome.Failed"/> under
    /// <see cref="ContentUpgradeCodes.PublishFailed"/>, naming the upgrade, the operation and the next step.
    /// </summary>
    /// <param name="failure">The fault.</param>
    internal ContentUpgradeReport Failed(Exception failure)
    {
        Add(
            ContentUpgradeCodes.PublishFailed,
            ContentUpgradeFault.Message(OperationId, Operation, Active, failure));
        return Report(ContentUpgradeOutcome.Failed);
    }

    /// <summary>One version this run itself published, which is the only way the version may move under it.</summary>
    /// <param name="versionNumber">The version the publish assigned.</param>
    internal void RecordPublished(int versionNumber)
    {
        _published.Add(versionNumber);
        Active = versionNumber;
    }

    /// <summary>
    /// Re-reads the active version and answers whether the run may CARRY ON. Every path that stands off and
    /// comes back reads the version again, and a supplied expected version has to hold at each of those
    /// reads rather than only at the gate: a run that stood off and found a version a rival left would
    /// otherwise publish onto a baseline nobody previewed, which is the one thing the hosted arm names a
    /// version to prevent.
    /// <para>
    /// A version THIS run published is not a move. Nothing else is accepted, because two runners of one
    /// deploy write the same actor and the ledger cannot tell their rows apart.
    /// </para>
    /// </summary>
    internal async Task<bool> RereadActiveAsync()
    {
        Operation = "read the active version";
        int active = await Store.GetActiveVersionAsync(CancellationToken).ConfigureAwait(false);
        Active = active;
        if (Options.ExpectedVersion is not int expected || active == expected || _published.Contains(active))
        {
            return true;
        }

        if (_stopped == ContentUpgradeOutcome.BaselineMoved)
        {
            // Already reported. The re-read runs on several paths and they can follow each other, and one
            // move of the baseline is one line in the report rather than one per path that noticed it.
            return false;
        }

        Add(
            ContentUpgradeCodes.BaselineMoved,
            FormattableString.Invariant(
                $"this run expected catalog version {expected} and version {active} is now active, published by neither this run nor the preview it was given, so the baseline moved under it. Preview again and rerun with the new version. Nothing further was changed."));
        _stopped = ContentUpgradeOutcome.BaselineMoved;
        return false;
    }

    /// <summary>Builds the report, adding the pin instruction when a publish moved the version under a pin.</summary>
    /// <param name="outcome">How the run ended.</param>
    internal ContentUpgradeReport Report(ContentUpgradeOutcome outcome)
    {
        if (PinnedOnActive && Active != ActiveBefore)
        {
            // The pin is never MOVED by an upgrade. Repinning is an operator's decision about which version a
            // restart serves, and a run that moved it would decide that on their behalf.
            Add(
                ContentUpgradeCodes.PinHeld,
                FormattableString.Invariant(
                    $"the operator pin remains on version {ActiveBefore}. Repin to version {Active} before the next restart, or the upgraded content will not be served."));
        }

        return new ContentUpgradeReport(outcome, ActiveBefore, Active, [.. _steps], [.. _diagnostics]);
    }

    /// <summary>One diagnostic the run only OBSERVES, which stops nothing and changes no outcome.</summary>
    /// <param name="diagnostic">The note.</param>
    internal void Note(ContentUpgradeDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

    /// <summary>One diagnostic and then the report, which is what every gate that stops the run returns.</summary>
    /// <param name="outcome">How the run ended.</param>
    /// <param name="code">The stable code.</param>
    /// <param name="message">The line, naming the catalog state and the next action.</param>
    internal ContentUpgradeReport Stop(ContentUpgradeOutcome outcome, string code, string message)
    {
        Add(code, message);
        return Report(outcome);
    }

    /// <summary>
    /// Step 10: plan the FIRST pending definition exactly and list the rest as pending, writing nothing. The
    /// rest cannot be planned, because a later plan reads the published result of an earlier one.
    /// </summary>
    /// <param name="pending">The pending definitions, ascending by order.</param>
    internal async Task<ContentUpgradeReport> PreviewAsync(IReadOnlyList<ContentUpgradeDefinition> pending)
    {
        ContentUpgradeDefinition first = pending[0];
        ContentUpgradePlan? plan = await PlanAsync(first).ConfigureAwait(false);
        if (plan is null)
        {
            AddPending(pending, 1);
            return Report(ContentUpgradeOutcome.Refused);
        }

        if (plan.Kind == ContentUpgradePlanKind.Refused)
        {
            RefusePlan(first, plan);
            AddPending(pending, 1);
            return Report(ContentUpgradeOutcome.Refused);
        }

        _steps.Add(ContentUpgradeStepResult.Planned(first, plan.ChangeLines, plan.Reason));
        AddPending(pending, 1);
        Add(
            ContentUpgradeCodes.PreviewOnly,
            FormattableString.Invariant(
                $"preview only on catalog version {Active}: {pending.Count} upgrade(s) pending and nothing was written. Rerun in apply mode with expected version {Active} to publish them."));
        return Report(ContentUpgradeOutcome.PreviewOnly);
    }

    /// <summary>
    /// Step 8: each pending definition in ascending order, planned against the CURRENT active version and
    /// published as its own version, so immutable history shows each upgrade separately and an interruption
    /// between two of them resumes at the second.
    /// </summary>
    /// <param name="pending">The pending definitions, ascending by order.</param>
    internal async Task<ContentUpgradeReport> ApplyAsync(IReadOnlyList<ContentUpgradeDefinition> pending)
    {
        // Held for the publish pre-flight, which has to ask whether a draft that appeared under it names an
        // upgrade some runner is still working on.
        _pending = pending;

        for (int i = 0; i < pending.Count; i++)
        {
            ContentUpgradeDefinition definition = pending[i];
            var standOff = new ContentUpgradeStandOff();
            bool settled = false;
            while (!settled)
            {
                settled = await AttemptAsync(definition, standOff).ConfigureAwait(false);
            }

            if (_stopped is ContentUpgradeOutcome stopped)
            {
                AddPending(pending, i + 1);
                return Report(stopped);
            }
        }

        return Report(ContentUpgradeOutcome.Applied);
    }

    /// <summary>
    /// ONE attempt at one definition: export the baseline, plan it, then adopt, refuse or publish. It answers
    /// whether the definition is SETTLED, and a false answer means the publish failed in a way a second
    /// attempt against the moved baseline can resolve.
    /// </summary>
    async Task<bool> AttemptAsync(ContentUpgradeDefinition definition, ContentUpgradeStandOff standOff)
    {
        ContentUpgradePlan? plan = await PlanAsync(definition).ConfigureAwait(false);
        if (plan is null)
        {
            return true;
        }

        switch (plan.Kind)
        {
            case ContentUpgradePlanKind.Refused:
                RefusePlan(definition, plan);
                return true;

            case ContentUpgradePlanKind.AlreadySatisfied:
                // No version is published, because nothing changed. The ledger row is what stops the next run
                // planning it again and reapplying defaults over an operator's values.
                Operation = "record it in the upgrade ledger";
                await Ledger.RecordUpgradeAsync(
                    definition.Stamp,
                    ContentUpgradeDisposition.Adopted,
                    Options.Actor,
                    Options.Operator,
                    CancellationToken).ConfigureAwait(false);
                _steps.Add(ContentUpgradeStepResult.Adopted(definition, plan.Reason));
                return true;

            default:
                return await PublishAsync(definition, plan, standOff).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The baseline exported at the CURRENT active version, handed to the planner as a value. A null answer
    /// means the planner itself refused, which it does by throwing the way a hand-written check does, and the
    /// step is already recorded.
    /// </summary>
    async Task<ContentUpgradePlan?> PlanAsync(ContentUpgradeDefinition definition)
    {
        OperationId = definition.Id;
        Operation = "export the baseline bundle";
        ContentBundle baseline = await Store.ExportBundleAsync(Active, CancellationToken).ConfigureAwait(false);
        var context = new ContentUpgradeContext(Active, baseline, Registry);

        try
        {
            ContentUpgradePlan plan = definition.Plan(context)
                ?? ContentUpgradePlan.Refused("the planner returned no plan at all.");
            if (plan.Kind == ContentUpgradePlanKind.Changes)
            {
                // Remembered here rather than where it is used, because this is the only place a change set
                // this run itself computed comes into being, and the discard proof may not accept anything
                // else as known work.
                _known.Remember(plan.Edits);
            }

            return plan;
        }
        catch (Exception refused) when (refused is InvalidDataException
            or ContentAuthoringException
            or ArgumentException)
        {
            // A planner that THROWS its refusal is the shape the first hand-written upgrade command had, and
            // an operator reading a stack trace instead of a line is the thing this whole report exists to
            // replace. Anything else propagates, because it is a defect rather than a refusal.
            RefusePlan(definition, ContentUpgradePlan.Refused(refused.Message));
            return null;
        }
    }

    /// <summary>The refusal step and its diagnostic, which the preview and the apply share.</summary>
    void RefusePlan(ContentUpgradeDefinition definition, ContentUpgradePlan plan)
    {
        _steps.Add(ContentUpgradeStepResult.Refused(definition, plan.Reason));
        Add(
            ContentUpgradeCodes.PlanRefused,
            FormattableString.Invariant(
                $"upgrade '{definition.Id}' refused this catalog at version {Active}: {plan.Reason} Nothing was changed by it."));
        _stopped = ContentUpgradeOutcome.Refused;
    }

    /// <summary>Every definition from <paramref name="from"/> onward, listed as pending and not run.</summary>
    void AddPending(IReadOnlyList<ContentUpgradeDefinition> pending, int from)
    {
        for (int i = from; i < pending.Count; i++)
        {
            _steps.Add(ContentUpgradeStepResult.Pending(pending[i]));
        }
    }

    void Add(string code, string message) => _diagnostics.Add(new ContentUpgradeDiagnostic(code, message));
}
