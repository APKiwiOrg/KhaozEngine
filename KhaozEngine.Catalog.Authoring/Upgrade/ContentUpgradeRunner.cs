using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The engine's upgrade orchestration: the gates a run passes before it plans anything, the ordered apply,
/// and the recovery that makes an interrupted run resumable. A game brings its definitions and nothing else.
/// <para>
/// <b>It never seeds and it never deletes.</b> A catalog with no active version is
/// <see cref="ContentUpgradeOutcome.NoCatalog"/> and the caller's own import is what fills it. Reseeding a
/// deployed catalog is how an operator's values get reverted on the next deploy, which is the whole reason
/// this path exists.
/// </para>
/// <para>
/// <b>Up to date writes NOTHING.</b> No draft, no version, no audit row and no ledger row, so the ordinary
/// boot of an already-current catalog costs one version read and one ledger read.
/// </para>
/// </summary>
public static partial class ContentUpgradeRunner
{
    /// <summary>
    /// What every note a run writes opens with. The note is how an interrupted run's own draft is told from
    /// an operator's: the runner's actor plus a note naming the upgrade id is the proof, and both are on the
    /// seam already, so nothing new had to be persisted for it.
    /// </summary>
    public const string NotePrefix = "content upgrade ";

    /// <summary>The note one definition's draft and published version carry.</summary>
    /// <param name="upgradeId">The definition's stable id.</param>
    /// <exception cref="ArgumentNullException"><paramref name="upgradeId"/> is null.</exception>
    public static string NoteFor(string upgradeId)
    {
        ArgumentNullException.ThrowIfNull(upgradeId);
        return NotePrefix + upgradeId;
    }

    /// <summary>
    /// Runs the pending upgrades, or previews them, and reports what happened. The gates run in the order
    /// design section 7 declares them, and each one that stops the run leaves the catalog exactly as it was.
    /// </summary>
    /// <param name="store">The authoring store, which must also implement <see cref="IContentUpgradeLedger"/>.</param>
    /// <param name="registry">This build's registry, which a planner checks a committed bundle against.</param>
    /// <param name="set">The definitions this build ships.</param>
    /// <param name="options">The mode, the expected version, who is running it and the build ordinals.</param>
    /// <param name="cancellationToken">Cancels the reads and the publishes.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<ContentUpgradeReport> RunAsync(
        IContentAuthoringStore store,
        ContentTypeRegistry registry,
        ContentUpgradeSet set,
        ContentUpgradeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(options);

        // Step 1. No ledger means no history, and an upgrade with no history reapplies its defaults over an
        // operator's values on the next run, which is the failure the ledger exists to prevent.
        if (store is not IContentUpgradeLedger ledger)
        {
            return Stop(
                ContentUpgradeOutcome.Unsupported,
                ContentUpgradeCodes.LedgerUnsupported,
                FormattableString.Invariant(
                    $"this catalog store keeps no upgrade ledger, so the {set.Count} shipped upgrade(s) cannot be applied. Open it at schema version 2 or later."));
        }

        // Step 2. The runner never seeds. A host that imports its bundle calls RecordBaselineAsync next.
        int active = await store.GetActiveVersionAsync(cancellationToken).ConfigureAwait(false);
        if (active <= 0)
        {
            return Stop(
                ContentUpgradeOutcome.NoCatalog,
                ContentUpgradeCodes.NoCatalog,
                FormattableString.Invariant(
                    $"this catalog holds no published version, so there is nothing to upgrade. Import the shipped bundle and record the baseline for the {set.Count} shipped upgrade(s)."));
        }

        var run = new ContentUpgradeRun(store, ledger, registry, options, active, cancellationToken);

        // Step 3. An id the ledger holds and this build does not ship means the catalog was upgraded by a
        // newer build. Running the older set against it would be a downgrade nothing here can reason about.
        IReadOnlyList<ContentUpgradeRecord> recorded = await ledger
            .ListUpgradesAsync(cancellationToken)
            .ConfigureAwait(false);
        var held = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();
        for (int i = 0; i < recorded.Count; i++)
        {
            held.Add(recorded[i].Id);
            if (!set.Contains(recorded[i].Id))
            {
                unknown.Add(recorded[i].Id);
            }
        }

        if (unknown.Count > 0)
        {
            return run.Stop(
                ContentUpgradeOutcome.CatalogAheadOfBuild,
                ContentUpgradeCodes.CatalogAheadOfBuild,
                FormattableString.Invariant(
                    $"this catalog at version {active} holds upgrade(s) '{string.Join("', '", unknown)}' that this build does not ship, so it is ahead of this build. Run the newer build or restore a catalog this one can read. Nothing was changed."));
        }

        // Step 4. Pending means shipped and not in the ledger. None pending writes NOTHING at all.
        List<ContentUpgradeDefinition> pending = Pending(set, held);
        if (pending.Count == 0)
        {
            return run.Stop(
                ContentUpgradeOutcome.UpToDate,
                ContentUpgradeCodes.UpToDate,
                FormattableString.Invariant(
                    $"this catalog at version {active} already holds all {set.Count} shipped upgrade(s). Nothing was written."));
        }

        // Step 5. An interrupted run's own draft is discarded, and anything else is operator work.
        ContentUpgradeOutcome? draftStop = await run.ResolveOpenDraftAsync(pending).ConfigureAwait(false);
        if (draftStop is ContentUpgradeOutcome stopped)
        {
            return run.Report(stopped);
        }

        // Step 6. A pin elsewhere means the baseline is not the version in force. A pin ON the active version
        // does not block the publish, and it is never moved: repinning is the operator's own decision.
        int? pinned = await store.GetPinnedVersionAsync(cancellationToken).ConfigureAwait(false);
        if (pinned is int pin && pin != active)
        {
            return run.Stop(
                ContentUpgradeOutcome.PinnedElsewhere,
                ContentUpgradeCodes.PinnedElsewhere,
                FormattableString.Invariant(
                    $"this catalog is pinned to version {pin} while version {active} is active, so an upgrade would publish onto a baseline nothing is serving. Move or clear the pin, then run the {pending.Count} pending upgrade(s). Nothing was changed."));
        }

        run.PinnedOnActive = pinned is not null;

        // Step 7. The hosted arm names the version it previewed, and a catalog that moved under it is refused.
        if (options.ExpectedVersion is int expected && expected != active)
        {
            return run.Stop(
                ContentUpgradeOutcome.BaselineMoved,
                ContentUpgradeCodes.BaselineMoved,
                FormattableString.Invariant(
                    $"expected catalog version {expected}, but version {active} is active, so the baseline moved since it was previewed. Preview again and rerun with the new version. Nothing was changed."));
        }

        // Steps 8 to 10.
        return options.Mode == ContentUpgradeMode.Preview
            ? await run.PreviewAsync(pending).ConfigureAwait(false)
            : await run.ApplyAsync(pending).ConfigureAwait(false);
    }

    /// <summary>
    /// Records every shipped definition as <see cref="ContentUpgradeDisposition.Baseline"/>, which is what a
    /// host calls straight after seeding a fresh catalog from the current bundle.
    /// <para>
    /// <b>A crash between the seed and this call is safe.</b> The next run finds each definition already
    /// satisfied by the seeded content and records it as <see cref="ContentUpgradeDisposition.Adopted"/>
    /// instead, and recording an id the ledger already holds is a no-op, so calling it twice is harmless.
    /// </para>
    /// </summary>
    /// <param name="store">The authoring store, which must implement <see cref="IContentUpgradeLedger"/>.</param>
    /// <param name="set">The definitions this build ships.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded, empty when it forwarded none.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="store"/> keeps no upgrade ledger.</exception>
    public static async Task RecordBaselineAsync(
        IContentAuthoringStore store,
        ContentUpgradeSet set,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentException.ThrowIfNullOrEmpty(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        if (store is not IContentUpgradeLedger ledger)
        {
            throw new ArgumentException(
                "This catalog store keeps no upgrade ledger, so a baseline cannot be recorded. Open it at schema version 2 or later.",
                nameof(store));
        }

        IReadOnlyList<ContentUpgradeDefinition> definitions = set.Definitions;
        for (int i = 0; i < definitions.Count; i++)
        {
            await ledger.RecordUpgradeAsync(
                definitions[i].Stamp,
                ContentUpgradeDisposition.Baseline,
                actor,
                operatorId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Every shipped definition the ledger does not hold, ascending by order.</summary>
    static List<ContentUpgradeDefinition> Pending(ContentUpgradeSet set, HashSet<string> held)
    {
        IReadOnlyList<ContentUpgradeDefinition> definitions = set.Definitions;
        var pending = new List<ContentUpgradeDefinition>(definitions.Count);
        for (int i = 0; i < definitions.Count; i++)
        {
            if (!held.Contains(definitions[i].Id))
            {
                pending.Add(definitions[i]);
            }
        }

        return pending;
    }

    /// <summary>A refusal taken before there is a run to hang it on, which is the first two gates only.</summary>
    static ContentUpgradeReport Stop(ContentUpgradeOutcome outcome, string code, string message)
        => new(outcome, 0, 0, [], [new ContentUpgradeDiagnostic(code, message)]);
}
