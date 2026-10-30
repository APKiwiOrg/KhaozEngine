using System;

namespace KhaozEngine.Primitives;

/// <summary>Selects an index from caller-prepared nonnegative integer weights.</summary>
public static class WeightedSelector
{
    /// <summary>
    /// Selects one index in input order, with probability proportional to its weight. Zero weights never win.
    /// </summary>
    /// <param name="weights">The weights, whose total must not exceed <see cref="int.MaxValue"/>.</param>
    /// <param name="rng">The caller-owned random source, required even for an empty pool.</param>
    /// <param name="index">The selected index relative to the supplied span, or -1 when returning false.</param>
    /// <returns>True when an index is selected, or false for an empty or all-zero pool without drawing.</returns>
    /// <remarks>
    /// Every nonempty call copies the weights into one owned array before validation and RNG invocation.
    /// The call takes O(n) time and O(n) storage and never writes caller memory or retains the inputs.
    /// The caller must prevent concurrent mutation during capture and keep candidates aligned with the
    /// supplied span until resolving the index. Mutation by injected RNG code cannot change this call's
    /// captured pool. Later calls capture the current weights anew.
    /// <para>
    /// Validation completes before any random state is consumed. A successful call reserves one logical
    /// slot through <see cref="RandomDraw.Below"/>. A total of one calls <see cref="IRandomSource.Skip"/>,
    /// while a larger total calls <c>NextInt(0, total)</c> once, even for a single positive entry.
    /// This does not guarantee one underlying generator step or replay stability when weights change.
    /// Source exceptions propagate without retry. Source failures cannot roll back random state, and no
    /// result is promised on an exception.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="rng"/> is null, checked before the pool.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A captured weight is negative or the captured total exceeds <see cref="int.MaxValue"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The source returns a draw outside the required half-open interval <c>[0, total)</c>.
    /// </exception>
    public static bool TrySelectIndex(ReadOnlySpan<int> weights, IRandomSource rng, out int index)
    {
        index = -1;
        ArgumentNullException.ThrowIfNull(rng);
        if (weights.IsEmpty)
            return false;

        int[] captured = weights.ToArray();
        int total = 0;
        foreach (int weight in captured)
        {
            if (weight < 0 || weight > int.MaxValue - total)
                throw new ArgumentOutOfRangeException(nameof(weights),
                    "Weights must be nonnegative and their total must not exceed int.MaxValue.");
            total += weight;
        }
        if (total == 0)
            return false;

        int roll = RandomDraw.Below(rng, total);
        if (roll < 0 || roll >= total)
            throw new InvalidOperationException("The random source returned a draw outside the weight total.");

        for (int i = 0; i < captured.Length; i++)
        {
            if (roll < captured[i])
            {
                index = i;
                return true;
            }
            roll -= captured[i];
        }

        throw new InvalidOperationException("The weighted draw did not select an entry.");
    }
}
