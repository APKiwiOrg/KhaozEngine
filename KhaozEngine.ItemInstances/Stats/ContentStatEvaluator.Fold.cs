using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The fold itself, contracts 13.2's eight steps, plus the two rules the steps lean on: the FIXED fold
/// order and FLOOR division. They sit together because they are one decision. The order matters only
/// because the division rounds, and the division is written out only because the order made the rounding
/// observable.
/// </summary>
public sealed partial class ContentStatEvaluator
{
    /// <summary>
    /// Contracts 13.2's eight steps over one stat, in <c>long</c> arithmetic with floor division at every
    /// divide.
    /// <para>
    /// Steps 1 to 3 are the two walks below: the inverted index is already in
    /// <c>(SourceKind, Ordinal, InstanceId, ModifierIndex)</c> order, so gathering IS walking it, and the
    /// scope and the condition are one <see cref="Applies"/> call per line. Steps 4 to 6 are the Flat and
    /// Increased pool and the first divide. Step 7 is the More loop, in the SAME order. Step 8 is the
    /// checked narrowing and the clamp.
    /// </para>
    /// </summary>
    int Fold(int statId, in StatContext context)
    {
        int[] index = _statIndex[statId];
        int count = _statIndexCount[statId];
        ReadOnlySpan<StatModifierLine> lines = _lines.AsSpan(0, _lineCount);
        ReadOnlySpan<int> scope = _scopeTags.AsSpan(0, _scopeTagCount);
        ReadOnlySpan<int> contextTags = context.Tags.Span;
        ReadOnlySpan<int> ownTags = _statTags[statId];

        // Steps 4 and 5. Flat is summed in the stat's scaled units and Increased is an ADDITIVE basis
        // point pool: every one is summed here and applied once below, which is what makes it Increased
        // rather than More. A More line is skipped without asking whether it applies, so a conditional
        // line costs exactly one registry call per fold rather than two.
        long flat = _statBase[statId];
        long increased = BasisPointScale;
        for (int position = 0; position < count; position++)
        {
            StatModifierLine line = lines[index[position]];
            if (line.Combine == StatCombineKind.More)
            {
                continue;
            }

            if (!Applies(in line, scope, contextTags, ownTags, in context))
            {
                continue;
            }

            if (line.Combine == StatCombineKind.Flat)
            {
                flat += line.Value;
            }
            else
            {
                increased += line.Value;
            }
        }

        // Step 6, the first divide. Both sides are saturated into int range first, so the product cannot
        // leave long: a flat or a pool that needs more than an int to state is already outside anything a
        // stat can hold, and saturating is how contracts 13.2's "saturates at the clamp rather than
        // overflowing" is delivered rather than a silent wrap.
        flat = Saturate(flat);
        increased = Saturate(increased);
        long value = Saturate(FloorDivide((flat * increased) + BasisPointHalf, BasisPointScale));

        // Step 7, the More loop, walking the SAME index in the SAME order. Each factor applies as its own
        // multiplicative step, and the order is the displayed number because rounding at each step makes
        // integer multiplication non associative.
        for (int position = 0; position < count; position++)
        {
            StatModifierLine line = lines[index[position]];
            if (line.Combine != StatCombineKind.More)
            {
                continue;
            }

            if (!Applies(in line, scope, contextTags, ownTags, in context))
            {
                continue;
            }

            long factor = BasisPointScale + line.Value;
            value = Saturate(FloorDivide((value * factor) + BasisPointHalf, BasisPointScale));
        }

        // Step 8. The narrowing is CHECKED and provably cannot throw, because the saturation above already
        // brought the value inside int, and then the stat row's own inclusive bounds clamp it.
        int narrowed = checked((int)value);
        int minimum = _statMinimum[statId];
        int maximum = _statMaximum[statId];
        return narrowed < minimum ? minimum : narrowed > maximum ? maximum : narrowed;
    }

    /// <summary>
    /// Steps 2 and 3 for one line: the tag scope against the UNION of the context's tags and the stat row's
    /// own, then the condition.
    /// <para>
    /// An EMPTY scope applies always and costs the length check alone, which is the common case. A non
    /// empty scope is an AND, and it is the ONLY relation between a modifier and its target: there is no
    /// scope expression, no negation and no OR.
    /// </para>
    /// </summary>
    bool Applies(
        in StatModifierLine line,
        ReadOnlySpan<int> scope,
        ReadOnlySpan<int> contextTags,
        ReadOnlySpan<int> ownTags,
        in StatContext context)
    {
        for (int position = 0; position < line.TagScopeLength; position++)
        {
            int required = scope[line.TagScopeStart + position];
            if (!Contains(contextTags, required) && !Contains(ownTags, required))
            {
                return false;
            }
        }

        return Satisfied(line.ConditionId, in context);
    }

    /// <summary>
    /// One condition id, spec 11.3. Id <see cref="IStatConditionRegistry.Unconditional"/> applies always.
    /// Every other id up to <see cref="IStatConditionRegistry.EngineBandMaximum"/> is the engine's own
    /// band, the engine defines NONE of them in v1, and so a line under one names a condition that does not
    /// exist and does not apply. The registry is asked about ids above the band and nothing else, which is
    /// what keeps the band free for a later engine condition.
    /// </summary>
    bool Satisfied(int conditionId, in StatContext context)
    {
        if (conditionId == IStatConditionRegistry.Unconditional)
        {
            return true;
        }

        if (conditionId <= IStatConditionRegistry.EngineBandMaximum)
        {
            return false;
        }

        return _conditions is not null && _conditions.Evaluate(conditionId, in context);
    }

    /// <summary>Membership over a short unsorted tag span, which is what a scope match needs and no more.</summary>
    static bool Contains(ReadOnlySpan<int> values, int value)
    {
        for (int position = 0; position < values.Length; position++)
        {
            if (values[position] == value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The FOLD ORDER, spec 11.4: <c>(SourceKind, Ordinal, InstanceId)</c>, with the modifier index coming
    /// for free from each source's own line order. Two sources that tie on kind and ordinal still order,
    /// which cannot happen through the engine's four kinds and can happen through a game source.
    /// </summary>
    internal static int CompareFoldOrder(in StatSourceKey left, in StatSourceKey right)
    {
        if (left.SourceKind != right.SourceKind)
        {
            return left.SourceKind < right.SourceKind ? -1 : 1;
        }

        if (left.Ordinal != right.Ordinal)
        {
            return left.Ordinal < right.Ordinal ? -1 : 1;
        }

        if (left.InstanceId != right.InstanceId)
        {
            return left.InstanceId < right.InstanceId ? -1 : 1;
        }

        return 0;
    }

    /// <summary>
    /// FLOOR division, toward negative infinity, contracts 13.2. C# <c>/</c> truncates toward zero instead,
    /// which rounds a debuff differently from a buff of the same size: <c>floordiv(-9000, 10000)</c> is -1
    /// and <c>(-9000) / 10000</c> is 0, so a plain divide loses a whole unit of a 1.4 unit penalty and
    /// reports no penalty at all.
    /// <para>
    /// It is <see cref="Math.DivRem(long, long)"/> with a negative remainder adjustment and it is NEVER
    /// <c>Math.Round</c>, which takes a <c>double</c> and would put floating point on the determinism path
    /// contracts 13.4 exists to keep clear of it. Contracts 6.4's roll formula is a different formula whose
    /// numerator is never negative, lives once in <see cref="RollPosition"/> and keeps its <c>/</c>.
    /// </para>
    /// </summary>
    /// <param name="numerator">The dividend, of either sign.</param>
    /// <param name="denominator">The divisor, which is always <see cref="BasisPointScale"/> here.</param>
    /// <returns>The quotient rounded toward negative infinity.</returns>
    internal static long FloorDivide(long numerator, long denominator)
    {
        long quotient = Math.DivRem(numerator, denominator, out long remainder);
        if (remainder != 0 && ((remainder < 0) != (denominator < 0)))
        {
            quotient--;
        }

        return quotient;
    }

    /// <summary>
    /// One intermediate held inside int range, which is what bounds every product in the fold below
    /// <c>long</c>'s ceiling and makes the checked narrowing at step 8 provably safe.
    /// </summary>
    static long Saturate(long value)
        => value < int.MinValue ? int.MinValue : value > int.MaxValue ? int.MaxValue : value;
}
