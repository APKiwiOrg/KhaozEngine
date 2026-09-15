using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The rule-list prefix check every store's step 10 runs before it appends anything: every rule a store
/// already holds must be the plan's own prefix, identity for identity and payload for payload.
/// <para>
/// <b>It lives here rather than once per backend</b> because a rule list is append only (contracts 8.1) and
/// the rule is therefore the same sentence on every store. Three copies of it drifted once already: two of
/// them compared every column except <see cref="RemapRule.Payload"/>, which is where a retire's policy and
/// its destination live, so a plan could rewrite what a published rule MEANS while keeping the columns that
/// identify it.
/// </para>
/// <para>
/// A plan built over a different rule history would renumber rules the store has already published, and the
/// rule chunk hash in both its manifests would then name a set nothing can rebuild. That is why the refusal
/// carries <see cref="ContentAuthoringException.BaseVersionMovedReason"/>: the remedy is the same one a base
/// version that moved gets, re-read the baseline and prepare the plan again.
/// </para>
/// </summary>
public static class ContentRulePrefix
{
    /// <summary>
    /// Refuses a plan whose rule list is not an extension of the one the store holds.
    /// </summary>
    /// <param name="held">The full ordered rule list the store holds, read inside the commit's transaction.</param>
    /// <param name="plan">The plan about to be committed.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ContentAuthoringException">The plan carries fewer rules, or a different one at a sequence the store already holds.</exception>
    public static void Require(IReadOnlyList<RemapRule> held, ContentPublishPlan plan)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Rules.Count < held.Count)
        {
            throw Moved(FormattableString.Invariant(
                $"The plan carries {plan.Rules.Count} remap rule(s) and this store already holds {held.Count}. A rule list is append only, so a plan can never carry fewer than the version it is published onto."));
        }

        for (int i = 0; i < held.Count; i++)
        {
            if (Same(held[i], plan.Rules[i]))
            {
                continue;
            }

            throw Moved(FormattableString.Invariant(
                $"Remap rule {i + 1} of the plan is not the one this store already holds at that sequence, so the plan was built over a different rule history."));
        }
    }

    /// <summary>
    /// Whether two rules are the same rule, WHOLE. The payload is part of the comparison because it is what
    /// a retire's policy and a lowered stack cap are written in, so two rules agreeing on every other column
    /// can still say different things.
    /// </summary>
    /// <param name="held">The rule the store holds.</param>
    /// <param name="planned">The rule the plan carries at that sequence.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool Same(RemapRule held, RemapRule planned)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(planned);

        return held.Sequence == planned.Sequence
            && held.IntroducedIn == planned.IntroducedIn
            && held.Type == planned.Type
            && held.Kind == planned.Kind
            && held.FromId == planned.FromId
            && held.ToId == planned.ToId
            && held.Payload.SequenceEqual(planned.Payload);
    }

    static ContentAuthoringException Moved(string message)
        => new(message, default, 0, ContentAuthoringException.BaseVersionMovedReason);
}
