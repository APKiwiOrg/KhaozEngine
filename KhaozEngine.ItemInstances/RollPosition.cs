namespace KhaozEngine.ItemInstances;

/// <summary>
/// Contracts 6.4's roll position, and its mapping onto a tier's range, in the ONE place the engine keeps
/// it. A roll is stored as a <c>ushort</c> POSITION rather than as the rolled value, so a published range
/// change RESCALES an existing item instead of re-rolling it, which is only expressible if the position is
/// what was stored.
/// <para>
/// <b>The formula exists exactly once on purpose.</b> The generator writes positions, a stat line builder
/// turns one into a value, and a tooltip shows it. If each carried a copy they would disagree the first
/// time one was touched, and every stored roll in the world is a position interpreted through this
/// arithmetic, so a disagreement silently restates items rather than failing anything.
/// </para>
/// <para>
/// Every term is an integer and there is no float anywhere on this path (contracts 13.4). The
/// <c>+ 32767</c> is round half up on the division, which is what makes position 32,768 land exactly
/// halfway rather than a rounding mode two implementations can differ about.
/// </para>
/// </summary>
public static class RollPosition
{
    /// <summary>The bottom of every range, which resolves to a tier's <c>min</c>.</summary>
    public const ushort Bottom = 0;

    /// <summary>The top of every range, which resolves to a tier's <c>max</c>.</summary>
    public const ushort Top = ushort.MaxValue;

    /// <summary>
    /// The value one stored position names inside one tier's inclusive bounds, contracts 6.4 verbatim.
    /// <para>
    /// The worked table, for a tier with <c>min</c> 10 and <c>max</c> 40: position 0 gives 10, position
    /// 16,384 gives 18, position 32,768 gives 25 and position 65,535 gives 40. Both ends are reachable and
    /// the midpoint lands exactly halfway. A tier whose <c>max</c> equals its <c>min</c> yields that value
    /// for every position, with no division by anything but the constant 65,535.
    /// </para>
    /// </summary>
    /// <param name="position">The stored roll position.</param>
    /// <param name="minimum">The tier's inclusive lower bound from content.</param>
    /// <param name="maximum">The tier's inclusive upper bound from content.</param>
    /// <returns>The value the position names, clamped by nothing: a maximum below the minimum walks the
    /// range downwards, which is ordinary content rather than a special case.</returns>
    public static int Resolve(ushort position, int minimum, int maximum)
        => minimum + (int)((((long)position * ((long)maximum - minimum)) + (Top / 2)) / Top);
}
