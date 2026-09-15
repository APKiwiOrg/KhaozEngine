using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// A rule set that has been checked for idempotence ONCE, which is how a load path pays contracts 8.3's
/// check per CONTAINER instead of per page.
/// <para>
/// <b>The check is quadratic and the rule set does not change between pages.</b>
/// <see cref="RemapRuleSet.IsIdempotent"/> scans every rule at or before each rule with a destination, which
/// is about <c>n^2 / 2</c> comparisons: 20,000 of them for spec 16's 200 rule budget, and 50 million at
/// contracts 8.4's ten thousand rules. <see cref="InstanceRemapPass"/> used to run it before every page, so
/// a 64 page container paid it 64 times over a set that could not have changed
/// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/928">#928</see>).
/// </para>
/// <para>
/// <b>The type IS the assertion.</b> A vetted set cannot be built without the walk and cannot be unbuilt, so
/// the pass takes one and re-checks nothing, and there is no "already vetted" flag argument for a caller to
/// get wrong. The pass keeps its bare <see cref="RemapRuleSet"/> doors for a caller with one page, and they
/// vet on the way through, so the refusal a broken set earns is the same from either door.
/// </para>
/// </summary>
public sealed class VettedRemapRules
{
    VettedRemapRules(RemapRuleSet rules) => Rules = rules;

    /// <summary>The rules, in sequence order, which is the order the pass applies them in.</summary>
    public RemapRuleSet Rules { get; }

    /// <summary>The stamp a page carries once this whole set has been applied to it.</summary>
    public int ActiveStamp => Rules.ActiveStamp;

    /// <summary>
    /// Walks the set once and answers a vetted handle, or throws.
    /// </summary>
    /// <param name="rules">The full ordered rule set the active pack carries.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is null.</exception>
    /// <exception cref="ArgumentException">The set is not idempotent: some rule's destination is an earlier
    /// rule's source for the same type, so applying it twice would not answer what applying it once
    /// answered. That is a fact about the PACK rather than about a stored byte, the publish validator is
    /// required to refuse it (contracts 8.3), and nothing here guesses around it.</exception>
    public static VettedRemapRules Vet(RemapRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!rules.IsIdempotent(out RemapRule? offending))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Remap rule {offending!.Sequence} sends content type {offending.Type.Value} id {offending.FromId} to an id that is an earlier rule's source, so applying the set twice would not answer what applying it once answered (contracts 8.3). The publish validator refuses this set and the pass will not guess around it."),
                nameof(rules));
        }

        return new VettedRemapRules(rules);
    }
}
