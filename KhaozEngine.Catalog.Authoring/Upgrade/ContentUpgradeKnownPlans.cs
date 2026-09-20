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

    /// <summary>One change set this run computed, which an empty or refused plan is not.</summary>
    /// <param name="edits">The plan's edits, or null when the planner produced no change set.</param>
    internal void Remember(IReadOnlyList<ContentEdit>? edits)
    {
        if (edits is null)
        {
            return;
        }

        for (int i = 0; i < edits.Count; i++)
        {
            _edits.Add(edits[i]);
        }
    }

    /// <summary>Whether every edit the draft holds came from a plan this run computed.</summary>
    /// <param name="draft">The open draft as the store handed it back.</param>
    internal bool Holds(ContentDraft draft) => ContentUpgradeDraftMatch.IsKnownWork(draft, _edits);

    /// <summary>
    /// Whether ANY edit the draft holds came from a plan this run computed, which is what tells a draft this
    /// run's own write merged into apart from one that is another writer's whole.
    /// </summary>
    /// <param name="draft">The open draft as the store handed it back.</param>
    internal bool HoldsSome(ContentDraft draft) => ContentUpgradeDraftMatch.HoldsKnownWork(draft, _edits);
}
