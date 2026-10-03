using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The store calls an upgrade run makes that differ once text exists: the baseline export with its text
/// provenance gate, the write, the freeze, and the atomic expected-draft discard.
/// <para>
/// <b>A store with the text companion is held to its complete state.</b> The baseline's text is read from
/// the exact version, so an unknown version refuses with
/// <see cref="ContentAuthoringException.TextProvenanceUnknownReason"/> before anything is planned, and a
/// baseline bundle that does not carry the text the version records, such as one a wrapper rebuilt through
/// the old constructor, refuses rather than letting a planner plan over text it cannot see. A plan carrying
/// text is written and frozen through the companion. A complete draft is discarded only through
/// <see cref="IContentTextAuthoringStore.TryDiscardChangesAsync"/>, which compares and deletes in one step, so a
/// rival translation landing after the proof is kept.
/// </para>
/// <para>
/// A store without the companion keeps exactly the row-only calls, and a plan carrying text is refused for it
/// before anything is written.
/// </para>
/// </summary>
/// <param name="store">The run's store.</param>
sealed class ContentUpgradeTextRoute(IContentAuthoringStore store)
{
    /// <summary>Why a plan carrying text is refused by a store without the companion.</summary>
    public const string NoCompanionReason =
        "the plan authors text and this catalog store has no text authoring companion, so it cannot publish text. Nothing was changed.";

    readonly IContentTextAuthoringStore? _text = store as IContentTextAuthoringStore;

    /// <summary>Whether the store can apply a plan carrying text.</summary>
    public bool CanAuthorText => _text is not null;

    /// <summary>
    /// The baseline bundle at the active version, after the text gate a store with the companion runs: the
    /// exact version's complete text read, and the bundle's own text compared with it.
    /// </summary>
    /// <param name="active">The active version.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="ContentAuthoringException">The version's text is unknown, or the bundle does not carry it.</exception>
    public async Task<ContentBundle> ExportBaselineAsync(int active, CancellationToken cancellationToken)
    {
        ContentBundle baseline = await store.ExportBundleAsync(active, cancellationToken).ConfigureAwait(false);
        if (_text is null)
        {
            return baseline;
        }

        ContentVersionTextSnapshot recorded = await _text
            .ReadTextSnapshotAsync(active, cancellationToken)
            .ConfigureAwait(false);
        ContentBundleTextState carried = ContentBundleTextCompatibility.TextOf(
            baseline, nameof(IContentAuthoringStore.ExportBundleAsync));
        if (!Agrees(recorded, carried))
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The baseline bundle of version {active} carries {carried.Languages.Count} language(s) and {carried.Values.Count} value(s) and the version records {recorded.Languages.Count} and {recorded.Revisions.Count}, so a planner would plan over text it cannot see. Nothing was planned or written."),
                default,
                0,
                ContentAuthoringException.TextUnrepresentedReason);
        }

        return baseline;
    }

    /// <summary>A plan's change set written into the draft, through the companion when it carries text.</summary>
    /// <param name="plan">The bound plan.</param>
    /// <param name="actor">The run's actor.</param>
    /// <param name="operatorId">The run's operator.</param>
    /// <param name="note">The definition's note.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task<ContentDraft> WriteAsync(
        ContentUpgradePlan plan,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken)
        => plan.CarriesText
            ? Companion().ApplyChangesAsync(plan.ToChanges(), actor, operatorId, note, cancellationToken)
            : store.ApplyEditsAsync(plan.Edits, actor, operatorId, note, cancellationToken);

    /// <summary>
    /// The draft frozen for the run's own publish and read back under that marker, through the companion's
    /// complete freeze when the plan carries text, which the row-only freeze refuses.
    /// </summary>
    /// <param name="plan">The bound plan.</param>
    /// <param name="active">The base version the plan stands on.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The frozen draft, or null when none is open after the freeze.</returns>
    public async Task<ContentDraft?> FreezeAsync(ContentUpgradePlan plan, int active, CancellationToken cancellationToken)
    {
        if (plan.CarriesText)
        {
            ContentTextPublishSnapshot snapshot = await Companion()
                .FreezeChangesAsync(active, cancellationToken)
                .ConfigureAwait(false);
            return snapshot.Draft;
        }

        await store.FreezeDraftAsync(active, cancellationToken).ConfigureAwait(false);
        return await store.GetOpenDraftAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards exactly <paramref name="expected"/> in one step when the store has the companion and the draft
    /// is complete, answering whether it went. Null means the row-only discard is the only route there is.
    /// </summary>
    /// <param name="expected">The complete draft the run proved holds only its own work.</param>
    /// <param name="actor">The run's actor.</param>
    /// <param name="operatorId">The run's operator.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<bool?> TryDiscardAsync(
        ContentDraft expected,
        string actor,
        string operatorId,
        CancellationToken cancellationToken)
    {
        if (_text is null || expected.TextState is null)
        {
            return null;
        }

        return await _text.TryDiscardChangesAsync(expected, actor, operatorId, cancellationToken).ConfigureAwait(false);
    }

    IContentTextAuthoringStore Companion()
        => _text ?? throw new InvalidOperationException(
            "A plan carrying text is refused at planning for a store without the companion.");

    static bool Agrees(ContentVersionTextSnapshot recorded, ContentBundleTextState carried)
    {
        if (recorded.Languages.Count != carried.Languages.Count || recorded.Revisions.Count != carried.Values.Count)
        {
            return false;
        }

        var declared = new HashSet<ContentTextLanguageDeclaration>(carried.Languages);
        foreach (ContentTextLanguage language in recorded.Languages)
        {
            if (!declared.Contains(language.Declaration))
            {
                return false;
            }
        }

        return true;
    }
}
