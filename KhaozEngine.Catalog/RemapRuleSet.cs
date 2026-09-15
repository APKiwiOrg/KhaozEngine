using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// The full ordered rule list of contracts 8.3, its idempotence check, and the one pass that answers the id
/// a page should now carry.
/// <para>
/// <b>Idempotence is a property of the SET rather than of one rule.</b> Applying the whole ordered set twice
/// must produce what applying it once produced, which is what makes a crash between apply and commit safe:
/// the retry runs the same rules over ids the interrupted pass already rewrote, at the same page stamp,
/// because the stamp is what did not commit.
/// </para>
/// <para>
/// <b>Scope note.</b> Contracts 8.3 writes the pass against a durable PAGE, which the item-instances program
/// owns and this phase does not have. What ships here is the ID-LEVEL resolution: <see cref="TryResolve"/>
/// answers one id, and the page walk that calls it belongs to whoever owns the page bytes.
/// </para>
/// </summary>
public sealed class RemapRuleSet
{
    readonly RemapRule[] _rules;

    /// <summary>
    /// Holds the rules in sequence order. Throws when they are not strictly ascending by
    /// <see cref="RemapRule.Sequence"/>, because the apply order IS the sequence and a set that lost or
    /// repeated one is not the list any page was brought forward through.
    /// </summary>
    public RemapRuleSet(IReadOnlyList<RemapRule> rulesInSequenceOrder)
    {
        ArgumentNullException.ThrowIfNull(rulesInSequenceOrder);

        _rules = new RemapRule[rulesInSequenceOrder.Count];
        int stamp = 0;
        for (int i = 0; i < _rules.Length; i++)
        {
            RemapRule rule = rulesInSequenceOrder[i] ?? throw new ArgumentException(
                FormattableString.Invariant($"Remap rule {i} is null."), nameof(rulesInSequenceOrder));

            if (i > 0 && rule.Sequence <= _rules[i - 1].Sequence)
            {
                throw new ArgumentException(FormattableString.Invariant(
                    $"Remap rules are strictly ascending by sequence, and rule {i} carries sequence {rule.Sequence} after {_rules[i - 1].Sequence}."),
                    nameof(rulesInSequenceOrder));
            }

            _rules[i] = rule;
            if (rule.IntroducedIn > stamp)
            {
                stamp = rule.IntroducedIn;
            }
        }

        ActiveStamp = stamp;
    }

    /// <summary>Every rule, in sequence order, which is the order the pass applies them in.</summary>
    public IReadOnlyList<RemapRule> Rules => _rules;

    /// <summary>
    /// The stamp a page carries once this whole set has been applied to it, which is the newest
    /// <see cref="RemapRule.IntroducedIn"/> in the set and 0 for an empty one.
    /// </summary>
    public int ActiveStamp { get; }

    /// <summary>
    /// Walks the whole ordered set and reports the first rule that would break idempotence, which is the
    /// check contracts 8.3 says the publish validator MUST run.
    /// <para>
    /// Two shapes are forbidden and this one walk catches both. A rule whose destination is an EARLIER
    /// rule's source would chain on a second pass, because the second pass meets the earlier rule with the
    /// id the later one just produced. A rule whose destination is its OWN source is the degenerate case of
    /// the same walk: its effect depends on the value it also changes.
    /// </para>
    /// <para>
    /// A kind that moves no id carries no destination, so it can never be the head of a chain and is never
    /// flagged.
    /// </para>
    /// </summary>
    public bool IsIdempotent([MaybeNullWhen(true)] out RemapRule offending)
    {
        for (int i = 0; i < _rules.Length; i++)
        {
            int destination = _rules[i].Destination;
            if (destination == 0)
            {
                continue;
            }

            for (int j = 0; j <= i; j++)
            {
                if (_rules[j].Type == _rules[i].Type && _rules[j].FromId == destination)
                {
                    offending = _rules[i];
                    return false;
                }
            }
        }

        offending = null;
        return true;
    }

    /// <summary>
    /// Applies every rule whose <see cref="RemapRule.IntroducedIn"/> is STRICTLY GREATER than
    /// <paramref name="pageStamp"/>, in sequence order, in one pass, and answers the id the page should now
    /// carry.
    /// <para>
    /// Returns false when no rule named the id, which is the common case and means the page needs no
    /// rewrite. <paramref name="toId"/> is then the id it was handed.
    /// </para>
    /// <para>
    /// <paramref name="kind"/> and <paramref name="payload"/> describe the LAST rule that applied, so a
    /// caller that needs every rule an id met walks <see cref="Rules"/> itself. A kind that moves no id,
    /// which is a retire under the placeholder policy and a lowered stack cap, still returns true with the
    /// id unchanged, because the caller has work to do that is not a remap.
    /// </para>
    /// </summary>
    public bool TryResolve(
        ContentTypeId type,
        int fromId,
        int pageStamp,
        out int toId,
        out RemapRuleKind kind,
        out ReadOnlySpan<byte> payload)
    {
        int current = fromId;
        bool applied = false;
        kind = default;
        payload = default;

        for (int i = 0; i < _rules.Length; i++)
        {
            RemapRule rule = _rules[i];
            if (rule.IntroducedIn <= pageStamp || rule.Type != type || rule.FromId != current)
            {
                continue;
            }

            applied = true;
            kind = rule.Kind;
            payload = rule.Payload;
            if (rule.Destination != 0)
            {
                current = rule.Destination;
            }
        }

        toId = current;
        return applied;
    }
}
