using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// The version-line actions of spec 10.7 and 10.8: <c>catalog-pin</c> and <c>catalog-rollback</c>.
/// <para>
/// <b>Which version a server boots has ONE order of precedence.</b> A version pinned in the SERVER'S OWN
/// CONFIG wins, always. Otherwise the operator's hold, when it is not null. Otherwise the active version.
/// A pin written against a server whose config already pins one is a 200 that NAMES the config pin, because
/// the write happened and takes effect the moment the config pin is removed, while a bare 200 for a call
/// with no effect on the next restart is the failure this action exists to prevent.
/// </para>
/// <para>
/// <b>A rollback BUILDS A DRAFT rather than publishing one.</b> The operator reviews the diff and publishes
/// it, which is what makes a rollback reviewable rather than a second uncontrolled change.
/// </para>
/// </summary>
/// <param name="store">The authoring store the hold is written to and the rollback is built from.</param>
/// <param name="registry">The registry the rolled-back rows' types are declared in.</param>
/// <param name="options">The server's own configuration, which the pin's precedence rule reads.</param>
internal sealed class CatalogVersionActions(
    IContentAuthoringStore store,
    ContentTypeRegistry registry,
    CatalogAdminActionOptions options)
{
    /// <summary>Registers the two names on the admin surface.</summary>
    /// <param name="admin">The surface to register on.</param>
    public void Register(ServerAdmin admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.RegisterAction(CatalogAdminActions.PinAction, PinAsync);
        admin.RegisterAction(CatalogAdminActions.RollbackAction, RollbackAsync);
    }

    /// <summary>
    /// Writes or clears the operator's hold, which is the only lever v1 gives between publishing and
    /// restarting: pinning is how a publish is staged for a later restart and unpinning is how a server
    /// catches up.
    /// </summary>
    async Task<AdminActionResult> PinAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        if (!CatalogRequest.TryBody(
            payload, CatalogAdminActions.PinAction, "'version', or null to clear the hold", out JsonElement body, out AdminActionResult refused))
        {
            return refused;
        }

        if (!CatalogRequest.TryOperator(body, out string operatorId, out string? refusal)
            || !CatalogRequest.TryNullableCount(body, "version", out int? version, out bool present, out refusal))
        {
            return CatalogRefusal.Malformed(refusal!);
        }

        if (!present)
        {
            return CatalogRefusal.Malformed(
                "'version' names the version to hold at, or null to clear the hold, and this request carries neither.");
        }

        var warnings = new List<string>();
        if (version is int wanted)
        {
            ContentVersionRecord? record = await store
                .GetVersionAsync(wanted, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return CatalogRefusal.BadRequest(
                    FormattableString.Invariant(
                        $"Version {wanted.ToString(CultureInfo.InvariantCulture)} does not exist, so it cannot be pinned."),
                    ContentAuthoringException.UnknownVersionReason);
            }

            if (options.ServerBuild > 0 && record.MinimumServerBuild > options.ServerBuild)
            {
                // ACCEPTED with a warning: the operator may be pinning ahead of an upgrade on purpose, and
                // the boot-time check is the real gate.
                warnings.Add(FormattableString.Invariant(
                    $"Version {wanted.ToString(CultureInfo.InvariantCulture)} requires server build {record.MinimumServerBuild.ToString(CultureInfo.InvariantCulture)} and this server is build {options.ServerBuild.ToString(CultureInfo.InvariantCulture)}. The pin was written, and boot is the gate that refuses it."));
            }
        }

        if (options.ConfiguredVersion is int configured)
        {
            warnings.Add(FormattableString.Invariant(
                $"This server's own config pins version {configured.ToString(CultureInfo.InvariantCulture)}, and config wins over the operator's hold. The hold was written and takes effect the moment the config pin is removed."));
        }

        try
        {
            await store
                .SetPinnedVersionAsync(version, CatalogAdminActions.Actor, operatorId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ContentAuthoringException failure)
        {
            return CatalogRefusal.From(failure, registry);
        }

        return AdminActionResult.Ok(new CatalogPinPayload(version, options.ConfiguredVersion, warnings));
    }

    /// <summary>
    /// Builds a draft that would restore an earlier version's field values. A row live at the target and
    /// RETIRED since blocks it, and the refusal names every blocking rule plus the way out.
    /// </summary>
    async Task<AdminActionResult> RollbackAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        if (!CatalogRequest.TryBody(
            payload, CatalogAdminActions.RollbackAction, "'toVersion'", out JsonElement body, out AdminActionResult refused))
        {
            return refused;
        }

        if (!CatalogRequest.TryOperator(body, out string operatorId, out string? refusal)
            || !CatalogRequest.TryNote(body, out string note, out refusal)
            || !CatalogRequest.TryCount(body, "toVersion", 0, out int target, out refusal))
        {
            return CatalogRefusal.Malformed(refusal!);
        }

        if (target < 1)
        {
            return CatalogRefusal.Malformed(
                "'toVersion' names the version to restore the field values of, and version numbers begin at 1.");
        }

        try
        {
            ContentDraft draft = await store
                .RollbackToAsync(target, CatalogAdminActions.Actor, operatorId, note, cancellationToken)
                .ConfigureAwait(false);

            return AdminActionResult.Ok(new CatalogRollbackPayload(true, draft.EditCount, []));
        }
        catch (ContentAuthoringException failure)
            when (failure.Reason == ContentAuthoringException.RetireIrreversibleReason)
        {
            return AdminActionResult.Conflict(new CatalogRollbackBlockedPayload(
                "rollback blocked by an irreversible retire",
                ContentAuthoringException.RetireIrreversibleReason,
                ContentRollback.BlockedCode,
                await BlockersAsync(target, cancellationToken).ConfigureAwait(false),
                ContentRollback.Remedy));
        }
        catch (ContentAuthoringException failure)
        {
            return CatalogRefusal.From(failure, registry);
        }
    }

    /// <summary>
    /// The rules that blocked a rollback, recomputed from the two row sets. The store's refusal carries its
    /// findings rather than the rules, and an operator needs the RULE: the sequence and the version that
    /// introduced it are what name the publish that did the retiring.
    /// </summary>
    async Task<IReadOnlyList<CatalogBlockingRulePayload>> BlockersAsync(
        int target,
        CancellationToken cancellationToken)
    {
        ContentPublishBaseline baseline = await store
            .ReadPublishBaselineAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ContentRowRevision> at = await CatalogRequest
            .LiveRowsAsync(store, registry, target, cancellationToken).ConfigureAwait(false);

        ContentRollbackPlan plan = ContentRollback.Prepare(
            target, at, baseline.VersionNumber, baseline.Rows, baseline.Rules, registry);

        var blockers = new List<CatalogBlockingRulePayload>(plan.Blockers.Count);
        for (int i = 0; i < plan.Blockers.Count; i++)
        {
            ContentRollbackBlocker blocker = plan.Blockers[i];
            blockers.Add(new CatalogBlockingRulePayload(
                blocker.RuleSequence,
                blocker.IntroducedIn,
                registry.TryGet(blocker.Type, out ContentTypeRegistration? type) ? type.TypeKey : string.Empty,
                blocker.DefinitionId,
                RemapRuleKind.Retired.ToString()));
        }

        return blockers;
    }
}
