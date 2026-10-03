using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Every edit of every change set ONE run computed, kept so the run can prove later that a draft it did not
/// write whole holds nothing but its own planned work.
/// <para>
/// <b>It only ever grows, and only from this run's own planners.</b> A plan is remembered when the apply loop
/// computes one and when a draft comparison replans a definition, so the set is exactly what this process
/// asked the shipped definitions for against the baselines it stood on. Nothing a store handed back and
/// nothing another runner wrote is ever added to it, which is what keeps the discard proof honest: an edit
/// the run cannot account for is an edit somebody else authored.
/// </para>
/// <para>
/// It is a type rather than a list on the run because the run has no business holding a bag of edits it
/// might compare the wrong way. The one question asked of it is whether a draft is covered.
/// </para>
/// </summary>
sealed class ContentUpgradeKnownPlans
{
    readonly List<ContentEdit> _edits = [];
    readonly List<ContentTextEdit> _text = [];

    /// <summary>One change set this run computed, rows and text, which an empty or refused plan is not.</summary>
    /// <param name="plan">The plan, or null when the planner produced no change set.</param>
    internal void Remember(ContentUpgradePlan? plan)
    {
        if (plan is null || plan.Kind != ContentUpgradePlanKind.Changes)
        {
            return;
        }

        _edits.AddRange(plan.Edits);
        _text.AddRange(plan.TextEdits);
    }

    /// <summary>
    /// Whether every edit, text intent and introduction the draft holds came from a plan this run computed. A
    /// complete draft is proved over its text too, and a draft a row-only route built is proved by the row-only
    /// overload, which never claims one holding text or holding nothing.
    /// </summary>
    /// <param name="draft">The open draft as the store handed it back.</param>
    internal bool Holds(ContentDraft draft)
        => draft.TextState is null
            ? ContentUpgradeDraftMatch.IsKnownWork(draft, _edits)
            : ContentUpgradeDraftMatch.IsKnownWork(draft, _edits, _text);

    /// <summary>
    /// Whether ANY edit or text intent the draft holds came from a plan this run computed, which is what tells
    /// a draft this run's own write merged into apart from one that is another writer's whole.
    /// </summary>
    /// <param name="draft">The open draft as the store handed it back.</param>
    internal bool HoldsSome(ContentDraft draft) => ContentUpgradeDraftMatch.HoldsKnownWork(draft, _edits, _text);
}
