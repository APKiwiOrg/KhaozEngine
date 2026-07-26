namespace KhaozEngine.Stats;

/// <summary>
/// One contribution to a single channel: an additive <see cref="Flat"/> term and an additive
/// <see cref="Percent"/> term. A <see cref="StatSet"/> sums every modifier from every source that
/// targets a channel, then folds the two sums into the channel's value (see <see cref="StatSet"/> for
/// the exact fold). The engine assigns no meaning to <see cref="Channel"/>: it is an opaque index the
/// game defines.
/// </summary>
/// <param name="Channel">The channel index this modifier targets. Must be in <c>[0, StatSet.ChannelCount)</c> of the set it is added to.</param>
/// <param name="Flat">The additive term folded into the channel's flat sum, applied before the percent multiplier.</param>
/// <param name="Percent">The additive term folded into the channel's percent sum (e.g. <c>0.1f</c> for +10%), applied as part of the multiplier.</param>
public readonly record struct StatModifier(int Channel, float Flat, float Percent);
